var builder = WebApplication.CreateBuilder(args);

// OpenAPI：只产出文档 JSON（/openapi/v1.json），本身不带界面。
// 界面由 Swashbuckle.AspNetCore.SwaggerUI 提供 —— 它只带静态页面，
// 不依赖 Microsoft.OpenApi，所以不会和内置生成器打架，也不会产出第二套文档。
builder.Services.AddOpenApi();

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

app.Run();
