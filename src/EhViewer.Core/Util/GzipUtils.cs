using System.IO.Compression;
using System.Text;

namespace EhViewer.Core.Util;

/// <summary>
/// Translation of upstream <c>util/GZIPUtils.java</c>: the gzip + Base64 text round-trip used by the
/// clipboard "announce" payload.
/// </summary>
/// <remarks>
/// Upstream relies on the platform default charset; this port fixes UTF-8 so a payload written by the
/// Android client round-trips byte-for-byte. Decompression is bounded: a hostile Base64 blob cannot
/// inflate past <see cref="MaxUncompressedBytes"/>, which the original had no guard against.
/// </remarks>
public static class GzipUtils
{
    /// <summary>Upper bound on a single decompressed payload.</summary>
    public const int MaxUncompressedBytes = 4 * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Gzip-compresses <paramref name="text"/> and returns it Base64-encoded.</summary>
    public static string? Compress(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            byte[] bytes = Utf8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return Convert.ToBase64String(output.ToArray());
    }

    /// <summary>Reverses <see cref="Compress"/>. Returns null when the payload is not valid.</summary>
    public static string? Uncompress(string? compressed)
    {
        if (compressed is null)
        {
            return null;
        }

        if (compressed.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            byte[] packed = Convert.FromBase64String(compressed);
            using var input = new MemoryStream(packed);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);

            using var result = new MemoryStream();
            byte[] buffer = new byte[8192];
            int total = 0;
            int read;
            while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxUncompressedBytes)
                {
                    return null;
                }

                result.Write(buffer, 0, read);
            }

            return Utf8.GetString(result.ToArray());
        }
        catch (Exception ex) when (ex is FormatException or InvalidDataException or IOException)
        {
            return null;
        }
    }
}
