using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using EhViewer.Core.Client.Data.UserTag;
using EhViewer.Core.Network;

namespace EhViewer.Core.Client;

public sealed record TagTranslationSuggestion(string Chinese, string English);

/// <summary>
/// Binary tag translation database matching upstream <c>EhTagDatabase</c>:
/// reads <c>[int32 BE length][UTF-8 lines "prefix:tag\rbase64(translation)\n"]</c>,
/// indexes translations for O(1) lookup, and updates via SHA-1 verification.
/// </summary>
public sealed class EhTagDatabase
{
    public const string DefaultSha1FileName = "tag-translations-zh-rCN.sha1";
    public const string DefaultSha1Url = "https://raw.githubusercontent.com/Mapaler/EhViewer/tag-translations/tag-translations/tag-translations-zh-rCN.sha1";
    public const string DefaultDataFileName = "tag-translations-zh-rCN";
    public const string DefaultDataUrl = "https://raw.githubusercontent.com/Mapaler/EhViewer/tag-translations/tag-translations/tag-translations-zh-rCN";

    private static readonly IReadOnlyDictionary<string, string> NamespaceToPrefixMap = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["rows"] = "n:",
        ["artist"] = "a:",
        ["cosplayer"] = "cos:",
        ["character"] = "c:",
        ["female"] = "f:",
        ["group"] = "g:",
        ["language"] = "l:",
        ["location"] = "loc:",
        ["male"] = "m:",
        ["misc"] = string.Empty,
        ["mixed"] = "x:",
        ["other"] = "o:",
        ["parody"] = "p:",
        ["reclass"] = "r:"
    };

    private static readonly Dictionary<string, string> PrefixToNamespaceMap = new(StringComparer.Ordinal)
    {
        ["n:"] = "rows",
        ["a:"] = "artist",
        ["cos:"] = "cosplayer",
        ["c:"] = "character",
        ["f:"] = "female",
        ["g:"] = "group",
        ["l:"] = "language",
        ["loc:"] = "location",
        ["m:"] = "male",
        [""] = "misc",
        ["x:"] = "mixed",
        ["o:"] = "other",
        ["p:"] = "parody",
        ["r:"] = "reclass"
    };

    private static readonly Dictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> PrefixToNamespaceLookup =
        PrefixToNamespaceMap.GetAlternateLookup<ReadOnlySpan<char>>();

    private readonly Dictionary<string, string> _translations;
    private readonly List<TagEntry> _tagList;

    public EhTagDatabase(string name, Stream source)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(source);

        Name = name;

        Span<byte> lengthBuffer = stackalloc byte[4];
        source.ReadExactly(lengthBuffer);
        int totalBytes = BinaryPrimitives.ReadInt32BigEndian(lengthBuffer);
        if (totalBytes < 0)
        {
            throw new InvalidDataException($"Invalid tag database length header: {totalBytes}.");
        }

        if (totalBytes == 0)
        {
            _translations = new Dictionary<string, string>(StringComparer.Ordinal);
            _tagList = [new TagEntry(string.Empty, "null")];
            return;
        }

        byte[] rented = ArrayPool<byte>.Shared.Rent(totalBytes);
        try
        {
            Span<byte> payload = rented.AsSpan(0, totalBytes);
            source.ReadExactly(payload);
            ParsePayload(payload, out _translations, out _tagList);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    public string Name { get; }
    public IReadOnlyList<TagEntry> TagList => _tagList;

    public static string? NamespaceToPrefix(string? ns)
    {
        if (ns is null)
        {
            return null;
        }

        if (NamespaceToPrefixMap.TryGetValue(ns, out string? prefix))
        {
            return prefix;
        }

        if (PrefixToNamespaceMap.ContainsKey(ns + ":"))
        {
            return ns;
        }

        return null;
    }

    public static string? PrefixToNamespace(string? prefix)
    {
        if (prefix is null)
        {
            return null;
        }

        return PrefixToNamespaceMap.TryGetValue(prefix, out string? ns) ? ns : null;
    }

    public string? GetTranslation(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || _translations.Count == 0)
        {
            return null;
        }

        return _translations.TryGetValue(tag, out string? translation) ? translation : null;
    }

    public List<TagTranslationSuggestion> Suggest(string? keyword, int maxResults = 40)
    {
        var results = new List<TagTranslationSuggestion>();
        if (string.IsNullOrEmpty(keyword) || maxResults <= 0)
        {
            return results;
        }

        foreach (TagEntry tag in _tagList)
        {
            if (results.Count >= maxResults)
            {
                break;
            }

            if (tag.Involve(keyword))
            {
                results.Add(new TagTranslationSuggestion(tag.Chinese, tag.English));
            }
        }

        return results;
    }

    public static async Task<EhTagDatabase?> LoadAndUpdateAsync(
        string directory,
        HttpClient httpClient,
        string sha1FileName = DefaultSha1FileName,
        string sha1Url = DefaultSha1Url,
        string dataFileName = DefaultDataFileName,
        string dataUrl = DefaultDataUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(httpClient);

        Directory.CreateDirectory(directory);
        string sha1Path = Path.Combine(directory, sha1FileName);
        string dataPath = Path.Combine(directory, dataFileName);

        if (!VerifyDataSha1(sha1Path, dataPath))
        {
            DeleteIfExists(sha1Path);
            DeleteIfExists(dataPath);
        }

        EhTagDatabase? current = TryLoadFromFile(dataFileName, dataPath);

        string tempSha1Path = sha1Path + ".tmp";
        string tempDataPath = dataPath + ".tmp";
        try
        {
            if (!await DownloadFileAsync(httpClient, sha1Url, tempSha1Path, cancellationToken).ConfigureAwait(false))
            {
                return current;
            }

            if (VerifyDataSha1(tempSha1Path, dataPath))
            {
                return current;
            }

            if (!await DownloadFileAsync(httpClient, dataUrl, tempDataPath, cancellationToken).ConfigureAwait(false))
            {
                return current;
            }

            if (!VerifyDataSha1(tempSha1Path, tempDataPath))
            {
                return current;
            }

            File.Move(tempSha1Path, sha1Path, overwrite: true);
            File.Move(tempDataPath, dataPath, overwrite: true);

            return TryLoadFromFile(dataFileName, dataPath) ?? current;
        }
        finally
        {
            DeleteIfExists(tempSha1Path);
            DeleteIfExists(tempDataPath);
        }
    }

    internal static bool VerifyDataSha1(string sha1Path, string dataPath)
    {
        if (!File.Exists(sha1Path) || !File.Exists(dataPath))
        {
            return false;
        }

        try
        {
            byte[] storedHash = File.ReadAllBytes(sha1Path);
            if (storedHash.Length < 20)
            {
                return false;
            }

            using FileStream fs = File.OpenRead(dataPath);
            byte[] computed = SHA1.HashData(fs);
            return storedHash.AsSpan(0, 20).SequenceEqual(computed);
        }
        catch
        {
            return false;
        }
    }

    private static EhTagDatabase? TryLoadFromFile(string name, string filePath)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using FileStream fs = File.OpenRead(filePath);
            return new EhTagDatabase(name, fs);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<bool> DownloadFileAsync(
        HttpClient httpClient,
        string url,
        string targetPath,
        CancellationToken cancellationToken)
    {
        try
        {
            using HttpRequestMessage request = EhRequestBuilder.Get(url);
            using HttpResponseMessage response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            await using Stream remoteStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using FileStream localStream = File.Create(targetPath);
            await remoteStream.CopyToAsync(localStream, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Ignore transient cleanup failure.
        }
    }

    private static void ParsePayload(
        ReadOnlySpan<byte> payload,
        out Dictionary<string, string> translations,
        out List<TagEntry> tagList)
    {
        int estimatedLines = payload.Count((byte)'\n') + 1;
        translations = new Dictionary<string, string>(estimatedLines, StringComparer.Ordinal);
        tagList = new List<TagEntry>(estimatedLines);

        int start = 0;
        while (true)
        {
            ReadOnlySpan<byte> remaining = payload.Slice(start);
            int nl = remaining.IndexOf((byte)'\n');
            ReadOnlySpan<byte> line = nl >= 0 ? remaining.Slice(0, nl) : remaining;

            int cr = line.IndexOf((byte)'\r');
            if (cr >= 0)
            {
                ReadOnlySpan<byte> rawKeyBytes = line.Slice(0, cr);
                ReadOnlySpan<byte> b64Bytes = line.Slice(cr + 1);
                if (b64Bytes.IndexOf((byte)'\r') < 0 && TryDecodeBase64Utf8(b64Bytes, out string? chinese))
                {
                    string rawKey = Encoding.UTF8.GetString(rawKeyBytes);
                    translations[rawKey] = chinese;
                    tagList.Add(new TagEntry(ExpandPrefix(rawKey), chinese));
                }
                else
                {
                    tagList.Add(new TagEntry(Encoding.UTF8.GetString(line), "null"));
                }
            }
            else
            {
                tagList.Add(new TagEntry(Encoding.UTF8.GetString(line), "null"));
            }

            if (nl < 0)
            {
                break;
            }

            start += nl + 1;
        }
    }

    private static bool TryDecodeBase64Utf8(ReadOnlySpan<byte> utf8Base64, [NotNullWhen(true)] out string? decoded)
    {
        if (utf8Base64.IsEmpty)
        {
            decoded = string.Empty;
            return true;
        }

        int maxDecodedLen = Base64.GetMaxDecodedFromUtf8Length(utf8Base64.Length);
        byte[]? rented = null;
        Span<byte> buffer = maxDecodedLen <= 512
            ? stackalloc byte[512]
            : (rented = ArrayPool<byte>.Shared.Rent(maxDecodedLen));

        try
        {
            OperationStatus status = Base64.DecodeFromUtf8(utf8Base64, buffer, out _, out int written);
            if (status == OperationStatus.Done)
            {
                decoded = Encoding.UTF8.GetString(buffer.Slice(0, written));
                return true;
            }

            decoded = null;
            return false;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    private static string ExpandPrefix(string rawKey)
    {
        int colon = rawKey.IndexOf(':');
        if (colon < 0 || rawKey.IndexOf(':', colon + 1) >= 0)
        {
            return rawKey;
        }

        string mapped = PrefixToNamespaceLookup.TryGetValue(rawKey.AsSpan(0, colon + 1), out string? ns)
            ? ns
            : rawKey;
        return $"{mapped}:{rawKey.AsSpan(colon + 1)}";
    }
}
