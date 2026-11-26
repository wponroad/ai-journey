using System.Net;

namespace HelloLlm.Tests
{
    /// <summary>
    /// 熔断与缓存准入的测试。
    ///
    /// 熔断这块手动测不了：你没法观察"第 2 次请求有没有真的打到服务端"。
    /// 但 fake 的 CallCount 能证明 —— 数字没涨，就是没发出去。
    /// </summary>
    public class CircuitAndCacheTests
    {
        private static List<LlmMessage> Ask(string question) =>
            [new LlmMessage { Role = LlmRole.User, Content = question }];

        private static LlmService Build(FakeChatClient fake) =>
            new(fake, cost: null, usageStore: null, userId: "test", department: "test", cache: null,
                retryDelay: TimeSpan.FromMilliseconds(1));

        private static Exception E429() =>
            new HttpRequestException("429 Too Many Requests", null, HttpStatusCode.TooManyRequests);

        [Fact]
        public async Task 连续失败后熔断打开_后续请求不再调client()
        {
            var fake = new FakeChatClient(
                Enumerable.Range(0, 50).Select(_ => (Exception?)E429()),
                "不该用到");
            var llm = Build(fake);

            // 第 1 次请求：重试用完，失败率达标，熔断器打开
            await llm.AskWithToolsAsync(Ask("订单 A20261109 什么状态"));
            var afterFirst = fake.CallCount;

            // 第 2 次请求：熔断已打开 → BrokenCircuitException 直接抛出
            // 这个异常不在 Retry 的 ShouldHandle 里，所以不会重试，一次都不调 client
            await llm.AskWithToolsAsync(Ask("订单 A20261110 什么状态"));
            var afterSecond = fake.CallCount;

            Assert.True(afterFirst >= 4, $"第一次请求应该至少调 4 次，实际 {afterFirst}");
            Assert.Equal(afterFirst, afterSecond);
        }

        [Theory]
        [InlineData("今天最大的一笔订单是多少", false, "相对时间")]
        [InlineData("最近那单什么状态", false, "相对时间")]
        [InlineData("最大的一笔订单", false, "聚合统计")]
        [InlineData("一共有多少笔订单", false, "聚合统计")]
        [InlineData("订单 A20261109 什么状态", true, "")]
        [InlineData("退货政策是什么", true, "")]
        public void 缓存准入判断(string question, bool expected, string reasonKeyword)
        {
            var cacheable = LlmCache.IsCacheable(question, out var reason);

            Assert.Equal(expected, cacheable);

            if (!expected)
                Assert.Contains(reasonKeyword, reason);
        }

        [Fact]
        public async Task 并发20个请求_熔断挡住大部分()
        {
            var fake = new FakeChatClient(
                Enumerable.Range(0, 500).Select(_ => (Exception?)E429()),
                "不该用到");
            var llm = Build(fake);

            // 20 个请求同时打进来，全部遇到 429
            // 没有熔断的话：20 × 5 次 = 100 次真实调用 —— 这就是"雪崩"
            var tasks = Enumerable.Range(0, 20)
                .Select(i => llm.AskWithToolsAsync(Ask($"订单 A202611{i:00} 什么状态")))
                .ToList();

            await Task.WhenAll(tasks);

            // 无熔断：20 请求 × 5 次尝试 = 100 次真实调用 —— 这就是"雪崩"
            // 有熔断：前 4 次失败达到阈值，熔断打开，剩下 16 个请求全部短路
            //         实测 4 次，省掉 96%
            Assert.InRange(fake.CallCount, 4, 10);
        }
    }
}
