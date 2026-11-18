namespace HelloLlm
{
    /// <summary>
    /// 记账配置。单独一节，不和 ModelOptions 混在一起 ——
    /// 换模型、换 provider 不影响「记到哪、记给谁」。
    /// </summary>
    public sealed class UsageOptions
    {
        /// <summary>
        /// 是否记账。false 时 LlmService 里所有记账调用都是空操作，
        /// 适合跑单元测试或者临时脚本。
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// 数据库路径。相对路径会相对程序运行目录解析（也就是 appsettings.json 所在目录）。
        ///
        /// 注意：那里是 bin 目录，dotnet clean 会连数据一起清掉。
        /// 想保留数据就写绝对路径。
        /// </summary>
        public string DbPath { get; set; } = "data/usage.db";

        /// <summary>
        /// 调用方标识。真实系统从登录态或请求上下文取，控制台先写死在配置里。
        /// </summary>
        public string UserId { get; set; } = "anonymous";

        /// <summary>部门，用于按部门汇总成本。</summary>
        public string Department { get; set; } = "unknown";
    }
}
