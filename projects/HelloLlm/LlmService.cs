using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace HelloLlm
{
    public sealed class LlmService : ILLMService
    {
        private readonly IChatClient _chatClient;
        private readonly CostCalculator? _cost;

        /// <summary>
        /// cost 为 null 表示不算钱（比如这个配置节没配价格）。
        /// 价格从外面递进来，服务自己不读配置 —— 和 IChatClient 一样的道理。
        /// </summary>
        public LlmService(IChatClient chatClient, CostCalculator? cost = null)
        {
            _chatClient = chatClient;
            _cost = cost;
        }

        public async Task<LlmReply> AskAsync(string question, LlmSettings settings = null)
        {
            var sw = Stopwatch.StartNew();
            ChatOptions? options = null;
            if (settings != null)
            {
                options = new ChatOptions
                {
                    Temperature = settings.Temperature,
                    TopP = settings.TopP,
                    MaxOutputTokens = settings.MaxTokens
                };
            }
            var response = await _chatClient.GetResponseAsync(question, options);
            sw.Stop();
            var modelInfo = _chatClient.GetService<ChatClientMetadata>();

            long? input = response.Usage?.InputTokenCount;
            long? output = response.Usage?.OutputTokenCount;

            return new LlmReply
            {
                Text = response.Text ?? "",
                InputTokens = input,
                OutputTokens = output,
                Amount = CalcAmount(input, output),
                ElapsedMs = sw.ElapsedMilliseconds,
                ModelType = modelInfo?.ProviderName,
                ModeUrl = modelInfo?.ProviderUri?.AbsoluteUri,
                ModeId = modelInfo?.DefaultModelId
            };
        }

        /// <summary>
        /// 算钱。三个条件缺一不可：有计算器、有输入数、有输出数。
        /// 任何一个缺了就返回 null —— 返回 0 会被误读成「这次免费」。
        /// </summary>
        private decimal? CalcAmount(long? input, long? output)
        {
            if (_cost is null || input is null || output is null)
                return null;

            return _cost.Calculate(input.Value, output.Value);
        }
        public async IAsyncEnumerable<LlmChunk> AskStreamAsync(string question, [EnumeratorCancellation] CancellationToken ct = default)
        {
            var sw = Stopwatch.StartNew();
            long? input = null;
            long? output = null;

            await foreach (ChatResponseUpdate update in
                _chatClient.GetStreamingResponseAsync(question, cancellationToken: ct))
            {
                foreach (AIContent content in update.Contents)
                {
                    switch (content)
                    {
                        // 思考过程（qwen3 默认开思考，会先吐一大段）
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

                        // 用量统计：流式下它不是单独的属性，也是当成一片 content 塞进来的，
                        // 通常在最后。有的 provider 压根不给。
                        case UsageContent usageContent when usageContent.Details is { } details:
                            input = details.InputTokenCount;
                            output = details.OutputTokenCount;
                            break;
                    }
                }
            }
            yield return new LlmChunk
            {
                IsFinal = true,
                ElapsedMs = sw.ElapsedMilliseconds,
                InputTokens = input,
                OutputTokens = output,
                Amount = CalcAmount(input, output)
            };
        }
    }
}
