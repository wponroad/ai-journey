namespace HelloLlm
{
    /// <summary>
    /// 按 token 数算钱。
    /// 单价来自配置（元 / 百万 token）；工作日的高峰时段按原价，
    /// 其余时段乘以折扣系数（配置里的 Discount）。
    /// </summary>
    public sealed class CostCalculator
    {
        private readonly ModelOptions _pricing;

        public CostCalculator(ModelOptions pricing)
        {
            _pricing = pricing;
        }

        /// <summary>
        /// 判断某个时刻是不是高峰（不打折）。
        /// 只有「星期命中 UnDiscountWeek」且「时间落在 UnDiscountTime 的某个区间内」才算高峰。
        /// </summary>
        public bool IsPeakTime(DateTime at)
        {
            // DayOfWeek: 周日 = 0，周一 = 1 ... 周六 = 6，和配置里的写法一致
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
        /// cachedInputTokens 是输入里命中缓存的部分，它按更便宜的 InputCachePrice 计费。
        /// 拿不到缓存数就传 0，结果是保守的高估，不会少算。
        /// </summary>
        public decimal Calculate(long inputTokens, long outputTokens,
                                 long cachedInputTokens = 0, DateTime? at = null)
        {
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
}
