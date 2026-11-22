using System.Globalization;
using System.Text;
using EhViewer.Core.Util;

namespace EhViewer.Core.Spider;

/// <summary>
/// Reader and writer for upstream <c>.ehviewer</c> progress/metadata files
/// (VERSION1, VERSION2 text formats, plus CBOR header compatibility).
/// </summary>
public sealed class SpiderInfo
{
    public const string VersionPrefix = "VERSION";
    public const int Version = 2;
    public const int MaxSpiderInfoPages = 100_000;
    public const int MaxSpiderInfoHeaderLine = 8192;
    public const int MaxPTokenFileLine = 2048;
    public const int MaxStoredPTokenChars = 1024;
    public const string TokenFailed = "failed";

    public int StartPage { get; set; }
    public long Gid { get; set; } = -1L;
    public string? Token { get; set; }
    public int PreviewPages { get; set; } = -1;
    public int PreviewPerPage { get; set; } = -1;
    public int Pages { get; set; } = -1;
    public SortedDictionary<int, string> PTokenMap { get; set; } = [];

    public static SpiderInfo? ReadHeader(Stream? stream) => Read(stream, includePTokens: false);

    public static SpiderInfo? ReadCborHeader(Stream? stream)
    {
        CborSpiderHeader.Fields? fields = CborSpiderHeader.Read(stream);
        if (fields is null)
        {
            return null;
        }

        return new SpiderInfo
        {
            Gid = fields.Gid,
            Token = fields.Token
        };
    }

    public static SpiderInfo? Read(Stream? stream, bool includePTokens = true)
    {
        if (stream is null)
        {
            return null;
        }

        Stream buffered = stream is BufferedStream ? stream : new BufferedStream(stream);
        SpiderInfo? spiderInfo;

        try
        {
            spiderInfo = ParseHeader(buffered);
            if (spiderInfo is null)
            {
                return null;
            }

            if (includePTokens)
            {
                ReadPTokens(buffered, spiderInfo);
            }
            else
            {
                spiderInfo.PTokenMap = [];
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or FormatException or OutOfMemoryException)
        {
            return null;
        }

        if (spiderInfo.Gid == -1L || spiderInfo.Token is null || spiderInfo.Pages == -1)
        {
            return null;
        }

        return spiderInfo;
    }

    private static SpiderInfo? ParseHeader(Stream stream)
    {
        var spiderInfo = new SpiderInfo();
        string line = IoUtils.ReadAsciiLine(stream, MaxSpiderInfoHeaderLine);
        int version = GetVersion(line);
        if (version == Version)
        {
            line = IoUtils.ReadAsciiLine(stream, MaxSpiderInfoHeaderLine);
        }
        else if (version != 1)
        {
            return null;
        }

        spiderInfo.StartPage = ParseStartPageHex(line);
        spiderInfo.Gid = long.Parse(IoUtils.ReadAsciiLine(stream, MaxSpiderInfoHeaderLine), CultureInfo.InvariantCulture);
        spiderInfo.Token = IoUtils.ReadAsciiLine(stream, MaxSpiderInfoHeaderLine);
        _ = IoUtils.ReadAsciiLine(stream, MaxSpiderInfoHeaderLine); // Unused historical field ("1")
        spiderInfo.PreviewPages = int.Parse(IoUtils.ReadAsciiLine(stream, MaxSpiderInfoHeaderLine), CultureInfo.InvariantCulture);

        line = IoUtils.ReadAsciiLine(stream, MaxSpiderInfoHeaderLine);
        if (version != 1)
        {
            spiderInfo.PreviewPerPage = int.Parse(line, CultureInfo.InvariantCulture);
            line = IoUtils.ReadAsciiLine(stream, MaxSpiderInfoHeaderLine);
        }

        spiderInfo.Pages = int.Parse(line, CultureInfo.InvariantCulture);
        if (spiderInfo.Pages <= 0 || spiderInfo.Pages > MaxSpiderInfoPages)
        {
            return null;
        }

        return spiderInfo;
    }

    private static void ReadPTokens(Stream stream, SpiderInfo spiderInfo)
    {
        spiderInfo.PTokenMap = [];
        for (int linesRead = 0; linesRead < spiderInfo.Pages; linesRead++)
        {
            string line;
            try
            {
                line = IoUtils.ReadAsciiLine(stream, MaxPTokenFileLine);
            }
            catch (EndOfStreamException)
            {
                break;
            }

            if (TryParsePTokenLine(line, out int index, out string? pToken))
            {
                spiderInfo.PTokenMap[index] = pToken!;
            }
        }
    }

    public static bool TryParsePTokenLine(string line, out int index, out string? pToken)
    {
        index = 0;
        pToken = null;
        if (string.IsNullOrEmpty(line))
        {
            return false;
        }

        int pos = line.IndexOf(' ');
        if (pos <= 0)
        {
            return false;
        }

        if (!int.TryParse(line.AsSpan(0, pos), NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
        {
            return false;
        }

        string token = line[(pos + 1)..];
        if (token.Length == 0 || token.Length > MaxStoredPTokenChars)
        {
            return false;
        }

        pToken = token;
        return true;
    }

    public void Write(Stream stream, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);

        // The writer must be disposed (flushed) before the stream it wraps; closing the stream
        // first makes the writer's own Dispose flush a closed handle and throw
        // ObjectDisposedException out of every caller that writes a progress file.
        using (var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            NewLine = "\n"
        })
        {
            writer.Write(VersionPrefix);
            writer.WriteLine(Version.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(Math.Max(StartPage, 0).ToString("x8", CultureInfo.InvariantCulture));
            writer.WriteLine(Gid.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(Token ?? string.Empty);
            writer.WriteLine("1");
            writer.WriteLine(PreviewPages.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(PreviewPerPage.ToString(CultureInfo.InvariantCulture));
            writer.WriteLine(Pages.ToString(CultureInfo.InvariantCulture));

            foreach ((int key, string value) in PTokenMap)
            {
                if (string.Equals(TokenFailed, value, StringComparison.Ordinal) || string.IsNullOrEmpty(value))
                {
                    continue;
                }

                writer.Write(key.ToString(CultureInfo.InvariantCulture));
                writer.Write(' ');
                writer.WriteLine(value);
            }

            writer.Flush();
        }

        if (!leaveOpen)
        {
            stream.Dispose();
        }
    }

    public void UpdateFrom(SpiderInfo newInfo)
    {
        ArgumentNullException.ThrowIfNull(newInfo);
        Pages = newInfo.Pages;
        Gid = newInfo.Gid;
        Token = newInfo.Token;
        PTokenMap = new SortedDictionary<int, string>(newInfo.PTokenMap);
        PreviewPerPage = newInfo.PreviewPerPage;
        PreviewPages = newInfo.PreviewPages;
    }

    private static int GetVersion(string? str)
    {
        if (str is null)
        {
            return -1;
        }

        if (str.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            return NumberUtils.ParseIntSafely(str[VersionPrefix.Length..], -1);
        }

        return 1;
    }

    private static int ParseStartPageHex(string? str)
    {
        if (str is null)
        {
            return 0;
        }

        int startPage = 0;
        foreach (char ch in str)
        {
            startPage *= 16;
            if (ch is >= '0' and <= '9')
            {
                startPage += ch - '0';
            }
            else if (ch is >= 'a' and <= 'f')
            {
                startPage += ch - 'a' + 10;
            }
        }

        return Math.Max(startPage, 0);
    }
}
