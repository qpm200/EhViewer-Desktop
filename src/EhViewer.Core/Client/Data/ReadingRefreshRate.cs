namespace EhViewer.Core.Client.Data;

/// <summary>One display mode as seen by the reader refresh-rate picker.</summary>
public readonly record struct DisplayModeInfo(int Width, int Height, float RefreshRate);

/// <summary>
/// Translation of upstream <c>util/ReadingRefreshRate.java</c>: the reader's refresh-rate preference
/// (<c>SYSTEM_DEFAULT</c>, <c>DISPLAY_MAX</c>, or an explicit 60/90/120 Hz) plus the mode-selection
/// algorithm that picks the best matching display mode.
/// </summary>
/// <remarks>
/// The Android original works on <c>android.view.Display.Mode</c>. This port keeps the algorithm but
/// takes the mode list as plain data, so it is testable and the platform layer simply supplies the
/// current display's modes.
/// </remarks>
public static class ReadingRefreshRate
{
    /// <summary>Follow whatever the system is doing.</summary>
    public const int SystemDefault = 0;

    /// <summary>Use the highest refresh rate the display supports.</summary>
    public const int DisplayMax = -1;

    /// <summary>Values the reader accepts, matching upstream <c>sanitize</c>.</summary>
    public static IReadOnlyList<int> AllowedValues { get; } = [SystemDefault, DisplayMax, 60, 90, 120];

    /// <summary>True when <paramref name="value"/> is one of the supported settings.</summary>
    public static bool IsSupported(int value)
        => value is SystemDefault or DisplayMax or 60 or 90 or 120;

    /// <summary>Falls back to <see cref="SystemDefault"/> for anything outside the allowed set.</summary>
    public static int Sanitize(int value) => IsSupported(value) ? value : SystemDefault;

    /// <summary>
    /// Chooses the mode closest to <paramref name="settingHz"/> without exceeding it, preferring a
    /// mode that matches the current resolution. Returns null when no mode can satisfy the request
    /// (which the caller treats as "keep the current mode").
    /// </summary>
    public static DisplayModeInfo? FindBestMode(
        IReadOnlyList<DisplayModeInfo>? modes,
        DisplayModeInfo current,
        int settingHz)
    {
        if (modes is null || modes.Count == 0 || settingHz == SystemDefault)
        {
            return null;
        }

        int width = current.Width;
        int height = current.Height;

        float maxHz = MaxRefreshRate(modes, width, height, matchResolution: true);
        if (maxHz <= 0f)
        {
            maxHz = MaxRefreshRate(modes, width, height, matchResolution: false);
        }

        if (maxHz <= 0f)
        {
            return current;
        }

        float targetHz = settingHz == DisplayMax ? maxHz : Math.Min(settingHz, maxHz);

        DisplayModeInfo? best = null;
        float bestHz = -1f;
        bool foundSameResolution = false;

        foreach (DisplayModeInfo mode in modes)
        {
            bool sameResolution = mode.Width == width && mode.Height == height;
            float hz = mode.RefreshRate;

            if (hz > targetHz + 0.1f)
            {
                continue;
            }

            if (sameResolution)
            {
                if (!foundSameResolution || hz > bestHz)
                {
                    foundSameResolution = true;
                    bestHz = hz;
                    best = mode;
                }
            }
            else if (!foundSameResolution && hz > bestHz)
            {
                bestHz = hz;
                best = mode;
            }
        }

        return best ?? current;
    }

    /// <summary>Resolves the effective refresh rate, or 0 when the setting follows the system.</summary>
    public static float ResolveHz(IReadOnlyList<DisplayModeInfo>? modes, DisplayModeInfo current, int settingHz)
    {
        if (settingHz == SystemDefault)
        {
            return 0f;
        }

        DisplayModeInfo? mode = FindBestMode(modes, current, settingHz);
        return mode?.RefreshRate ?? 0f;
    }

    private static float MaxRefreshRate(
        IReadOnlyList<DisplayModeInfo> modes,
        int width,
        int height,
        bool matchResolution)
    {
        float max = 0f;
        foreach (DisplayModeInfo mode in modes)
        {
            if (matchResolution && (mode.Width != width || mode.Height != height))
            {
                continue;
            }

            max = Math.Max(max, mode.RefreshRate);
        }

        return max;
    }
}
