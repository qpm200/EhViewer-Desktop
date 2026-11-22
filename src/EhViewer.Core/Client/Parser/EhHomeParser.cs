using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Util;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses personal quota, GP breakdown, and moderation power from <c>home.php</c>.
/// </summary>
public static partial class EhHomeParser
{
    private const string HomeBoxClass = "homebox";

    private static readonly Regex PatternImageLimit = CreateImageLimitPattern();
    private static readonly Regex PatternImageLimitNew = CreateImageLimitNewPattern();
    private static readonly Regex PatternImageLimitNoReset = CreateImageLimitNoResetPattern();

    public static HomeDetail Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var homeDetail = new HomeDetail();
        IHtmlDocument document = EhHtmlHelper.ParseDocument(body);
        IHtmlCollection<IElement> homeBoxes = document.GetElementsByClassName(HomeBoxClass);
        if (homeBoxes.Length == 0)
        {
            return homeDetail;
        }

        ParseImageLimits(homeBoxes[0], homeDetail);

        if (homeBoxes.Length > 2)
        {
            ParseTotalGpGained(homeBoxes[2], homeDetail);
        }

        if (homeBoxes.Length > 4)
        {
            ParseModerationPower(homeBoxes[4], homeDetail);
        }

        return homeDetail;
    }

    private static void ParseModerationPower(IElement moderationPower, HomeDetail homeDetail)
    {
        IElement? row = EhHtmlHelper.GetChildElement(
            EhHtmlHelper.GetChildElement(
                EhHtmlHelper.GetChildElement(
                    EhHtmlHelper.GetChildElement(moderationPower, 0), 0), 0), 0);
        IElement? powerDiv = EhHtmlHelper.GetChildElement(row, 1);
        if (powerDiv is null)
        {
            return;
        }

        string text = EhHtmlHelper.GetNormalizedText(powerDiv).Replace(",", string.Empty, StringComparison.Ordinal);
        homeDetail.CurrentModerationPowerValue = NumberUtils.ParseLongSafely(text, -1L);
    }

    private static void ParseTotalGpGained(IElement totalGpGained, HomeDetail homeDetail)
    {
        IElement? tbody = EhHtmlHelper.GetChildElement(EhHtmlHelper.GetChildElement(totalGpGained, 0), 0);
        if (tbody is null || tbody.Children.Length < 4)
        {
            return;
        }

        homeDetail.FromGalleryVisitsValue = ParseCellLong(tbody.Children[0]);
        homeDetail.FromTorrentCompletionsValue = ParseCellLong(tbody.Children[1]);
        homeDetail.FromArchiveDownloadsValue = ParseCellLong(tbody.Children[2]);
        homeDetail.FromHentaiAtHomeValue = ParseCellLong(tbody.Children[3]);
    }

    private static long ParseCellLong(IElement tr)
    {
        IElement? td = EhHtmlHelper.GetChildElement(tr, 0);
        if (td is null)
        {
            return -1L;
        }

        string text = EhHtmlHelper.GetNormalizedText(td).Replace(",", string.Empty, StringComparison.Ordinal);
        return NumberUtils.ParseLongSafely(text, -1L);
    }

    private static void ParseImageLimits(IElement imageLimits, HomeDetail homeDetail)
    {
        string html = imageLimits.InnerHtml;
        Match matcher = PatternImageLimitNew.Match(html);
        if (!matcher.Success)
        {
            matcher = PatternImageLimit.Match(html);
            if (!matcher.Success)
            {
                Match noReset = PatternImageLimitNoReset.Match(html);
                if (!noReset.Success)
                {
                    return;
                }

                homeDetail.Used = NumberUtils.ParseLongSafely(StripCommas(noReset.Groups[1]), -1L);
                homeDetail.Total = NumberUtils.ParseLongSafely(StripCommas(noReset.Groups[2]), -1L);
                homeDetail.ResetCostValue = 0;
                return;
            }
        }

        homeDetail.Used = NumberUtils.ParseLongSafely(StripCommas(matcher.Groups[1]), -1L);
        homeDetail.Total = NumberUtils.ParseLongSafely(StripCommas(matcher.Groups[2]), -1L);
        homeDetail.ResetCostValue = NumberUtils.ParseLongSafely(StripCommas(matcher.Groups[3]), -1L);
    }

    private static string StripCommas(Group group) =>
        group.Success ? group.Value.Replace(",", string.Empty, StringComparison.Ordinal).Trim() : "0";

    [GeneratedRegex(
        @"<p>You are currently at\s*<strong>(\d+)</strong>\s*towards a limit of\s*<strong>(\d+)</strong>\.</p>.+?<p>Reset Cost:\s*<strong>(\d+)</strong>\s*GP</p>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CreateImageLimitPattern();

    [GeneratedRegex(
        @"<p>You are currently at\s*<strong>(.+?)</strong>\s*towards your account limit of\s*<strong>(.+?)</strong>\.</p>\s*<p>You can reset your image quota by spending\s*<strong>(.+?)</strong>\s*GP\.</p>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CreateImageLimitNewPattern();

    [GeneratedRegex(
        @"<p>You are currently at\s*<strong>(.+?)</strong>\s*towards (?:a|your account) limit of\s*<strong>(.+?)</strong>\.</p>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CreateImageLimitNoResetPattern();
}
