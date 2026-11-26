using LlmGateway.Billing;
using LlmGateway.Providers;
using LlmGateway.Telemetry;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using System.ClientModel;
using System.Diagnostics;
using System.Text.Json;

namespace LlmGateway.Chat;

/// <summary>
/// POST /v1/chat —— 网关的唯一业务入口。
///
/// 这个类里能看到的四件事，正好对应本周的四个目标：
///   1. 路由    —— _registry.Resolve(model) 决定谁干活
///   2. 韧性    —— entry.Pipeline 包住每一次上游调用
///   3. 计费    —— 拿到 usage 就写一行账
///   4. 可观测  —— chat.request / llm.call 两层 span
///
/// 为什么是 Controller 而不是 Minimal API：
/// 这个项目将来要被复用成其他 AI 项目，那时接口会有十几个（模型列表、会话管理、
/// 组织配额……）。MVC 的分组、模型验证、过滤器在那种规模下省事得多；
/// Minimal API 的优势只在「一两个端点」时成立，端点一多反而要自己搭框架。
/// </summary>
[ApiController]
[Route("v1")]
public class ChatController : ControllerBase
{
    private readonly ProviderRegistry _registry;
    private readonly UsageStore _usageStore;
    private readonly ILogger<ChatController> _logger;

    /// <summary>
    /// UsageStore 总是被注册（关闭记账时它是个空实现，不写任何东西），
    /// 所以这里可以正常构造函数注入 —— 不需要 ServiceProvider.GetService 那种
    /// 服务定位器写法，依赖关系在编译期就是明确的。
    /// </summary>
    public ChatController(
        ProviderRegistry registry,
        UsageStore usageStore,
        ILogger<ChatController> logger)
    {
        _registry = registry;
        _usageStore = usageStore;
        _logger = logger;
    }

