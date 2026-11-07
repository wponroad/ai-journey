namespace HelloLlm
{
    /// <summary>
    /// 消息角色。和 ChatRole 一一对应，但它是自己的类型 ——
    /// 业务层不应该知道 Microsoft.Extensions.AI 的存在。
    /// </summary>
    public enum LlmRole
    {
        /// <summary>开发者设定：身份、规则、边界。优先级最高。</summary>
        System,
        /// <summary>用户提问。永远不要把用户输入放进 System，那是 prompt 注入的入口。</summary>
        User,
        /// <summary>模型回答。多轮对话时要把它塞回历史。</summary>
        Assistant
    }

    /// <summary>
    /// 一条对话消息。和 LlmReply / LlmChunk 的区别：
    /// 那两个是「调用的结果」，这个是「调用的输入」。
    /// </summary>
    public sealed class LlmMessage
    {
        public LlmRole Role { get; init; }
        public string Content { get; init; } = "";
    }
}
