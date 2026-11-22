namespace EhViewer.Core.Util;

/// <summary>
/// Integer and floating-point helpers matching upstream <c>MathUtils</c>.
/// </summary>
public static class MathUtils
{
    public static int CeilDivide(int a, int b)
    {
        return (a + b - 1) / b;
    }

    public static int Clamp(int x, int min, int max)
    {
        if (x > max) return max;
        if (x < min) return min;
        return x;
    }

    public static long Clamp(long x, long min, long max)
    {
        if (x > max) return max;
        if (x < min) return min;
        return x;
    }

    public static float Clamp(float x, float min, float max)
    {
        if (x > max) return max;
        if (x < min) return min;
        return x;
    }

    public static int NextPowerOfTwo(int n)
    {
        if (n <= 0) return 1;
        n--;
        n |= n >> 1;
        n |= n >> 2;
        n |= n >> 4;
        n |= n >> 8;
        n |= n >> 16;
        return n + 1;
    }

    public static float Lerp(float start, float stop, float amount)
    {
        return start + (stop - start) * amount;
    }
}
