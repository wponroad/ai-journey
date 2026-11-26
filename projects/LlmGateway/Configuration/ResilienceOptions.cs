namespace LlmGateway.Configuration;

/// <summary>
/// 韧性策略参数。
///
/// ⚠️ 这几个数字不是独立的，它们是一份**时间预算**：
///
///     单次超时 × (重试次数 + 1) + 退避总和  ≤  总超时
///
/// 默认值：25 × (1 + 1) + 1 = 51 秒 ≤ 55 秒 ✓
///
/// 改任何一个都要重算另外三个，否则会出现「配了 3 次重试但第 3 次永远跑不到」
/// 这种看起来正常、实际无效的配置。
///
/// 为什么单次给 25 秒：qwen3:8b 开思考模式单次要 20 秒左右（W1 实测 19.7s）。
/// 超时值必须大于对方的 P99，否则每次都在掐断一个本来能成功的请求。
/// </summary>
public sealed class ResilienceOptions
{
    /// <summary>一次请求的总时长上限（含所有重试）。</summary>
    public int TimeoutSeconds { get; set; } = 55;

    /// <summary>单次调用的时长上限。</summary>
    public int AttemptTimeoutSeconds { get; set; } = 25;

    /// <summary>额外重试次数。1 表示总共调用 2 次。</summary>
    public int MaxRetryAttempts { get; set; } = 1;

    /// <summary>首次重试前的等待毫秒数（后续按指数增长）。</summary>
    public int RetryDelayMs { get; set; } = 1000;

    // ── 熔断 ──
    // 熔断器是**有状态**的，所以每个 provider 一条独立的 pipeline（见 ProviderRegistry）。
    // 共用一条的话，OpenAI 挂了会把 DeepSeek 一起熔断 —— 那网关就成了单点。

    /// <summary>失败率阈值，超过就打开。</summary>
    public double CircuitFailureRatio { get; set; } = 0.5;

    /// <summary>统计窗口内至少要有这么多次调用才判定，太少不动手。</summary>
    public int CircuitMinimumThroughput { get; set; } = 4;

    /// <summary>
    /// 统计窗口长度。**必须大于一次请求的最坏耗时**（默认 55 秒），
    /// 否则窗口内的样本会被滑出去，永远凑不够吞吐门槛，熔断器永远不打开。
    /// </summary>
    public int CircuitSamplingSeconds { get; set; } = 120;

    /// <summary>打开后多久转半开（放一个请求去试探）。</summary>
    public int CircuitBreakSeconds { get; set; } = 30;
}
