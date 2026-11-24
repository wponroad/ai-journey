using System.Net;

namespace HelloLlm.Tests
{
    /// <summary>
    /// 重试与降级路径的单元测试。
    ///
    /// 全程用 FakeChatClient：不连 Ollama、不连 Redis，毫秒级、零成本、结果确定。
    ///
    /// 为什么这些路径用真模型测不了：
    /// 你没法让真实服务「第 2 次调用精确失败」。拔网线是全失败，等它偶尔 429 是碰运气。
    /// 只有把模型换成一个按剧本走的演员，才能验证「重试了几次」这种内部事实。
    /// </summary>
    public class RetryAndFallbackTests
    {
        private static List<LlmMessage> Ask(string question) =>
            [new LlmMessage { Role = LlmRole.User, Content = question }];

        /// <summary>
        /// cost / usageStore / cache 全传 null —— 这个测试只关心重试和降级。
        /// retryDelay 传 10 毫秒：生产环境是 1 秒，但测试里没必要等真实退避。
        /// 这是「时间参数可注入」的意义 —— 否则三个测试光等退避就要 10 秒。
        /// </summary>
        private static LlmService Build(FakeChatClient fake) =>
            new(fake, cost: null, usageStore: null, userId: "test", department: "test", cache: null,
                retryDelay: TimeSpan.FromMilliseconds(10));

        private static Exception E429() =>
            new HttpRequestException("429 Too Many Requests", null, HttpStatusCode.TooManyRequests);

        [Fact]
        public async Task 一次成功_不降级()
        {
            var fake = new FakeChatClient([null], "订单 A20261109：已发货，128.50 元");

            var reply = await Build(fake).AskWithToolsAsync(Ask("订单 A20261109 什么状态"));

            Assert.Equal(1, fake.CallCount);          // 只调一次，没有重试
            Assert.False(reply.IsFallback);
            Assert.Contains("128.50", reply.Text);
        }

        [Fact]
        public async Task 前两次429_第三次成功()
        {
            var fake = new FakeChatClient([E429(), E429(), null], "订单 A20261109：已发货，128.50 元");

            var reply = await Build(fake).AskWithToolsAsync(Ask("订单 A20261109 什么状态"));

            // 1 次原始 + 2 次重试 = 3 次。
            // 这个数字用真 client 是断言不出来的 ——「没有发生」和「发生了几次」只有 fake 能证明。
            Assert.Equal(3, fake.CallCount);
            // 第 3 次拿到了答案 → 没有走降级。IsFallback 是「降级」的标记，不是「成功」的标记。
            Assert.False(reply.IsFallback);
        }

        [Fact]
        public async Task 一直429_走降级而不是抛异常()
        {
            // 每次一个新异常实例：复用同一个实例会串 StackTrace
            var script = Enumerable.Range(0, 10).Select(_ => (Exception?)E429()).ToList();
            var fake = new FakeChatClient(script, "模型回答（不该用到）");

            var reply = await Build(fake).AskWithToolsAsync(Ask("订单 A20261109 什么状态"));

            Assert.True(reply.IsFallback);
            // 降级用正则抽到订单号，直接查了本地 OrderService，所以能拿到真实数据
            Assert.Contains("A20261109", reply.Text);

            // 不要断言 CallCount 的具体值：熔断打开后请求会被短路，
            // 到底是 4 还是 5 取决于阈值和时序，写死就变成一个脆弱的测试。
            Assert.True(fake.CallCount >= 4, $"实际调用 {fake.CallCount} 次，应该至少 4 次");
        }
    }
}
