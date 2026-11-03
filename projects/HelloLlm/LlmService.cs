using System.Diagnostics;
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

        public async Task<LlmReply> AskAsync(string question)
        {
            var sw = Stopwatch.StartNew();
            var response = await _chatClient.GetResponseAsync(question);
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
                ModeId=modelInfo?.DefaultModelId
            };
        }
    }
}
