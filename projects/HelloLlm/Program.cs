using HelloLlm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using System.ClientModel;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .AddJsonFile("appsettings.local.json", optional: true)
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

using (IChatClient chat = new OpenAIClient(
       new ApiKeyCredential(options.ApiKey),
       new OpenAIClientOptions { Endpoint = new Uri(options.BaseUrl) })
   .GetChatClient(options.Model)
   .AsIChatClient())
{
    ILLMService llm = new LlmService(chat);

    Console.WriteLine($"配置节   {modelType}");
    Console.WriteLine($"模型     {options.Model}");
    Console.WriteLine($"地址     {options.BaseUrl}");
    Console.WriteLine(new string('-', 60));

    var question = "用一句话介绍你自己。";
    Console.WriteLine($"你 > {question}");

    var reply = await llm.AskAsync(question);

    Console.WriteLine();
    Console.WriteLine($"AI > {reply.Text}");
    Console.WriteLine(new string('-', 60));
    Console.WriteLine($"耗时 {reply.ElapsedMs} ms");
    Console.WriteLine($"InputTokens {reply.InputTokens}");
    Console.WriteLine($"OutTokens {reply.OutputTokens}");
    Console.WriteLine($"ModelType {reply.ModelType}");
    Console.WriteLine($"ModeUrl {reply.ModeUrl}");
    Console.WriteLine($"ModeId {reply.ModeId}");

    if (reply.InputTokens is not null)
        Console.WriteLine($"token    输入 {reply.InputTokens} / 输出 {reply.OutputTokens}");
}
