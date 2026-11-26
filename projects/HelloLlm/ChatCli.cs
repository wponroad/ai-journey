namespace HelloLlm
{
    /// <summary>
    /// 控制台应用：菜单选择模式，两种模式共用同一个 AskOnce，
    /// 保证"手动测一次"和"批量跑 10 次"走的是完全相同的路径。
    /// </summary>
    public sealed class ChatCli
    {
        private readonly ILLMService _llm;
        private readonly string _systemPrompt;
        private readonly bool _isLocal;
        private readonly LlmSettings _settings = new() { OutputFormat = LlmOutputFormat.Text };

        /// <param name="isLocal">本地调用不产生费用，显示成本时标注「等价云端」避免误读</param>
        public ChatCli(ILLMService llm, string systemPrompt, bool isLocal = false)
        {
            _llm = llm;
            _systemPrompt = systemPrompt;
            _isLocal = isLocal;
        }

        public async Task RunAsync()
        {
            while (true)
            {
                Console.WriteLine();
                Console.WriteLine("============================================================");
                Console.WriteLine("请选择模式：");
                Console.WriteLine("  1  对话模式（一句一句问）");
                Console.WriteLine("  2  批量测试（10 个用例，自动判成败）");
                Console.WriteLine("  0  退出");
                Console.Write("输入 1 / 2 / 0： ");

                var choice = Console.ReadLine()?.Trim();

                if (string.IsNullOrEmpty(choice) || choice == "0") return;

                switch (choice)
                {
                    case "1": await RunInteractiveAsync(); break;
                    case "2": await RunBatchAsync(); break;
                    default: Console.WriteLine("无效输入。"); break;
                }
            }
        }

        // ───────── 模式一：交互 ─────────

        private async Task RunInteractiveAsync()
        {
            Console.WriteLine();
            Console.WriteLine("对话模式（输入 exit 返回菜单）");

            while (true)
            {
                Console.Write("\n你 > ");
                var input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input) || input.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                    break;

                var record = await AskOnce("chat", input.Trim());
                PrintRecord(record);

                if (record.Error is not null)
                    Console.WriteLine($"  ⚠️ 出错：{record.Error}");
            }
        }

        // ───────── 模式二：批量 ─────────

        private async Task RunBatchAsync()
        {
            Console.WriteLine();
            Console.WriteLine($"批量测试：{TestCases.All.Length} 个用例");
            var records = new List<RunRecord>();

            foreach (var c in TestCases.All)
            {
                Console.WriteLine();
                Console.WriteLine(new string('-', 60));
                Console.WriteLine($"[{c.Id}] {c.Question}");
                Console.WriteLine($"     考察点：{c.Note}");

                var record = await AskOnce(c.Id, c.Question);
                Judge(c, record);
                records.Add(record);

                Console.WriteLine($"  AI > {record.Answer}");
                Console.WriteLine(record.Ok ? "  ✅ 通过" : $"  ❌ 失败：{record.FailReason}");
            }

            PrintSummary(records);
        }

        // ───────── 两种模式共用 ─────────

        private async Task<RunRecord> AskOnce(string id, string question)
        {
            var record = new RunRecord { Id = id, Question = question };

            var messages = new List<LlmMessage>
            {
                new() { Role = LlmRole.System, Content = _systemPrompt },
                // /no_think 是为了跑得快；换成复杂任务时应该去掉，让模型先想
                new() { Role = LlmRole.User, Content = $"/no_think {question}" }
            };

            try
            {
                var reply = await _llm.AskWithToolsAsync(
                    messages,
                    _settings,
                    maxSteps: 5,
                    trace: line =>
                    {
                        record.Steps++;
                        Console.WriteLine($"  {line}");
                    },
                    onCalls: calls => record.ToolCalls.AddRange(calls));

                record.Answer = reply.Text;
                record.InputTokens = reply.InputTokens;
                record.OutputTokens = reply.OutputTokens;
                record.Cost = reply.Amount;
                record.ElapsedMs = reply.ElapsedMs;
                record.IsFallback = reply.IsFallback;
            }
            catch (Exception ex)
            {
                record.Error = ex.Message;
            }

            return record;
        }

        /// <summary>三层判据：程序没崩 → 工具调用符合预期 → 答案里有该有的内容。</summary>
        private static void Judge(TestCase c, RunRecord r)
        {
            if (r.Error is not null)
            {
                r.Ok = false;
                r.FailReason = $"程序异常：{r.Error}";
                return;
            }

            if (string.IsNullOrWhiteSpace(r.Answer))
            {
                r.Ok = false;
                r.FailReason = "回答为空";
                return;
            }

            if (c.ExpectTool && r.ToolCalls.Count == 0)
            {
                r.Ok = false;
                r.FailReason = "没有调用工具（可能在凭印象回答）";
                return;
            }

            if (!c.ExpectTool && r.ToolCalls.Count > 0)
            {
                r.Ok = false;
                r.FailReason = $"不该调工具，却调了 {r.ToolCalls.Count} 次";
                return;
            }

            if (c.ExpectContains is not null && !r.Answer.Contains(c.ExpectContains))
            {
                r.Ok = false;
                r.FailReason = $"回答里没有「{c.ExpectContains}」";
                return;
            }

            r.Ok = true;
        }

        private void PrintRecord(RunRecord r)
        {
            // 降级必须显式标出来。不标的话，用户以为模型在答，监控以为系统正常 ——
            // 降级做得越好，故障越难被发现。
            var tag = r.IsFallback ? "  [降级·规则兜底]" : "";
            Console.WriteLine($"  AI > {r.Answer}{tag}");
            var tools = r.ToolCalls.Count == 0 ? "无" : string.Join(" → ", r.ToolCalls.Select(t => t.Name));
            Console.WriteLine($"  步数 {r.Steps} | 工具 {tools}");
            var costLabel = _isLocal ? "等价云端成本" : "成本";
            Console.WriteLine($"  token 输入 {r.InputTokens} / 输出 {r.OutputTokens} | {costLabel} {r.Cost:F6} 元 | 耗时 {r.ElapsedMs} ms");
        }

        private static void PrintSummary(List<RunRecord> records)
        {
            int ok = records.Count(r => r.Ok);

            Console.WriteLine();
            Console.WriteLine(new string('=', 70));
            Console.WriteLine($"批量结果：{ok} / {records.Count} 通过");
            Console.WriteLine(new string('-', 70));

            foreach (var r in records)
            {
                long tokens = (r.InputTokens ?? 0) + (r.OutputTokens ?? 0);
                Console.WriteLine($"[{r.Id}] {(r.Ok ? "✅" : "❌")}  步数 {r.Steps}  token {tokens}  成本 {r.Cost ?? 0:F6}");
                Console.WriteLine($"     {r.Question}");

                if (!r.Ok)
                    Console.WriteLine($"     原因：{r.FailReason}");
            }

            Console.WriteLine(new string('-', 70));
            Console.WriteLine($"总成本 {records.Sum(r => r.Cost ?? 0):F6} 元");
            Console.WriteLine(new string('=', 70));
        }
    }
}
