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

IChatClient realChat = new OpenAIClient(
        new ApiKeyCredential(options.ApiKey),
        new OpenAIClientOptions { Endpoint = new Uri(options.BaseUrl) })
    .GetChatClient(options.Model)
    .AsIChatClient();

// 故障注入开关：failTimes 是「前几次调用故意失败」，0 = 不注入。
// 平时保持 0，测重试/熔断时改成 99（配合 Faults.RateLimited / ServerError / Timeout）。
// 位置必须在 Polly 的内层 —— flaky 包在 Polly 外面的话，它一抛异常 Polly 根本没被执行到。
var failTimes = 0;
using (IChatClient chat = failTimes > 0
           ? new FlakyChatClient(realChat, failTimes, Faults.RateLimited)
           : realChat)
{
    // 记账：配置决定开关、路径、归属人。服务自己不知道也不关心「记到哪」。
    var usage = config.GetSection("Usage").Get<UsageOptions>() ?? new UsageOptions();

    var dbPath = Path.IsPathRooted(usage.DbPath)
        ? usage.DbPath
        : Path.Combine(AppContext.BaseDirectory, usage.DbPath);

    var store = usage.Enabled ? new UsageStore(dbPath) : null;

    // 缓存：Redis 连不上就降级为「不缓存」，不让整个程序起不来。
    var cacheOptions = config.GetSection("Cache").Get<CacheOptions>() ?? new CacheOptions();
    LlmCache? cache = null;

    if (cacheOptions.Enabled)
    {
        try
        {
            var embedClient = new EmbeddingClient(options.BaseUrl, cacheOptions.EmbeddingModel);
            cache = new LlmCache(cacheOptions.Redis, embedClient, cacheOptions.Threshold, cacheOptions.TtlMinutes);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Redis缓存失败，本次不缓存：{ex.Message}");
        }
    }

    ILLMService llm = new LlmService(chat, cost, store, usage.UserId, usage.Department, cache);
    var cli = new ChatCli(llm, toolSystemPrompt, isLocal);

    Console.WriteLine($"配置节   {modelType}");
    Console.WriteLine($"模型     {options.Model}");
    Console.WriteLine($"地址     {options.BaseUrl}");
    Console.WriteLine($"缓存     {(cache is null ? "未启用" : $"已启用（{cacheOptions.Redis}，阈值 {cacheOptions.Threshold}）")}");
    if (cost is not null && pricing is not null)
    {
        bool peak = cost.IsPeakTime(DateTime.Now);
        Console.WriteLine($"计价     {(peak ? "高峰 · 原价" : $"空闲 · {pricing.Discount:0.##} 折")}");
    }

    await cli.RunAsync();

    // 退出时看看账本。真实系统里这些是报表，这里先打屏幕上。
    if (store is null)
    {
        Console.WriteLine("\n（记账未开启：Usage.Enabled = false）");
        return;
    }

    Console.WriteLine();
    Console.WriteLine("──────── 记账汇总（按部门）────────");
    foreach (var line in store.SummarizeByDepartment())
        Console.WriteLine($"  {line}");

    Console.WriteLine();
    Console.WriteLine("──────── 最近 10 条 ────────");
    foreach (var line in store.RecentRows(10))
        Console.WriteLine($"  {line}");

    if (cache is not null)
    {
        Console.WriteLine();
        Console.WriteLine("──────── 缓存统计 ────────");
        Console.WriteLine($"  {cache.Stats()}");
    }
}
