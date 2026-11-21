using StackExchange.Redis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HelloLlm
{
    /// <summary>
    /// 两级缓存：精确缓存 + 语义缓存。
    ///
    /// 精确：问题一字不差才命中。命中率低，但绝不出错。
    /// 语义：意思相近就命中。命中率高，但会答错题 —— 所以必须配实体守卫。
    ///
    /// 实测数据（bge-m3，1024 维）：
    ///   同义改写       0.9543   ← 想命中
    ///   只差一个数字   0.9892   ← 绝不能命中，分数反而更高
    ///   任意两句中文   0.4976   ← 基线，不是 0
    ///
    /// 因为「想命中的」和「怕命中的」分数是反的，任何单一阈值都切不开。
    /// 所以真正的判据是：先比实体，再比相似度。
    /// </summary>
    public sealed class LlmCache : IDisposable
    {
        private const string ExactPrefix = "llm:exact:";
        private const string SemPrefix = "llm:sem:";
        private const string SemIndex = "llm:sem:ids";

        private readonly IConnectionMultiplexer _redis;
        private readonly IDatabase _db;
        private readonly EmbeddingClient _embed;
        private readonly double _threshold;
        private readonly TimeSpan _ttl;

        private int _exactHit, _semanticHit, _miss, _skipped;

        public LlmCache(string redisConnection, EmbeddingClient embed, double threshold = 0.93, int ttlMinutes = 5)
        {
            // ConnectionMultiplexer 必须是单例 —— 它内部维护连接池和订阅，
            // 每次 new 一个会瞬间打爆服务端连接数。这是 Redis 客户端最经典的使用错误。
            _redis = ConnectionMultiplexer.Connect(redisConnection);
            _db = _redis.GetDatabase();
            _embed = embed;
            _threshold = threshold;
            _ttl = TimeSpan.FromMinutes(ttlMinutes);
        }

        public sealed record CacheHit(string Answer, string Kind, double Score, string MatchedQuestion);

        /// <summary>
        /// 这个问题能不能进缓存。
        ///
        /// 缓存的前提是「同样的输入 → 同样的输出」。但 LLM 的真实输入不只是问题，
        /// 还有 <b>时间、数据状态、用户身份</b>。只看问题就会缓存出错误答案：
        ///
        ///   昨天问「今天最大的一笔订单是多少」 → 订单 X
        ///   今天问同一句话                   → 订单 Y
        ///   一字不差，正确答案不同。
        ///
        /// 所以：<b>缓存「知识」，不缓存「计算结果」。</b>
        /// 知识类答案（退货政策、功能说明）是固定的；数据类答案是算出来的，基表一变就错。
        /// </summary>
        public static bool IsCacheable(string question, out string reason)
        {
            // 相对时间：答案随日期变，key 里却没有日期
            var time = Regex.Match(question, @"今天|昨天|明天|最近|现在|当前|本月|上月|上周|这周|今年|截至目前");
            if (time.Success)
            {
                reason = $"含相对时间「{time.Value}」，答案会随日期变化";
                return false;
            }

            // 聚合统计：基表一变，结果就变
            var agg = Regex.Match(question, @"最大|最小|最多|最少|一共|总共|合计|总计|排名|平均|前\d+");
            if (agg.Success)
            {
                reason = $"含聚合统计「{agg.Value}」，数据一变结果就变";
                return false;
            }

            reason = "";
            return true;
        }

        /// <summary>
        /// 查缓存。返回 (命中结果, 本次算出的查询向量)。
        ///
        /// 为什么要把向量一起返回：未命中之后调用方要写缓存，而写缓存需要同一句话的向量。
        /// 不返回的话就得再 embed 一次 —— 同一句话算两遍，白花一次调用的时间和钱。
        /// </summary>
        public async Task<(CacheHit? Hit, float[]? QueryVector)> LookupAsync(string question, CancellationToken ct = default)
        {
            if (!IsCacheable(question, out var reason))
            {
                Console.WriteLine($"[cache] 跳过：{reason}");
                _skipped++;
                return (null, null);
            }

            // ── 第一级：精确 ──
            var exactKey = ExactPrefix + Hash(question);
            var exact = await _db.StringGetAsync(exactKey);
            if (exact.HasValue)
            {
                _exactHit++;
                // 精确命中走的是 KV，压根没算向量，也不需要 —— 调用方不会写缓存
                return (new CacheHit(exact!, "exact", 1.0, question), null);
            }

            // ── 第二级：语义 ──
            // 向量在这里算一次，无论后面命中与否都复用给 StoreAsync
            var queryVec = await _embed.EmbedAsync(question, ct);
            var queryEntities = ExtractEntities(question);

            var ids = await _db.SetMembersAsync(SemIndex);
            if (ids.Length == 0)
            {
                _miss++;
                return (null, queryVec);
            }

            CacheHit? best = null;
            double bestScore = 0;

            foreach (var id in ids)
            {
                var entries = await _db.HashGetAllAsync(SemPrefix + id);
                if (entries.Length == 0) continue;

                var dict = entries.ToDictionary(e => e.Name.ToString(), e => e.Value);

                // ── 实体守卫（今天最关键的一步）──
                // 不是"实体不同就减分"，是"实体不同直接不参与竞争"。
                // 因为 embedding 对实体替换几乎无感：A20261109 和 A20261110 差 0.99，
                // 光靠阈值拦不住，只能靠这一行硬拦。
                var cachedEntities = dict.TryGetValue("e", out var eRaw)
                    ? JsonSerializer.Deserialize<List<string>>(eRaw.ToString()) ?? []
                    : [];
                if (!SameEntities(queryEntities, cachedEntities)) continue;

                if (!dict.TryGetValue("v", out var vRaw)) continue;
                if ((byte[]?)vRaw is not { Length: > 0 } bytes) continue;
                var vec = EmbeddingClient.FromBytes(bytes);

                var score = EmbeddingClient.Cosine(queryVec, vec);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = new CacheHit(dict["a"].ToString(), "semantic", score, dict["q"].ToString());
                }
            }

            if (best is not null && bestScore >= _threshold)
            {
                _semanticHit++;
                return (best, queryVec);
            }

            _miss++;
            return (null, queryVec);
        }

        /// <param name="vector">调用方已经算好的向量，传进来就不重复算了</param>
        public async Task StoreAsync(string question, string answer,
                                     float[]? vector = null, CancellationToken ct = default)
        {
            if (!IsCacheable(question, out _)) return;

            await _db.StringSetAsync(ExactPrefix + Hash(question), answer, _ttl);

            // 复用 LookupAsync 算过的向量；只有调用方没给才自己算
            var vec = vector ?? await _embed.EmbedAsync(question, ct);
            var id = Hash(question);                    // 和精确 key 同源，一个问题只存一份向量

            await _db.HashSetAsync(SemPrefix + id,
            [
                new HashEntry("q", question),
                new HashEntry("a", answer),
                new HashEntry("v", EmbeddingClient.ToBytes(vec)),
                new HashEntry("e", JsonSerializer.Serialize(ExtractEntities(question))),
            ]);
            await _db.KeyExpireAsync(SemPrefix + id, _ttl);

            await _db.SetAddAsync(SemIndex, id);
            // 注意：索引里的 id 在数据过期后会残留，Lookup 时取到空 Hash 会跳过。
            // 数据量大了要定期清理，或者用 SCAN 重建索引。
        }

        /// <summary>清空所有缓存。改 prompt 或者换模型之后必须清，否则会返回旧答案。</summary>
        public async Task ClearAsync()
        {
            var ids = await _db.SetMembersAsync(SemIndex);
            foreach (var id in ids)
                await _db.KeyDeleteAsync(SemPrefix + id);

            await _db.KeyDeleteAsync(SemIndex);

            // 精确缓存按前缀扫（数据量小可以这样，大了要用 SCAN + 批量删）
            var server = _redis.GetServer(_redis.GetEndPoints()[0]);
            var keys = server.Keys(pattern: ExactPrefix + "*").ToArray();
            if (keys.Length > 0)
                await _db.KeyDeleteAsync(keys);
        }

        public string Stats()
        {
            int total = _exactHit + _semanticHit + _miss + _skipped;
            int eligible = total - _skipped;          // 分母只算「本来可以缓存」的那些

            // 命中率的分母很关键：如果把「不该缓存的问题」也算进去，
            // 命中率会被它们拉低，你会误以为缓存没效果。
            double rate = eligible == 0 ? 0 : (double)(_exactHit + _semanticHit) / eligible;

            return $"查询 {total} 次（{_skipped} 次按规则不缓存）| " +
                   $"精确命中 {_exactHit} | 语义命中 {_semanticHit} | 未命中 {_miss} | " +
                   $"可缓存查询的命中率 {rate:P0}";
        }

        // ───────── 内部 ─────────

        /// <summary>
        /// 抽实体：订单号（A20261109）、商品编号（SKU-001）。
        /// 和降级规则用的是同一套正则 —— 同一个领域知识，两处复用。
        /// </summary>
        private static List<string> ExtractEntities(string text)
        {
            var list = new List<string>();

            foreach (Match m in Regex.Matches(text, @"[A-Z]\d{8}"))
                list.Add(m.Value);
            foreach (Match m in Regex.Matches(text, @"SKU-\d{3}"))
                list.Add(m.Value);

            list.Sort(StringComparer.Ordinal);
            return list;
        }

        private static bool SameEntities(List<string> a, List<string> b)
            => a.Count == b.Count && a.SequenceEqual(b);

        private static string Hash(string text)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

        public void Dispose() => _redis.Dispose();
    }
}
