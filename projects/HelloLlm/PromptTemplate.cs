using System.Text;

namespace HelloLlm
{
    /// <summary>
    /// 一个 prompt 模板，对应 prompts/ 目录下的一个 JSON 文件。
    ///
    /// 为什么分层：四层的修改频率完全不同。
    /// role 几乎不动，constraints 天天改，format 偶尔改，examples 改一次影响很大。
    /// 混在一个大字符串里，改约束会碰到示例，而示例一动模型行为就变。
    /// </summary>
    public sealed class PromptTemplate
    {
        public string Id { get; init; } = "";
        public string Version { get; init; } = "";
        public string UpdatedAt { get; init; } = "";
        public string Note { get; init; } = "";

        public PromptLayers Layers { get; init; } = new();

        /// <summary>
        /// 组装成最终的 system prompt。
        /// 顺序：角色 → 约束 → 格式 → 示例。
        /// 示例放最后，因为它离用户消息最近，模型的模仿效果最强。
        /// </summary>
        public string Build()
        {
            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(Layers.Role))
            {
                sb.AppendLine(Layers.Role.Trim());
                sb.AppendLine();
            }

            if (Layers.Constraints.Count > 0)
            {
                sb.AppendLine("约束：");
                foreach (var c in Layers.Constraints)
                    sb.AppendLine($"- {c}");
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(Layers.Format))
            {
                sb.AppendLine(Layers.Format.Trim());
                sb.AppendLine();
            }

            if (Layers.Examples.Count > 0)
            {
                sb.AppendLine("示例：");
                foreach (var e in Layers.Examples)
                {
                    sb.AppendLine($"输入：{e.Input}");
                    sb.AppendLine($"输出：{e.Output}");
                }
            }

            return sb.ToString().Trim();
        }

        /// <summary>日志里记这个，出问题能立刻定位到具体版本。</summary>
        public override string ToString() => $"{Id}@{Version}";
    }

    public sealed class PromptLayers
    {
        /// <summary>你是谁、干什么。</summary>
        public string Role { get; init; } = "";

        /// <summary>必须做什么、不能做什么。改得最多的一层。</summary>
        public List<string> Constraints { get; init; } = [];

        /// <summary>输出长什么样：字段定义 + 形状。</summary>
        public string Format { get; init; } = "";

        /// <summary>具体案例。模型是模仿机器，一个示例胜过十行规则。</summary>
        public List<PromptExample> Examples { get; init; } = [];
    }

    public sealed class PromptExample
    {
        public string Input { get; init; } = "";
        public string Output { get; init; } = "";
    }
}
