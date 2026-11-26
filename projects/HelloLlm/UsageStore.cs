using Microsoft.Data.Sqlite;

namespace HelloLlm
{
    /// <summary>
    /// 一次调用的记账行。对应表里的一行。
    ///
    /// 字段分三类：
    /// - 谁花的：UserId / Department
    /// - 花了多少：tokens / AmountMicros
    /// - 过程如何：Status / ErrorType / ElapsedMs
    ///
    /// 第三类是「不受服务状态影响」的那部分 —— 失败了也要留痕，
    /// 否则你只知道成功的那几次花了多少，不知道失败的那几次浪费了多少。
    /// </summary>
    public sealed record LlmUsageRow(
        string RequestId,
        string UserId,
        string Department,
        string? ModelId,
        long? InputTokens,
        long? OutputTokens,
        long? CachedTokens,
        long? AmountMicros,
        long ElapsedMs,
        string Status,            // ok / fallback / error
        string? ErrorType,
        string? PromptId,
        DateTime CreatedAt);

    /// <summary>
    /// 记账的落库层。只负责「写一行、查一下」，不含任何 LLM 逻辑。
    /// 换存储（SQLite → MySQL）时，只改这个类。
    /// </summary>
    public sealed class UsageStore
    {
        private readonly string _connectionString;

        public UsageStore(string dbPath)
        {
            var dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString();
            EnsureTable();
        }

        /// <summary>
        /// 金额转微元（1e-6 元）。
        ///
        /// SQLite 没有 decimal 类型，只有 INTEGER / REAL / TEXT / BLOB。
        /// 存 REAL 会让累加出现浮点误差（一个月下来对不上账），
        /// 存 TEXT 又没法 SUM。
        /// 所以统一存整数微元，查询时再除回元 —— 和「金额不用 float」是同一条原则的延伸。
        /// </summary>
        public static long? ToMicros(decimal? yuan)
            => yuan is null ? null : (long)Math.Round(yuan.Value * 1_000_000m, MidpointRounding.AwayFromZero);

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(_connectionString);
            conn.Open();
            return conn;
        }

        private void EnsureTable()
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS LlmUsage (
                    Id           INTEGER PRIMARY KEY AUTOINCREMENT,
                    RequestId    TEXT    NOT NULL,
                    UserId       TEXT    NOT NULL,
                    Department   TEXT    NOT NULL,
                    ModelId      TEXT,
                    InputTokens  INTEGER,
                    OutputTokens INTEGER,
                    CachedTokens INTEGER,
                    AmountMicros INTEGER,
                    ElapsedMs    INTEGER NOT NULL,
                    Status       TEXT    NOT NULL,
                    ErrorType    TEXT,
                    PromptId     TEXT,
                    CreatedAt    TEXT    NOT NULL
                );

                CREATE INDEX IF NOT EXISTS IX_LlmUsage_Dept
                    ON LlmUsage(Department, CreatedAt);

                CREATE INDEX IF NOT EXISTS IX_LlmUsage_Request
                    ON LlmUsage(RequestId);
                """;
            cmd.ExecuteNonQuery();
        }

        public void Insert(LlmUsageRow row)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO LlmUsage
                    (RequestId, UserId, Department, ModelId,
                     InputTokens, OutputTokens, CachedTokens, AmountMicros,
                     ElapsedMs, Status, ErrorType, PromptId, CreatedAt)
                VALUES
                    ($rid, $uid, $dept, $model,
                     $in, $out, $cached, $amount,
                     $ms, $status, $err, $prompt, $at);
                """;

            cmd.Parameters.AddWithValue("$rid", row.RequestId);
            cmd.Parameters.AddWithValue("$uid", row.UserId);
            cmd.Parameters.AddWithValue("$dept", row.Department);
            // 可空字段一律走 DBNull：直接传 null 会被当成「没给参数」而不是「值是空」
            cmd.Parameters.AddWithValue("$model", (object?)row.ModelId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$in", (object?)row.InputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$out", (object?)row.OutputTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$cached", (object?)row.CachedTokens ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$amount", (object?)row.AmountMicros ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$ms", row.ElapsedMs);
            cmd.Parameters.AddWithValue("$status", row.Status);
            cmd.Parameters.AddWithValue("$err", (object?)row.ErrorType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$prompt", (object?)row.PromptId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", row.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));

            cmd.ExecuteNonQuery();
        }

        /// <summary>按部门汇总：调用次数、总花费（元）、成功/降级/失败各几次。</summary>
        public List<string> SummarizeByDepartment()
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT Department,
                       COUNT(*)                                        AS Calls,
                       COALESCE(SUM(AmountMicros), 0) / 1000000.0      AS TotalYuan,
                       SUM(CASE WHEN Status = 'ok'       THEN 1 ELSE 0 END) AS OkCount,
                       SUM(CASE WHEN Status = 'fallback' THEN 1 ELSE 0 END) AS FallbackCount,
                       SUM(CASE WHEN Status = 'error'    THEN 1 ELSE 0 END) AS ErrorCount
                FROM LlmUsage
                GROUP BY Department
                ORDER BY TotalYuan DESC;
                """;

            var lines = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                lines.Add($"{reader.GetString(0)}：{reader.GetInt64(1)} 次 | " +
                          $"{reader.GetDouble(2):F6} 元 | " +
                          $"成功 {reader.GetInt64(3)} / 降级 {reader.GetInt64(4)} / 失败 {reader.GetInt64(5)}");
            }
            return lines;
        }

        /// <summary>最近 N 条明细，用来人工核对。</summary>
        public List<string> RecentRows(int limit = 10)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT CreatedAt, UserId, Status, InputTokens, OutputTokens, AmountMicros, ElapsedMs, ErrorType
                FROM LlmUsage
                ORDER BY Id DESC
                LIMIT $n;
                """;
            cmd.Parameters.AddWithValue("$n", limit);

            var lines = new List<string>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                // 可空列必须逐个判 IsDBNull —— GetInt64 读到 NULL 会直接抛异常。
                // 降级和失败那两行，token 全是空的，不判就崩。
                var inTok = reader.IsDBNull(3) ? "—" : reader.GetInt64(3).ToString();
                var outTok = reader.IsDBNull(4) ? "—" : reader.GetInt64(4).ToString();
                var amount = reader.IsDBNull(5) ? "—" : $"{reader.GetInt64(5) / 1000000.0:F6} 元";
                var err = reader.IsDBNull(7) ? "" : $" ({reader.GetString(7)})";

                lines.Add($"{reader.GetString(0)} | {reader.GetString(1)} | {reader.GetString(2)}{err} | " +
                          $"in {inTok} out {outTok} | {amount} | {reader.GetInt64(6)}ms");
            }
            return lines;
        }
    }
}
