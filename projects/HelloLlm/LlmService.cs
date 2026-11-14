using Microsoft.Extensions.AI;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

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

        /// <summary>
        /// 工具列表。放在字段里，手写循环和 BuildOptions 共用同一份实例。
        /// </summary>
        private readonly IList<AITool> _tools;

        /// <summary>
        /// cost 为 null 表示不算钱（比如这个配置节没配价格）。
        /// 价格从外面递进来，服务自己不读配置 —— 和 IChatClient 一样的道理。
        /// </summary>
        public LlmService(IChatClient chatClient, CostCalculator? cost = null)
        {
            _rawClient = chatClient;

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
            var sw = Stopwatch.StartNew();
            var response = await _chatClient.GetResponseAsync(ToChatMessages(messages), BuildOptions(settings));
            sw.Stop();

            return ToReply(response, sw.ElapsedMilliseconds);
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
            var sw = Stopwatch.StartNew();
            var chatMessages = ToChatMessages(messages);
            var options = BuildOptions(settings);

            // 多轮要累加用量：账单按每一次 API 调用算，只看最后一轮会严重低估成本
            long totalIn = 0, totalOut = 0, totalCached = 0;

            for (int step = 1; step <= maxSteps; step++)
            {
                var response = await _rawClient.GetResponseAsync(chatMessages, options);

                if (response.Usage is { } u)
                {
                    totalIn += u.InputTokenCount ?? 0;
                    totalOut += u.OutputTokenCount ?? 0;
                    totalCached += u.CachedInputTokenCount ?? 0;
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
                        InputTokens = totalIn,
                        OutputTokens = totalOut,
                        CachedInputTokens = totalCached,
                        Amount = CalcAmount(totalIn, totalOut, totalCached),
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
            var sw = Stopwatch.StartNew();
            long? input = null;
            long? output = null;
            long? cachedInput = null;

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

            // 收尾片：文本为空，只带统计。调用方靠 IsFinal 判断「真的结束了」
            yield return new LlmChunk
            {
                IsFinal = true,
                ElapsedMs = sw.ElapsedMilliseconds,
                InputTokens = input,
                OutputTokens = output,
                CachedInputTokens = cachedInput,
                Amount = CalcAmount(input, output, cachedInput)
            };
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
