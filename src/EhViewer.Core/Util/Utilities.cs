namespace EhViewer.Core.Util;

/// <summary>
/// General XML unescape and array utilities matching upstream <c>Utilities</c>.
/// </summary>
public static class Utilities
{
    public static string? UnescapeXml(string? str)
    {
        return StringUtils.UnescapeXml(str);
    }

    public static bool Contains<T>(T[]? array, T value)
    {
        if (array == null)
        {
            return false;
        }

        var comparer = EqualityComparer<T>.Default;
        foreach (T item in array)
        {
            if (comparer.Equals(item, value))
            {
                return true;
            }
        }
        return false;
    }

    public static bool Contains(int[]? array, int value)
    {
        if (array == null)
        {
            return false;
        }

        foreach (int item in array)
        {
            if (item == value)
            {
                return true;
            }
        }
        return false;
    }

    public static int IndexOf(int[]? array, int value)
    {
        if (array == null)
        {
            return -1;
        }

        for (int i = 0; i < array.Length; i++)
        {
            if (array[i] == value)
            {
                return i;
            }
        }
        return -1;
    }

    public static byte[] Copy(byte[] src)
    {
        var copy = new byte[src.Length];
        Buffer.BlockCopy(src, 0, copy, 0, src.Length);
        return copy;
    }
}
