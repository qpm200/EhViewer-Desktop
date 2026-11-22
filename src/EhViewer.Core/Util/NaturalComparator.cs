using System.Globalization;

namespace EhViewer.Core.Util;

/// <summary>
/// Implements upstream natural sort order: numeric runs compare numerically (with
/// more leading zeros sorting earlier on ties), '.' and ' ' act as single-char separators,
/// and text runs compare case-insensitively.
/// </summary>
public sealed class NaturalComparator : IComparer<string?>
{
    public static readonly NaturalComparator Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null && y is null)
        {
            return 0;
        }
        if (x is null)
        {
            return -1;
        }
        if (y is null)
        {
            return 1;
        }

        ReadOnlySpan<char> s1 = x.AsSpan();
        ReadOnlySpan<char> s2 = y.AsSpan();
        int index1 = 0;
        int index2 = 0;

        while (true)
        {
            bool has1 = TryNextSlice(s1, ref index1, out ReadOnlySpan<char> data1);
            bool has2 = TryNextSlice(s2, ref index2, out ReadOnlySpan<char> data2);

            if (!has1 && !has2)
            {
                return 0;
            }
            if (!has1)
            {
                return -1;
            }
            if (!has2)
            {
                return 1;
            }

            int result = IsAsciiDigit(data1[0]) && IsAsciiDigit(data2[0])
                ? CompareNumberSlices(data1, data2)
                : CompareIgnoreCase(data1, data2);

            if (result != 0)
            {
                return result;
            }
        }
    }

    private static bool TryNextSlice(ReadOnlySpan<char> str, ref int index, out ReadOnlySpan<char> slice)
    {
        if (index >= str.Length)
        {
            slice = default;
            return false;
        }

        int start = index;
        char ch = str[index++];
        if (ch is '.' or ' ')
        {
            slice = str.Slice(start, 1);
            return true;
        }

        if (IsAsciiDigit(ch))
        {
            while (index < str.Length && IsAsciiDigit(str[index]))
            {
                index++;
            }
        }
        else
        {
            while (index < str.Length)
            {
                char next = str[index];
                if (next is '.' or ' ' || IsAsciiDigit(next))
                {
                    break;
                }
                index++;
            }
        }

        slice = str[start..index];
        return true;
    }

    private static bool IsAsciiDigit(char ch) => ch is >= '0' and <= '9';

    private static ReadOnlySpan<char> RemoveLeadingZeros(ReadOnlySpan<char> s)
    {
        if (s.IsEmpty)
        {
            return s;
        }

        int lastIndex = s.Length - 1;
        for (int i = 0; i < lastIndex; i++)
        {
            if (s[i] != '0')
            {
                return s[i..];
            }
        }

        return s[lastIndex..];
    }

    private static int CompareNumberSlices(ReadOnlySpan<char> s1, ReadOnlySpan<char> s2)
    {
        ReadOnlySpan<char> p1 = RemoveLeadingZeros(s1);
        ReadOnlySpan<char> p2 = RemoveLeadingZeros(s2);

        if (p1.Length != p2.Length)
        {
            return p1.Length.CompareTo(p2.Length);
        }

        for (int i = 0; i < p1.Length; i++)
        {
            if (p1[i] != p2[i])
            {
                return p1[i].CompareTo(p2[i]);
            }
        }

        // Preserve upstream behavior: equal numeric values with more leading zeros sort first.
        return s2.Length.CompareTo(s1.Length);
    }

    private static int CompareIgnoreCase(ReadOnlySpan<char> s1, ReadOnlySpan<char> s2)
    {
        int len = Math.Min(s1.Length, s2.Length);
        for (int i = 0; i < len; i++)
        {
            char c1 = s1[i];
            char c2 = s2[i];
            if (c1 == c2)
            {
                continue;
            }

            // Match Kotlin/JVM String.compareTo(ignoreCase = true): toUpper then toLower.
            char u1 = char.ToUpper(c1, CultureInfo.InvariantCulture);
            char u2 = char.ToUpper(c2, CultureInfo.InvariantCulture);
            if (u1 == u2)
            {
                continue;
            }

            char l1 = char.ToLower(u1, CultureInfo.InvariantCulture);
            char l2 = char.ToLower(u2, CultureInfo.InvariantCulture);
            if (l1 != l2)
            {
                return l1.CompareTo(l2);
            }
        }

        return s1.Length.CompareTo(s2.Length);
    }
}
