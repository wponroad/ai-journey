using System.Text.Json;

namespace HelloLlm
{
    /// <summary>
    /// 模板库：启动时把 prompts/ 目录下所有 JSON 读进内存，按 id 取用。
    ///
    /// 不做热加载 —— 改 JSON 要重启程序完全可以接受。
    /// 等你真的需要频繁调到"不能重启"的程度再考虑，那是过度设计。
    /// </summary>
    public sealed class PromptLibrary
    {
        private readonly Dictionary<string, PromptTemplate> _map;

        private PromptLibrary(Dictionary<string, PromptTemplate> map) => _map = map;

        public static PromptLibrary Load(string dir)
        {
            if (!Directory.Exists(dir))
                throw new DirectoryNotFoundException(
                    $"prompt 目录不存在：{dir}\n" +
                    $"检查 csproj 里有没有把 prompts/ 复制到输出目录。");

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };

            var map = new Dictionary<string, PromptTemplate>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                var json = File.ReadAllText(file);
                var tpl = JsonSerializer.Deserialize<PromptTemplate>(json, options)
                          ?? throw new InvalidOperationException($"模板解析结果为空：{file}");

                if (string.IsNullOrWhiteSpace(tpl.Id))
                    throw new InvalidOperationException($"模板缺少 id：{file}");

                // 同名模板直接报错，不搞"后加载覆盖先加载"——
                // 那种行为在文件多了以后会变成最难查的问题。
                if (map.ContainsKey(tpl.Id))
                    throw new InvalidOperationException($"模板 id 重复：{tpl.Id}（{file}）");

                map[tpl.Id] = tpl;
            }

            return new PromptLibrary(map);
        }

        public PromptTemplate Get(string id)
            => _map.TryGetValue(id, out var tpl)
                ? tpl
                : throw new KeyNotFoundException($"找不到 prompt 模板 [{id}]，已加载：{string.Join("、", _map.Keys)}");

        public IReadOnlyCollection<PromptTemplate> All => _map.Values;
    }
}
