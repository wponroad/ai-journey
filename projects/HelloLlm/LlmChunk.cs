namespace HelloLlm
{
    /// <summary>
    /// 流式返回的一个片段。和 LlmReply 的区别：
    /// LlmReply 是「一次完整调用的结果」，LlmChunk 是「过程中的一小段」。
    /// 最后一片用 IsFinal 标记收尾，统计数字都在那一片上。
    /// </summary>
    public sealed class LlmChunk
    {
        /// <summary>这一片的文本。收尾片（IsFinal）为空。</summary>
        public string Text { get; init; } = "";

        /// <summary>true 表示这是模型的思考过程，不是给用户看的正文。</summary>
        public bool IsReasoning { get; init; }

        /// <summary>true 表示流结束，这一片带着整次调用的统计。</summary>
        public bool IsFinal { get; init; }

        /// <summary>从发起请求到这一片到达的毫秒数。第一片的值就是 TTFT。</summary>
        public long ElapsedMs { get; init; }

        /// <summary>输入 token。只有 IsFinal 片上有值。</summary>
        public long? InputTokens { get; init; }

        /// <summary>输出 token。只有 IsFinal 片上有值。</summary>
        public long? OutputTokens { get; init; }
    }
}
