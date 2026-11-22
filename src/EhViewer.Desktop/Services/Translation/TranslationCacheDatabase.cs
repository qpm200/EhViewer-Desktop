using Microsoft.Data.Sqlite;

namespace EhViewer.Desktop.Services.Translation;

/// <summary>
/// Thread-safe SQLite repository persisting translated manga dialogue across sessions
/// with bounded LRU eviction by timestamp.
/// </summary>
public sealed class TranslationCacheDatabase : IDisposable
{
    private const int DefaultMaxRows = 4096;

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly int _maxRows;
    private long _lastTimestamp;
    private int _knownRowCount = -1;
    private bool _disposed;

    private SqliteCommand? _tryGetCmd;
    private SqliteParameter? _tryGetKeyParam;

    private SqliteCommand? _touchCmd;
    private SqliteParameter? _touchNowParam;
    private SqliteParameter? _touchKeyParam;

    private SqliteCommand? _insertIgnoreCmd;
    private SqliteParameter? _insKeyParam;
    private SqliteParameter? _insTextParam;
    private SqliteParameter? _insPidParam;
    private SqliteParameter? _insDispParam;
    private SqliteParameter? _insFbParam;
    private SqliteParameter? _insNoteParam;
    private SqliteParameter? _insNowParam;

    private SqliteCommand? _updateCmd;
    private SqliteParameter? _updKeyParam;
    private SqliteParameter? _updTextParam;
    private SqliteParameter? _updPidParam;
    private SqliteParameter? _updDispParam;
    private SqliteParameter? _updFbParam;
    private SqliteParameter? _updNoteParam;
    private SqliteParameter? _updNowParam;

    internal int ExactCountQueryCount { get; private set; }

    internal (string JournalMode, long BusyTimeoutMs, long TempStore, long CacheSize) ReadConnectionPragmasForTests()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            static object? ReadScalar(SqliteConnection conn, string sql)
            {
                using SqliteCommand cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                return cmd.ExecuteScalar();
            }

