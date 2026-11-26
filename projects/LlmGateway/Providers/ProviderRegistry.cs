using LlmGateway.Billing;
using LlmGateway.Configuration;
using Microsoft.Extensions.AI;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Net;
using OpenAI;

namespace LlmGateway.Providers;

/// <summary>
/// 一个可选中的"模型出口"：某个 provider 的某个模型。
/// </summary>
public sealed class ProviderEntry
{
    public required string ProviderName { get; init; }
    public required string ModelId { get; init; }
    public required IChatClient Client { get; init; }
    public required CostCalculator Cost { get; init; }

    /// <summary>该 provider 的韧性管道。同一 provider 的所有模型共用，熔断状态也共用。</summary>
    public required ResiliencePipeline Pipeline { get; init; }
}

/// <summary>请求的模型没有配置，属于调用方的错（400），不是服务端故障（500）。</summary>
public sealed class UnknownModelException(string message) : Exception(message);

/// <summary>
/// model 名 → ProviderEntry 的路由表。
///
/// 三个设计点：
///
/// ① **熔断器必须按 provider 隔离**
///    熔断器是有状态的。如果全网关共用一条 pipeline，DeepSeek 挂了会把
///    本地 Ollama 一起熔断 —— 网关自己变成了单点故障，比不做熔断更糟。
///    所以：pipeline 建在 provider 级别，client 建在 model 级别。
///
/// ② **client 必须复用**
///    OpenAIClient 内部持有 HttpClient。每次请求 new 一个会耗尽 socket
///    （TIME_WAIT 堆积），这是 .NET 里最经典的服务端事故之一。
///
/// ③ 配置错了要早失败
///    启动时就把每个 provider 校验一遍并打日志。等到第一个请求进来才炸，
///    陌生人会以为是项目坏了。
/// </summary>
public sealed class ProviderRegistry : IDisposable
{
    /// <summary>
    /// 标记「这次调用不允许重试」。
    ///
    /// 用在流式上：内容已经开始往客户端写了，重试会从头再写一遍，
    /// 用户看到的是重复的两段话。「还没开始」和「已经开始」在重试语义上完全不同。
    ///
    /// 为什么不给流式单独建一条 pipeline：那会有两个独立的熔断器，
    /// 流式把 provider 打挂了、非流式那边还在继续打 —— 保护等于做了一半。
    /// 所以用一条 pipeline，靠这个标记按调用类型改变行为。
    ///
    /// key 必须是静态字段：Properties 按对象身份索引，不是按名字字符串。
    /// </summary>
    public static readonly ResiliencePropertyKey<bool> NoRetryKey = new("no-retry");

    private readonly Dictionary<string, ProviderEntry> _byModel = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> _disposables = [];
    private readonly ILogger<ProviderRegistry> _logger;
    private readonly string _defaultModel;

    public ProviderRegistry(GatewayOptions options, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<ProviderRegistry>();
        _defaultModel = options.DefaultModel;
        var r = options.Resilience;

        foreach (var provider in options.Providers)
        {
            if (string.IsNullOrWhiteSpace(provider.Name))
            {
                _logger.LogWarning("跳过 BaseUrl={BaseUrl} 的 provider：缺 Name", provider.BaseUrl);
                continue;
            }
            if (string.IsNullOrWhiteSpace(provider.BaseUrl))
            {
                _logger.LogWarning("跳过 provider {Name}：缺 BaseUrl", provider.Name);
                continue;
            }
            if (provider.Models.Length == 0)
            {
                _logger.LogWarning("跳过 provider {Name}：Models 为空", provider.Name);
                continue;
            }

            // 一条 pipeline 服务这个 provider 下的所有模型 —— 熔断按 provider 统计
            var pipeline = BuildPipeline(provider, r, loggerFactory.CreateLogger($"Polly.{provider.Name}"));
            var cost = new CostCalculator(provider);

            foreach (var model in provider.Models)
            {
                if (_byModel.TryGetValue(model, out var existing))
                {
                    _logger.LogWarning(
                        "模型 {Model} 被多个 provider 声明，保留先注册的 {Kept}，忽略 {Ignored}",
                        model, existing.ProviderName, provider.Name);
                    continue;
                }

                var client = CreateClient(provider, model);
                _disposables.Add(client);

                _byModel[model] = new ProviderEntry
                {
                    ProviderName = provider.Name,
                    ModelId = model,
                    Client = client,
                    Cost = cost,
                    Pipeline = pipeline,
                };

                _logger.LogInformation(
                    "注册模型 {Model} → {Provider}（{BaseUrl}，计价 {Pricing}）",
                    model, provider.Name, provider.BaseUrl,
                    cost.HasPricing ? $"输入 {provider.InputPrice}/M 输出 {provider.OutputPrice}/M" : "不计费");

                // 云端 provider 没配 key 时，启动不报错、调用才 401 ——
                // 对陌生人来说这是最难查的一类问题：他以为服务起来了，一试就挂。
                // 在启动日志里说清楚，比让他去猜要好。
                if (string.IsNullOrWhiteSpace(provider.ApiKey) && !IsLocalEndpoint(provider.BaseUrl))
                {
                    _logger.LogWarning(
                        "provider {Provider} 没有配置 ApiKey，调用 {Model} 会返回 401。" +
                        "在 appsettings.Development.json 里补上（该文件已被 .gitignore 排除）",
                        provider.Name, model);
                }
            }
        }

        if (_byModel.Count == 0)
            _logger.LogError("没有任何可用模型 —— 检查配置里 Gateway:Providers 是否配了 BaseUrl / Models");
    }

