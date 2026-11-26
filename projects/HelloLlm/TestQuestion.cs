namespace HelloLlm
{
    /// <summary>
    /// 一个测试用例：问题 + 期望。
    /// 没有期望就没法自动判失败 —— "10 次 0 失败"只能靠人眼看，看几次就记混了。
    /// </summary>
    public sealed record TestCase(
        string Id,
        string Question,
        bool ExpectTool,          // 应该有工具调用吗
        string? ExpectContains,   // 回答里应该出现什么（null = 不检查内容）
        string Note);             // 这道题考什么

    /// <summary>一次运行的完整现场。失败的记录也要留，否则没法复盘。</summary>
    public sealed class RunRecord
    {
        public string Id { get; init; } = "";
        public string Question { get; init; } = "";
        public string Answer { get; set; } = "";
        public List<ToolCallTrace> ToolCalls { get; } = [];
        public int Steps { get; set; }
        public long? InputTokens { get; set; }
        public long? OutputTokens { get; set; }
        public decimal? Cost { get; set; }
        public long ElapsedMs { get; set; }
        public string? Error { get; set; }
        public bool Ok { get; set; }
        public string FailReason { get; set; } = "";

        /// <summary>这次回答是否来自降级规则（模型挂了走正则兜底）</summary>
        public bool IsFallback { get; set; }
    }

    public static class TestCases
    {
        /// <summary>
        /// 10 个用例覆盖不同类型：单工具、多工具、串行依赖、查不到、边界、模糊指代、不需要工具。
        /// 只问同一类问题 10 遍，测不出真实失败率。
        /// </summary>
        public static readonly TestCase[] All =
        [
            new("1",  "订单 A20261109 现在什么状态",            true,  "已发货", "单工具：查订单"),
            new("2",  "SKU-001 还有多少库存",                    true,  "100",    "单工具：查库存"),
            new("3",  "SKU-001 买 5 件总共多少钱",               true,  "64.5",   "双工具：库存 + 算价"),
            new("4",  "我最近那单怎么样了",                       true,  "已完成", "串行依赖：先查号再查详情"),
            new("5",  "订单 A99999 是什么状态",                  true,  null,     "查不到，但不能崩"),
            new("6",  "SKU-002 买 3 件多少钱",                   true,  null,     "已售罄，看怎么答"),
            new("7",  "SKU-003 我要 20 件",                      true,  null,     "超库存，看怎么答"),
            new("8",  "那个 199 块钱的东西还有货吗",              true,  null,     "模糊指代，要自己推断是 SKU-003"),
            new("9",  "你能做什么",                              false, null,     "不该调工具，直接答"),
            new("10", "订单 A20261110 什么状态",                 true,  "待付款", "另一笔订单"),
        ];
    }
}