            string journalMode = Convert.ToString(ReadScalar(_connection, "PRAGMA journal_mode;")) ?? string.Empty;
            long busyTimeout = Convert.ToInt64(ReadScalar(_connection, "PRAGMA busy_timeout;"));
            long tempStore = Convert.ToInt64(ReadScalar(_connection, "PRAGMA temp_store;"));
            long cacheSize = Convert.ToInt64(ReadScalar(_connection, "PRAGMA cache_size;"));
            return (journalMode, busyTimeout, tempStore, cacheSize);
        }
    }

    public TranslationCacheDatabase(string databasePath, int maxRows = DefaultMaxRows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullPath = Path.GetFullPath(databasePath);
        string? dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _maxRows = Math.Clamp(maxRows, 64, 50_000);
        var connBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        };

        _connection = new SqliteConnection(connBuilder.ToString());
        _connection.Open();
        EnsureSchema();
    }

    private int ReadExactCountLocked()
    {
        ExactCountQueryCount++;
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM TRANSLATION_CACHE;";
        object? raw = cmd.ExecuteScalar();
        return raw is long count ? (int)Math.Clamp(count, 0, int.MaxValue) : 0;
    }

    private int EnsureKnownRowCountLocked()
    {
        if (_knownRowCount < 0)
        {
            _knownRowCount = ReadExactCountLocked();
        }

        return _knownRowCount;
    }

    private void EnsureSchema()
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA busy_timeout=5000;
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA temp_store=MEMORY;
            PRAGMA cache_size=-8000;
            CREATE TABLE IF NOT EXISTS TRANSLATION_CACHE (
                cache_key TEXT PRIMARY KEY,
                translated_text TEXT NOT NULL,
                provider_id TEXT NOT NULL,
                provider_display TEXT NOT NULL,
                used_fallback INTEGER NOT NULL,
                fallback_note TEXT NULL,
                updated_utc INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IDX_TRANSLATION_CACHE_UPDATED
                ON TRANSLATION_CACHE (updated_utc);
            """;
        cmd.ExecuteNonQuery();
    }

    public bool TryGet(string cacheKey, out TranslationResult? result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        lock (_gate)
        {
            if (_disposed)
            {
                result = null;
                return false;
            }

            SqliteCommand cmd = GetOrCreateTryGetCommandLocked();
            _tryGetKeyParam!.Value = cacheKey;

            long updatedUtc;
            using (SqliteDataReader reader = cmd.ExecuteReader())
            {
                if (!reader.Read())
                {
                    result = null;
                    return false;
                }

                result = new TranslationResult(
                    TranslatedText: reader.GetString(0),
                    ProviderId: reader.GetString(1),
                    ProviderDisplayName: reader.GetString(2),
                    UsedFallback: reader.GetInt64(3) != 0,
                    FallbackNote: reader.IsDBNull(4) ? null : reader.GetString(4));
                updatedUtc = reader.GetInt64(5);
            }

            // Skip synchronous LRU touch writes on recent hits when well below capacity,
            // preserving strict LRU behavior near/above capacity and in small-cap instances.
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool skipTouch = updatedUtc >= _lastTimestamp ||
                (_maxRows >= 256 && EnsureKnownRowCountLocked() < _maxRows && (nowMs - updatedUtc) < 300_000L);
            if (!skipTouch)
            {
                SqliteCommand touch = GetOrCreateTouchCommandLocked();
                _touchNowParam!.Value = NextTimestampLocked();
                _touchKeyParam!.Value = cacheKey;
                touch.ExecuteNonQuery();
            }

            return true;
        }
    }

    public void Upsert(string cacheKey, TranslationResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheKey);
        ArgumentNullException.ThrowIfNull(result);

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (UpsertSingleLocked(cacheKey, result) && _knownRowCount >= 0)
            {
                _knownRowCount++;
            }

            PruneIfOversizedLocked();
        }
    }

    /// <summary>Persists a batch of translated entries in a single SQLite transaction.</summary>
    public void UpsertBatch(IReadOnlyList<KeyValuePair<string, TranslationResult>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            SqliteCommand insertCmd = GetOrCreateInsertIgnoreCommandLocked();
            SqliteCommand updateCmd = GetOrCreateUpdateCommandLocked();
            using SqliteTransaction tx = _connection.BeginTransaction();
            insertCmd.Transaction = tx;
            updateCmd.Transaction = tx;

            try
            {
                int added = 0;
                foreach ((string key, TranslationResult value) in entries)
                {
                    if (string.IsNullOrWhiteSpace(key) || value is null)
                    {
                        continue;
                    }

                    if (UpsertSingleLocked(key, value))
                    {
                        added++;
                    }
                }

                tx.Commit();
                if (_knownRowCount >= 0)
                {
                    _knownRowCount += added;
                }

                PruneIfOversizedLocked();
            }
            finally
            {
                insertCmd.Transaction = null;
                updateCmd.Transaction = null;
            }
        }
    }

    private bool UpsertSingleLocked(string cacheKey, TranslationResult result)
    {
        SqliteCommand insertCmd = GetOrCreateInsertIgnoreCommandLocked();
        long timestamp = NextTimestampLocked();
        BindWriteParameters(
            _insKeyParam!, _insTextParam!, _insPidParam!, _insDispParam!, _insFbParam!, _insNoteParam!, _insNowParam!,
            cacheKey, result, timestamp);
        if (insertCmd.ExecuteNonQuery() > 0)
        {
            return true;
        }

        SqliteCommand updateCmd = GetOrCreateUpdateCommandLocked();
        BindWriteParameters(
            _updKeyParam!, _updTextParam!, _updPidParam!, _updDispParam!, _updFbParam!, _updNoteParam!, _updNowParam!,
            cacheKey, result, timestamp);
        updateCmd.ExecuteNonQuery();
        return false;
    }

    private SqliteCommand GetOrCreateTryGetCommandLocked()
    {
        if (_tryGetCmd is not null)
        {
            return _tryGetCmd;
        }

        SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT translated_text, provider_id, provider_display, used_fallback, fallback_note, updated_utc
            FROM TRANSLATION_CACHE
            WHERE cache_key = $key
            LIMIT 1;
            """;
        _tryGetKeyParam = cmd.Parameters.Add("$key", SqliteType.Text);
        cmd.Prepare();
        _tryGetCmd = cmd;
        return cmd;
    }

    private SqliteCommand GetOrCreateTouchCommandLocked()
    {
        if (_touchCmd is not null)
        {
            return _touchCmd;
        }

        SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = "UPDATE TRANSLATION_CACHE SET updated_utc = $now WHERE cache_key = $key;";
        _touchNowParam = cmd.Parameters.Add("$now", SqliteType.Integer);
        _touchKeyParam = cmd.Parameters.Add("$key", SqliteType.Text);
        cmd.Prepare();
        _touchCmd = cmd;
        return cmd;
    }

    private SqliteCommand GetOrCreateInsertIgnoreCommandLocked()
    {
        if (_insertIgnoreCmd is not null)
        {
            return _insertIgnoreCmd;
        }

        SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO TRANSLATION_CACHE (
                cache_key, translated_text, provider_id, provider_display, used_fallback, fallback_note, updated_utc)
            VALUES ($key, $text, $pid, $pdisp, $fb, $note, $now);
            """;
        AddWriteParameters(
            cmd,
            out _insKeyParam, out _insTextParam, out _insPidParam, out _insDispParam,
            out _insFbParam, out _insNoteParam, out _insNowParam);
        cmd.Prepare();
        _insertIgnoreCmd = cmd;
        return cmd;
    }

    private SqliteCommand GetOrCreateUpdateCommandLocked()
    {
        if (_updateCmd is not null)
        {
            return _updateCmd;
        }

        SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            UPDATE TRANSLATION_CACHE
            SET translated_text = $text,
                provider_id = $pid,
                provider_display = $pdisp,
                used_fallback = $fb,
                fallback_note = $note,
                updated_utc = $now
            WHERE cache_key = $key;
            """;
        AddWriteParameters(
            cmd,
            out _updKeyParam, out _updTextParam, out _updPidParam, out _updDispParam,
            out _updFbParam, out _updNoteParam, out _updNowParam);
        cmd.Prepare();
        _updateCmd = cmd;
        return cmd;
    }

    private static void AddWriteParameters(
        SqliteCommand cmd,
        out SqliteParameter pKey,
        out SqliteParameter pText,
        out SqliteParameter pPid,
        out SqliteParameter pDisp,
        out SqliteParameter pFb,
        out SqliteParameter pNote,
        out SqliteParameter pNow)
    {
        pKey = cmd.Parameters.Add("$key", SqliteType.Text);
        pText = cmd.Parameters.Add("$text", SqliteType.Text);
        pPid = cmd.Parameters.Add("$pid", SqliteType.Text);
        pDisp = cmd.Parameters.Add("$pdisp", SqliteType.Text);
        pFb = cmd.Parameters.Add("$fb", SqliteType.Integer);
        pNote = cmd.Parameters.Add("$note", SqliteType.Text);
        pNow = cmd.Parameters.Add("$now", SqliteType.Integer);
    }

    private static void BindWriteParameters(
        SqliteParameter pKey,
        SqliteParameter pText,
        SqliteParameter pPid,
        SqliteParameter pDisp,
        SqliteParameter pFb,
        SqliteParameter pNote,
        SqliteParameter pNow,
        string cacheKey,
        TranslationResult result,
        long timestamp)
    {
        pKey.Value = cacheKey;
        pText.Value = result.TranslatedText;
        pPid.Value = result.ProviderId;
        pDisp.Value = result.ProviderDisplayName;
        pFb.Value = result.UsedFallback ? 1 : 0;
        pNote.Value = (object?)result.FallbackNote ?? DBNull.Value;
        pNow.Value = timestamp;
    }

    private long NextTimestampLocked()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (now <= _lastTimestamp)
        {
            now = _lastTimestamp + 1;
        }

        _lastTimestamp = now;
        return now;
    }

    public int GetEntryCount()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return 0;
            }

            return EnsureKnownRowCountLocked();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "DELETE FROM TRANSLATION_CACHE;";
            cmd.ExecuteNonQuery();
            _knownRowCount = 0;
        }
    }

    private void PruneIfOversizedLocked()
    {
        int total = EnsureKnownRowCountLocked();
        int hysteresis = _maxRows >= 256 ? 64 : 0;
        if (total <= _maxRows + hysteresis)
        {
            return;
        }

        int toDelete = total - _maxRows;
        using SqliteCommand prune = _connection.CreateCommand();
        prune.CommandText = """
            DELETE FROM TRANSLATION_CACHE
            WHERE cache_key IN (
                SELECT cache_key FROM TRANSLATION_CACHE
                ORDER BY updated_utc ASC, rowid ASC
                LIMIT $limit
            );
            """;
        prune.Parameters.AddWithValue("$limit", toDelete);
        int deleted = prune.ExecuteNonQuery();
        _knownRowCount = deleted == toDelete
            ? Math.Max(0, total - deleted)
            : ReadExactCountLocked();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _tryGetCmd?.Dispose();
            _tryGetCmd = null;
            _touchCmd?.Dispose();
            _touchCmd = null;
            _insertIgnoreCmd?.Dispose();
            _insertIgnoreCmd = null;
            _updateCmd?.Dispose();
            _updateCmd = null;
            _connection.Dispose();
        }
    }
}
