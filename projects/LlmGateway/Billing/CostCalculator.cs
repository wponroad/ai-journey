using LlmGateway.Configuration;

namespace LlmGateway.Billing;

/// <summary>
/// 按 token 数算钱。
///
/// 单价来自每个 provider 自己的配置（元 / 百万 token）；
/// 工作日的高峰时段按原价，其余时段乘以折扣系数。
///
/// 和 CLI 版的差别：价格不再是一个全局配置节，而是**跟着 provider 走** ——
/// 网关里 DeepSeek 和本地 Ollama 同时在用，各算各的。
/// </summary>
public sealed class CostCalculator
{
    private readonly ProviderOptions _pricing;

    public CostCalculator(ProviderOptions pricing) => _pricing = pricing;

    /// <summary>
    /// 这个 provider 有没有配价格。
    /// 全 0 表示不计费（本地模型），此时调用方应该返回 null 而不是 0 ——
    /// 「花了 0 元」和「不知道花多少」是两件事。
    /// </summary>
    public bool HasPricing => _pricing.InputPrice > 0 || _pricing.OutputPrice > 0;

    /// <summary>
    /// 判断某个时刻是不是高峰（不打折）。
    /// 只有「星期命中」且「时间落在某个区间内」才算高峰。
    /// </summary>
    public bool IsPeakTime(DateTime at)
    {
        // DayOfWeek: 周日 = 0，周一 = 1 ... 周六 = 6
        if (!_pricing.UnDiscountWeek.Contains((int)at.DayOfWeek))
            return false;

        var now = at.TimeOfDay;
        foreach (var range in _pricing.UnDiscountTime)
        {
            if (!TimeSpan.TryParse(range.StartTime, out var start)) continue;
            if (!TimeSpan.TryParse(range.EndTime, out var end)) continue;

            // 左闭右开：[9:00, 12:00)
            if (now >= start && now < end) return true;
        }
        return false;
    }

    /// <summary>
    /// 算这次调用的金额（元）。
    /// 没配价格时返回 null —— 让「不计费」和「花了 0 元」在数据上可区分。
    /// </summary>
    public decimal? Calculate(long inputTokens, long outputTokens,
                              long cachedInputTokens = 0, DateTime? at = null)
    {
        if (!HasPricing) return null;

        decimal discount = IsPeakTime(at ?? DateTime.Now) ? 1m : _pricing.Discount;

        // 缓存命中数不可能超过总输入，防一手配置或数据异常
        long cached = Math.Clamp(cachedInputTokens, 0, inputTokens);
        long uncached = inputTokens - cached;
        decimal million = 1000000m;
        // 单价是「元 / 百万 token」，所以这里除以 1000000
        return uncached / million * _pricing.InputPrice * discount
             + cached / million * _pricing.InputCachePrice * discount
             + outputTokens / million * _pricing.OutputPrice * discount;
    }
}
