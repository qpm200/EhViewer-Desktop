using EhViewer.Core.Client;

namespace EhViewer.Core.Client.Data;

/// <summary>One category check box in upstream <c>widget/CategoryTable.java</c>.</summary>
public sealed record CategoryOption(string Name, int Flag);

/// <summary>
/// Translation of upstream <c>widget/CategoryTable.java</c>. The Android widget's check boxes mean
/// <b>excluded</b>: a ticked box removes that category from the search, so <see cref="GetCategory"/>
/// ors together the flags whose box is <i>not</i> ticked.
/// </summary>
/// <remarks>
/// Upstream's long-press "select inverse" is expressed here as <see cref="InvertExcept"/>; the UI
/// layer decides how to bind it (middle-click, context menu, ...).
/// </remarks>
public static class CategoryTable
{
    /// <summary>The ten categories in upstream layout order (row-major, two per row).</summary>
    public static IReadOnlyList<CategoryOption> Options { get; } =
    [
        new CategoryOption("同人志", EhConfig.Doujinshi),
        new CategoryOption("漫画", EhConfig.Manga),
        new CategoryOption("画师 CG", EhConfig.ArtistCg),
        new CategoryOption("游戏 CG", EhConfig.GameCg),
        new CategoryOption("西方", EhConfig.Western),
        new CategoryOption("非 H", EhConfig.NonH),
        new CategoryOption("图集", EhConfig.ImageSet),
        new CategoryOption("Cosplay", EhConfig.Cosplay),
        new CategoryOption("亚洲", EhConfig.AsianPorn),
        new CategoryOption("其它", EhConfig.Misc)
    ];

    /// <summary>Combined mask of every category.</summary>
    public const int All = EhConfig.Doujinshi | EhConfig.Manga | EhConfig.ArtistCg | EhConfig.GameCg
        | EhConfig.Western | EhConfig.NonH | EhConfig.ImageSet | EhConfig.Cosplay
        | EhConfig.AsianPorn | EhConfig.Misc;

    /// <summary>
    /// Builds the category mask from per-option excluded flags, in <see cref="Options"/> order.
    /// Equivalent to <c>CategoryTable.getCategory()</c>.
    /// </summary>
    public static int GetCategory(IReadOnlyList<bool> excluded)
    {
        ArgumentNullException.ThrowIfNull(excluded);

        int category = 0;
        for (int i = 0; i < Options.Count; i++)
        {
            // A missing trailing entry is treated as "not excluded".
            bool isExcluded = i < excluded.Count && excluded[i];
            if (!isExcluded)
            {
                category |= Options[i].Flag;
            }
        }

        return category;
    }

    /// <summary>Expands a mask into per-option excluded flags. Equivalent to <c>setCategory</c>.</summary>
    public static bool[] GetExcluded(int category)
    {
        var excluded = new bool[Options.Count];
        for (int i = 0; i < Options.Count; i++)
        {
            excluded[i] = (category & Options[i].Flag) == 0;
        }

        return excluded;
    }

    /// <summary>
    /// Upstream long-press behaviour: set every option except <paramref name="index"/> to the
    /// inverse of that option's current value.
    /// </summary>
    public static void InvertExcept(bool[] excluded, int index)
    {
        ArgumentNullException.ThrowIfNull(excluded);
        if (index < 0 || index >= excluded.Length)
        {
            return;
        }

        bool source = excluded[index];
        for (int i = 0; i < excluded.Length; i++)
        {
            if (i != index)
            {
                excluded[i] = !source;
            }
        }
    }

    /// <summary>True when every category is allowed (the upstream "all unchecked" state).</summary>
    public static bool IsAllCategories(int category) => (category & All) == All;

    /// <summary>Localized display name for a single category bit, matching <c>EhUtils.GetCategory</c>.</summary>
    public static string GetName(int category) => EhUtils.GetCategory(category);
}
