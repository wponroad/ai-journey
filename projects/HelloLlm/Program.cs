using HelloLlm;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using OpenAI;
using System.ClientModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    // ───────── 测试：结构化输出 ─────────
    // 目的不是让模型「说 JSON 给用户看」，而是把文本里的信息搬进一个强类型对象，
    // 再由程序决定怎么呈现。JSON 只是中间产物。
    Console.WriteLine("【结构化抽取】");
    Console.WriteLine("输入：订单号 A20261109，状态已发货，金额 128.5 元。");

    // 四段式：角色 / 字段定义 / 示例 / 禁止项
    // 示例数据故意和真实输入不同（B20260101 / 99.9 vs A20261109 / 128.5），
    // 否则模型可能把示例原样抄回来。
    const string extractSystemPrompt = """
        你是信息抽取器。从用户给的文字里提取订单信息，只输出一个 JSON 对象。

        字段定义：
        - orderNo：字符串，订单编号，只保留编号本身
        - status：字符串，订单状态
        - amount：数字类型，只保留数值，不要单位，不要引号

        示例：
        输入：订单 B20260101，已签收，共 99.9 元
        输出：{"orderNo":"B20260101","status":"已签收","amount":99.9}

        只输出 JSON，不要解释，不要 markdown 代码块。
        """;

    var extractMessages = new List<LlmMessage>
    {
        new() { Role = LlmRole.System, Content = extractSystemPrompt },
        new() { Role = LlmRole.User, Content = "从这句话里提取订单信息：订单号 A20261109，状态已发货，金额 128.5 元。" }
    };



    var jsonOptions = new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,   // orderNo / OrderNo 都认
    };

    // 跑 5 次看分布：一次成功说明不了什么
    for (int i = 1; i <= 5; i++)
    {
        var extract = await llm.AskAsync(extractMessages, new LlmSettings
        {
            OutputFormat = LlmOutputFormat.JsonSchema,
            SchemaType = typeof(ParsedOrder)
        });

        try
        {
            // 必须带 jsonOptions：默认区分大小写，模型给 orderNo、record 写 OrderNo，
            // 不忽略大小写就是一个字段都匹配不上，而且不报错。
            var order = JsonSerializer.Deserialize<ParsedOrder>(extract.Text, jsonOptions);

            // 解析成功不等于拿到数据：字段全默认值时说明「名字没对上」，
            // 这种假成功比抛异常更危险 —— 程序会拿着空对象继续跑。
            bool isEmpty = order is null
                || (string.IsNullOrEmpty(order.OrderNo)
                    && string.IsNullOrEmpty(order.Status)
                    && order.Amount == 0);

            if (isEmpty)
            {
                Console.WriteLine($"第 {i} 次 ⚠️ 解析成功但字段全空");
                Console.WriteLine($"        原始：{extract.Text}");
            }
            else
            {
                Console.WriteLine($"第 {i} 次 ✅ {order}");
            }
        }
        catch (JsonException ex)
        {
            Console.WriteLine($"第 {i} 次 ❌ 解析失败");
            Console.WriteLine($"        原始：{extract.Text}");
            Console.WriteLine($"        原因：{ex.Message}");
        }
    }

    Console.WriteLine(new string('=', 60));

    // ───────── 人设：属于产品设定，所以放在调用方，不写死在 service 里 ─────────
    Console.WriteLine("定义我的人设（直接回车用默认）：");
    var systemPrompt = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(systemPrompt))
        systemPrompt = "你是我部署在本地的 AI 助手，协助我学习 AI 知识、处理工作文件。被问到部署位置时，如实回答你在本地运行。";

    // 历史从一条 System 消息开始，之后每轮往后追加
    var history = new List<LlmMessage>
    {
        new() { Role = LlmRole.System, Content = systemPrompt.Trim() }
    };

    decimal total = 0m;
    int round = 0;

    Console.WriteLine(new string('=', 60));
    Console.WriteLine("输入 exit 退出");

    while (true)
    {
        Console.Write("\n你 > ");
        var input = Console.ReadLine();

        if (string.IsNullOrWhiteSpace(input)) continue;
        if (input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;

        history.Add(new LlmMessage { Role = LlmRole.User, Content = input.Trim() });
        round++;

        Console.Write("AI > ");

        long? ttft = null;
        long? firstAnswer = null;
        long? inTok = null;
        long? outTok = null;
        long? cachedTok = null;
        decimal? amount = null;
        long totalMs = 0;
        bool reasoningTag = false;
        var answer = new StringBuilder();

        await foreach (LlmChunk chunk in llm.AskStreamAsync(history,new LlmSettings
        {
            OutputFormat = LlmOutputFormat.Json
        }))
        {
            if (chunk.IsFinal)
            {
                totalMs = chunk.ElapsedMs;
                inTok = chunk.InputTokens;
                outTok = chunk.OutputTokens;
                cachedTok = chunk.CachedInputTokens;
                amount = chunk.Amount;
                break;
            }

            ttft ??= chunk.ElapsedMs;

            if (chunk.IsReasoning)
            {
                // 思考内容不进正文，只提示一次「它在想」。
                // 想看完整思考过程的话，把下面的 continue 换成打印 chunk.Text。
                if (!reasoningTag)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("[思考中…] ");
                    Console.ResetColor();
                    reasoningTag = true;
                }
                continue;
            }

            firstAnswer ??= chunk.ElapsedMs;
            Console.Write(chunk.Text);
            answer.Append(chunk.Text);
        }

        Console.WriteLine();

        // 模型的回答必须进历史，否则下一轮它不知道自己刚说过什么
        history.Add(new LlmMessage { Role = LlmRole.Assistant, Content = answer.ToString() });

        if (amount is not null) total += amount.Value;

        Console.WriteLine(new string('-', 60));
        Console.WriteLine($"第 {round} 轮 | 输入 {inTok}（缓存命中 {cachedTok}）/ 输出 {outTok} token | 历史 {history.Count} 条");
        Console.WriteLine($"TTFT {ttft}ms | 正文首字 {firstAnswer}ms | 总耗时 {totalMs}ms");
        Console.WriteLine($"本轮 {Money(amount)} | 累计 {Money(total)}");
    }

    Console.WriteLine();
    Console.WriteLine(new string('=', 60));
    Console.WriteLine($"共 {round} 轮 | {(isLocal ? "本地实际 0 元，等价云端 " : "总计 ")}{total:F6} 元");
}

// ───────── 输出辅助（顶层语句里的局部函数） ─────────

string Money(decimal? amount)
{
    if (amount is null) return "未计算（没拿到用量统计）";

    string money = $"{amount.Value:F6} 元（{amount.Value * 100:F4} 分）";
    return isLocal ? $"等价云端 {money}" : money;
}
