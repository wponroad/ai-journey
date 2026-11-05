namespace HelloLlm
{
    public interface ILLMService
    {
        Task<LlmReply> AskAsync(string question, LlmSettings settings = null);
        IAsyncEnumerable<LlmChunk> AskStreamAsync(string question, CancellationToken ct = default);
    }
}
