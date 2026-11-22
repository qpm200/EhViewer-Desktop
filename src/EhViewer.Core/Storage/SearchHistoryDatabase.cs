using Microsoft.Data.Sqlite;

namespace EhViewer.Core.Storage;

/// <summary>
/// Translation of upstream <c>widget/SearchDatabase.java</c>: the search-suggestion history kept in
/// its own SQLite file (<c>search_database.db</c>, table <c>suggestions</c>).
/// </summary>
/// <remarks>
/// Upstream builds the <c>LIKE</c> clause by string concatenation and relies on
/// <c>SqlUtils.sqlEscapeString</c>. This port keeps every value parameterized
/// (<c>$prefix</c>/<c>$query</c>) so a pasted query containing quotes cannot alter the statement,
/// and it escapes <c>%</c>/<c>_</c> so a literal prefix still matches literally.
/// </remarks>
public sealed class SearchHistoryDatabase : IDisposable
{
    /// <summary>Upstream <c>MAX_HISTORY</c>.</summary>
    public const int MaxHistory = 100;

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public SearchHistoryDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _connection = new SqliteConnection(BuildConnectionString(databasePath));
        _connection.Open();
        ConfigureConnection(_connection, databasePath);
        EnsureSchema(_connection);
    }

    private static void ConfigureConnection(SqliteConnection connection, string databasePath)
    {
        if (string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Returns up to <paramref name="limit"/> recent queries starting with the prefix.</summary>
    public List<string> GetSuggestions(string? prefix, int limit)
    {
        var results = new List<string>();
        if (limit <= 0)
        {
            return results;
        }

        lock (_gate)
        {
            EnsureNotDisposed();

            using SqliteCommand command = _connection.CreateCommand();

            // Upstream skips the row equal to the prefix itself so the drop-down never echoes input.
            command.CommandText = string.IsNullOrEmpty(prefix)
                ? """
                  SELECT QUERY FROM SUGGESTIONS
                  ORDER BY DATE DESC, _ID DESC
                  LIMIT $limit;
                  """
                : """
                  SELECT QUERY FROM SUGGESTIONS
                  WHERE QUERY LIKE $prefix ESCAPE '\'
                    AND QUERY <> $exact
                  ORDER BY DATE DESC, _ID DESC
                  LIMIT $limit;
                  """;

            command.Parameters.AddWithValue("$limit", limit);
            if (!string.IsNullOrEmpty(prefix))
            {
                command.Parameters.AddWithValue("$prefix", EscapeLike(prefix) + "%");
                command.Parameters.AddWithValue("$exact", prefix);
            }

            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!reader.IsDBNull(0))
                {
                    results.Add(reader.GetString(0));
                }
            }
        }

        return results;
    }

    /// <summary>Records a query, refreshing its timestamp and trimming old entries.</summary>
    public void AddQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        lock (_gate)
        {
            EnsureNotDisposed();

            using (SqliteTransaction transaction = _connection.BeginTransaction())
            {
                using (SqliteCommand delete = _connection.CreateCommand())
                {
                    delete.CommandText = "DELETE FROM SUGGESTIONS WHERE QUERY = $query;";
                    delete.Parameters.AddWithValue("$query", query);
                    delete.ExecuteNonQuery();
                }

                using (SqliteCommand insert = _connection.CreateCommand())
                {
                    insert.CommandText =
                        "INSERT INTO SUGGESTIONS (QUERY, DATE) VALUES ($query, $date);";
                    insert.Parameters.AddWithValue("$query", query);
                    insert.Parameters.AddWithValue("$date", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    insert.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            TruncateLocked(MaxHistory);
        }
    }

    public void DeleteQuery(string? query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return;
        }

        lock (_gate)
        {
            EnsureNotDisposed();

            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM SUGGESTIONS WHERE QUERY = $query;";
            command.Parameters.AddWithValue("$query", query);
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Removes every recorded query.</summary>
    public void ClearQuery()
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            TruncateLocked(0);
        }
    }

    /// <summary>Keeps the newest <paramref name="maxEntries"/> rows; 0 removes everything.</summary>
    public void TruncateHistory(int maxEntries)
    {
        if (maxEntries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        }

        lock (_gate)
        {
            EnsureNotDisposed();
            TruncateLocked(maxEntries);
        }
    }

    private void TruncateLocked(int maxEntries)
    {
        using SqliteCommand command = _connection.CreateCommand();
        if (maxEntries == 0)
        {
            command.CommandText = "DELETE FROM SUGGESTIONS;";
            command.ExecuteNonQuery();
            return;
        }

        // Upstream: delete everything except the newest N rows.
        command.CommandText = """
            DELETE FROM SUGGESTIONS
            WHERE _ID NOT IN (
                SELECT _ID FROM SUGGESTIONS
                ORDER BY DATE DESC, _ID DESC
                LIMIT $keep);
            """;
        command.Parameters.AddWithValue("$keep", maxEntries);
        command.ExecuteNonQuery();
    }

    /// <summary>Escapes the LIKE metacharacters so a literal prefix stays literal.</summary>
    internal static string EscapeLike(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("%", "\\%", StringComparison.Ordinal)
                .Replace("_", "\\_", StringComparison.Ordinal);

    private static void EnsureSchema(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SUGGESTIONS (
                _ID INTEGER PRIMARY KEY AUTOINCREMENT,
                QUERY TEXT NOT NULL,
                DATE INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IDX_SUGGESTIONS_DATE ON SUGGESTIONS (DATE DESC, _ID DESC);
            """;
        command.ExecuteNonQuery();
    }

    private static string BuildConnectionString(string databasePath)
    {
        if (string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return "Data Source=:memory:";
        }

        string? directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    private void EnsureNotDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _connection.Dispose();
    }
}