    /// <summary>
    /// 统一对话入口：按 model 名路由到对应 provider，自带重试 / 超时 / 熔断，并按 token 记账。
    /// </summary>
    /// <remarks>
    /// 请求头（可选）：
    /// - X-User-Id：谁在调用，缺省 anonymous
    /// - X-Department：哪个部门，缺省 unknown（只能是 ASCII）
    ///
    /// stream=true 时返回 SSE，每片形如 `data: {"delta":"..."}`，
    /// 最后一片是 `data: {"done":true,...}`，带 usage 和 cost。
    /// </remarks>
    [HttpPost("chat")]
    [ProducesResponseType(typeof(ChatResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(typeof(ErrorDto), StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Chat([FromBody] ChatRequest request, CancellationToken ct)
    {
        var requestId = Guid.NewGuid().ToString("N")[..12];
        var userId = Header("X-User-Id", "anonymous");
        var department = Header("X-Department", "unknown");

        using var activity = GatewayTelemetry.Source.StartActivity(GatewayTelemetry.Spans.ChatRequest);
        activity?.SetTag("request.id", requestId);
        activity?.SetTag("user.id", userId);
        activity?.SetTag("dept", department);
        activity?.SetTag("chat.stream", request.Stream);
        activity?.SetTag("chat.model", request.Model);
        activity?.SetTag("chat.message_count", request.Messages.Count);

        using var scope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["requestId"] = requestId,
            ["userId"] = userId,
        });

        // ── ① 路由 ──
        ProviderEntry entry;
        try
        {
            entry = _registry.Resolve(request.Model);
        }
        catch (UnknownModelException ex)
        {
            // 模型名写错是调用方的问题，返回 400，不要记成服务故障
            _logger.LogWarning("未知模型：{Model}", request.Model);
            return BadRequest(new ErrorDto
            {
                Error = ex.Message,
                ErrorType = "unknown_model",
                RequestId = requestId,
            });
        }

        activity?.SetTag("provider", entry.ProviderName);
        activity?.SetTag("model", entry.ModelId);

        var messages = BuildMessages(request);
        if (messages.Count == 0)
        {
            return BadRequest(new ErrorDto
            {
                Error = "messages 不能为空",
                RequestId = requestId,
            });
        }

        var options = new ChatOptions
        {
            Temperature = request.Temperature,
            MaxOutputTokens = request.MaxOutputTokens,
        };

        // ── ② 分流 ──
        if (request.Stream)
        {
            await StreamAsync(entry, messages, options, requestId, userId, department, ct);

            // 响应体已经手写进去了，不能再让 MVC 序列化一次
            return new EmptyResult();
        }

        return await CompleteAsync(entry, messages, options, requestId, userId, department, ct);
    }

    // ══════════════════════════════════════════════════════════════
    //  非流式
    // ══════════════════════════════════════════════════════════════

    private async Task<IActionResult> CompleteAsync(
        ProviderEntry entry,
        IReadOnlyList<ChatMessage> messages, ChatOptions options,
        string requestId, string userId, string department,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            using var callActivity = GatewayTelemetry.Source.StartActivity(GatewayTelemetry.Spans.ProviderCall);
            callActivity?.SetTag("provider", entry.ProviderName);
            callActivity?.SetTag("model", entry.ModelId);

            // pipeline 里的重试，每一次都会重新进这一段 —— 所以一次逻辑请求
            // 可能产生多笔真实的 token 消耗，而只有最后一次的 response 能拿到 usage。
            // 这是"计费黑洞"的来源，也是将来要接服务端对账的原因。
            var response = await entry.Pipeline.ExecuteAsync(
                async token => await entry.Client.GetResponseAsync(messages, options, token),
                ct);

            sw.Stop();

            var input = response.Usage?.InputTokenCount ?? 0;
            var output = response.Usage?.OutputTokenCount ?? 0;
            var cached = response.Usage?.CachedInputTokenCount ?? 0;
            var cost = entry.Cost.Calculate(input, output, cached);

            callActivity?.SetTag("llm.input_tokens", input);
            callActivity?.SetTag("llm.output_tokens", output);
            callActivity?.SetTag("llm.cost_yuan", cost);
            callActivity?.SetTag("llm.elapsed_ms", sw.ElapsedMilliseconds);

            Record(requestId, userId, department, entry,
                   input, output, cached, cost, sw.ElapsedMilliseconds, "ok", null);

            _logger.LogInformation(
                "完成 {Provider}/{Model} in={Input} out={Output} cost={Cost} {Ms}ms",
                entry.ProviderName, entry.ModelId, input, output,
                cost?.ToString("F6") ?? "不计费", sw.ElapsedMilliseconds);

            return Ok(new ChatResponseDto
            {
                RequestId = requestId,
                Provider = entry.ProviderName,
                Model = entry.ModelId,
                Text = response.Text ?? "",
                Usage = new UsageDto(input, output, cached),
                Cost = cost,
                ElapsedMs = sw.ElapsedMilliseconds,
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            var (status, type) = MapError(ex);

            // 失败也要记 —— 否则"账上只有成功的那些"，成本被系统性低估。
            // 注意这里 token 全是 null：没拿到响应，就不知道花了多少，
            // 不代表没花（超时的上游可能已经生成了一半并计了费）。
            Record(requestId, userId, department, entry,
                   null, null, null, null, sw.ElapsedMilliseconds, "error", type);

            // 熔断、超时、上游报错都是**预期内的失败** —— 它们正是韧性层在工作的表现。
            // 给它们打 ERROR + 完整堆栈，一次熔断短路就刷 60 行（Polly 会把上次失败的
            // 异常整条 InnerException 链挂上去），真正的代码 bug 反而被淹没了。
            // 只有「我们自己的代码出问题」才配得上 Error + 堆栈。
            if (IsExpectedFailure(ex))
                _logger.LogWarning("{Type} {Provider}/{Model} {Ms}ms requestId={RequestId}：{Message}",
                    type, entry.ProviderName, entry.ModelId, sw.ElapsedMilliseconds, requestId, ex.Message);
            else
                _logger.LogError(ex, "失败 {Provider}/{Model} {Type} {Ms}ms requestId={RequestId}",
                    entry.ProviderName, entry.ModelId, type, sw.ElapsedMilliseconds, requestId);

            return StatusCode(status, new ErrorDto
            {
                Error = ex.Message,
                ErrorType = type,
                RequestId = requestId,
            });
        }
    }

    // ══════════════════════════════════════════════════════════════
    //  流式（SSE）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 流式不走 IActionResult —— 响应头一发出去，状态码就定死了，
    /// 只能直接往 Response 里写 SSE 帧。这是流式和请求/响应最本质的差别。
    /// </summary>
    private async Task StreamAsync(
        ProviderEntry entry,
        IReadOnlyList<ChatMessage> messages, ChatOptions options,
        string requestId, string userId, string department,
        CancellationToken ct)
    {
        // 先把响应头发出去。之后发生什么都改不了状态码了 ——
        // 这就是流式的代价：错误只能靠"事件里带一个 error 字段"来表达。
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache";
        // 反向代理（nginx / 各种云网关）默认会缓冲响应，加上这行让它们直通。
        // 不加的话你在本地能看到逐字输出，部署到服务器就变成"一次性刷出来"。
        Response.Headers["X-Accel-Buffering"] = "no";
        await Response.StartAsync(ct);

        var sw = Stopwatch.StartNew();
        long input = 0, output = 0, cached = 0;

        // 用 ResilienceContext 传"这次不许重试"。一条 pipeline 两种行为，
        // 好处是熔断状态只有一份 —— 流式把 provider 打挂，非流式那边也能立刻感知。
        var context = ResilienceContextPool.Shared.Get(ct);
        context.Properties.Set(ProviderRegistry.NoRetryKey, true);

        try
        {
            // 参数显式标成 ResilienceContext —— 写成隐式类型的话，编译器会先按
            // Func<CancellationToken, ValueTask> 猜，然后第二个实参对不上就报错，
            // 不会自动回溯到正确的重载。
            await entry.Pipeline.ExecuteAsync(async (ResilienceContext token) =>
            {
                await foreach (var update in entry.Client.GetStreamingResponseAsync(messages, options, token.CancellationToken))
                {
                    foreach (var content in update.Contents)
                    {
                        switch (content)
                        {
                            case TextContent text when !string.IsNullOrEmpty(text.Text):
                                await WriteEventAsync(new { delta = text.Text }, token.CancellationToken);
                                break;

                            // 流式下用量不是单独的属性，是当成一片 content 塞进来的
                            case UsageContent usageContent when usageContent.Details is { } details:
                                input = details.InputTokenCount ?? 0;
                                output = details.OutputTokenCount ?? 0;
                                cached = details.CachedInputTokenCount ?? 0;
                                break;
                        }
                    }
                }
            }, context);

            sw.Stop();
            var cost = entry.Cost.Calculate(input, output, cached);

            Record(requestId, userId, department, entry,
                   input, output, cached, cost, sw.ElapsedMilliseconds, "ok", null);

            await WriteEventAsync(new
            {
                done = true,
                requestId,
                provider = entry.ProviderName,
                model = entry.ModelId,
                usage = new { inputTokens = input, outputTokens = output, cachedInputTokens = cached },
                cost,
                elapsedMs = sw.ElapsedMilliseconds,
            }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            sw.Stop();
            var (_, type) = MapError(ex);

            Record(requestId, userId, department, entry,
                   input == 0 ? null : input,
                   output == 0 ? null : output,
                   cached == 0 ? null : cached,
                   null, sw.ElapsedMilliseconds, "stream_error", type);

            if (IsExpectedFailure(ex))
                _logger.LogWarning("流式中断 {Type} {Provider}/{Model} {Ms}ms requestId={RequestId}：{Message}",
                    type, entry.ProviderName, entry.ModelId, sw.ElapsedMilliseconds, requestId, ex.Message);
            else
                _logger.LogError(ex, "流式中断 {Provider}/{Model} {Type} {Ms}ms requestId={RequestId}",
                    entry.ProviderName, entry.ModelId, type, sw.ElapsedMilliseconds, requestId);

            // 头已经发出去了，改不了状态码。只能补一个 error 事件让客户端知道"没正常结束"。
            try
            {
                await WriteEventAsync(new
                {
                    done = true,
                    requestId,
                    error = ex.Message,
                    errorType = type,
                }, CancellationToken.None);
            }
            catch
            {
                // 客户端可能已经断开，写不出去就算了
            }
        }
        finally
        {
            ResilienceContextPool.Shared.Return(context);
        }
    }

    private async Task WriteEventAsync(object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOpts);
        await Response.WriteAsync($"data: {json}\n\n", ct);
        // 不 Flush 的话内容会攒在缓冲区里，"流式"就变成了"最后一次性出来"。
        // 注意是 Body.FlushAsync —— HttpResponse 自己没有 FlushAsync 方法。
        await Response.Body.FlushAsync(ct);
    }

    // ══════════════════════════════════════════════════════════════
    //  辅助
    // ══════════════════════════════════════════════════════════════

    /// <summary>中文不转成 \uXXXX —— 否则 curl 看到的是天书。</summary>
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static List<ChatMessage> BuildMessages(ChatRequest request)
    {
        var list = new List<ChatMessage>(request.Messages.Count);
        foreach (var m in request.Messages)
        {
            if (string.IsNullOrWhiteSpace(m.Content)) continue;
            list.Add(new ChatMessage(ToRole(m.Role), m.Content));
        }
        return list;
    }

    private static ChatRole ToRole(string role) => role.ToLowerInvariant() switch
    {
        "system" => ChatRole.System,
        "assistant" => ChatRole.Assistant,
        _ => ChatRole.User,
    };

    private string Header(string name, string fallback)
    {
        var value = Request.Headers[name].FirstOrDefault();
        return string.IsNullOrWhiteSpace(value) ? fallback : value!;
    }

    /// <summary>
    /// 异常 → HTTP 状态码。
    ///
    /// 把"上游挂了"翻译成 5xx、"调用方错了"翻译成 4xx，
    /// 是网关的基本职责 —— 否则调用方看到所有失败都长一样，没法决定该不该重试。
    /// </summary>
    private static (int Status, string Type) MapError(Exception ex) => ex switch
    {
        // 熔断打开：请求根本没发出去
        BrokenCircuitException => (StatusCodes.Status503ServiceUnavailable, "circuit_open"),

        // 超时：发出去了但没等到答案
        TimeoutRejectedException => (StatusCodes.Status504GatewayTimeout, "timeout"),

        // OpenAI SDK 自己包出来的异常。不透传会让上游 401 / 429 全都变成 500，
        // 调用方没法判断"是我错了"还是"对面挂了"。
        ClientResultException c => (c.Status is 429
            ? StatusCodes.Status429TooManyRequests
            : StatusCodes.Status502BadGateway, "upstream_error"),

        // 上游返回了错误
        HttpRequestException => (StatusCodes.Status502BadGateway, "upstream_error"),

        // 调用方自己断开了。499 是 nginx 的非标准码，这里沿用
        OperationCanceledException => (499, "client_cancelled"),

        _ => (StatusCodes.Status500InternalServerError, "internal_error"),
    };

    /// <summary>
    /// 这个异常是不是「系统正常运转时会发生的」。
    ///
    /// 分界线很实际：预期内的失败用 Warning + 一行消息，
    /// 意料之外的用 Error + 完整堆栈。不分开的话，日志里全是熔断和超时的栈，
    /// 你会开始不看日志 —— 那才是真的事故开始。
    /// </summary>
    private static bool IsExpectedFailure(Exception ex)
        => ex is BrokenCircuitException or TimeoutRejectedException
            or ClientResultException or HttpRequestException or OperationCanceledException;

    private void Record(
        string requestId, string userId, string department,
        ProviderEntry entry, long? input, long? output, long? cached,
        decimal? cost, long elapsedMs, string status, string? errorType)
    {
        _usageStore.SafeInsert(new LlmUsageRow(
            requestId, userId, department,
            entry.ProviderName, entry.ModelId,
            input, output, cached,
            UsageStore.ToMicros(cost),
            elapsedMs, status, errorType,
            DateTime.Now));
    }
}
