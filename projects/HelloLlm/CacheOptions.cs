namespace HelloLlm
{
    /// <summary>
    /// 缓存配置
    /// </summary>
    public sealed class CacheOptions
    {
        /// <summary>
        /// 是否启用缓存。Redis 连不上时会自动降级为「不缓存」，不会让程序起不来 ——
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// Redis连接串，默认本机 6379。
        /// </summary>
        public string Redis { get; set; } = "127.0.0.1:6379";

        /// <summary>
        /// 语义命中的相似度阈值。
        ///
        /// 实测参考（bge-m3）：同义改写 0.9543，只差一个订单号 0.9892，任意两句中文 0.4976。
        /// 因为有实体守卫兜底，阈值可以设得比较保守 —— 0.93 只放过「说法几乎一样」的问题。
        /// </summary>
        public double Threshold { get; set; } = 0.93;

        /// <summary>
        /// 缓存存活时间（分钟）。
        ///
        /// 这是「我最多能容忍多久的过期答案」。即使问题本身可缓存，
        /// 底层数据也可能悄悄变了 —— TTL 是你留给自己的止损线。
        /// 静态知识可以设几小时，业务数据建议 5~30 分钟。
        /// </summary>
        public int TtlMinutes { get; set; } = 30;

        /// <summary>
        /// embedding 模型名，需要先 ollama pull。
        /// </summary>
        public string EmbeddingModel { get; set; } = "bge-m3";
    }
}
