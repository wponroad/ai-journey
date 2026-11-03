namespace HelloLlm
{
    public sealed class LlmReply
    {
        /// <summary>
        /// 回答的结果
        /// </summary>
        public string Text { get; init; } = "";
        /// <summary>
        /// 输入token数。为 null 表示这次调用没拿到用量统计。
        /// </summary>
        public long? InputTokens { get; init; }
        /// <summary>
        /// 输出 token
        /// </summary>
        public long? OutputTokens { get; init; }
        /// <summary>
        /// 本地调用的耗时
        /// </summary>
        public long ElapsedMs { get; init; }
        /// <summary>
        /// 大模型类型
        /// </summary>
        public string? ModelType { get; init; }
        /// <summary>
        /// 大模型的链接地址
        /// </summary>
        public string? ModeUrl { get; init; }
        /// <summary>
        /// 大模型的Id
        /// </summary>
        public string? ModeId { get; init; }
    }
}
