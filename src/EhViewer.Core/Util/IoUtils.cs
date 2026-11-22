using System.Buffers;
using System.Text;

namespace EhViewer.Core.Util;

/// <summary>
/// Stream reading and copying helpers preserving upstream <c>IOUtils</c> line-reading contract.
/// </summary>
public static class IoUtils
{
    public const int DefaultMaxLineLength = 131072; // 128 KiB, matches upstream MAX_LINE_LENGTH

    /// <summary>
    /// Reads one LF-terminated ASCII/Latin-1 line from <paramref name="stream"/>,
    /// stripping an optional trailing CR and enforcing <paramref name="maxLength"/>.
    /// Throws <see cref="EndOfStreamException"/> on EOF before LF and <see cref="IOException"/> if maxLength is exceeded.
    /// </summary>
    public static string ReadAsciiLine(Stream stream, int maxLength = DefaultMaxLineLength)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var sb = new StringBuilder();
        while (true)
        {
            int nextByte = stream.ReadByte();
            if (nextByte == -1)
            {
                throw new EndOfStreamException();
            }

            char nextChar = (char)nextByte;
            if (nextChar == '\n')
            {
                break;
            }

            if (sb.Length >= maxLength)
            {
                throw new IOException($"Line exceeds maximum allowed length of {maxLength} bytes.");
            }

            sb.Append(nextChar);
        }

        if (sb.Length > 0 && sb[^1] == '\r')
        {
            sb.Length -= 1;
        }

        return sb.ToString();
    }

    public static void Copy(Stream input, Stream output, int bufferSize = 8192)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            int read;
            while ((read = input.Read(buffer, 0, bufferSize)) > 0)
            {
                output.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static byte[] ReadAllBytes(Stream input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input is MemoryStream ms && ms.TryGetBuffer(out ArraySegment<byte> seg))
        {
            return seg.ToArray();
        }

        using var buffer = new MemoryStream();
        Copy(input, buffer);
        return buffer.ToArray();
    }
}
