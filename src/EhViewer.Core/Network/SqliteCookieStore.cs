using EhViewer.Core.Client;
using EhViewer.Core.Security;
using Microsoft.Data.Sqlite;

namespace EhViewer.Core.Network;

/// <summary>
/// Encrypted SQLite cookie repository compatible with upstream <c>okhttp3-cookie.db</c> (<c>OK_HTTP_3_COOKIE</c>)
/// and <c>EhCookieStore</c> (<c>nw</c> / <c>uconfig</c> handling), encrypting cookie values at rest via <see cref="ICredentialProtector"/>.
/// </summary>
public sealed class SqliteCookieStore : ICookieStore, IDisposable
{
    public const string TableName = "OK_HTTP_3_COOKIE";
    public const string KeyIpbMemberId = "ipb_member_id";
    public const string KeyIpbPassHash = "ipb_pass_hash";
    public const string KeyIgneous = "igneous";

    public static readonly EhCookie TipsCookie = new(
        name: EhConfig.KeyContentWarning,
        value: EhConfig.ContentWarningNotShow,
        domain: EhUrl.DomainE,
        path: "/",
        expiresAt: EhCookie.MaxExpiresAt,
        secure: false,
        httpOnly: false,
        persistent: true,
        hostOnly: false);

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;
    private readonly ICredentialProtector _protector;
    private readonly Dictionary<string, Dictionary<CookieKey, EhCookie>> _domainCookies = new(StringComparer.Ordinal);
    private readonly Dictionary<CookieKey, long> _cookieIds = new();
    private readonly Func<long> _utcNowMillis;

    public SqliteCookieStore(
        string databasePath,
        ICredentialProtector? protector = null,
        Func<long>? utcNowMillis = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _protector = protector ?? DpapiCredentialProtector.Instance;
        _utcNowMillis = utcNowMillis ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds;

        string connectionString = BuildConnectionString(databasePath);
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        ConfigureConnection(databasePath);

        EnsureSchema();
        LoadFromDatabase();
    }

