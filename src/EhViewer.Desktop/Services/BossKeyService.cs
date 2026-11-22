namespace EhViewer.Desktop.Services;

public sealed record BossKeyPresetOption(string Gesture, string DisplayLabel);

/// <summary>
/// Parses and resolves curated global Boss Key hotkeys into Win32 <c>RegisterHotKey</c>
/// modifier masks and virtual-key codes, plus idle auto-lock timeout options.
/// </summary>
public static class BossKeyBinding
{
    public const string DefaultGesture = "Ctrl+Alt+H";

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    public const uint VkOem3 = 0xC0; // '~' / '`' key
    public const uint VkF9 = 0x78;
    public const uint VkB = 0x42;
    public const uint VkH = 0x48;

    public static IReadOnlyList<BossKeyPresetOption> Presets { get; } =
    [
        new("Ctrl+Alt+H", "Ctrl + Alt + H（默认推荐）"),
        new("Alt+~", "Alt + ~（左手单手极速）"),
        new("Ctrl+Shift+H", "Ctrl + Shift + H"),
        new("F9", "F9（单键瞬间隐身）"),
        new("Ctrl+B", "Ctrl + B")
    ];

    public static IReadOnlyList<int> AutoLockMinuteOptions { get; } = [0, 1, 3, 5, 10];

    public static bool TryParse(
        string? gesture,
        out uint modifiers,
        out uint virtualKey,
        out string normalizedGesture)
    {
        string raw = string.IsNullOrWhiteSpace(gesture) ? DefaultGesture : gesture.Trim();
        string compact = raw.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase);

        if (compact.Equals("Ctrl+Alt+H", StringComparison.OrdinalIgnoreCase))
        {
            modifiers = ModControl | ModAlt;
            virtualKey = VkH;
            normalizedGesture = "Ctrl+Alt+H";
            return true;
        }

        if (compact.Equals("Alt+~", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("Alt+`", StringComparison.OrdinalIgnoreCase) ||
            compact.Equals("Alt+Oem3", StringComparison.OrdinalIgnoreCase))
        {
            modifiers = ModAlt;
            virtualKey = VkOem3;
            normalizedGesture = "Alt+~";
            return true;
        }

        if (compact.Equals("Ctrl+Shift+H", StringComparison.OrdinalIgnoreCase))
        {
            modifiers = ModControl | ModShift;
            virtualKey = VkH;
            normalizedGesture = "Ctrl+Shift+H";
            return true;
        }

        if (compact.Equals("F9", StringComparison.OrdinalIgnoreCase))
        {
            modifiers = 0;
            virtualKey = VkF9;
            normalizedGesture = "F9";
            return true;
        }

        if (compact.Equals("Ctrl+B", StringComparison.OrdinalIgnoreCase))
        {
            modifiers = ModControl;
            virtualKey = VkB;
            normalizedGesture = "Ctrl+B";
            return true;
        }

        modifiers = ModControl | ModAlt;
        virtualKey = VkH;
        normalizedGesture = DefaultGesture;
        return false;
    }

    public static int IndexOfPreset(string? gesture)
    {
        TryParse(gesture, out _, out _, out string normalized);
        for (int i = 0; i < Presets.Count; i++)
        {
            if (Presets[i].Gesture.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }

    public static string GestureAt(int index) =>
        index >= 0 && index < Presets.Count ? Presets[index].Gesture : DefaultGesture;
}
