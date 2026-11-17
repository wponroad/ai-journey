using Microsoft.Extensions.AI;
using System.Net;

namespace HelloLlm
{
    /// <summary>
    /// 会故障的假 client：包在真 client 外面，前 N 次调用故意失败。
    ///
    /// 位置和 CostCalculator、UseFunctionInvocation 完全一样 —— 都是「装饰器」：
    /// 实现同一个接口，拿着内部那个真 client，在转发前后插入自己的行为。
    ///
    /// 为什么需要它：测重试不能靠运气去等 429。故障必须可控，
    /// 否则「重试 3 次后成功」和「重试用完抛异常」这两条路径永远验证不了。
    /// </summary>
    public sealed class FlakyChatClient : IChatClient
    {
        private readonly IChatClient _inner;
        private readonly int _failTimes;

        /// <summary>造故障的工厂，参数是「第几次调用」。用工厂而不是存一个异常实例，
        /// 是因为异常实例复用会串 StackTrace —— 三次失败的调用栈长得一模一样，就分不清是第几次了。</summary>
        private readonly Func<int, Exception> _makeFault;

        private int _calls;

        public FlakyChatClient(IChatClient inner, int failTimes, Func<int, Exception> makeFault)
        {
            _inner = inner;
            _failTimes = failTimes;
            _makeFault = makeFault;
        }

        /// <summary>已经进来的请求次数（含失败的那些）。重试时这个数会把每一次都算进去。</summary>
        public int Calls => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
                                                   ChatOptions? options = null,
                                                   CancellationToken cancellationToken = default)
        {
            // Interlocked 而不是 ++：重试可能并发，虽然今天是单线程，
            // 但装饰器写错了以后被人拿去并发用，这种 bug 查不出来。
            int n = Interlocked.Increment(ref _calls);

            if (n <= _failTimes)
            {
                var ex = _makeFault(n);
                Console.WriteLine($"[flaky] 第 {n} 次调用 → 抛 {ex.GetType().Name}: {ex.Message}");

                // 返回 faulted Task，而不是 throw。
                // 非 async 方法里直接 throw 是「同步抛出」，调用方如果先建 Task 再 await，
                // 两条失败路径的行为会不一致。Task.FromException 让它始终走异步那条。
                return Task.FromException<ChatResponse>(ex);
            }

            Console.WriteLine($"[flaky] 第 {n} 次调用 → 放行");
            return _inner.GetResponseAsync(messages, options, cancellationToken);
        }

        /// <summary>流式不模拟故障。今天只测非流式 —— 流式重试是另一个问题：
        /// 已经吐了 500 个字断线，你重试就是从头发一遍，用户会看到内容跳回开头。</summary>
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => _inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        /// <summary>
        /// 必须转发，否则 LlmService 里 `_rawClient.GetService&lt;ChatClientMetadata&gt;()`
        /// 会返回 null，模型名和 provider 全变空 —— 而且不报错。
        /// </summary>
        public object? GetService(Type serviceType, object? serviceKey = null)
            => _inner.GetService(serviceType, serviceKey);

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>三种故障的工厂，配合 FlakyChatClient 用。</summary>
    public static class Faults
    {
        /// <summary>429：服务端说「你太快了」。最典型的瞬时故障，最该重试。</summary>
        public static Exception RateLimited(int n)
            => new HttpRequestException($"429 Too Many Requests（第 {n} 次）", null, HttpStatusCode.TooManyRequests);

        /// <summary>500：服务端自己出错了，等一会儿大概率能好。</summary>
        public static Exception ServerError(int n)
            => new HttpRequestException($"500 Internal Server Error（第 {n} 次）", null, HttpStatusCode.InternalServerError);

        /// <summary>超时：连不上或者太慢。对 LLM 来说这个最贵 —— 钱可能已经花了。</summary>
        public static Exception Timeout(int n)
            => new TaskCanceledException($"请求超时（第 {n} 次，模拟）");
    }
}
