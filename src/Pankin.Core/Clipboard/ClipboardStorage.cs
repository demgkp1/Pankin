using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Pankin.Core.Clipboard;

public sealed class ClipboardStorage : IDisposable
{
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly object _lock = new();

    public string DbPath => _dbPath;

    public static string GetDefaultDbPath()
    {
        var xdgData = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var baseDir = !string.IsNullOrWhiteSpace(xdgData)
            ? xdgData
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(baseDir, "pankin", "clipboard.db");
    }

    public ClipboardStorage(string? dbPath = null)
    {
        _dbPath = dbPath ?? GetDefaultDbPath();
        var dir = Path.GetDirectoryName(_dbPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };
        _connectionString = builder.ToString();

        InitializeDatabase();
    }

    private void InitializeDatabase()
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            // 开启 WAL 模式以提升并发读写性能
            using (var pragmaCmd = conn.CreateCommand())
            {
                pragmaCmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
                pragmaCmd.ExecuteNonQuery();
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS clipboard_entries (
                    id TEXT PRIMARY KEY,
                    kind INTEGER NOT NULL,
                    text TEXT,
                    files_json TEXT,
                    content_path TEXT,
                    image_width INTEGER,
                    image_height INTEGER,
                    source TEXT,
                    copied_at INTEGER NOT NULL,
                    pinned INTEGER NOT NULL DEFAULT 0,
                    fingerprint TEXT UNIQUE NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_clipboard_copied_at ON clipboard_entries(copied_at DESC);
                CREATE INDEX IF NOT EXISTS idx_clipboard_pinned ON clipboard_entries(pinned DESC);
                CREATE INDEX IF NOT EXISTS idx_clipboard_fingerprint ON clipboard_entries(fingerprint);
            """;
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 插入剪贴板条目（若指纹已存在则更新复制时间及来源，实现去重置顶）
    /// </summary>
    public ClipboardEntry Upsert(ClipboardEntry entry)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            long timestampMs = entry.CopiedAt.ToUnixTimeMilliseconds();
            string filesJson = entry.Files != null ? JsonSerializer.Serialize(entry.Files) : "";

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO clipboard_entries (
                    id, kind, text, files_json, content_path, image_width, image_height, source, copied_at, pinned, fingerprint
                ) VALUES (
                    @id, @kind, @text, @files_json, @content_path, @image_width, @image_height, @source, @copied_at, @pinned, @fingerprint
                )
                ON CONFLICT(fingerprint) DO UPDATE SET
                    copied_at = excluded.copied_at,
                    source = CASE WHEN excluded.source != '' THEN excluded.source ELSE clipboard_entries.source END
                RETURNING id, kind, text, files_json, content_path, image_width, image_height, source, copied_at, pinned, fingerprint;
            """;

            cmd.Parameters.AddWithValue("@id", entry.Id);
            cmd.Parameters.AddWithValue("@kind", (int)entry.Kind);
            cmd.Parameters.AddWithValue("@text", (object?)entry.Text ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@files_json", string.IsNullOrEmpty(filesJson) ? DBNull.Value : filesJson);
            cmd.Parameters.AddWithValue("@content_path", (object?)entry.ContentPath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@image_width", (object?)entry.ImageWidth ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@image_height", (object?)entry.ImageHeight ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@source", entry.Source ?? string.Empty);
            cmd.Parameters.AddWithValue("@copied_at", timestampMs);
            cmd.Parameters.AddWithValue("@pinned", entry.Pinned ? 1 : 0);
            cmd.Parameters.AddWithValue("@fingerprint", entry.Fingerprint);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return ReadEntry(reader);
            }

            return entry;
        }
    }

    /// <summary>
    /// 条件检索剪贴板历史
    /// </summary>
    public IReadOnlyList<ClipboardEntry> QueryEntries(
        int limit = 100,
        int offset = 0,
        string? search = null,
        ClipKind? kind = null,
        bool? pinned = null)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            var conditions = new List<string>();

            if (!string.IsNullOrWhiteSpace(search))
            {
                conditions.Add("(text LIKE @search OR source LIKE @search)");
                cmd.Parameters.AddWithValue("@search", $"%{search}%");
            }

            if (kind.HasValue)
            {
                conditions.Add("kind = @kind");
                cmd.Parameters.AddWithValue("@kind", (int)kind.Value);
            }

            if (pinned.HasValue)
            {
                conditions.Add("pinned = @pinned");
                cmd.Parameters.AddWithValue("@pinned", pinned.Value ? 1 : 0);
            }

            string whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";
            cmd.CommandText = $"""
                SELECT id, kind, text, files_json, content_path, image_width, image_height, source, copied_at, pinned, fingerprint
                FROM clipboard_entries
                {whereClause}
                ORDER BY pinned DESC, copied_at DESC
                LIMIT @limit OFFSET @offset;
            """;

            cmd.Parameters.AddWithValue("@limit", limit);
            cmd.Parameters.AddWithValue("@offset", offset);

            var list = new List<ClipboardEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(ReadEntry(reader));
            }

