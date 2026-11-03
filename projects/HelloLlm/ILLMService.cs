namespace HelloLlm
{
    public interface ILLMService
    {
        Task<LlmReply> AskAsync(string question);
    }
}
