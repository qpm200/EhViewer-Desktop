using System.Net;
using Microsoft.Data.Sqlite;

namespace EhViewer.Core.Network;

public sealed record HostEntry(string Host, string Ip);

/// <summary>
/// Custom host-to-IP mapping repository backed by SQLite table <c>HOSTS</c> (<c>hosts.db</c>),
/// matching upstream <c>com.hippo.ehviewer.Hosts</c>.
/// </summary>
public sealed class HostsDatabase : IDisposable
{
    public const string TableName = "HOSTS";

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;

    public HostsDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        string connectionString = BuildConnectionString(databasePath);
        _connection = new SqliteConnection(connectionString);
        _connection.Open();
        ConfigureConnection(databasePath);
        EnsureSchema();
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

    public IPAddress? Get(string? host)
    {
        if (!IsValidHost(host))
        {
            return null;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT IP FROM {TableName} WHERE HOST = $host LIMIT 1;";
            cmd.Parameters.AddWithValue("$host", host!);
            object? result = cmd.ExecuteScalar();
            return result is string ip ? ToHostAddress(host!, ip) : null;
        }
    }

    public IReadOnlyList<IPAddress>? GetList(string? host)
    {
        if (!IsValidHost(host))
        {
            return null;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT IP FROM {TableName} WHERE HOST = $host;";
            cmd.Parameters.AddWithValue("$host", host!);
            using SqliteDataReader reader = cmd.ExecuteReader();

            var list = new List<IPAddress>();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                IPAddress? address = ToHostAddress(host!, reader.GetString(0));
                if (address is not null)
                {
                    list.Add(address);
                }
            }

            return list;
        }
    }

    public bool Put(string? host, string? ip)
    {
        if (!IsValidHost(host) || !IsValidIp(ip))
        {
            return false;
        }

        lock (_gate)
        {
            using SqliteCommand checkCmd = _connection.CreateCommand();
            checkCmd.CommandText = $"SELECT 1 FROM {TableName} WHERE HOST = $host LIMIT 1;";
            checkCmd.Parameters.AddWithValue("$host", host!);
            bool exists = checkCmd.ExecuteScalar() is not null;

            using SqliteCommand writeCmd = _connection.CreateCommand();
            writeCmd.CommandText = exists
                ? $"UPDATE {TableName} SET IP = $ip WHERE HOST = $host;"
                : $"INSERT INTO {TableName} (HOST, IP) VALUES ($host, $ip);";
            writeCmd.Parameters.AddWithValue("$host", host!);
            writeCmd.Parameters.AddWithValue("$ip", ip!);
            writeCmd.ExecuteNonQuery();
            return true;
        }
    }

    public void Delete(string? host)
    {
        if (host is null)
        {
            return;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {TableName} WHERE HOST = $host;";
            cmd.Parameters.AddWithValue("$host", host);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<HostEntry> GetAll()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"SELECT HOST, IP FROM {TableName};";
            using SqliteDataReader reader = cmd.ExecuteReader();

            var result = new List<HostEntry>();
            while (reader.Read())
            {
                if (reader.IsDBNull(0) || reader.IsDBNull(1))
                {
                    continue;
                }

                string host = reader.GetString(0);
                string ip = reader.GetString(1);
                if (ToHostAddress(host, ip) is not null)
                {
                    result.Add(new HostEntry(host, ip));
                }
            }

            return result;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"DELETE FROM {TableName};";
            cmd.ExecuteNonQuery();
        }
    }

    public static IPAddress? ToHostAddress(string? host, string? ip)
    {
        if (!IsValidHost(host) || ip is null)
        {
            return null;
        }

        byte[]? bytes = TryParseV4(ip) ?? TryParseV6(ip);
        return bytes is null ? null : new IPAddress(bytes);
    }

    public static bool IsValidHost(string? host)
    {
        if (host is null || host.Length > 253)
        {
            return false;
        }

        int labelLength = 0;
        foreach (char ch in host)
        {
            if (ch == '.')
            {
                if (labelLength is < 1 or > 63)
                {
                    return false;
                }

                labelLength = 0;
            }
            else
            {
                labelLength++;
            }

            if (ch is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.'))
            {
                return false;
            }
        }

        return labelLength is >= 1 and <= 63;
    }

    public static bool IsValidIp(string? ip)
    {
        return ip is not null && (TryParseV4(ip) is not null || TryParseV6(ip) is not null);
    }

    internal static byte[]? TryParseV4(string s)
    {
        byte[] values = new byte[4];
        int currentOctet = 0;
        int currentValue = 0;
        int numDigits = 0;

        foreach (char c in s)
        {
            if (c is >= '0' and <= '9')
            {
                if (numDigits == 3)
                {
                    return null;
                }

                if (numDigits > 0 && currentValue == 0)
                {
                    return null;
                }

                numDigits++;
                currentValue = (currentValue * 10) + (c - '0');
                if (currentValue > 255)
                {
                    return null;
                }
            }
            else if (c == '.')
            {
                if (currentOctet == 3 || numDigits == 0)
                {
                    return null;
                }

                values[currentOctet++] = (byte)currentValue;
                currentValue = 0;
                numDigits = 0;
            }
            else
            {
                return null;
            }
        }

        if (currentOctet != 3 || numDigits == 0)
        {
            return null;
        }

        values[currentOctet] = (byte)currentValue;
        return values;
    }

    internal static byte[]? TryParseV6(string s)
    {
        int range = -1;
        byte[] data = new byte[16];
        string[] tokens = s.Split(':', StringSplitOptions.None);

        int first = 0;
        int last = tokens.Length - 1;

        if (tokens[0].Length == 0)
        {
            if (last - first > 0 && tokens[1].Length == 0)
            {
                first++;
            }
            else
            {
                return null;
            }
        }

        if (tokens[last].Length == 0)
        {
            if (last - first > 0 && tokens[last - 1].Length == 0)
            {
                last--;
            }
            else
            {
                return null;
            }
        }

        if (last - first + 1 > 8)
        {
            return null;
        }

        int j = 0;
        for (int i = first; i <= last; i++)
        {
            string token = tokens[i];
            if (token.Length == 0)
            {
                if (range >= 0)
                {
                    return null;
                }

                range = j;
                continue;
            }

            if (token.Contains('.'))
            {
                if (i < last || i > 6)
                {
                    return null;
                }

                byte[]? v4Addr = TryParseV4(token);
                if (v4Addr is null)
                {
                    return null;
                }

                Buffer.BlockCopy(v4Addr, 0, data, j, 4);
                j += 4;
                break;
            }

            int x = 0;
            foreach (char c in token)
            {
                int digit = c switch
                {
                    >= '0' and <= '9' => c - '0',
                    >= 'a' and <= 'f' => c - 'a' + 10,
                    >= 'A' and <= 'F' => c - 'A' + 10,
                    _ => -1
                };
                if (digit < 0)
                {
                    return null;
                }

                x = (x << 4) | digit;
                if (x > 0xFFFF)
                {
                    return null;
                }
            }

            data[j++] = (byte)(x >>> 8);
            data[j++] = (byte)(x & 0xFF);
        }

        if (j < 16 && range < 0)
        {
            return null;
        }

        if (range >= 0)
        {
            int empty = 16 - j;
            Buffer.BlockCopy(data, range, data, range + empty, j - range);
            Array.Clear(data, range, empty);
        }

        return data;
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

        string? dir = Path.GetDirectoryName(databasePath);
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
                HOST TEXT,
                IP TEXT
            );
            CREATE INDEX IF NOT EXISTS IDX_HOSTS_HOST ON {TableName} (HOST);
            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();
    }
}
