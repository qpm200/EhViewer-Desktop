using System.Text.RegularExpressions;
using EhViewer.Core.Client.Data;

namespace EhViewer.Core.Client;

/// <summary>
/// Gallery category mappings, title extraction, and thumbnail URL helpers matching upstream <c>EhUtils</c>.
/// </summary>
public static partial class EhUtils
{
    public const int None = -1;
    public const int Unknown = 0x400;

    public const int BgColorDoujinshi = unchecked((int)0xFFF44336);
    public const int BgColorManga = unchecked((int)0xFFFF9800);
    public const int BgColorArtistCg = unchecked((int)0xFFFBC02D);
    public const int BgColorGameCg = unchecked((int)0xFF4CAF50);
    public const int BgColorWestern = unchecked((int)0xFF8BC34A);
    public const int BgColorNonH = unchecked((int)0xFF2196F3);
    public const int BgColorImageSet = unchecked((int)0xFF3F51B5);
    public const int BgColorCosplay = unchecked((int)0xFF9C27B0);
    public const int BgColorAsianPorn = unchecked((int)0xFF9575CD);
    public const int BgColorMisc = unchecked((int)0xFFF06292);
    public const int BgColorUnknown = unchecked((int)0xFF000000);

    private static readonly Regex PatternTitlePrefix = PrefixRegex();
    private static readonly Regex PatternTitleSuffix = SuffixRegex();

    private static readonly int[] CategoryValues =
    [
        EhConfig.Misc,
        EhConfig.Doujinshi,
        EhConfig.Manga,
        EhConfig.ArtistCg,
        EhConfig.GameCg,
        EhConfig.ImageSet,
        EhConfig.Cosplay,
        EhConfig.AsianPorn,
        EhConfig.NonH,
        EhConfig.Western,
        Unknown
    ];

    private static readonly string[][] CategoryStrings =
    [
        ["misc"],
        ["doujinshi"],
        ["manga"],
        ["artistcg", "Artist CG Sets", "Artist CG"],
        ["gamecg", "Game CG Sets", "Game CG"],
        ["imageset", "Image Sets", "Image Set"],
        ["cosplay"],
        ["asianporn", "Asian Porn"],
        ["non-h"],
        ["western"],
        ["unknown"]
    ];

    public static int GetCategory(string? type)
    {
        int i = 0;
        while (i < CategoryStrings.Length - 1)
        {
            foreach (string str in CategoryStrings[i])
            {
                if (string.Equals(str, type, StringComparison.OrdinalIgnoreCase))
                {
                    return CategoryValues[i];
                }
            }
            i++;
        }

        return CategoryValues[i];
    }

    public static string GetCategory(int type)
    {
        int i = 0;
        while (i < CategoryValues.Length - 1)
        {
            if (CategoryValues[i] == type)
            {
                break;
            }
            i++;
        }
        return CategoryStrings[i][0];
    }

    public static int GetCategoryColor(int category) => category switch
    {
        EhConfig.Doujinshi => BgColorDoujinshi,
        EhConfig.Manga => BgColorManga,
        EhConfig.ArtistCg => BgColorArtistCg,
        EhConfig.GameCg => BgColorGameCg,
        EhConfig.Western => BgColorWestern,
        EhConfig.NonH => BgColorNonH,
        EhConfig.ImageSet => BgColorImageSet,
        EhConfig.Cosplay => BgColorCosplay,
        EhConfig.AsianPorn => BgColorAsianPorn,
        EhConfig.Misc => BgColorMisc,
        _ => BgColorUnknown
    };

    public static string? GetSuitableTitle(GalleryInfo gi, bool showJpnTitle = false)
    {
        ArgumentNullException.ThrowIfNull(gi);
        if (showJpnTitle)
        {
            return string.IsNullOrEmpty(gi.TitleJpn) ? gi.Title : gi.TitleJpn;
        }
        return string.IsNullOrEmpty(gi.Title) ? gi.TitleJpn : gi.Title;
    }

    public static bool JudgeSuitableTitle(GalleryInfo gi, string key)
    {
        ArgumentNullException.ThrowIfNull(gi);
        string titleB = (gi.TitleJpn ?? string.Empty) + (gi.Title ?? string.Empty);
        return titleB.Contains(key, StringComparison.Ordinal);
    }

    public static string? ExtractTitle(string? title)
    {
        if (title is null)
        {
            return null;
        }

        title = PatternTitlePrefix.Replace(title, string.Empty, 1);
        title = PatternTitleSuffix.Replace(title, string.Empty, 1);

        int index = title.IndexOf('|');
        if (index >= 0)
        {
            title = title[..index];
        }

        return title.Length == 0 ? null : title;
    }

    public static string? HandleThumbUrlResolution(string? url, int thumbResolution = 0)
    {
        if (url is null)
        {
            return null;
        }

        string? resolution = thumbResolution switch
        {
            1 => "250",
            2 => "300",
            _ => null
        };

        if (resolution is null)
        {
            return url;
        }

        int index1 = url.LastIndexOf('_');
        int index2 = url.LastIndexOf('.');
        if (index1 >= 0 && index2 >= 0 && index1 < index2)
        {
            return string.Concat(url.AsSpan(0, index1 + 1), resolution, url.AsSpan(index2));
        }

        return url;
    }

    [GeneratedRegex(@"^(?:(?:\([^\)]*\))|(?:\[[^\]]*\])|(?:\{[^\}]*\})|(?:~[^~]*~)|\s+)*", RegexOptions.CultureInvariant)]
    private static partial Regex PrefixRegex();

    [GeneratedRegex(@"(?:\s+ch.[\s\d-]+)?(?:(?:\([^\)]*\))|(?:\[[^\]]*\])|(?:\{[^\}]*\})|(?:~[^~]*~)|\s+)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SuffixRegex();
}
