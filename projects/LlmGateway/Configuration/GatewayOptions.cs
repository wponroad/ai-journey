namespace LlmGateway.Configuration;

/// <summary>
/// 网关的根配置，对应 appsettings.json 的 "Gateway" 节。
/// </summary>
public sealed class GatewayOptions
{
    public const string SectionName = "Gateway";

    /// <summary>所有供应商。顺序不重要，路由按 model 名查表。</summary>
    public ProviderOptions[] Providers { get; set; } = [];

    /// <summary>请求里没写 model 时用哪个。留空则强制要求传 model。</summary>
    public string DefaultModel { get; set; } = "";

    public ResilienceOptions Resilience { get; set; } = new();

    public UsageOptions Usage { get; set; } = new();

    public TelemetryOptions Telemetry { get; set; } = new();
}

/// <summary>埋点配置。</summary>
public sealed class TelemetryOptions
{
    /// <summary>
    /// 打开后每个 span 往控制台打十行。只适合本地验证"链路通不通"，
    /// 真实使用应该换成 OTLP 导出到 Jaeger。
    /// </summary>
    public bool ConsoleExporter { get; set; }
}
