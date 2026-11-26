using Microsoft.Data.Sqlite;

namespace LlmGateway.Billing;

/// <summary>
/// 一次调用的记账行。
///
/// 字段分三类：
/// - 谁花的：UserId / Department
/// - 谁答的：Provider / ModelId
/// - 花了多少、成没成：tokens / AmountMicros / Status / ErrorType
///
/// 最后一类是「不受服务状态影响」的部分 —— 失败了也要留痕，
/// 否则你只知道成功的那几次花了多少，不知道失败的那几次浪费了多少。
/// </summary>
public sealed record LlmUsageRow(
    string RequestId,
    string UserId,
    string Department,
    string? Provider,
    string? ModelId,
    long? InputTokens,
    long? OutputTokens,
    long? CachedTokens,
    long? AmountMicros,
    long ElapsedMs,
    string Status,            // ok / error / stream_error
    string? ErrorType,
    DateTime CreatedAt);

/// <summary>
/// 记账的落库层。只负责「写一行、查一下」，不含任何 LLM 逻辑。
/// 换存储（SQLite → MySQL）时只改这个类。
/// </summary>
public sealed class UsageStore
{
    /// <summary>
    /// null 表示「记账关闭」。不是 null 引用，是一个刻意的空实现 ——
    /// 这样所有调用方都不用判空，写成 Null Object 模式比传 null 干净得多。
    /// </summary>
    private readonly string? _connectionString;
    private readonly ILogger<UsageStore> _logger;

    /// <summary>记账是否真的在写库。关闭时所有公开方法都是空操作。</summary>
    public bool IsEnabled => _connectionString is not null;

    /// <summary>
    /// 实际用的路径（兜底之后的值）。关闭时为 null —— 用于启动横幅显示，
    /// 直接读配置的话，配置里是空字符串时横幅会显示一片空白。
    /// </summary>
    public string? DisplayPath { get; private set; }

    /// <summary>
    /// dbPath 传 null 或空都可以 —— 但只在 enabled = false 时合法。
    /// 关闭记账时压根不需要知道记到哪，构造完 `_connectionString` 是 null，
    /// 后面所有方法直接返回，连目录都不会创建。
    /// </summary>
    public UsageStore(string? dbPath, bool enabled, ILogger<UsageStore> logger)
    {
        _logger = logger;

        if (!enabled)
        {
            // 关闭记账时不建目录、不建库、不建表 —— 不该在磁盘上留任何痕迹。
            // 这样「关掉记账」和「装一个开着记账的版本」是两个完全独立的状态。
            // 注意这一段在 path 解析之前，所以 dbPath 为空也不会出错。
            _connectionString = null;
            _logger.LogInformation(
                "记账已关闭（Gateway:Usage:Enabled = false）—— 不建库、不需要 DbPath，账目改打文本日志");
            return;
        }

        // 开了记账就必须有路径。没配就兜底成默认值 ——
        // 宁可写进一个约定位置，也不要为了一个配置项的空值让整个服务起不来。
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            dbPath = "data/usage.db";
            _logger.LogWarning("记账已开启但 DbPath 为空，使用默认路径 {Path}", dbPath);
        }

        DisplayPath = dbPath;

