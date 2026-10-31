using System.ClientModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;

Console.OutputEncoding = Encoding.UTF8;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.Local.json", optional: true)
    .Build();


string modelType = config["ModelType"] ?? "LocalModel";

var options = config.GetSection(modelType).Get<ModelOptions>();

if (options is null || string.IsNullOrWhiteSpace(options.BaseUrl))
{
    Console.WriteLine($"找不到配置节 [{modelType}]，或者它缺少 BaseUrl。");
    Console.WriteLine("可选值：LocalModel / CloudModel，检查 appsettings.json");
    return;
}

if (string.IsNullOrWhiteSpace(options.Model))
{
    Console.WriteLine($"配置节 [{modelType}] 缺少 Model，不知道该调哪个模型。");
    return;
}

if (string.IsNullOrWhiteSpace(options.ApiKey))
{
    Console.WriteLine($"配置节 [{modelType}] 缺少 ApiKey。");
    Console.WriteLine("云端 Key 应该写在 appsettings.local.json 里，不要写进 appsettings.json。");
    return;
}

IChatClient chat = new OpenAIClient(
        new ApiKeyCredential(options.ApiKey),
        new OpenAIClientOptions { Endpoint = new Uri(options.BaseUrl) })
    .GetChatClient(options.Model)
    .AsIChatClient();

Console.WriteLine($"配置节   {modelType}");
Console.WriteLine($"模型     {options.Model}");
Console.WriteLine($"地址     {options.BaseUrl}");
Console.WriteLine(new string('-', 60));

var prompt = "用一句话介绍你自己。";
Console.WriteLine($"你 > {prompt}");

var sw = System.Diagnostics.Stopwatch.StartNew();
var reply = await chat.GetResponseAsync(prompt);
sw.Stop();

Console.WriteLine();
Console.WriteLine($"AI > {reply.Text}");
Console.WriteLine(new string('-', 60));
Console.WriteLine($"耗时 {sw.ElapsedMilliseconds} ms");

if (reply.Usage is not null)
    Console.WriteLine($"token    输入 {reply.Usage.InputTokenCount} / 输出 {reply.Usage.OutputTokenCount}");

public sealed class ModelOptions
{
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
}
