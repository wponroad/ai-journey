namespace LlmGateway.Configuration;

/// <summary>
/// 一个模型供应商（provider）。
///
/// 网关存在的唯一理由就是这个类：把 OpenAI / DeepSeek / Ollama 的差异
/// 全部收敛到一张配置表里 —— 业务方只写 model 名，不关心谁在背后干活。
///
/// 三家都走 OpenAI 兼容协议，所以这里没有「协议类型」字段；
/// 将来接一家非兼容的（比如某些自研 SDK），再加一个 Protocol 字段，
/// 在 ProviderRegistry 里按它选不同的 client 工厂。
/// </summary>
public sealed class ProviderOptions
{
    /// <summary>逻辑名，日志和响应里用它标识"这次谁答的"，例如 local / deepseek。</summary>
    public string Name { get; set; } = "";

    /// <summary>OpenAI 兼容端点，注意要带 /v1，例如 http://127.0.0.1:11434/v1。</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// 密钥。**不要写在这个文件里提交** —— 用 appsettings.local.json 或环境变量，
    /// 见 appsettings.sample.json 的说明。
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>这个 provider 支持的模型名列表。路由就是按它查的。</summary>
    public string[] Models { get; set; } = [];

    // ── 计价（元 / 百万 token）。全为 0 表示"不算钱"，比如本地 Ollama。 ──
    public decimal InputPrice { get; set; }
    public decimal InputCachePrice { get; set; }
    public decimal OutputPrice { get; set; }

    /// <summary>非高峰时段的折扣系数，1 表示不打折。</summary>
    public decimal Discount { get; set; } = 1m;

    /// <summary>不打折的星期：1 = 周一 ... 0 = 周日。</summary>
    public int[] UnDiscountWeek { get; set; } = [];

    /// <summary>不打折的时间段。</summary>
    public List<TimeRange> UnDiscountTime { get; set; } = [];
}

/// <summary>不打折的时间段，格式 HH:mm。</summary>
public sealed class TimeRange
{
    public string StartTime { get; set; } = "";
    public string EndTime { get; set; } = "";
}