    private void ConfigureConnection(string databasePath)
    {
        if (string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA busy_timeout = 5000;
            """;
        cmd.ExecuteNonQuery();
    }

    public bool HasSignedIn
    {
        get
        {
            var urlE = new Uri(EhUrl.HostE);
            return Contains(urlE, KeyIpbMemberId) && Contains(urlE, KeyIpbPassHash);
        }
    }

    public void AddCookie(EhCookie cookie)
    {
        ArgumentNullException.ThrowIfNull(cookie);

        lock (_gate)
        {
            long now = _utcNowMillis();
            if (!_domainCookies.TryGetValue(cookie.Domain, out Dictionary<CookieKey, EhCookie>? set))
            {
                set = new Dictionary<CookieKey, EhCookie>();
                _domainCookies[cookie.Domain] = set;
            }

            var key = new CookieKey(cookie);
            EhCookie? toAdd = null;
            EhCookie? toUpdate = null;
            EhCookie? toRemove = null;

            if (cookie.ExpiresAt <= now)
            {
                if (set.Remove(key, out EhCookie? removed) && removed.Persistent)
                {
                    toRemove = removed;
                }
            }
            else
            {
                set.TryGetValue(key, out EhCookie? previous);
                set[key] = cookie;

                toAdd = cookie.Persistent ? cookie : null;
                toUpdate = previous is { Persistent: true } ? previous : null;

                if (toAdd is null && toUpdate is not null)
                {
                    toRemove = toUpdate;
                    toUpdate = null;
                }
            }

            if (toRemove is not null)
            {
                DeleteRow(key);
            }

            if (toAdd is not null)
            {
                if (toUpdate is not null)
                {
                    UpdateRow(key, toAdd);
                }
                else
                {
                    InsertRow(key, toAdd);
                }
            }
        }
    }

    public void SaveFromResponse(Uri requestUrl, IEnumerable<string> setCookieHeaders)
    {
        ArgumentNullException.ThrowIfNull(requestUrl);
        ArgumentNullException.ThrowIfNull(setCookieHeaders);

        long now = _utcNowMillis();
        foreach (string header in setCookieHeaders)
        {
            if (EhCookie.TryParse(requestUrl, header, now, out EhCookie? parsed) && parsed is not null)
            {
                AddCookie(parsed);
            }
        }
    }

    public IReadOnlyList<EhCookie> GetCookies(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        lock (_gate)
        {
            long now = _utcNowMillis();
            var accepted = new List<EhCookie>();
            var expired = new List<EhCookie>();

            foreach ((string domain, Dictionary<CookieKey, EhCookie> set) in _domainCookies)
            {
                if (!EhCookie.DomainMatch(url, domain))
                {
                    continue;
                }

                List<CookieKey>? expiredKeys = null;
                foreach ((CookieKey key, EhCookie cookie) in set)
                {
                    if (cookie.ExpiresAt <= now)
                    {
                        expiredKeys ??= new List<CookieKey>();
                        expiredKeys.Add(key);
                        expired.Add(cookie);
                    }
                    else if (cookie.Matches(url))
                    {
                        accepted.Add(cookie);
                    }
                }

                if (expiredKeys is not null)
                {
                    foreach (CookieKey key in expiredKeys)
                    {
                        set.Remove(key);
                    }
                }
            }

            foreach (EhCookie cookie in expired)
            {
                if (cookie.Persistent)
                {
                    DeleteRow(new CookieKey(cookie));
                }
            }

            // RFC 6265 Section 5.4 step 2: longer paths precede shorter paths.
            accepted.Sort(static (a, b) => b.Path.Length.CompareTo(a.Path.Length));
            return accepted;
        }
    }

    public IReadOnlyList<EhCookie> LoadForRequest(Uri requestUrl)
    {
        ArgumentNullException.ThrowIfNull(requestUrl);

        IReadOnlyList<EhCookie> cookies = GetCookies(requestUrl);
        if (!EhCookie.DomainMatch(requestUrl, EhUrl.DomainE))
        {
            return cookies;
        }

        var result = new List<EhCookie>(cookies.Count + 1);
        foreach (EhCookie cookie in cookies)
        {
            if (cookie.Name is EhConfig.KeyContentWarning or EhConfig.KeyUconfig)
            {
                continue;
            }

            result.Add(cookie);
        }

        result.Add(TipsCookie);
        return result;
    }

    public string GetCookieHeader(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        IReadOnlyList<EhCookie> cookies = LoadForRequest(url);
        return FormatCookieHeader(cookies);
    }

    public bool Contains(Uri url, string name)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(name);

        foreach (EhCookie cookie in GetCookies(url))
        {
            if (string.Equals(cookie.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public void SignOut() => Clear();

    public void Clear()
    {
        lock (_gate)
        {
            _domainCookies.Clear();
            _cookieIds.Clear();

            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {TableName};";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Imports persistent cookies from an Android <c>okhttp3-cookie.db</c> file (plaintext or protected)
    /// and re-encrypts them using this store's <see cref="ICredentialProtector"/>.
    /// </summary>
    public int ImportFromDatabase(string sourceDatabasePath, bool allowUnprotectedSource = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabasePath);

        long now = _utcNowMillis();
        var imported = new List<EhCookie>();

        string sourceConnStr = new SqliteConnectionStringBuilder
        {
            DataSource = sourceDatabasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        using (var srcConn = new SqliteConnection(sourceConnStr))
        {
            srcConn.Open();
            using SqliteCommand cmd = srcConn.CreateCommand();
            cmd.CommandText = $"SELECT NAME, VALUE, EXPIRES_AT, DOMAIN, PATH, SECURE, HTTP_ONLY, PERSISTENT, HOST_ONLY FROM {TableName};";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                EhCookie? cookie = TryReadRow(reader, now, allowUnprotectedSource);
                if (cookie is not null)
                {
                    imported.Add(cookie);
                }
            }
        }

        foreach (EhCookie cookie in imported)
        {
            AddCookie(cookie);
        }

        return imported.Count;
    }

    public static string FormatCookieHeader(IEnumerable<EhCookie> cookies)
    {
        return string.Join("; ", cookies.Select(static c => $"{c.Name}={c.Value}"));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }

    private static string BuildConnectionString(string databasePath)
    {
        if (string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return "Data Source=:memory:";
        }

        string? dir = System.IO.Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    private void EnsureSchema()
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            CREATE TABLE IF NOT EXISTS {TableName} (
                _id INTEGER PRIMARY KEY AUTOINCREMENT,
                NAME TEXT,
                VALUE TEXT,
                EXPIRES_AT INTEGER,
                DOMAIN TEXT,
                PATH TEXT,
                SECURE INTEGER,
                HTTP_ONLY INTEGER,
                PERSISTENT INTEGER,
                HOST_ONLY INTEGER
            );
            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();
    }

    private void LoadFromDatabase()
    {
        long now = _utcNowMillis();
        var toDelete = new List<long>();

        using (SqliteCommand cmd = _connection.CreateCommand())
        {
            cmd.CommandText = $"SELECT _id, NAME, VALUE, EXPIRES_AT, DOMAIN, PATH, SECURE, HTTP_ONLY, PERSISTENT, HOST_ONLY FROM {TableName};";
            using SqliteDataReader reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long id = reader.GetInt64(0);
                EhCookie? cookie = TryReadRow(reader, now, allowUnprotected: false, colOffset: 1);
                if (cookie is null)
                {
                    toDelete.Add(id);
                    continue;
                }

                var key = new CookieKey(cookie);
                _cookieIds[key] = id;
                if (!_domainCookies.TryGetValue(cookie.Domain, out Dictionary<CookieKey, EhCookie>? set))
                {
                    set = new Dictionary<CookieKey, EhCookie>();
                    _domainCookies[cookie.Domain] = set;
                }

                set[key] = cookie;
            }
        }

        if (toDelete.Count > 0)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();
            using SqliteCommand del = _connection.CreateCommand();
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {TableName} WHERE _id = $id;";
            SqliteParameter idParam = del.Parameters.Add("$id", SqliteType.Integer);
            foreach (long id in toDelete)
            {
                idParam.Value = id;
                del.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    private EhCookie? TryReadRow(
        SqliteDataReader reader,
        long now,
        bool allowUnprotected,
        int colOffset = 0)
    {
        if (reader.IsDBNull(colOffset) || reader.IsDBNull(colOffset + 1) ||
            reader.IsDBNull(colOffset + 3) || reader.IsDBNull(colOffset + 4))
        {
            return null;
        }

        string name = reader.GetString(colOffset);
        string rawValue = reader.GetString(colOffset + 1);
        long expiresAt = reader.IsDBNull(colOffset + 2) ? 0L : reader.GetInt64(colOffset + 2);
        string domain = reader.GetString(colOffset + 3);
        string path = reader.GetString(colOffset + 4);
        bool secure = !reader.IsDBNull(colOffset + 5) && reader.GetInt64(colOffset + 5) != 0;
        bool httpOnly = !reader.IsDBNull(colOffset + 6) && reader.GetInt64(colOffset + 6) != 0;
        bool persistent = !reader.IsDBNull(colOffset + 7) && reader.GetInt64(colOffset + 7) != 0;
        bool hostOnly = !reader.IsDBNull(colOffset + 8) && reader.GetInt64(colOffset + 8) != 0;

        if (!persistent || expiresAt <= now)
        {
            return null;
        }

        string value;
        if (_protector.IsProtected(rawValue))
        {
            try
            {
                value = _protector.Unprotect(rawValue);
            }
            catch
            {
                return null;
            }
        }
        else if (allowUnprotected)
        {
            value = rawValue;
        }
        else
        {
            return null;
        }

        return new EhCookie(name, value, domain, path, expiresAt, secure, httpOnly, persistent, hostOnly);
    }

    private void InsertRow(CookieKey key, EhCookie cookie)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO {TableName} (NAME, VALUE, EXPIRES_AT, DOMAIN, PATH, SECURE, HTTP_ONLY, PERSISTENT, HOST_ONLY)
            VALUES ($name, $value, $expiresAt, $domain, $path, $secure, $httpOnly, $persistent, $hostOnly);
            SELECT last_insert_rowid();
            """;
        BindCookieParameters(cmd, cookie);
        object? scalar = cmd.ExecuteScalar();
        if (scalar is long id)
        {
            _cookieIds[key] = id;
        }
    }

    private void UpdateRow(CookieKey key, EhCookie cookie)
    {
        if (!_cookieIds.TryGetValue(key, out long id))
        {
            InsertRow(key, cookie);
            return;
        }

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            UPDATE {TableName}
            SET NAME = $name,
                VALUE = $value,
                EXPIRES_AT = $expiresAt,
                DOMAIN = $domain,
                PATH = $path,
                SECURE = $secure,
                HTTP_ONLY = $httpOnly,
                PERSISTENT = $persistent,
                HOST_ONLY = $hostOnly
            WHERE _id = $id;
            """;
        BindCookieParameters(cmd, cookie);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private void DeleteRow(CookieKey key)
    {
        if (!_cookieIds.Remove(key, out long id))
        {
            return;
        }

        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM {TableName} WHERE _id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private void BindCookieParameters(SqliteCommand cmd, EhCookie cookie)
    {
        cmd.Parameters.AddWithValue("$name", cookie.Name);
        cmd.Parameters.AddWithValue("$value", _protector.Protect(cookie.Value));
        cmd.Parameters.AddWithValue("$expiresAt", cookie.ExpiresAt);
        cmd.Parameters.AddWithValue("$domain", cookie.Domain);
        cmd.Parameters.AddWithValue("$path", cookie.Path);
        cmd.Parameters.AddWithValue("$secure", cookie.Secure ? 1 : 0);
        cmd.Parameters.AddWithValue("$httpOnly", cookie.HttpOnly ? 1 : 0);
        cmd.Parameters.AddWithValue("$persistent", cookie.Persistent ? 1 : 0);
        cmd.Parameters.AddWithValue("$hostOnly", cookie.HostOnly ? 1 : 0);
    }

    private readonly record struct CookieKey(string Name, string Domain, string Path)
    {
        public CookieKey(EhCookie cookie)
            : this(cookie.Name, cookie.Domain, cookie.Path)
        {
        }
    }
}
