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

// 计价永远用云端价：调云端时它就是实际成本；
// 调本地时算出来的，是「这一次如果走云端要花多少」，也就是本地省下的钱。
var pricing = config.GetSection("CloudModel").Get<ModelOptions>();
CostCalculator? cost = pricing is null ? null : new CostCalculator(pricing);
bool isLocal = modelType == "LocalModel";

// 工具的 system prompt。第三条规则是 D4 踩出来的：
// 模型拿到订单号后不会自动追查详情，必须明说。
const string toolSystemPrompt = """
    你可以调用工具查询订单、库存和价格。

    规则：
    - 需要真实数据时必须调工具，不要凭印象回答
    - 工具返回什么就说什么，不要编造数字
    - 如果工具只返回了标识（如订单号），而用户要的是详情，必须再用对应工具查一次
    """;

using (IChatClient chat = new OpenAIClient(
       new ApiKeyCredential(options.ApiKey),
       new OpenAIClientOptions { Endpoint = new Uri(options.BaseUrl) })
   .GetChatClient(options.Model)
   .AsIChatClient())
{
    ILLMService llm = new LlmService(chat, cost);
    var cli = new ChatCli(llm, toolSystemPrompt, isLocal);

    Console.WriteLine($"配置节   {modelType}");
    Console.WriteLine($"模型     {options.Model}");
    Console.WriteLine($"地址     {options.BaseUrl}");
    if (cost is not null && pricing is not null)
    {
        bool peak = cost.IsPeakTime(DateTime.Now);
        Console.WriteLine($"计价     {(peak ? "高峰 · 原价" : $"空闲 · {pricing.Discount:0.##} 折")}");
    }

    await cli.RunAsync();
}
