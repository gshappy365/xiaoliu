using Microsoft.Data.Sqlite;
using Xiaoliu.Core.Capture;
using Xiaoliu.Core.Configuration;

namespace Xiaoliu.Core.Storage;

public sealed record HistoryEntry(
    long Id,
    DateTimeOffset CreatedAt,
    string Question,
    string Answer,
    int ScreenshotCount);

public sealed record HistoryHit(long EntryId, string Summary, int ScreenshotCount);

/// <summary>
/// 本地历史库。明文 SQLite，和点点同档 —— 这台机器上唯一的高价值泄露物就是截图，
/// 所以文本与图片的留存参数是分开的（docs/adr/0004）。
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private readonly SqliteConnection _connection;

    public HistoryStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        }.ToString());
        _connection.Open();
        Initialize();
    }

    public static HistoryStore OpenDefault(AppSettings settings) => new(settings.DatabasePath);

    private void Initialize()
    {
        Execute("""
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS entries (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT NOT NULL,
                question TEXT NOT NULL,
                answer TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS screenshots (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
                seq INTEGER NOT NULL,
                created_at TEXT NOT NULL,
                media_type TEXT NOT NULL,
                content_hash TEXT NOT NULL,
                width INTEGER NOT NULL,
                height INTEGER NOT NULL,
                bytes BLOB NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_screenshots_entry ON screenshots(entry_id);
            CREATE INDEX IF NOT EXISTS idx_entries_created ON entries(created_at);
            CREATE TABLE IF NOT EXISTS facts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                created_at TEXT NOT NULL,
                category TEXT NOT NULL,
                fact TEXT NOT NULL,
                normalized TEXT NOT NULL UNIQUE
            );
            """);
    }

    public long AddEntry(string question, string answer, IReadOnlyList<CapturedImage> screenshots,
        DateTimeOffset? at = null)
    {
        using var transaction = _connection.BeginTransaction();
        var now = at ?? DateTimeOffset.UtcNow;

        long entryId;
        using (var command = _connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO entries (created_at, question, answer) VALUES ($at, $q, $a); SELECT last_insert_rowid();";
            command.Parameters.AddWithValue("$at", now.ToString("O"));
            command.Parameters.AddWithValue("$q", question);
            command.Parameters.AddWithValue("$a", answer);
            entryId = (long)(command.ExecuteScalar() ?? 0L);
        }

        for (var i = 0; i < screenshots.Count; i++)
        {
            var shot = screenshots[i];
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO screenshots (entry_id, seq, created_at, media_type, content_hash, width, height, bytes)
                VALUES ($e, $s, $at, $m, $h, $w, $h2, $b);
                """;
            command.Parameters.AddWithValue("$e", entryId);
            command.Parameters.AddWithValue("$s", i + 1);
            command.Parameters.AddWithValue("$at", now.ToString("O"));
            command.Parameters.AddWithValue("$m", shot.MediaType);
            command.Parameters.AddWithValue("$h", shot.ContentHash);
            command.Parameters.AddWithValue("$w", shot.Width);
            command.Parameters.AddWithValue("$h2", shot.Height);
            command.Parameters.AddWithValue("$b", shot.Bytes);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        return entryId;
    }

    public IReadOnlyList<HistoryHit> Search(string query, int limit = 5)
    {
        var hits = new List<HistoryHit>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT e.id, e.question, e.answer,
                   (SELECT COUNT(*) FROM screenshots s WHERE s.entry_id = e.id) AS shots
            FROM entries e
            WHERE e.question LIKE $like OR e.answer LIKE $like
            ORDER BY e.id DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$like", $"%{query}%");
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var question = reader.GetString(1);
            var answer = reader.GetString(2);
            var summary = question.Length > 60 ? question[..60] + "…" : question;
            if (!string.IsNullOrWhiteSpace(answer))
            {
                var snippet = answer.Length > 120 ? answer[..120] + "…" : answer;
                summary += " → " + snippet;
            }

            hits.Add(new HistoryHit(reader.GetInt64(0), summary, reader.GetInt32(3)));
        }

        return hits;
    }

    public IReadOnlyList<HistoryEntry> Recent(int limit = 200)
    {
        var entries = new List<HistoryEntry>();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT e.id, e.created_at, e.question, e.answer,
                   (SELECT COUNT(*) FROM screenshots s WHERE s.entry_id = e.id)
            FROM entries e ORDER BY e.id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            entries.Add(new HistoryEntry(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt32(4)));
        }

        return entries;
    }

    /// <summary>记一条事实；同一事实重复出现时更新而不是堆叠（点点也是这么做的）。</summary>
    public bool AddFact(string category, string fact)
    {
        var normalized = Normalize(fact);
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO facts (created_at, category, fact, normalized) VALUES ($at, $c, $f, $n)
            ON CONFLICT(normalized) DO UPDATE SET fact = excluded.fact, category = excluded.category;
            """;
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$c", category);
        command.Parameters.AddWithValue("$f", fact.Trim());
        command.Parameters.AddWithValue("$n", normalized);
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<string> Facts()
    {
        var facts = new List<string>();
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT category, fact FROM facts ORDER BY id DESC;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            facts.Add($"[{reader.GetString(0)}] {reader.GetString(1)}");
        }

        return facts;
    }

    private static string Normalize(string fact) =>
        new string(fact.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c)).ToArray()).ToLowerInvariant();

    /// <summary>
    /// 三档留存：文本按天、图片按天、总量兜底。超限先删最旧图片再删最旧文本 ——
    /// 因为图片是「一次性证据」，文本是「可回看的内容」。
    /// </summary>
    public RetentionReport Sweep(AppSettings settings, DateTimeOffset? now = null)
    {
        var reference = now ?? DateTimeOffset.UtcNow;
        var textCutoff = reference.AddDays(-settings.TextRetentionDays).ToString("O");
        var imageCutoff = reference.AddDays(-settings.ImageRetentionDays).ToString("O");

        var deletedImages = Execute("DELETE FROM screenshots WHERE created_at < $cutoff;",
            ("$cutoff", imageCutoff));
        var deletedTexts = Execute("DELETE FROM entries WHERE created_at < $cutoff;",
            ("$cutoff", textCutoff));

        var limitBytes = (long)settings.MaxTotalMegabytes * 1024 * 1024;
        var total = TotalBytes();
        var evictedImages = 0;
        var evictedTexts = 0;

        while (total > limitBytes && TryDeleteOldestScreenshot())
        {
            evictedImages++;
            total = TotalBytes();
        }

        while (total > limitBytes && TryDeleteOldestEntry())
        {
            evictedTexts++;
            total = TotalBytes();
        }

        return new RetentionReport(deletedImages, deletedTexts, evictedImages, evictedTexts, total);
    }

    /// <summary>清空全部对话、截图与记忆。用户显式要求时才会调用。</summary>
    public void ClearAll()
    {
        Execute("""
            DELETE FROM screenshots;
            DELETE FROM entries;
            DELETE FROM facts;
            VACUUM;
            """);
    }

    public long TotalBytes()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT IFNULL((SELECT SUM(LENGTH(bytes)) FROM screenshots), 0)
                 + IFNULL((SELECT SUM(LENGTH(question) + LENGTH(answer)) FROM entries), 0);
            """;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    private bool TryDeleteOldestScreenshot() =>
        Execute("DELETE FROM screenshots WHERE id = (SELECT id FROM screenshots ORDER BY id LIMIT 1);") > 0;

    private bool TryDeleteOldestEntry() =>
        Execute("DELETE FROM entries WHERE id = (SELECT id FROM entries ORDER BY id LIMIT 1);") > 0;

    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }
}

public sealed record RetentionReport(
    int DeletedImages,
    int DeletedTexts,
    int EvictedImages,
    int EvictedTexts,
    long RemainingBytes);
