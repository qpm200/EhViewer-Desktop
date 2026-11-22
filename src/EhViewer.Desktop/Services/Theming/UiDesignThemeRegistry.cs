namespace EhViewer.Desktop.Services.Theming;

/// <summary>
/// Central registry of available UI design themes. Future UI designs register here as new
/// <see cref="IUiDesignTheme"/> implementations and appear automatically in appearance settings.
/// </summary>
public static class UiDesignThemeRegistry
{
    public const string DefaultThemeId = ClassicMaterialDesignTheme.ThemeId;

    private static readonly object Gate = new();
    private static readonly List<IUiDesignTheme> Registered =
    [
        ClassicMaterialDesignTheme.Instance,
        MiuixDesignTheme.Instance
    ];

    public static IUiDesignTheme Default => ClassicMaterialDesignTheme.Instance;

    public static IReadOnlyList<IUiDesignTheme> All
    {
        get
        {
            lock (Gate)
            {
                return Registered.ToArray();
            }
        }
    }

    /// <summary>
    /// Registers a new UI design theme or replaces an existing entry with the same <see cref="UiThemeMetadata.Id"/>.
    /// </summary>
    public static void Register(IUiDesignTheme theme)
    {
        ArgumentNullException.ThrowIfNull(theme);
        if (string.IsNullOrWhiteSpace(theme.Metadata.Id))
        {
            throw new ArgumentException("UI design theme id cannot be empty.", nameof(theme));
        }

        lock (Gate)
        {
            int existingIndex = Registered.FindIndex(t =>
                string.Equals(t.Metadata.Id, theme.Metadata.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                Registered[existingIndex] = theme;
            }
            else
            {
                Registered.Add(theme);
            }
        }
    }

    internal static bool Unregister(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId)
            || string.Equals(themeId, DefaultThemeId, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        lock (Gate)
        {
            return Registered.RemoveAll(t =>
                string.Equals(t.Metadata.Id, themeId, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    public static bool TryGet(string? themeId, out IUiDesignTheme? theme)
    {
        if (string.IsNullOrWhiteSpace(themeId))
        {
            theme = null;
            return false;
        }

        lock (Gate)
        {
            foreach (IUiDesignTheme candidate in Registered)
            {
                if (string.Equals(candidate.Metadata.Id, themeId.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    theme = candidate;
                    return true;
                }
            }
        }

        theme = null;
        return false;
    }

    /// <summary>
    /// Resolves a persisted or requested theme identifier, falling back to <see cref="Default"/>
    /// when missing or unrecognized.
    /// </summary>
    public static IUiDesignTheme Resolve(string? themeId)
        => TryGet(themeId, out IUiDesignTheme? found) && found is not null ? found : Default;

    public static int IndexOf(string? themeId)
    {
        IReadOnlyList<IUiDesignTheme> items = All;
        string normalized = Resolve(themeId).Metadata.Id;
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].Metadata.Id, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return 0;
    }
}
