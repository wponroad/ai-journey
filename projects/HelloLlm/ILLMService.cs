namespace HelloLlm
{
    public interface ILLMService
    {
        Task<LlmReply> AskAsync(string question);
        IAsyncEnumerable<LlmChunk> AskStreamAsync(string question, CancellationToken ct = default);
    }
}
