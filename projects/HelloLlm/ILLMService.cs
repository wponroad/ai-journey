namespace HelloLlm
{
    public interface ILLMService
    {
        /// <summary>单轮提问，内部转成一条 User 消息。</summary>
        Task<LlmReply> AskAsync(string question, LlmSettings? settings = null);

        /// <summary>
        /// 多轮提问：把完整的历史消息列表传进来。
        /// 模型是无状态的，它不记得上一轮 —— 「记得」是你每次把历史重发一遍的效果。
        /// </summary>
        Task<LlmReply> AskAsync(IList<LlmMessage> messages, LlmSettings? settings = null);

        /// <summary>单轮流式，内部转成一条 User 消息。</summary>
        IAsyncEnumerable<LlmChunk> AskStreamAsync(string question, CancellationToken ct = default);

        /// <summary>
        /// 多轮流式。和非流式走同一套消息翻译，保证两条路径行为一致。
        /// </summary>
        IAsyncEnumerable<LlmChunk> AskStreamAsync(IList<LlmMessage> messages,
                                                  LlmSettings? settings = null,
                                                  CancellationToken ct = default);
    }
}
