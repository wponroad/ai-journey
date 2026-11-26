namespace LlmGateway.Chat;

/// <summary>请求里的一条消息。role 取 system / user / assistant。</summary>
public sealed class ChatMessageDto
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = "";
}

/// <summary>
/// POST /v1/chat 的请求体。
///
/// 刻意不复刻 OpenAI 的 /v1/chat/completions 协议：
/// 那套协议要处理 tool_calls、function_calling、多模态 content 数组等一整套，
/// 是几个月的活。网关 v1 只需要证明"能路由、能流式、能计费、能追踪"。
/// 这也是范围定死的一部分 —— 别在第一天就想要完整兼容。
/// </summary>
public sealed class ChatRequest
{
    /// <summary>模型名。不传则用配置里的 DefaultModel。</summary>
    public string? Model { get; set; }

    public List<ChatMessageDto> Messages { get; set; } = [];

    /// <summary>true 时走 SSE 流式返回。</summary>
    public bool Stream { get; set; }

    public float? Temperature { get; set; }

    public int? MaxOutputTokens { get; set; }
}

public sealed record UsageDto(long InputTokens, long OutputTokens, long CachedInputTokens);

/// <summary>非流式的响应体。</summary>
public sealed class ChatResponseDto
{
    /// <summary>本次请求的追踪 ID，和日志、账本里的是同一个。</summary>
    public string RequestId { get; set; } = "";

    /// <summary>实际干活的 provider 逻辑名。</summary>
    public string Provider { get; set; } = "";

    public string Model { get; set; } = "";
    public string Text { get; set; } = "";
    public UsageDto Usage { get; set; } = new(0, 0, 0);

    /// <summary>本次花费（元）。null 表示这个 provider 没配价格（比如本地模型）。</summary>
    public decimal? Cost { get; set; }

    public long ElapsedMs { get; set; }
}

/// <summary>统一的错误响应体。任何 4xx / 5xx 都长这样。</summary>
public sealed class ErrorDto
{
    public string Error { get; set; } = "";
    public string? ErrorType { get; set; }
    public string? RequestId { get; set; }
}
