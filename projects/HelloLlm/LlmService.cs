using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace HelloLlm
{
    public sealed class LlmService : ILLMService
    {
        private readonly IChatClient _chatClient;
        public LlmService(IChatClient chatClient)
        {
            _chatClient = chatClient;
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
            return new LlmReply
            {
                Text = response.Text ?? "",
                InputTokens = response.Usage?.InputTokenCount,
                OutputTokens = response.Usage?.OutputTokenCount,
                ElapsedMs = sw.ElapsedMilliseconds,
                ModelType = modelInfo?.ProviderName,
                ModeUrl = modelInfo?.ProviderUri?.AbsoluteUri,
                ModeId = modelInfo?.DefaultModelId
            };
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
                OutputTokens = output
            };
        }
    }
}
