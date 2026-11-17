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
        /// 输入里命中提示词缓存的 token 数。缓存单价远低于未命中，算钱必须把两者拆开。
        /// 为 null 表示 provider 没给这个字段。
        /// </summary>
        public long? CachedInputTokens { get; init; }
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
        /// <summary>
        /// 本次调用的金额（元）。用 decimal 而不是 float：
        /// 金额一旦参与累加，二进制浮点的误差会累积成真实的对不上账。
        /// 为 null 表示没算（没拿到用量统计，或者这个配置节没有配价格）。
        /// </summary>
        public decimal? Amount { get; init; }
        /// <summary>
        /// true 表示这条回答来自降级规则（模型不可用时用正则兜底），不是模型生成的。
        ///
        /// 必须标出来，三个原因：
        /// 一是界面要能告诉用户「现在不是 AI 在答」；
        /// 二是计费要区分（降级不花钱），混进正常统计会拉低平均成本；
        /// 三是监控 —— 降级率飙升说明模型全挂了，但系统看起来一切正常。
        /// <b>降级做得越好，故障越难被发现。</b>
        /// </summary>
        public bool IsFallback { get; init; }
    }
}
