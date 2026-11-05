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
    Console.WriteLine(new string('=', 60));
    //想测「关掉思考」的对比，把question 值改成 "/no_think 用一句话介绍你自己。"
    var question = "/no_think先用一句话介绍你自己，你是本地模型还是云端模型？再做一个简要的说明你能帮助我做什么事情。";

    Console.WriteLine($"你 > {question}");
    // ───────── 1. 非流式：全部生成完才返回 ─────────
    List<float> temperatures = new List<float> { 0f, 0.7f, 1f, 1.5f };
    List<float> topPs = new List<float> { 0.01f, 0.1f, 0.5f, 1f };
    List<int> maxTokens = new List<int> { 20, 100, 500 };
    foreach (var item in temperatures)
    {
        Console.WriteLine($"【非流式】Temperature {item}");
        LlmSettings settings = new LlmSettings
        {
            Temperature = item
        };
        var reply = await llm.AskAsync(question, settings);
        Console.WriteLine($"AI > {reply.Text}");
        Console.WriteLine($"耗时 {reply.ElapsedMs} ms | token 输入 {reply.InputTokens} / 输出 {reply.OutputTokens}");
        Console.WriteLine($"provider {reply.ModelType} | 模型 {reply.ModeId}");
        Console.WriteLine(new string('=', 60));
    }
    for (int i = 0; i < 5; i++)
    {
        foreach (var item in topPs)
        {
            Console.WriteLine($"【非流式】TopP {item}");
            LlmSettings settings = new LlmSettings
            {
                Temperature = 0.7f,
                TopP = item
            };
            var reply = await llm.AskAsync(question, settings);
            Console.WriteLine($"AI > {reply.Text}");
            Console.WriteLine($"耗时 {reply.ElapsedMs} ms | token 输入 {reply.InputTokens} / 输出 {reply.OutputTokens}");
            Console.WriteLine($"provider {reply.ModelType} | 模型 {reply.ModeId}");
            Console.WriteLine(new string('=', 60));
        }
    }
    foreach (var item in maxTokens)
    {
        Console.WriteLine($"【非流式】MaxTokens {item}");
        LlmSettings settings = new LlmSettings
        {
            MaxTokens = item
        };
        var reply = await llm.AskAsync(question, settings);
        Console.WriteLine($"AI > {reply.Text}");
        Console.WriteLine($"耗时 {reply.ElapsedMs} ms | token 输入 {reply.InputTokens} / 输出 {reply.OutputTokens}");
        Console.WriteLine($"provider {reply.ModelType} | 模型 {reply.ModeId}");
        Console.WriteLine(new string('=', 60));
    }
    // ───────── 2. 流式：来一片打一片 ─────────
    Console.WriteLine("【流式】");
    Console.WriteLine($"你 > {question}");
    Console.Write("AI > ");

    long? ttft = null;          // 第一片到达（可能还是思考内容）
    long? firstAnswer = null;   // 第一段正文到达
    long total = 0;
    long? inTok = null;
    long? outTok = null;

    await foreach (LlmChunk chunk in llm.AskStreamAsync(question))
    {
        if (chunk.IsFinal)
        {
            total = chunk.ElapsedMs;
            inTok = chunk.InputTokens;
            outTok = chunk.OutputTokens;
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
}