            return list;
        }
    }

    public bool SetPinned(string id, bool pinned)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE clipboard_entries SET pinned = @pinned WHERE id = @id;";
            cmd.Parameters.AddWithValue("@pinned", pinned ? 1 : 0);
            cmd.Parameters.AddWithValue("@id", id);

            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM clipboard_entries WHERE id = @id;";
            cmd.Parameters.AddWithValue("@id", id);

            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public int ClearUnpinned()
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM clipboard_entries WHERE pinned = 0;";
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 清理超出最大数量限制的未固定历史记录
    /// </summary>
    public int TrimHistory(int maxUnpinnedCount = 500)
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                DELETE FROM clipboard_entries
                WHERE pinned = 0 AND id NOT IN (
                    SELECT id FROM clipboard_entries
                    WHERE pinned = 0
                    ORDER BY copied_at DESC
                    LIMIT @maxCount
                );
            """;
            cmd.Parameters.AddWithValue("@maxCount", maxUnpinnedCount);
            return cmd.ExecuteNonQuery();
        }
    }

    public int GetTotalCount()
    {
        lock (_lock)
        {
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM clipboard_entries;";
            var res = cmd.ExecuteScalar();
            return res != null ? Convert.ToInt32(res) : 0;
        }
    }

    private static ClipboardEntry ReadEntry(SqliteDataReader reader)
    {
        string id = reader.GetString(0);
        var kind = (ClipKind)reader.GetInt32(1);
        string? text = !reader.IsDBNull(2) ? reader.GetString(2) : null;
        string? filesJson = !reader.IsDBNull(3) ? reader.GetString(3) : null;
        string? contentPath = !reader.IsDBNull(4) ? reader.GetString(4) : null;
        int? imgWidth = !reader.IsDBNull(5) ? reader.GetInt32(5) : null;
        int? imgHeight = !reader.IsDBNull(6) ? reader.GetInt32(6) : null;
        string source = !reader.IsDBNull(7) ? reader.GetString(7) : string.Empty;
        long copiedAtMs = reader.GetInt64(8);
        bool pinned = reader.GetInt32(9) == 1;
        string fingerprint = reader.GetString(10);

        IReadOnlyList<string>? files = null;
        if (!string.IsNullOrEmpty(filesJson))
        {
            try
            {
                files = JsonSerializer.Deserialize<List<string>>(filesJson);
            }
            catch { }
        }

        return new ClipboardEntry(
            id,
            kind,
            text,
            files,
            contentPath,
            imgWidth,
            imgHeight,
            source,
            DateTimeOffset.FromUnixTimeMilliseconds(copiedAtMs),
            pinned,
            fingerprint);
    }

    public void Dispose()
    {
        // SqliteConnection uses pooling; database is file-based.
    }
}
