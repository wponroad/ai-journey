namespace LlmGateway.Configuration;

/// <summary>
/// 记账配置。
///
/// 和 CLI 版的区别：UserId / Department 不再写在配置里 ——
/// 网关服务多个调用方，"谁花的钱"必须跟着**请求**走，不能跟着实例走。
/// 现在从请求头 X-User-Id / X-Department 取，缺省记成 anonymous。
///
/// Enabled = false 时不是「不记录」，而是**降级成文本日志**：
/// 每次调用仍然会打一条 `[usage] ...` 的结构化日志，只是不落库、不建文件。
/// </summary>
public sealed class UsageOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// SQLite 文件路径。相对路径按应用目录解析。
    ///
    /// **Enabled = false 时可以留空**——关闭记账时 UsageStore 不会碰这个值，
    /// 也不会创建任何目录或文件。只有开着记账才需要它。
    /// </summary>
    public string DbPath { get; set; } = "data/usage.db";
}
