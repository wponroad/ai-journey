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

using (IChatClient chat = new OpenAIClient(
       new ApiKeyCredential(options.ApiKey),
       new OpenAIClientOptions { Endpoint = new Uri(options.BaseUrl) })
   .GetChatClient(options.Model)
   .AsIChatClient())
{
    ILLMService llm = new LlmService(chat, cost);

    Console.WriteLine($"配置节   {modelType}");
    Console.WriteLine($"模型     {options.Model}");
    Console.WriteLine($"地址     {options.BaseUrl}");
    if (cost is not null && pricing is not null)
    {
        bool peak = cost.IsPeakTime(DateTime.Now);
        Console.WriteLine($"计价     {(peak ? "高峰 · 原价" : $"空闲 · {pricing.Discount:0.##} 折")}");
    }
    Console.WriteLine(new string('=', 60));

    var question = "/no_think先用一句话介绍你自己，你是本地模型还是云端模型？再做一个简要的说明你能帮助我做什么事情。";

    // ───────── 1. 非流式 ─────────
    Console.WriteLine("【非流式】");
    Console.WriteLine($"你 > {question}");

    var reply = await llm.AskAsync(question);
    PrintReply(reply);
    PrintCostLine(reply.Amount);

    // ───────── 2. 流式 ─────────
    Console.WriteLine("【流式】");
    Console.WriteLine($"你 > {question}");
    Console.Write("AI > ");

    long? ttft = null;
    long? firstAnswer = null;
    long total = 0;
    long? inTok = null;
    long? outTok = null;
    decimal? streamAmount = null;

    await foreach (LlmChunk chunk in llm.AskStreamAsync(question))
    {
        if (chunk.IsFinal)
        {
            total = chunk.ElapsedMs;
            inTok = chunk.InputTokens;
            outTok = chunk.OutputTokens;
            streamAmount = chunk.Amount;
            break;
        }

        ttft ??= chunk.ElapsedMs;
        if (!chunk.IsReasoning)
            firstAnswer ??= chunk.ElapsedMs;

        if (chunk.IsReasoning)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(chunk.Text);
            Console.ResetColor();
        }
        else
        {
            Console.Write(chunk.Text);
        }
    }

    Console.WriteLine();
    Console.WriteLine(new string('=', 60));
    Console.WriteLine($"TTFT 第一片到达     {ttft} ms");
    Console.WriteLine($"正文首字            {firstAnswer} ms");
    Console.WriteLine($"总耗时              {total} ms");
    Console.WriteLine($"token               输入 {inTok} / 输出 {outTok}");
    PrintCostLine(streamAmount);
}

// ───────── 输出辅助（顶层语句里的局部函数） ─────────

void PrintReply(LlmReply r)
{
    Console.WriteLine($"AI > {r.Text}");
    Console.WriteLine($"耗时 {r.ElapsedMs} ms | token 输入 {r.InputTokens} / 输出 {r.OutputTokens}");
    Console.WriteLine($"provider {r.ModelType} | 模型 {r.ModeId}");
}

void PrintCostLine(decimal? amount)
{
    if (amount is null)
    {
        Console.WriteLine("金额     未计算（没拿到用量统计，或者这个配置没配价格）");
        return;
    }

    // 金额小到小数点后三四位才看得见，用固定 6 位小数，再折算成「分」
    string money = $"{amount.Value:F6} 元（{amount.Value * 100:F4} 分）";
    Console.WriteLine(isLocal ? $"金额     本地实际 0 元 | 等价云端 {money}" : $"金额     {money}");
    Console.WriteLine(new string('=', 60));
}