    /// <summary>可用模型名，用于错误提示。</summary>
    public IEnumerable<string> AvailableModels => _byModel.Keys;

    /// <summary>按模型名路由。传空则用默认模型。</summary>
    public ProviderEntry Resolve(string? model)
    {
        var want = string.IsNullOrWhiteSpace(model) ? _defaultModel : model;

        if (string.IsNullOrWhiteSpace(want))
            throw new UnknownModelException("请求里没有指定 model，配置里也没有 DefaultModel");

        if (!_byModel.TryGetValue(want, out var entry))
            throw new UnknownModelException(
                $"未知模型 '{want}'。可用模型：{string.Join(", ", _byModel.Keys)}");

        return entry;
    }

    /// <summary>
    /// 是不是本机地址。本地服务（Ollama）不校验 key，缺 key 不算问题 ——
    /// 只有指向公网的 provider 缺 key 才值得警告。
    /// </summary>
    private static bool IsLocalEndpoint(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return false;
        return uri.IsLoopback
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 「可能自己好」的故障清单。
    ///
    /// 抽成一个方法，是因为重试和熔断必须用同一份判断 —— 两处各写一遍，
    /// 迟早会出现「重试认为该重试、熔断认为不算失败」的分裂，
    /// 而且这种分裂在日志里几乎看不出来。
    /// </summary>
    private static bool IsTransient(Exception? ex) => ex switch
    {
        // StatusCode 为 null 表示"根本没建立连接"：端口不通、DNS 解析失败、
        // 对端在重启。**这是最该重试的一类**，比 500 还该重试 ——
        // 只按状态码白名单判断的话，会把这种情况漏掉。
        HttpRequestException h => h.StatusCode is null || h.StatusCode is
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout,

        // OpenAI SDK 会把很多失败包成自己的异常类型（继承 ClientResultException），
        // 底层 HTTP 异常到不了这一层。不认它的话，重试和熔断对 SDK 报的错全部失效：
        // 日志里照样有错误，但重试不触发、熔断不计数，而且一点提示都没有。
        // Status == 0 表示没拿到 HTTP 响应（连接失败）。
        ClientResultException c => c.Status == 0
            || c.Status is 408 or 429
            || c.Status >= 500,

        // 4xx 里的 401 / 400 不在此列：key 错、参数错，重试一百次还是错

        // Polly 自己造的异常。不显式声明它，超时永远不会触发重试。
        TimeoutRejectedException => true,

        // 只算「不是被外层取消」的取消。不加这个条件，总超时到了之后
        // 重试还会再来一轮，白等一个单次超时的时长。
        TaskCanceledException t => !t.CancellationToken.IsCancellationRequested,

        _ => false,
    };

    private static bool IsNoRetry(ResilienceContext ctx)
        => ctx.Properties.TryGetValue(NoRetryKey, out bool v) && v;

    private static IChatClient CreateClient(ProviderOptions provider, string model)
    {
        // 本地服务（Ollama）不校验 key，但 OpenAI SDK 要求传一个非空值
        var key = string.IsNullOrWhiteSpace(provider.ApiKey) ? "none" : provider.ApiKey;

        return new OpenAIClient(
                new ApiKeyCredential(key),
                new OpenAIClientOptions
                {
                    Endpoint = new Uri(provider.BaseUrl),

                    // ⚠️ 必须关掉 SDK 自带的重试。
                    //
                    // OpenAI SDK 默认会自己重试（ClientRetryPolicy，默认 2 次）。
                    // 外面再套一层 Polly，两者是**乘**的关系不是加：
                    // 配 1 次重试，实际打出 4 次请求（实测日志 "Retry failed after 4 tries"）。
                    //
                    // 三个后果：
                    // ① 重试次数失控，成本按倍数涨
                    // ② 退避节奏由 SDK 决定，你配的 jitter / MaxDelay 不起作用
                    // ③ 熔断器看到的是一次 SDK 包装后的异常，不是每次尝试 —— 统计全错
                    //
                    // 原则：**重试只能有一层**。要么 SDK 要么你，不能都要。
                    RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
                })
            .GetChatClient(model)
            .AsIChatClient();
    }

    /// <summary>
    /// 从外到内：总超时 → 重试 → 熔断 → 单次超时。
    ///
    /// 顺序不是随便排的：
    /// - 熔断在重试**内层**：每次重试都经过熔断器，失败计数快速累积；
    ///   反过来熔断在外面，只能看到"一次最终失败"，重试 4 次也只记 1 次。
    /// - 总超时在重试**外层**：它管的是"用户最多等多久"，放内层就只管单次了。
    /// </summary>
    private static ResiliencePipeline BuildPipeline(
        ProviderOptions provider, ResilienceOptions r, ILogger logger)
    {
        return new ResiliencePipelineBuilder()
            .AddTimeout(TimeSpan.FromSeconds(r.TimeoutSeconds))
            .AddRetry(new RetryStrategyOptions
            {
                // 重试要同时满足两个条件：故障是「可能自己好」的，且这次调用没被标记不重试。
                // 第二个条件给流式用 —— 内容已经写出去了，重试会让用户看到重复的两段。
                ShouldHandle = args => ValueTask.FromResult(
                    !IsNoRetry(args.Context) && IsTransient(args.Outcome.Exception)),

                MaxRetryAttempts = r.MaxRetryAttempts,
                Delay = TimeSpan.FromMilliseconds(r.RetryDelayMs),
                BackoffType = DelayBackoffType.Exponential,
                MaxDelay = TimeSpan.FromSeconds(10),
                UseJitter = true,

                OnRetry = args =>
                {
                    logger.LogWarning(
                        "[{Provider}] 重试 {Attempt}/{Max}：{Error}，等 {Delay:F1}s",
                        provider.Name, args.AttemptNumber + 1, r.MaxRetryAttempts,
                        args.Outcome.Exception?.Message, args.RetryDelay.TotalSeconds);
                    return default;
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = r.CircuitFailureRatio,
                MinimumThroughput = r.CircuitMinimumThroughput,
                // 窗口必须盖住一次请求的最坏耗时（TimeoutSeconds），
                // 否则样本会被滑出窗口，永远凑不够吞吐门槛 —— 熔断器永远不打开，
                // 而且日志里看不出任何异常。
                SamplingDuration = TimeSpan.FromSeconds(r.CircuitSamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(r.CircuitBreakSeconds),

                // 熔断不看 NoRetry：流式失败也是真失败，必须计入统计
                ShouldHandle = args => ValueTask.FromResult(IsTransient(args.Outcome.Exception)),

                OnOpened = args =>
                {
                    logger.LogError(
                        "[{Provider}] 熔断打开，{Seconds}s 内不再发起请求",
                        provider.Name, r.CircuitBreakSeconds);
                    return default;
                },
                OnClosed = args =>
                {
                    logger.LogInformation("[{Provider}] 熔断关闭，恢复正常", provider.Name);
                    return default;
                },
                OnHalfOpened = args =>
                {
                    logger.LogWarning("[{Provider}] 熔断转半开，放一个请求去试探", provider.Name);
                    return default;
                },
            })
            .AddTimeout(TimeSpan.FromSeconds(r.AttemptTimeoutSeconds))
            .Build();
    }

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); }
            catch (Exception ex) { _logger.LogWarning(ex, "释放 client 失败"); }
        }
    }
}
