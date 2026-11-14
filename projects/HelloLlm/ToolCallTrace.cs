namespace HelloLlm
{
    /// <summary>
    /// 一次工具调用的记录：调了谁、参数是什么。
    /// 用自己的类型而不是 FunctionCallContent，是为了不让 SDK 类型漏到应用层。
    /// </summary>
    public sealed record ToolCallTrace(string Name, string Arguments);
}
