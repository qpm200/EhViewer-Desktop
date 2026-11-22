namespace EhViewer.Core.Client.Data;

/// <summary>
/// Translation of upstream <c>widget/AdvanceSearchTable.java</c>: the advanced-search state that is
/// packed into <see cref="ListUrlBuilder.AdvanceSearch"/>, <see cref="ListUrlBuilder.MinRating"/>,
/// <see cref="ListUrlBuilder.PageFrom"/> and <see cref="ListUrlBuilder.PageTo"/>.
/// </summary>
/// <remarks>
/// The Android widget keeps three pieces of state that are easy to get wrong: the search flags are
/// independent check boxes (not one "advanced" switch), the minimum rating only applies when its own
/// check box is ticked, and the page range only applies when the pages check box is ticked. The
/// <see cref="Effective*"/> members reproduce that gating exactly and return the upstream sentinel
/// <c>-1</c> when a field is switched off.
/// </remarks>
public sealed class AdvanceSearchOptions
{
    // Upstream AdvanceSearchTable.S* flag values.
    public const int SName = 0x1;
    public const int STags = 0x2;
    public const int SDesc = 0x4;
    public const int STorr = 0x8;
    public const int STo = 0x10;
    public const int SDt1 = 0x20;
    public const int SDt2 = 0x40;
    public const int SH = 0x80;
    public const int SFl = 0x100;
    public const int SFu = 0x200;
    public const int SFt = 0x400;

    /// <summary>Lowest rating reachable through the spinner (upstream uses index + 2).</summary>
    public const int MinRatingFloor = 2;

    /// <summary>Highest rating reachable through the spinner.</summary>
    public const int MinRatingCeiling = 5;

    /// <summary>Combined <c>f_*</c> flag mask.</summary>
    public int AdvanceSearch { get; set; } = ListUrlBuilder.DefaultAdvance;

    /// <summary>Whether the "minimum rating" check box is ticked.</summary>
    public bool MinRatingEnabled { get; set; }

    /// <summary>Zero-based spinner index; rating = index + 2.</summary>
    public int MinRatingIndex { get; set; }

    /// <summary>Whether the "pages" check box is ticked.</summary>
    public bool PagesEnabled { get; set; }

    /// <summary>Raw lower page bound as typed; 0 or negative means "unset".</summary>
    public int PageFrom { get; set; }

    /// <summary>Raw upper page bound as typed; 0 or negative means "unset".</summary>
    public int PageTo { get; set; }

    /// <summary>Rating to send, or -1 when the check box is unticked.</summary>
    public int EffectiveMinRating
        => MinRatingEnabled
            ? Math.Clamp(MinRatingIndex + MinRatingFloor, MinRatingFloor, MinRatingCeiling)
            : -1;

    /// <summary>Lower page bound to send, or -1 when disabled.</summary>
    public int EffectivePageFrom => PagesEnabled && PageFrom > 0 ? PageFrom : -1;

    /// <summary>Upper page bound to send, or -1 when disabled.</summary>
    public int EffectivePageTo => PagesEnabled && PageTo > 0 ? PageTo : -1;

    public bool IsFlagSet(int flag) => (AdvanceSearch & flag) != 0;

    public void SetFlag(int flag, bool enabled)
    {
        if (enabled)
        {
            AdvanceSearch |= flag;
        }
        else
        {
            AdvanceSearch &= ~flag;
        }
    }

    /// <summary>Copies the state onto a list URL builder, preserving its keyword/category.</summary>
    public void ApplyTo(ListUrlBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AdvanceSearch = AdvanceSearch;
        builder.MinRating = EffectiveMinRating;
        builder.PageFrom = EffectivePageFrom;
        builder.PageTo = EffectivePageTo;
    }

    /// <summary>Restores state from a builder (upstream <c>setAdvanceSearch</c>/<c>setMinRating</c>).</summary>
    public static AdvanceSearchOptions From(ListUrlBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AdvanceSearchOptions
        {
            AdvanceSearch = builder.AdvanceSearch < 0 ? ListUrlBuilder.DefaultAdvance : builder.AdvanceSearch
        };

        int minRating = builder.MinRating;
        if (minRating is >= MinRatingFloor and <= MinRatingCeiling)
        {
            options.MinRatingEnabled = true;
            options.MinRatingIndex = minRating - MinRatingFloor;
        }

        if (builder.PageFrom > 0 || builder.PageTo > 0)
        {
            options.PagesEnabled = true;
            options.PageFrom = Math.Max(0, builder.PageFrom);
            options.PageTo = Math.Max(0, builder.PageTo);
        }

        return options;
    }

    /// <summary>Human-readable flag names, in upstream query-key order, for diagnostics and UI.</summary>
    public static IReadOnlyList<(int Flag, string QueryKey, string Label)> Flags { get; } =
    [
        (SName, "f_sname", "搜索标题"),
        (STags, "f_stags", "搜索标签"),
        (SDesc, "f_sdesc", "搜索描述"),
        (STorr, "f_storr", "搜索种子文件名"),
        (STo, "f_sto", "仅有种子的画廊"),
        (SDt1, "f_sdt1", "搜索低权重标签"),
        (SDt2, "f_sdt2", "搜索被踩标签"),
        (SH, "f_sh", "包含已隐藏画廊"),
        (SFl, "f_sfl", "关闭默认语言过滤"),
        (SFu, "f_sfu", "关闭默认上传者过滤"),
        (SFt, "f_sft", "关闭默认标签过滤")
    ];
}
