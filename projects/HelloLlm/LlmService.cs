using Microsoft.Extensions.AI;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HelloLlm
{
    /// <summary>
    /// LLM 调用的唯一入口。
    /// 这个类里可以出现 SDK 类型（ChatMessage / ChatOptions / ChatResponse），
    /// 但它的公开签名里一个都不能有 —— 那是它存在的意义。
    /// </summary>
    public sealed class LlmService : ILLMService
    {
        private readonly IChatClient _chatClient;
        private readonly IChatClient _rawClient;          // 未包装的原始 client，手写循环时用
        private readonly CostCalculator? _cost;
        private readonly OrderService _orderService = new();

        // 记账：为 null 表示不记账（测试、临时脚本可以不传）。
        // 用户和部门是「这次调用属于谁」，所以跟服务实例绑定，
        // 真实系统里从请求上下文取，这里硬编码。
        private readonly UsageStore? _usageStore;
        private readonly string _userId;
        private readonly string _department;

        // 缓存：为 null 表示不启用。启用后每次提问先查，命中就不调模型。
        private readonly LlmCache? _cache;

        /// <summary>
        /// 工具列表。放在字段里，手写循环和 BuildOptions 共用同一份实例。
        /// </summary>
        private readonly IList<AITool> _tools;

        /// <summary>
        /// 重试策略。提到字段而不是每次请求现建，有两个原因：
        /// 一是两条路径（AskAsync / AskWithToolsAsync）共用同一份，不会出现「改了一条忘另一条」；
        /// 二是策略属于配置，不是每次调用的产物。
        /// </summary>
        private readonly ResiliencePipeline _pipeline = new ResiliencePipelineBuilder()
            // 降级没有放在这里。Polly 的 AddFallback 拿不到用户原始的问题，
            // 而规则兜底必须先知道「问了什么」。所以降级写在 AskWithToolsAsync
            // 外层的 try/catch 里 —— 策略层管「失败了怎么办」，业务层管「给用户什么」。
            // 总时长：含重试在内最多 30 秒。必须放最外层 ——
            // 放内层的话它只管单次，重试 4 次就是 4 × 30 秒，封不住用户等待时间。
            .AddTimeout(TimeSpan.FromSeconds(100))
            .AddRetry(new RetryStrategyOptions
            {
                // 只对「可能自己好」的故障重试。4xx 一律不重试 —— 401 key 无效、
                // 400 参数错，重试一百次还是错，只是多花几笔钱。
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>(ex => ex.StatusCode is
                        HttpStatusCode.RequestTimeout or
                        HttpStatusCode.TooManyRequests or
                        HttpStatusCode.InternalServerError or
                        HttpStatusCode.BadGateway or
                        HttpStatusCode.ServiceUnavailable or
                        HttpStatusCode.GatewayTimeout)
                .Handle<TimeoutRejectedException>()
                    .Handle<TaskCanceledException>(),

                MaxRetryAttempts = 4,                           // 总共执行 5 次（1 次原始 + 4 次重试）
                Delay = TimeSpan.FromSeconds(1),
                BackoffType = DelayBackoffType.Exponential,     // 1s → 2s → 4s
                MaxDelay = TimeSpan.FromSeconds(10),            // 封顶，否则第 10 次要等 8 分钟
                UseJitter = true,                               // 抖动：把重试时刻散开，避免重试风暴
                //UseJitter = false,          // 关掉 Polly 的去相关抖动
                //DelayGenerator = args =>    // 自己算：2^n 秒 + 0~300ms 随机
                //    new ValueTask<TimeSpan?>(
                //        TimeSpan.FromSeconds(Math.Pow(2, args.AttemptNumber))
                //        + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 300))),
                OnRetry = args =>
                {
                    // AttemptNumber 是 0-based（第 0 次重试 = 第一次重试），+1 才是人话
                    Console.WriteLine($"[retry {args.AttemptNumber + 1}] {args.Outcome.Exception?.Message}，" +
                                      $"等 {args.RetryDelay.TotalSeconds:F1}s");
                    return default;                             // 返回 ValueTask，不是 void
                }
            })
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                FailureRatio = 0.5,                              // 失败率超一半
                MinimumThroughput = 4,                           // 至少 4 次调用才判定
                SamplingDuration = TimeSpan.FromSeconds(90),     // 统计窗口
                BreakDuration = TimeSpan.FromSeconds(20),        // 打开后多久转半开
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>(ex => ex.StatusCode is
                        HttpStatusCode.RequestTimeout or
                        HttpStatusCode.TooManyRequests or
                        HttpStatusCode.InternalServerError or
                        HttpStatusCode.BadGateway or
                        HttpStatusCode.ServiceUnavailable or
                        HttpStatusCode.GatewayTimeout)
                        .Handle<TimeoutRejectedException>()
                    .Handle<TaskCanceledException>(),
                OnOpened = args => { Console.WriteLine("[circuit] 打开"); return default; },
                OnClosed = args => { Console.WriteLine("[circuit] 关闭"); return default; },
            })
            // 单次：一次调用最多 15 秒。放最内层，防止某一次卡死。
            .AddTimeout(TimeSpan.FromSeconds(30))
            .Build();

        /// <summary>
        /// cost 为 null 表示不算钱（比如这个配置节没配价格）。
        /// 价格从外面递进来，服务自己不读配置 —— 和 IChatClient 一样的道理。
        ///
        /// usageStore 为 null 表示不记账。记账放在这一层而不是外面包一层装饰器，
        /// 是因为只有这里能看到重试真正消耗了多少 —— 装饰器只能看到最终结果。
        /// </summary>
        public LlmService(IChatClient chatClient, CostCalculator? cost = null,
                          UsageStore? usageStore = null,
                          string userId = "anonymous", string department = "unknown",
                          LlmCache? cache = null)
        {
            
            _rawClient = chatClient;
            _usageStore = usageStore;
            _userId = userId;
            _department = department;
            _cache = cache;

            _tools =
            [
                AIFunctionFactory.Create((GetOrderDelegate)_orderService.GetOrder),
                AIFunctionFactory.Create((GetStockDelegate)_orderService.GetStock),
                AIFunctionFactory.Create((CalcPriceDelegate)_orderService.CalcPrice),
                AIFunctionFactory.Create((GetLatestOrderNoDelegate)_orderService.GetLatestOrderNo),
            ];

            // 包一层装饰器：自动处理「模型请求调用 → 执行工具 → 结果回传 → 再问一次」的循环。
            // 不包的话，模型返回的只是一条调用请求，Text 是空的，工具永远不会被执行。
            _chatClient = new ChatClientBuilder(chatClient)
                .UseFunctionInvocation(configure: c => c.MaximumIterationsPerRequest = 5)
                .Build();
            _cost = cost;
        }

        // ───────── 非流式 ─────────

        public Task<LlmReply> AskAsync(string question, LlmSettings? settings = null)
            => AskAsync(AsMessages(question), settings);

        public async Task<LlmReply> AskAsync(IList<LlmMessage> messages, LlmSettings? settings = null)
        {
            var requestId = NewRequestId();
            var sw = Stopwatch.StartNew();
            try
            {
                var response = await _pipeline.ExecuteAsync(
                    async ct => await _rawClient.GetResponseAsync(ToChatMessages(messages), BuildOptions(settings), ct));
                sw.Stop();

                var reply = ToReply(response, sw.ElapsedMilliseconds);
                RecordUsage(requestId, reply, sw.ElapsedMilliseconds);
                return reply;
            }
            catch (Exception ex)
            {
                // 失败也要留痕：这一行证明「尝试发生过」，将来对账时没有它就完全查不到
                RecordError(requestId, ex, sw.ElapsedMilliseconds);
                throw;      // 记账是旁观者，异常必须继续往上抛
            }
        }

        /// <summary>
        /// 手写版的工具调用循环 —— 不用 UseFunctionInvocation，每一步自己控制。
        /// 存在的意义：看清消息怎么长、工具怎么执行、循环怎么停。
        /// 真实网关必须这么写，因为要在循环里插日志、计费、超时、熔断。
        /// </summary>
        /// <param name="trace">每步的回调，用来打印诊断信息</param>
        /// <param name="onCalls">每轮结束后回传「调用了哪些工具」，调用方拿去做统计</param>
        public async Task<LlmReply> AskWithToolsAsync(IList<LlmMessage> messages, LlmSettings? settings = null,
                                                      int maxSteps = 5, Action<string>? trace = null,
                                                      Action<IReadOnlyList<ToolCallTrace>>? onCalls = null)
        {
            var requestId = NewRequestId();
            var sw = Stopwatch.StartNew();

            // 用量累加器放在外层：失败时它里面还存着「前面几轮已经花掉的钱」。
            // 如果它只是 core 里的局部变量，异常一抛，这些数字就跟着变量一起没了 ——
            // 钱花了，账没记，这是计费系统最典型的漏洞。
            var usage = new UsageAccumulator();

            // 缓存 key 只取用户问题，不含 system prompt。
            // 因为语义缓存算的是「问题像不像」，掺进一大段固定人设会把所有问题的向量拉得更接近。
            // 代价：改了 system prompt 必须手动清缓存（LlmCache.ClearAsync）。
            var userQuestion = messages.LastOrDefault(m => m.Role == LlmRole.User)?.Content ?? "";

            if (_cache is not null && userQuestion.Length > 0)
            {
                var hit = await _cache.LookupAsync(userQuestion);
                if (hit is not null)
                {
                    Console.WriteLine(hit.Kind == "exact"
                        ? "[cache] 精确命中（一字不差）"
                        : $"[cache] 语义命中（相似度 {hit.Score:F4}）");

                    var cached = new LlmReply
                    {
                        Text = hit.Answer,
                        IsCacheHit = true,
                        CacheKind = hit.Kind,
                        ElapsedMs = sw.ElapsedMilliseconds,
                    };
                    RecordUsage(requestId, cached, sw.ElapsedMilliseconds);
                    return cached;
                }
            }

            try
            {
                var reply = await AskWithToolsCoreAsync(messages, settings, maxSteps, trace, onCalls, sw, usage);

                // 只缓存模型真正生成的答案。降级答案是「服务不可用」的兜底，
                // 缓存它等于把这个临时状态固化 24 小时，比不缓存更糟。
                if (_cache is not null && !reply.IsFallback && userQuestion.Length > 0)
                    await _cache.StoreAsync(userQuestion, reply.Text);

                RecordUsage(requestId, reply, sw.ElapsedMilliseconds);
                return reply;
            }
            catch (Exception ex) when (IsServiceDown(ex))
            {
                // 降级：服务不可用时用规则兜底，不把异常抛给用户。
                // 只兜「服务挂了」这一类。参数错误、代码 bug 要照常抛 ——
                // 否则自己的 bug 会被规则悄悄盖住，永远发现不了。
                Console.WriteLine($"[degrade] {ex.GetType().Name}: {ex.Message}");
                Console.WriteLine($"[degrade] 走规则兜底，问题：{userQuestion}");
                Console.WriteLine(usage.IsEmpty
                    ? "[degrade] 没有产生 token 消耗（请求没真正发出去）"
                    : $"[degrade] 但已经花掉 输入 {usage.Input} / 输出 {usage.Output} token，照记");

                var fallbackReply = new LlmReply
                {
                    Text = RuleBasedAnswer(userQuestion),
                    IsFallback = true,
                    ElapsedMs = sw.ElapsedMilliseconds,

                    // 降级不等于免费。熔断短路那次确实零消耗（请求压根没发），
                    // 但「第 2 轮超时」这种情况，第 1 轮的 token 是真金白银付过的。
                    InputTokens = usage.IsEmpty ? null : usage.Input,
                    OutputTokens = usage.IsEmpty ? null : usage.Output,
                    CachedInputTokens = usage.IsEmpty ? null : usage.Cached,
                    Amount = usage.IsEmpty ? null : CalcAmount(usage.Input, usage.Output, usage.Cached),
                };

                // Status 记成 fallback 而不是 ok ——
                // 混在一起的话，降级率永远看不见，模型挂了三个月你都以为系统正常。
                RecordUsage(requestId, fallbackReply, sw.ElapsedMilliseconds);
                return fallbackReply;
            }
            catch (Exception ex)
            {
                // 不在 IsServiceDown 清单里的异常（自己的 bug、参数错），
                // 也要留痕，但必须原样抛出去 —— 兜底会把它们藏起来。
                RecordError(requestId, ex, sw.ElapsedMilliseconds);
                throw;
            }
        }

        /// <summary>
        /// 真正干活的实现。抽出来只是为了让外层能整体包一层 try/catch ——
        /// 里面每个策略的位置一个没变，变的只是「失败时谁来收尾」。
        /// </summary>
        private async Task<LlmReply> AskWithToolsCoreAsync(IList<LlmMessage> messages, LlmSettings? settings,
                                                           int maxSteps, Action<string>? trace,
                                                           Action<IReadOnlyList<ToolCallTrace>>? onCalls,
                                                           Stopwatch sw, UsageAccumulator usage)
        {
            var chatMessages = ToChatMessages(messages);
            var options = BuildOptions(settings);

            // 用量累加：账单按每一次 API 调用算，只看最后一轮会严重低估成本。
            // 存在 usage 对象里而不是局部变量，是为了失败时外层还能读到。

            for (int step = 1; step <= maxSteps; step++)
            {
                // 重试包在循环体里，只管单次调用。
                // 不能包住整个 for：第 2 轮失败时第 1 轮的工具已经执行过了，
                // 重跑循环会再调一次 GetOrder —— 非幂等，真实系统里就是重复下单。
                var response = await _pipeline.ExecuteAsync(
                    async ct => await _rawClient.GetResponseAsync(chatMessages, options, ct));

                if (response.Usage is { } u)
                {
                    usage.Input += u.InputTokenCount ?? 0;
                    usage.Output += u.OutputTokenCount ?? 0;
                    usage.Cached += u.CachedInputTokenCount ?? 0;
                }

                // assistant 这条消息必须回填历史：它里面有 FunctionCallContent，
                // 模型靠它记住「我刚才请求过什么」。不回填的话，它会一直重复请求。
                chatMessages.AddRange(response.Messages);

                // 调用请求藏在 assistant 消息的 Contents 里，要穿过 Messages 才拿得到
                var calls = response.Messages
                    .SelectMany(m => m.Contents)
                    .OfType<FunctionCallContent>()
                    .ToList();

                trace?.Invoke($"[step {step}] messages={chatMessages.Count} " +
                              $"输入={response.Usage?.InputTokenCount} token " +
                              $"调用={(calls.Count == 0 ? "无（给最终回答）" : string.Join(", ", calls.Select(c => c.Name)))}");

                if (calls.Count == 0)
                {
                    var info = _rawClient.GetService<ChatClientMetadata>();
                    return new LlmReply
                    {
                        Text = response.Text ?? "",
                        InputTokens = usage.Input,
                        OutputTokens = usage.Output,
                        CachedInputTokens = usage.Cached,
                        Amount = CalcAmount(usage.Input, usage.Output, usage.Cached),
                        ElapsedMs = sw.ElapsedMilliseconds,
                        ModelType = info?.ProviderName,
                        ModeUrl = info?.ProviderUri?.AbsoluteUri,
                        ModeId = info?.DefaultModelId
                    };
                }

                // 回传结构化调用记录，调用方不用去解析 trace 字符串
                onCalls?.Invoke(calls
                    .Select(c => new ToolCallTrace(c.Name, JsonSerializer.Serialize(c.Arguments)))
                    .ToList());

                foreach (var call in calls)
                {
                    object? result;
                    try
                    {
                        var fn = _tools.OfType<AIFunction>().First(f => f.Name == call.Name);
                        result = await fn.InvokeAsync(new AIFunctionArguments(call.Arguments));
                    }
                    catch (Exception ex)
                    {
                        // 工具炸了不能把整个对话炸掉。把错误当成结果交回给模型，
                        // 它至少能告诉用户「查询失败了」，而不是整个请求 500。
                        result = $"工具执行失败：{ex.Message}";
                    }

                    // FunctionResultContent 必须绑定 CallId —— 模型靠这个 ID
                    // 把「结果」和「刚才那次请求」对上。绑错就是乱套。
                    chatMessages.Add(new ChatMessage(ChatRole.Tool,
                        [new FunctionResultContent(call.CallId, result)]));
                }
            }

            throw new InvalidOperationException($"工具调用超过 {maxSteps} 步仍未结束，可能陷入了循环。");
        }

        // ───────── 流式 ─────────

        public IAsyncEnumerable<LlmChunk> AskStreamAsync(string question, CancellationToken ct = default)
            => AskStreamAsync(AsMessages(question), null, ct);

        public async IAsyncEnumerable<LlmChunk> AskStreamAsync(IList<LlmMessage> messages,
                                                               LlmSettings? settings = null,
                                                               [EnumeratorCancellation] CancellationToken ct = default)
        {
            var requestId = NewRequestId();
            var sw = Stopwatch.StartNew();
            long? input = null;
            long? output = null;
            long? cachedInput = null;
            decimal? amount = null;
            bool finished = false;

            try
            {
                await foreach (ChatResponseUpdate update in
                    _chatClient.GetStreamingResponseAsync(ToChatMessages(messages), BuildOptions(settings), cancellationToken: ct))
                {
                    foreach (AIContent content in update.Contents)
                    {
                        switch (content)
                        {
                            // 思考过程（推理模型才有，qwen3 默认开，DeepSeek 也会回传）
                            case TextReasoningContent reasoning when !string.IsNullOrEmpty(reasoning.Text):
                                yield return new LlmChunk
                                {
                                    Text = reasoning.Text,
                                    IsReasoning = true,
                                    ElapsedMs = sw.ElapsedMilliseconds
                                };
                                break;

                            // 正文
                            case TextContent text when !string.IsNullOrEmpty(text.Text):
                                yield return new LlmChunk
                                {
                                    Text = text.Text,
                                    ElapsedMs = sw.ElapsedMilliseconds
                                };
                                break;

                            // 用量统计：流式下不是单独的属性，也是当成一片 content 塞进来的
                            case UsageContent usageContent when usageContent.Details is { } details:
                                input = details.InputTokenCount;
                                output = details.OutputTokenCount;
                                cachedInput = details.CachedInputTokenCount;
                                break;
                        }
                    }
                }

                amount = CalcAmount(input, output, cachedInput);

                // 收尾片：文本为空，只带统计。调用方靠 IsFinal 判断「真的结束了」
                yield return new LlmChunk
                {
                    IsFinal = true,
                    ElapsedMs = sw.ElapsedMilliseconds,
                    InputTokens = input,
                    OutputTokens = output,
                    CachedInputTokens = cachedInput,
                    Amount = amount
                };

                finished = true;
            }
            finally
            {
                // C# 不允许在带 catch 的 try 里 yield，所以流式只能用 finally 记账：
                // 走到最后一片 = 正常结束；否则 = 中途断了（抛异常，或者调用方提前 Dispose）。
                SafeInsert(new LlmUsageRow(
                    requestId, _userId, _department, null,
                    finished ? input : null,
                    finished ? output : null,
                    finished ? cachedInput : null,
                    finished ? UsageStore.ToMicros(amount) : null,
                    sw.ElapsedMilliseconds,
                    finished ? "ok" : "error",
                    finished ? null : "StreamInterrupted",
                    null, DateTime.Now));
            }
        }

        // ───────── 翻译（这个类存在的唯一理由） ─────────

        /// <summary>单句提问包成一条 User 消息，避免两个重载各写一遍。</summary>
        private static List<LlmMessage> AsMessages(string question)
            => [new LlmMessage { Role = LlmRole.User, Content = question }];

        /// <summary>自己的消息类型 → SDK 的消息类型。这是业务层和 SDK 之间唯一的桥。</summary>
        private static List<ChatMessage> ToChatMessages(IList<LlmMessage> messages)
        {
            var result = new List<ChatMessage>(messages.Count);
            foreach (var m in messages)
                result.Add(new ChatMessage(ToChatRole(m.Role), m.Content));
            return result;
        }

        private static ChatRole ToChatRole(LlmRole role) => role switch
        {
            LlmRole.System => ChatRole.System,
            LlmRole.Assistant => ChatRole.Assistant,
            _ => ChatRole.User,
        };

        /// <summary>
        /// settings 为 null 时必须返回 null，不能返回 new ChatOptions()。
        /// 空的 ChatOptions 会把服务端默认值覆盖成一堆 null。
        /// </summary>
        private ChatOptions? BuildOptions(LlmSettings? settings)
        {
            if (settings is null) return null;

            return new ChatOptions
            {
                Temperature = settings.Temperature,
                TopP = settings.TopP,
                MaxOutputTokens = settings.MaxTokens,
                Tools = _tools,
                ResponseFormat = settings.OutputFormat switch
                {
                    LlmOutputFormat.Json => ChatResponseFormat.Json,
                    LlmOutputFormat.Text => ChatResponseFormat.Text,
                    LlmOutputFormat.JsonSchema when settings.SchemaType is not null
       => ChatResponseFormat.ForJsonSchema(settings.SchemaType, JsonSerializerOptions.Default),
                    _ => null,          // null 不指定，别硬塞 Text 覆盖服务端默认
                }
            };
        }

        /// <summary>
        /// 一次请求内跨重试轮次累加的 token 用量。
        ///
        /// 单独做成对象而不是局部变量，是因为它必须能穿过「抛异常」这个边界。
        /// 局部变量的生命周期止于方法返回，异常一抛它就没了 —— 而恰恰是失败的那一次，
        /// 最需要知道「前面已经花掉多少」。
        /// </summary>
        private sealed class UsageAccumulator
        {
            public long Input;
            public long Output;
            public long Cached;

            /// <summary>全零 = 请求压根没发出去（比如熔断短路），不是「发了但没算出来」</summary>
            public bool IsEmpty => Input == 0 && Output == 0 && Cached == 0;
        }

        // ───────── 记账 ─────────

        private static string NewRequestId() => Guid.NewGuid().ToString("N")[..16];

        /// <summary>成功或降级：把 reply 上的用量写成一行。</summary>
        private void RecordUsage(string requestId, LlmReply reply, long elapsedMs)
            => SafeInsert(new LlmUsageRow(
                requestId, _userId, _department, reply.ModeId,
                reply.InputTokens, reply.OutputTokens, reply.CachedInputTokens,
                UsageStore.ToMicros(reply.Amount),
                elapsedMs,
                // fallback 和 ok 必须分开记：混在一起，降级率就永远看不见，
                // 模型挂了三个月你都以为系统一切正常。
                reply.IsFallback ? "fallback" : "ok",
                null, null, DateTime.Now));

        /// <summary>失败：没有 token 数据，但「这次尝试发生过」必须留痕，否则对账时完全查不到。</summary>
        private void RecordError(string requestId, Exception ex, long elapsedMs)
            => SafeInsert(new LlmUsageRow(
                requestId, _userId, _department, null,
                null, null, null, null,
                elapsedMs, "error", ex.GetType().Name, null, DateTime.Now));

        /// <summary>
        /// 记账失败不能拖垮主流程 —— 但也不能悄悄吞掉，否则等于没有记账。
        /// </summary>
        private void SafeInsert(LlmUsageRow row)
        {
            if (_usageStore is null) return;

            try
            {
                _usageStore.Insert(row);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[meter] 记账失败（不影响本次调用）：{ex.Message}");
            }
        }

        /// <summary>
        /// 哪些异常算「服务不可用」，值得用规则兜底。
        ///
        /// 这个清单必须窄。参数错误（400）、认证失败（401）、自己的代码 bug
        /// 都不该被兜底盖住 —— 兜底会让它们变成一句「服务暂时不可用」，
        /// 你就再也不会去修了。
        /// </summary>
        private static bool IsServiceDown(Exception ex) => ex switch
        {
            BrokenCircuitException => true,      // 熔断打开，压根没发请求
            TimeoutRejectedException => true,    // Polly 主动掐断
            HttpRequestException => true,        // 429 / 5xx / 连不上
            _ => false,
        };

        /// <summary>
        /// 规则兜底：模型挂了的时候，用正则 + 工具把最简单的那部分事情办了。
        ///
        /// 为什么值得做：模型在这类任务里干的事就两件 —— 识别意图、抽参数。
        /// 订单号是 [A-Z]\d{8}，SKU 是 SKU-\d{3}，正则比模型稳得多，而且永远不会挂。
        ///
        /// 这也是「降级不是道歉」的意思：能办的事照样办，办不了的才说办不了。
        /// </summary>
        private string RuleBasedAnswer(string question)
        {
            // 订单号：一个大写字母 + 8 位数字，例如 A20261109
            var orderNo = Regex.Match(question, @"[A-Z]\d{8}").Value;
            if (orderNo.Length > 0)
                return _orderService.GetOrder(orderNo);

            // 商品编号：SKU- 加三位数字，例如 SKU-001
            var sku = Regex.Match(question, @"SKU-\d{3}").Value;
            if (sku.Length > 0)
                return _orderService.GetStock(sku);

            // 连规则都答不了，才老实说 —— 但要告诉用户「现在还能做什么」，
            // 不能只说「服务不可用」然后把人晾在那儿。
            return "智能助手暂时不可用。你可以直接说订单号（如 A20261109）或商品编号（如 SKU-001），我按规则帮你查。";
        }

        private LlmReply ToReply(ChatResponse response, long elapsedMs)
        {
            var modelInfo = _chatClient.GetService<ChatClientMetadata>();
            long? input = response.Usage?.InputTokenCount;
            long? output = response.Usage?.OutputTokenCount;
            long? cachedInput = response.Usage?.CachedInputTokenCount;

            return new LlmReply
            {
                Text = response.Text ?? "",
                InputTokens = input,
                OutputTokens = output,
                CachedInputTokens = cachedInput,
                Amount = CalcAmount(input, output, cachedInput),
                ElapsedMs = elapsedMs,
                ModelType = modelInfo?.ProviderName,
                ModeUrl = modelInfo?.ProviderUri?.AbsoluteUri,
                ModeId = modelInfo?.DefaultModelId
            };
        }

        /// <summary>
        /// 算钱。三个条件缺一不可：有计算器、有输入数、有输出数。
        /// 任何一个缺了就返回 null —— 返回 0 会被误读成「这次免费」。
        /// cachedInput 缺失时按 0 传，结果是保守的高估（缓存比未命中便宜得多）。
        /// </summary>
        private decimal? CalcAmount(long? input, long? output, long? cachedInput = null)
        {
            if (_cost is null || input is null || output is null)
                return null;

            return _cost.Calculate(input.Value, output.Value, cachedInput ?? 0);
        }
    }
}
