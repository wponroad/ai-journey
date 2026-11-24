using Microsoft.Extensions.AI;
using System.Collections.Concurrent;

namespace HelloLlm
{
    public sealed class FakeChatClient : IChatClient
    {
        // ConcurrentQueue 而不是 Queue：并发测试里多个线程会同时出队，
        // 普通 Queue 不是线程安全的，会抛 InvalidOperationException。
        private readonly ConcurrentQueue<Exception?> _script;   // 每次调用出一个；null = 正常返回
        private readonly string _reply;

        // 计数也必须原子：20 个并发下 CallCount++ 会丢计数，
        // 明明调了 8 次却显示 5 次，你的断言就建立在错误的数据上。
        private int _callCount;
        public int CallCount => Volatile.Read(ref _callCount);

        public FakeChatClient(IEnumerable<Exception?> script, string reply)
        {
            this._script = new ConcurrentQueue<Exception?>(script);
            this._reply = reply;
        }
        public void Dispose()
        {
            // 没有非托管资源，队列清不清无所谓
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            // 剧本用完就默认成功 —— 这是约定，测试写清楚要几次失败即可
            if (_script.TryDequeue(out var ex) && ex is not null)
                return Task.FromException<ChatResponse>(ex);   // 明确："这次调用失败了"
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, _reply))
            {
                Usage = new UsageDetails { InputTokenCount = 513, OutputTokenCount = 33 }
            });
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
        {
            return null;
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
