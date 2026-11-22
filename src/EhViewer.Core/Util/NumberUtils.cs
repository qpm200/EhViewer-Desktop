using System.Globalization;

namespace EhViewer.Core.Util;

/// <summary>
/// Safe invariant-culture numeric parsing utilities matching upstream NumberUtils.
/// </summary>
public static class NumberUtils
{
    public static int ParseIntSafely(string? str, int defaultValue)
    {
        if (string.IsNullOrEmpty(str))
        {
            return defaultValue;
        }

        return int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
            ? result
            : defaultValue;
    }

    public static long ParseLongSafely(string? str, long defaultValue)
    {
        if (string.IsNullOrEmpty(str))
        {
            return defaultValue;
        }

        return long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
            ? result
            : defaultValue;
    }

    public static float ParseFloatSafely(string? str, float defaultValue)
    {
        if (string.IsNullOrEmpty(str))
        {
            return defaultValue;
        }

        return float.TryParse(str, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float result)
            ? result
            : defaultValue;
    }

    public static double ParseDoubleSafely(string? str, double defaultValue)
    {
        if (string.IsNullOrEmpty(str))
        {
            return defaultValue;
        }

        return double.TryParse(str, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double result)
            ? result
            : defaultValue;
    }

    public static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    public static long Clamp(long value, long min, long max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    public static float Clamp(float value, float min, float max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
