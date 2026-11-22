namespace EhViewer.Core.Util;

/// <summary>
/// Compares file paths segment-by-segment in natural sort order.
/// Both '/' and '\' are treated as identical path separators; leading and duplicate
/// separators are ignored, and '.' / '..' are treated as ordinary path segments.
/// </summary>
public sealed class PathNaturalComparator : IComparer<string?>
{
    public static readonly PathNaturalComparator Instance = new();

    private readonly IComparer<string?> _naturalComparator = NaturalComparator.Instance;

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

        int index1 = 0;
        int index2 = 0;

        while (true)
        {
            string? seg1 = NextNormalSegment(x, ref index1);
            string? seg2 = NextNormalSegment(y, ref index2);

            if (seg1 is null && seg2 is null)
            {
                return 0;
            }
            if (seg1 is null)
            {
                return -1;
            }
            if (seg2 is null)
            {
                return 1;
            }

            int result = _naturalComparator.Compare(seg1, seg2);
            if (result != 0)
            {
                return result;
            }
        }
    }

    private static string? NextNormalSegment(string str, ref int index)
    {
        while (index < str.Length && IsSeparator(str[index]))
        {
            index++;
        }

        if (index >= str.Length)
        {
            return null;
        }

        int start = index;
        while (index < str.Length && !IsSeparator(str[index]))
        {
            index++;
        }

        return str[start..index];
    }

    private static bool IsSeparator(char c) => c is '/' or '\\';
}
