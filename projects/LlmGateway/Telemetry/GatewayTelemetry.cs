using System.Diagnostics;

namespace LlmGateway.Telemetry;

/// <summary>
/// 全局唯一的 ActivitySource。
///
/// 名字必须和 Program.cs 里 AddSource("LlmGateway") 完全一致，
/// 差一个字符就一条都收不到 —— 而且不报错，只是空。
/// </summary>
public static class GatewayTelemetry
{
    public const string SourceName = "LlmGateway";

    public static readonly ActivitySource Source = new(SourceName, "1.0.0");

    /// <summary>
    /// span 名字是**分类**，不是标识 —— 取值必须有限。
    /// "工具调用第几次""哪个订单号"这类每次都不同的值放 tag，不能拼进名字里，
    /// 否则监控系统的索引会被撑爆。
    /// </summary>
    public static class Spans
    {
        public const string ChatRequest = "chat.request";
        public const string ProviderCall = "llm.call";
        public const string UsageWrite = "usage.write";
    }
}
