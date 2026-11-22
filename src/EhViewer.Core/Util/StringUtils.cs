using System.Buffers;
using System.Text;

namespace EhViewer.Core.Util;

/// <summary>
/// String manipulation utilities preserving upstream EhViewer parsing semantics.
/// </summary>
public static class StringUtils
{
    public static readonly char[] WhiteSpaceArray = ['\u0009', '\u0020', '\u00A0', '\u3000'];

    private static readonly string[] XmlEscaped =
        ["&amp;", "&lt;", "&gt;", "&quot;", "&#039;", "&times;", "&nbsp;"];

    private static readonly string[] XmlUnescaped =
        ["&", "<", ">", "\"", "'", "\u00D7", " "];

    /// <summary>
    /// Compares two strings treating null and empty strings as equal.
    /// </summary>
    public static bool Equals(string? s1, string? s2)
    {
        if (string.IsNullOrEmpty(s1) && string.IsNullOrEmpty(s2))
        {
            return true;
        }
        return string.Equals(s1, s2, StringComparison.Ordinal);
    }

    /// <summary>
    /// Performs a single-pass replacement of XML character entities matching upstream StringUtils.unescapeXml.
    /// </summary>
    public static string? UnescapeXml(string? str)
    {
        return ReplaceEach(str, XmlEscaped, XmlUnescaped);
    }

    /// <summary>
    /// Single-pass non-repeating multi-pattern replacement matching Apache Commons / upstream StringUtils.replaceEach.
    /// </summary>
    public static string? ReplaceEach(string? text, string?[]? searchList, string?[]? replacementList)
    {
        if (string.IsNullOrEmpty(text) || searchList == null || searchList.Length == 0 ||
            replacementList == null || replacementList.Length == 0)
        {
            return text;
        }

        if (searchList.Length != replacementList.Length)
        {
            throw new ArgumentException("Search and replacement array lengths must match.");
        }

        int searchLength = searchList.Length;
        bool[] noMoreMatches = new bool[searchLength];

        int textIndex = -1;
        int replaceIndex = -1;

        for (int i = 0; i < searchLength; i++)
        {
            if (noMoreMatches[i] || string.IsNullOrEmpty(searchList[i]) || replacementList[i] == null)
            {
                continue;
            }
            int tempIndex = text.IndexOf(searchList[i]!, StringComparison.Ordinal);
            if (tempIndex == -1)
            {
                noMoreMatches[i] = true;
            }
            else if (textIndex == -1 || tempIndex < textIndex)
            {
                textIndex = tempIndex;
                replaceIndex = i;
            }
        }

        if (textIndex == -1)
        {
            return text;
        }

        int start = 0;
        var buf = new StringBuilder(text.Length);

        while (textIndex != -1)
        {
            buf.Append(text, start, textIndex - start);
            buf.Append(replacementList[replaceIndex]);
            start = textIndex + searchList[replaceIndex]!.Length;

            textIndex = -1;
            replaceIndex = -1;

            for (int i = 0; i < searchLength; i++)
            {
                if (noMoreMatches[i] || string.IsNullOrEmpty(searchList[i]) || replacementList[i] == null)
                {
                    continue;
                }
                int tempIndex = text.IndexOf(searchList[i]!, start, StringComparison.Ordinal);
                if (tempIndex == -1)
                {
                    noMoreMatches[i] = true;
                }
                else if (textIndex == -1 || tempIndex < textIndex)
                {
                    textIndex = tempIndex;
                    replaceIndex = i;
                }
            }
        }

        buf.Append(text, start, text.Length - start);
        return buf.ToString();
    }

    /// <summary>
    /// Splits a string by a single separator character, collapsing adjacent separators and returning null for null input.
    /// </summary>
    public static string[]? Split(string? str, char separatorChar)
    {
        if (str == null)
        {
            return null;
        }

        int len = str.Length;
        if (len == 0)
        {
            return [];
        }

        var list = new List<string>();
        int i = 0;
        int start = 0;
        bool match = false;

        while (i < len)
        {
            if (str[i] == separatorChar)
            {
                if (match)
                {
                    list.Add(str.Substring(start, i - start));
                    match = false;
                }
                start = ++i;
                continue;
            }
            match = true;
            i++;
        }

        if (match)
        {
            list.Add(str.Substring(start, i - start));
        }

        return [.. list];
    }

    /// <summary>
    /// Trims leading and trailing whitespace characters matching <see cref="WhiteSpaceArray"/>.
    /// </summary>
    public static string? Trim(string? str)
    {
        return Trim(str, WhiteSpaceArray);
    }

    /// <summary>
    /// Trims leading and trailing characters present in <paramref name="excluded"/>.
    /// </summary>
    public static string? Trim(string? str, ReadOnlySpan<char> excluded)
    {
        if (str == null)
        {
            return null;
        }

        int len = str.Length;
        int start = 0;
        while (start < len && excluded.Contains(str[start]))
        {
            start++;
        }

        int end = len;
        while (end > start && excluded.Contains(str[end - 1]))
        {
            end--;
        }

        return start == 0 && end == len ? str : str.Substring(start, end - start);
    }

    /// <summary>
    /// Finds the 1-based ordinal index of <paramref name="searchStr"/> within <paramref name="str"/>, or -1 if absent.
    /// </summary>
    public static int OrdinalIndexOf(string? str, string? searchStr, int ordinal)
    {
        if (str == null || searchStr == null || ordinal <= 0)
        {
            return -1;
        }

        if (searchStr.Length == 0)
        {
            return 0;
        }

        int found = 0;
        int index = -1;
        do
        {
            index = str.IndexOf(searchStr, index + 1, StringComparison.Ordinal);
            if (index < 0)
            {
                return index;
            }
            found++;
        }
        while (found < ordinal);

        return index;
    }

    public static string? Combine(string?[]? array, char separator)
    {
        if (array == null)
        {
            return null;
        }
        return string.Join(separator, array);
    }
}
