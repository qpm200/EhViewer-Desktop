using System.Buffers;
using System.Text;

namespace EhViewer.Core.Spider;

/// <summary>
/// Bounded streaming CBOR header reader matching upstream <c>CborSpiderHeader</c>.
/// Reads <c>gid</c> and <c>token</c> from top-level definite or indefinite CBOR maps.
/// </summary>
public static class CborSpiderHeader
{
    private const int MaxTokenChars = 128;
    private const int MaxKeyChars = 64;
    private const int MaxDepth = 16;
    private const long MaxBytes = 32L * 1024 * 1024;
    private const int BreakByte = 0xFF;

    public sealed record Fields(long Gid, string Token);

    public static Fields? Read(Stream? input)
    {
        if (input is null)
        {
            return null;
        }

        var cursor = new Cursor(input);
        long gid = -1L;
        string? token = null;

        try
        {
            int head = cursor.ReadByte();
            int major = head >> 5;
            int info = head & 0x1F;
            if (major != 5)
            {
                return null;
            }

            if (info == 31)
            {
                while (!cursor.PeekBreak())
                {
                    ReadEntry(cursor, ref gid, ref token);
                    if (gid >= 0 && !string.IsNullOrEmpty(token))
                    {
                        break;
                    }
                }
            }
            else
            {
                long pairs = cursor.ReadArgument(info);
                for (long i = 0; i < pairs; i++)
                {
                    ReadEntry(cursor, ref gid, ref token);
                    if (gid >= 0 && !string.IsNullOrEmpty(token))
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            // Preserve partial gid+token if already read before trailing skip error.
        }

        return gid >= 0 && !string.IsNullOrEmpty(token)
            ? new Fields(gid, token)
            : null;
    }

    private static void ReadEntry(Cursor cursor, ref long gid, ref string? token)
    {
        string? key = cursor.ReadTextOrSkip(MaxKeyChars);
        switch (key)
        {
            case "gid":
                long? parsedGid = cursor.ReadIntegerOrSkip();
                if (parsedGid is >= 0)
                {
                    gid = parsedGid.Value;
                }
                break;

            case "token":
                string? parsedToken = cursor.ReadTextOrSkip(MaxTokenChars);
                if (!string.IsNullOrEmpty(parsedToken))
                {
                    token = parsedToken;
                }
                break;

            default:
                cursor.SkipValue(0);
                break;
        }
    }

    private sealed class Cursor(Stream input)
    {
        private readonly Stream _stream = input;
        private readonly byte[] _skipBuffer = new byte[4096];
        private long _bytesRead;
        private int _pushedByte = -1;

        public int ReadByte()
        {
            int value;
            if (_pushedByte >= 0)
            {
                value = _pushedByte;
                _pushedByte = -1;
                return value;
            }

            value = _stream.ReadByte();
            if (value < 0)
            {
                throw new EndOfStreamException();
            }

            _bytesRead++;
            if (_bytesRead > MaxBytes)
            {
                throw new IOException("cbor too large");
            }

            return value;
        }

        public bool PeekBreak()
        {
            int value;
            if (_pushedByte >= 0)
            {
                value = _pushedByte;
            }
            else
            {
                value = _stream.ReadByte();
                if (value < 0)
                {
                    throw new EndOfStreamException();
                }

                _bytesRead++;
                if (_bytesRead > MaxBytes)
                {
                    throw new IOException("cbor too large");
                }

                _pushedByte = value;
            }

            return value == BreakByte;
        }

        public void ConsumeBreak()
        {
            if (ReadByte() != BreakByte)
            {
                throw new IOException("expected cbor break");
            }
        }

        public long ReadArgument(int info)
        {
            if (info < 24)
            {
                return info;
            }

            int width = info switch
            {
                24 => 1,
                25 => 2,
                26 => 4,
                27 => 8,
                _ => throw new IOException("bad cbor argument")
            };

            long value = 0L;
            for (int i = 0; i < width; i++)
            {
                int next = ReadByte();
                value = (value << 8) | (uint)next;
                if (i == 7 && (value & (1L << 63)) != 0)
                {
                    for (int j = i + 1; j < width; j++)
                    {
                        ReadByte();
                    }
                    throw new IOException("cbor integer overflow");
                }
            }

            // Note: since Big-endian builds MSB first in earlier iterations for i==0, check sign bit on 8-byte MSB:
            return value < 0 ? throw new IOException("cbor integer overflow") : value;
        }

        public string? ReadTextOrSkip(int maxChars)
        {
            int head = ReadByte();
            if ((head >> 5) != 3)
            {
                SkipWithHead(head, 0);
                return null;
            }

            return ReadTextBody(head & 0x1F, maxChars);
        }

        public long? ReadIntegerOrSkip()
        {
            int head = ReadByte();
            int major = head >> 5;
            int info = head & 0x1F;
            if (major != 0 && major != 1)
            {
                SkipWithHead(head, 0);
                return null;
            }

            if (info == 31)
            {
                throw new IOException("bad cbor integer");
            }

            try
            {
                long unsigned = ReadArgument(info);
                return major == 0 ? unsigned : -1L - unsigned;
            }
            catch (IOException ex) when (ex.Message == "cbor integer overflow")
            {
                return null;
            }
        }

        public void SkipValue(int depth)
        {
            SkipWithHead(ReadByte(), depth);
        }

        private void SkipWithHead(int head, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new IOException("cbor too deep");
            }

            int major = head >> 5;
            int info = head & 0x1F;
            switch (major)
            {
                case 0:
                case 1:
                    if (info == 31)
                    {
                        throw new IOException("bad cbor integer");
                    }
                    ReadArgument(info);
                    break;
                case 2:
                case 3:
                    SkipString(major, info);
                    break;
                case 4:
                    SkipContainer(info, depth, isMap: false);
                    break;
                case 5:
                    SkipContainer(info, depth, isMap: true);
                    break;
                case 6:
                    if (info == 31)
                    {
                        throw new IOException("bad cbor tag");
                    }
                    ReadArgument(info);
                    SkipValue(depth);
                    break;
                case 7:
                    SkipSimple(info);
                    break;
                default:
                    throw new IOException("bad cbor major");
            }
        }

        private void SkipSimple(int info)
        {
            switch (info)
            {
                case < 24:
                    return;
                case 24:
                    ReadByte();
                    return;
                case 25:
                    SkipBytes(2);
                    return;
                case 26:
                    SkipBytes(4);
                    return;
                case 27:
                    SkipBytes(8);
                    return;
                default:
                    throw new IOException("bad cbor simple");
            }
        }

        private void SkipString(int major, int info)
        {
            if (info != 31)
            {
                SkipBytes(ReadArgument(info));
                return;
            }

            while (!PeekBreak())
            {
                int chunk = ReadByte();
                if ((chunk >> 5) != major || (chunk & 0x1F) == 31)
                {
                    throw new IOException("bad indefinite string");
                }
                SkipBytes(ReadArgument(chunk & 0x1F));
            }
            ConsumeBreak();
        }

        private void SkipContainer(int info, int depth, bool isMap)
        {
            int items = isMap ? 2 : 1;
            if (info == 31)
            {
                while (!PeekBreak())
                {
                    for (int k = 0; k < items; k++)
                    {
                        SkipValue(depth + 1);
                    }
                }
                ConsumeBreak();
                return;
            }

            long count = ReadArgument(info);
            for (long i = 0; i < count; i++)
            {
                for (int k = 0; k < items; k++)
                {
                    SkipValue(depth + 1);
                }
            }
        }

        private string? ReadTextBody(int info, int maxChars)
        {
            if (info == 31)
            {
                var builder = new StringBuilder();
                bool tooLong = false;
                while (!PeekBreak())
                {
                    int chunk = ReadByte();
                    if ((chunk >> 5) != 3 || (chunk & 0x1F) == 31)
                    {
                        throw new IOException("bad indefinite text");
                    }

                    long chunkLen = ReadArgument(chunk & 0x1F);
                    if (tooLong || builder.Length + chunkLen > maxChars)
                    {
                        tooLong = true;
                        SkipBytes(chunkLen);
                    }
                    else
                    {
                        builder.Append(ReadUtf8(chunkLen));
                    }
                }
                ConsumeBreak();
                return tooLong ? null : builder.ToString();
            }

            long length = ReadArgument(info);
            if (length > maxChars)
            {
                SkipBytes(length);
                return null;
            }

            return ReadUtf8(length);
        }

        private string ReadUtf8(long length)
        {
            if (length > int.MaxValue)
            {
                throw new IOException("cbor text too large");
            }

            int count = (int)length;
            byte[] data = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                int offset = 0;
                while (offset < count)
                {
                    int read = _stream.Read(data, offset, count - offset);
                    if (read <= 0)
                    {
                        throw new EndOfStreamException();
                    }
                    offset += read;
                    _bytesRead += read;
                    if (_bytesRead > MaxBytes)
                    {
                        throw new IOException("cbor too large");
                    }
                }
                return Encoding.UTF8.GetString(data, 0, count);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }

        private void SkipBytes(long length)
        {
            if (length < 0)
            {
                throw new IOException("negative cbor length");
            }

            long left = length;
            while (left > 0)
            {
                int want = (int)Math.Min(_skipBuffer.Length, left);
                int read = _stream.Read(_skipBuffer, 0, want);
                if (read <= 0)
                {
                    throw new EndOfStreamException();
                }
                left -= read;
                _bytesRead += read;
                if (_bytesRead > MaxBytes)
                {
                    throw new IOException("cbor too large");
                }
            }
        }
    }
}
