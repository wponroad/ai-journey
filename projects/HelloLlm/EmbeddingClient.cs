using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace HelloLlm
{
    /// <summary>
    /// embedding 客户端：把一句话变成 1024 维向量。
    ///
    /// 为什么不用 MEAI 的 IEmbeddingGenerator：Ollama 的 /api/embed 一次 HTTP 就够，
    /// 多引一个包、多一层抽象，今天反而看不清请求和响应的真实形状。
    ///
    /// 注意走的不是 OpenAI 兼容层 —— 兼容层是 /v1/embeddings，
    /// 原生接口是 /api/embed，两者返回结构不同（这个返回里还带 token 数）。
    /// </summary>
    public sealed class EmbeddingClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _model;

        /// <summary>上一次调用消耗的输入 token 数。embedding 也要钱，虽然很便宜。</summary>
        public long LastPromptTokens { get; private set; }

        /// <param name="baseUrl">形如 http://127.0.0.1:11434/v1，末尾的 /v1 会被去掉</param>
        public EmbeddingClient(string baseUrl, string model = "bge-m3")
        {
            // 用 Uri 取 scheme + authority，而不是字符串 Replace("/v1","") ——
            // 后者会把路径里任何位置的 /v1 都干掉，比如 /v1/v1/xxx
            var uri = new Uri(baseUrl);
            var root = $"{uri.Scheme}://{uri.Authority}";

            _http = new HttpClient
            {
                BaseAddress = new Uri(root),
                Timeout = TimeSpan.FromSeconds(30)
            };
            _model = model;
        }

        public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
        {
            var payload = new EmbedRequest { Model = _model, Input = text };

            using var resp = await _http.PostAsJsonAsync("/api/embed", payload, ct);
            resp.EnsureSuccessStatusCode();

            var doc = await resp.Content.ReadFromJsonAsync<EmbedResponse>(cancellationToken: ct)
                      ?? throw new InvalidOperationException("embedding 返回了空 body");

            LastPromptTokens = doc.PromptEvalCount;

            return doc.Embeddings?.FirstOrDefault()
                   ?? throw new InvalidOperationException($"embedding 返回为空：{text}");
        }

        /// <summary>
        /// 余弦相似度。范围 -1 ~ 1，对文本向量通常落在 0.4 ~ 1.0。
        ///
        /// 实测基线（bge-m3）：任意两句中文大约 0.5，
        /// 同义改写 0.95，而「只差一个订单号」能到 0.99 —— 这就是语义缓存最大的坑。
        /// </summary>
        public static double Cosine(float[] a, float[] b)
        {
            if (a.Length != b.Length)
                throw new ArgumentException($"维度不一致：{a.Length} vs {b.Length}");

            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }

            if (na == 0 || nb == 0) return 0;
            return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }

        /// <summary>float[] ↔ byte[]：Redis 里存二进制比存 JSON 小 3 倍（4KB vs 12KB/条）</summary>
        public static byte[] ToBytes(float[] vec)
        {
            var bytes = new byte[vec.Length * sizeof(float)];
            Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
            return bytes;
        }

        public static float[] FromBytes(byte[] bytes)
        {
            var vec = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, vec, 0, bytes.Length);
            return vec;
        }

        public void Dispose() => _http.Dispose();

        private sealed class EmbedRequest
        {
            [JsonPropertyName("model")] public string Model { get; set; } = "";
            [JsonPropertyName("input")] public string Input { get; set; } = "";
        }

        private sealed class EmbedResponse
        {
            [JsonPropertyName("embeddings")] public List<float[]>? Embeddings { get; set; }
            [JsonPropertyName("prompt_eval_count")] public long PromptEvalCount { get; set; }
        }
    }
}
