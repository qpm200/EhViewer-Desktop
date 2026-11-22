using System.Text;

namespace EhViewer.Core.Util;

/// <summary>
/// Filename sanitization and extension helpers, including Android-compatible sanitization
/// and Windows reserved-name/path hardening.
/// </summary>
public static class FileUtils
{
    public const int MaxFilenameUtf8Bytes = 255;

    private static readonly char[] ForbiddenFilenameCharacters =
        ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Matches upstream Android <c>FileUtils.sanitizeFilename</c>: strips forbidden characters,
    /// truncates at UTF-8 byte length <= 255 preserving UTF-16 surrogate pairs, and trims EhViewer whitespace.
    /// </summary>
    public static string SanitizeFilename(string? filename)
    {
        if (string.IsNullOrEmpty(filename))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(filename.Length);
        int utf8Bytes = 0;

        for (int i = 0; i < filename.Length; i++)
        {
            char c = filename[i];
            if (Array.IndexOf(ForbiddenFilenameCharacters, c) >= 0)
            {
                continue;
            }

            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < filename.Length && char.IsLowSurrogate(filename[i + 1]))
                {
                    if (utf8Bytes + 4 > MaxFilenameUtf8Bytes)
                    {
                        break;
                    }
                    utf8Bytes += 4;
                    sb.Append(c);
                    sb.Append(filename[++i]);
                    continue;
                }
            }

            int charBytes = c < 0x80 ? 1 : (c < 0x800 ? 2 : 3);
            if (utf8Bytes + charBytes > MaxFilenameUtf8Bytes)
            {
                break;
            }
            utf8Bytes += charBytes;
            sb.Append(c);
        }

        return StringUtils.Trim(sb.ToString()) ?? string.Empty;
    }

    /// <summary>
    /// Produces a Windows-safe directory/file name on top of Android <see cref="SanitizeFilename"/> rules:
    /// strips ASCII control characters, trims trailing dots/spaces, and escapes DOS/Win32 reserved device names.
    /// </summary>
    public static string SanitizeWindowsName(string? name)
    {
        string sanitized = SanitizeFilename(name);
        if (sanitized.Length > 0)
        {
            var sb = new StringBuilder(sanitized.Length);
            foreach (char ch in sanitized)
            {
                if (ch >= 0x20)
                {
                    sb.Append(ch);
                }
            }
            sanitized = sb.ToString();
        }

        sanitized = sanitized.TrimEnd('.', ' ', '\t', '\u00A0', '\u3000');
        sanitized = StringUtils.Trim(sanitized) ?? string.Empty;

        if (sanitized.Length == 0)
        {
            return "_";
        }

        int dotIndex = sanitized.IndexOf('.');
        string stem = dotIndex >= 0 ? sanitized[..dotIndex] : sanitized;
        if (WindowsReservedNames.Contains(stem))
        {
            sanitized = "_" + sanitized;
        }

        return sanitized;
    }

    public static string? GetExtensionFromFilename(string? filename)
    {
        if (string.IsNullOrEmpty(filename))
        {
            return null;
        }

        int index = filename.LastIndexOf('.');
        if (index != -1 && index < filename.Length - 1)
        {
            return filename[(index + 1)..];
        }
        return null;
    }

    public static string? GetNameFromFilename(string? filename)
    {
        if (string.IsNullOrEmpty(filename))
        {
            return null;
        }

        int index = filename.LastIndexOf('.');
        return index != -1 ? filename[..index] : filename;
    }
}
