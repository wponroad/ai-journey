namespace HelloLlm
{
    public sealed class ModelOptions
    {
        /// <summary>
        /// 模型名称，例如 qwen3:8b。
        /// </summary>
        public string Model { get; set; } = "";

        /// <summary>
        /// 接口地址，例如 http://127.0.0.1:11434/v1。
        /// </summary>
        public string BaseUrl { get; set; } = "";

        /// <summary>
        /// 接口密钥，云端 Key 建议放在 appsettings.local.json。
        /// </summary>
        public string ApiKey { get; set; } = "";

        /// <summary>
        /// 输入 token 单价（元 / 百万 token）。
        /// </summary>
        public decimal InputPrice { get; set; } = 0;

        /// <summary>
        /// 命中缓存的输入 token 单价（元 / 百万 token）。
        /// </summary>
        public decimal InputCachePrice { get; set; } = 0;

        /// <summary>
        /// 输出 token 单价（元 / 百万 token）。
        /// </summary>
        public decimal OutputPrice { get; set; } = 0;

        /// <summary>
        /// 折扣系数，例如 0.5 表示五折；不打折时为 1。
        /// </summary>
        public decimal Discount { get; set; } = 1m;

        /// <summary>
        /// 不打折的星期，1 = 周一 ... 0 = 周日。
        /// </summary>
        public int[] UnDiscountWeek { get; set; } = [];

        /// <summary>
        /// 不打折的时间段，对应配置里的 UnDiscountTime 数组。
        /// </summary>
        public List<TimeRange> UnDiscountTime { get; set; } = [];
    }

    /// <summary>
    /// 不打折的时间段，时间格式 HH:mm，例如 "9:00"。
    /// </summary>
    public sealed class TimeRange
    {
        /// <summary>
        /// 开始时间，例如 "9:00"。
        /// </summary>
        public string StartTime { get; set; } = "";

        /// <summary>
        /// 结束时间，例如 "12:00"。
        /// </summary>
        public string EndTime { get; set; } = "";
    }
}