        // 相对路径按「应用目录」而不是「当前工作目录」解析：
        // dotnet run（工作目录=项目目录）和容器内运行（工作目录=/app）能落到同一个地方。
        var path = Path.IsPathRooted(dbPath)
            ? dbPath
            : Path.Combine(AppContext.BaseDirectory, dbPath);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        _connectionString = new SqliteConnectionStringBuilder { DataSource = path }.ToString();
        EnsureTable();
        _logger.LogInformation("记账库就绪：{Path}", path);
    }

    /// <summary>
    /// 金额转微元（1e-6 元）。
    ///
    /// SQLite 没有 decimal 类型，只有 INTEGER / REAL / TEXT / BLOB。
    /// 存 REAL 会让累加出现浮点误差（一个月下来对不上账），存 TEXT 又没法 SUM。
    /// 所以统一存整数微元，查询时再除回元。
    /// </summary>
    public static long? ToMicros(decimal? yuan)
        => yuan is null ? null : (long)Math.Round(yuan.Value * 1_000_000m, MidpointRounding.AwayFromZero);

    private SqliteConnection Open()
    {
        // 只有 IsEnabled 为 true 时才会走到这里，所以 ! 是安全的
        var conn = new SqliteConnection(_connectionString!);
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
                Provider     TEXT,
                ModelId      TEXT,
                InputTokens  INTEGER,
                OutputTokens INTEGER,
                CachedTokens INTEGER,
                AmountMicros INTEGER,
                ElapsedMs    INTEGER NOT NULL,
                Status       TEXT    NOT NULL,
                ErrorType    TEXT,
                CreatedAt    TEXT    NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_LlmUsage_Dept
                ON LlmUsage(Department, CreatedAt);

            CREATE INDEX IF NOT EXISTS IX_LlmUsage_Provider
                ON LlmUsage(Provider, CreatedAt);

            CREATE INDEX IF NOT EXISTS IX_LlmUsage_Request
                ON LlmUsage(RequestId);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 写入一行。
    ///
    /// **记账失败不能影响主流程** —— 用户不该因为账本写不进去而拿不到答案。
    /// 但也绝不能静默：这里必须留日志，否则「账单丢了」会变成一件没人知道的事。
    ///
    /// （更严肃的做法是写进本地队列后面重放，那是「账单 vs 日志」那个话题的内容。）
    /// </summary>
    /// <summary>
    /// 写入一行账。
    ///
    /// 两条路径：
    /// - **开着数据库记账** → 写 SQLite，能用 SQL 汇总、按天对账
    /// - **关掉数据库记账** → 退化成一条结构化文本日志，照样能在日志里看到花了多少
    ///
    /// 降级而不是丢弃，是因为「看不到账」和「没花钱」是两件事：
    /// 关掉数据库只是不想维护那个文件，不代表这次调用免费。
    /// </summary>
    public void SafeInsert(LlmUsageRow row)
    {
        if (!IsEnabled)
        {
            WriteTextLog(row);
            return;
        }

        try
        {
            Insert(row);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "记账写入失败 requestId={RequestId}", row.RequestId);
        }
    }

    /// <summary>
    /// 关闭数据库记账时的退路：打一条结构化日志。
    ///
    /// 用参数化而不是字符串拼接 —— Serilog / OTel 这些后端能把每个占位符
    /// 变成独立字段，之后可以按 Provider 统计、按用户过滤，和 SQL 差不多的用法。
    /// 拼成一个长字符串就永远只能靠眼睛看了。
    /// </summary>
    private void WriteTextLog(LlmUsageRow row)
    {
        var cost = row.AmountMicros is null
            ? "—"
            : (row.AmountMicros.Value / 1_000_000m).ToString("F6");

        // 成功的账 Information，失败的账 Warning —— 后者更该被注意到
        var level = row.Status == "ok" ? LogLevel.Information : LogLevel.Warning;

        _logger.Log(level,
            "[usage] {RequestId} | {UserId} | {Dept} | {Provider}/{Model} | " +
            "in={In} out={Out} cached={Cached} | cost={Cost} | {Ms}ms | {Status}{Error}",
            row.RequestId, row.UserId, row.Department, row.Provider ?? "-", row.ModelId ?? "-",
            row.InputTokens?.ToString() ?? "—",
            row.OutputTokens?.ToString() ?? "—",
            row.CachedTokens?.ToString() ?? "—",
            cost, row.ElapsedMs, row.Status,
            row.ErrorType is null ? "" : $" | {row.ErrorType}");
    }

    private void Insert(LlmUsageRow row)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO LlmUsage
                (RequestId, UserId, Department, Provider, ModelId,
                 InputTokens, OutputTokens, CachedTokens, AmountMicros,
                 ElapsedMs, Status, ErrorType, CreatedAt)
            VALUES
                ($rid, $uid, $dept, $provider, $model,
                 $in, $out, $cached, $amount,
                 $ms, $status, $err, $at);
            """;

        cmd.Parameters.AddWithValue("$rid", row.RequestId);
        cmd.Parameters.AddWithValue("$uid", row.UserId);
        cmd.Parameters.AddWithValue("$dept", row.Department);
        // 可空字段一律走 DBNull：直接传 null 会被当成「没给参数」而不是「值是空」
        cmd.Parameters.AddWithValue("$provider", (object?)row.Provider ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$model", (object?)row.ModelId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$in", (object?)row.InputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$out", (object?)row.OutputTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cached", (object?)row.CachedTokens ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$amount", (object?)row.AmountMicros ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ms", row.ElapsedMs);
        cmd.Parameters.AddWithValue("$status", row.Status);
        cmd.Parameters.AddWithValue("$err", (object?)row.ErrorType ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$at", row.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"));

        cmd.ExecuteNonQuery();
    }

    /// <summary>按部门汇总：调用次数、总花费（元）、成功/失败各几次。</summary>
    public List<string> SummarizeByDepartment()
    {
        if (!IsEnabled) return [];

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Department,
                   COUNT(*)                                   AS Calls,
                   COALESCE(SUM(AmountMicros), 0) / 1000000.0 AS TotalYuan,
                   SUM(CASE WHEN Status = 'ok'    THEN 1 ELSE 0 END) AS OkCount,
                   SUM(CASE WHEN Status <> 'ok'   THEN 1 ELSE 0 END) AS ErrorCount
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
                      $"成功 {reader.GetInt64(3)} / 失败 {reader.GetInt64(4)}");
        }
        return lines;
    }

    /// <summary>按 provider 汇总 —— 网关特有：回答"钱花在谁身上"。</summary>
    public List<string> SummarizeByProvider()
    {
        if (!IsEnabled) return [];

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COALESCE(Provider, '(unknown)')      AS Provider,
                   COUNT(*)                             AS Calls,
                   COALESCE(SUM(AmountMicros), 0) / 1000000.0 AS TotalYuan,
                   COALESCE(SUM(InputTokens), 0)        AS InTok,
                   COALESCE(SUM(OutputTokens), 0)       AS OutTok
            FROM LlmUsage
            GROUP BY Provider
            ORDER BY Calls DESC;
            """;

        var lines = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            lines.Add($"{reader.GetString(0)}：{reader.GetInt64(1)} 次 | " +
                      $"{reader.GetDouble(2):F6} 元 | " +
                      $"in {reader.GetInt64(3)} out {reader.GetInt64(4)}");
        }
        return lines;
    }

    /// <summary>最近 N 条明细，用来人工核对。</summary>
    public List<string> RecentRows(int limit = 10)
    {
        if (!IsEnabled) return [];

        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT CreatedAt, UserId, Provider, ModelId, Status,
                   InputTokens, OutputTokens, AmountMicros, ElapsedMs, ErrorType
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
            // 失败那几行 token 全是空的，不判就崩。
            var provider = reader.IsDBNull(2) ? "—" : reader.GetString(2);
            var model = reader.IsDBNull(3) ? "—" : reader.GetString(3);
            var inTok = reader.IsDBNull(5) ? "—" : reader.GetInt64(5).ToString();
            var outTok = reader.IsDBNull(6) ? "—" : reader.GetInt64(6).ToString();
            var amount = reader.IsDBNull(7) ? "—" : $"{reader.GetInt64(7) / 1000000.0:F6} 元";
            var err = reader.IsDBNull(9) ? "" : $" ({reader.GetString(9)})";

            lines.Add($"{reader.GetString(0)} | {reader.GetString(1)} | {provider}/{model} | " +
                      $"{reader.GetString(4)}{err} | in {inTok} out {outTok} | {amount} | " +
                      $"{reader.GetInt64(8)}ms");
        }
        return lines;
    }
}
