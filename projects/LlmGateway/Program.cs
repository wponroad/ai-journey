using LlmGateway.Billing;
using LlmGateway.Chat;
using LlmGateway.Configuration;
using LlmGateway.Providers;
using LlmGateway.Telemetry;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// OpenAPI：只产出文档 JSON（/openapi/v1.json），本身不带界面。
// 界面由 Swashbuckle.AspNetCore.SwaggerUI 提供 —— 它只带静态页面，
// 不依赖 Microsoft.OpenApi，所以不会和内置生成器打架，也不会产出第二套文档。
builder.Services.AddOpenApi();

// MVC：控制器的发现靠「继承 ControllerBase + 类名以 Controller 结尾 + [ApiController]」
// 三条约定，AddControllers 时扫描程序集一次性记下来。
// 这和 Minimal API 的 app.MapPost(...) 手动注册是两条完全不同的路 ——
// 前者漏写不会出问题，后者漏写就是 404 且编译不报错。
builder.Services.AddControllers()
    .AddJsonOptions(o =>
    {
        // 中文不转成 \uXXXX，否则 curl 看到的是天书。
        // MVC 的序列化走这里配置，和流式 SSE 里那个 JsonOpts 是两处。
        o.JsonSerializerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
    });

var gatewayOptions = builder.Configuration
    .GetSection(GatewayOptions.SectionName)
    .Get<GatewayOptions>() ?? new GatewayOptions();

builder.Services.AddSingleton(gatewayOptions);

// ProviderRegistry 必须是单例。
// 它缓存着 IChatClient（内部持有 HttpClient）和熔断器状态 ——
// 每次请求 new 一个的话，熔断器永远从 0 开始计数、永远不会打开，
// 而且 HttpClient 会被耗尽。.NET 服务端最经典的两个坑，一起来了。
builder.Services.AddSingleton<ProviderRegistry>();

// 记账：**总是注册**。Enabled 是一个开关，不是「这个依赖存不存在」。
// 关掉时 UsageStore 内部不建目录、不建库、不建表，所有方法都是空操作。
// 这样 ChatController 可以正常构造函数注入，不用 ServiceProvider.GetService；
// 调用方也不用到处写 store?. 判空 —— 这是 Null Object 模式，
// 比「依赖可能为 null」干净得多。
builder.Services.AddSingleton(sp => new UsageStore(
    gatewayOptions.Usage.DbPath,
    gatewayOptions.Usage.Enabled,
    sp.GetRequiredService<ILogger<UsageStore>>()));

if (gatewayOptions.Telemetry.ConsoleExporter)
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(r => r.AddService("LlmGateway"))
        .WithTracing(t => t
            // 名字必须和 GatewayTelemetry.SourceName 完全一致，
            // 差一个字符就一条都收不到，而且不报错。
            .AddSource(GatewayTelemetry.SourceName)
            .AddConsoleExporter());
}

var app = builder.Build();

// 文档一直开着：这是示例项目，没有敏感信息，陌生人 clone 下来就能看。
// 真要上线时再包环境判断，或者改成配置项控制。
app.MapOpenApi();

// 根路径直接跳到文档页。不加这行的话，/ 是 404 —— 陌生人打开首页看到空白会以为项目坏了。
// ExcludeFromDescription：这只是一个跳转，不是业务接口，没必要出现在 OpenAPI 文档里。
app.MapGet("/", () => Results.Redirect("/swagger")).ExcludeFromDescription();

// Swagger UI：访问地址 /swagger
// SwaggerEndpoint 必须指向 MapOpenApi 产出的那个 JSON，
// 指错了页面会是空的，而且不报错 —— 只显示 "No API definition provided"。
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/openapi/v1.json", "LlmGateway v1");
    options.RoutePrefix = "swagger";
});

// 把 AddControllers 扫到的控制器映射成路由。
// 少了这一行，所有 Controller 都不对外 —— 同样是静默 404。
app.MapControllers();

// 启动时把"能干什么"打在屏幕上。
// 陌生人跑起来第一眼看到的就是这里，比 README 更早被看到。
var registry = app.Services.GetRequiredService<ProviderRegistry>();
var usageStore = app.Services.GetRequiredService<UsageStore>();
var models = string.Join(", ", registry.AvailableModels);

// 在 ApplicationStarted 里打印，而不是 app.Run() 之前 ——
// 端口是 Run 的时候才真正绑定的，提前打只能写死一个值，
// 用 --urls 换端口就会显示错的地址。
app.Lifetime.ApplicationStarted.Register(() =>
{
    // 从 Kestrel 拿实际监听地址，而不是猜
    var url = app.Services.GetRequiredService<IServer>()
                  .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
              ?? "http://localhost:5083";

    Console.WriteLine($"""

        ╭─ LlmGateway ──────────────────────────────────────────
        │  文档   {url}/swagger
        │  接口   POST {url}/v1/chat
        │  模型   {(string.IsNullOrEmpty(models) ? "（未配置，检查 Gateway:Providers）" : models)}
        │  记账   {usageStore.DisplayPath ?? "已关闭"}
        ╰───────────────────────────────────────────────────────
        """);
});

app.Run();
