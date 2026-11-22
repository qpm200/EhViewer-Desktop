using System.Text.RegularExpressions;
using EhViewer.Core.Util;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses gallery page URLs (e.g. <c>https://exhentai.org/s/91ea4b6d89/901103-12</c>).
/// </summary>
public static partial class GalleryPageUrlParser
{
    public sealed record Result(long Gid, string PToken, int Page);

    private static readonly Regex StrictPattern = CreateStrictPattern();
    private static readonly Regex LoosePattern = CreateLoosePattern();

    public static Result? Parse(string? url, bool strict = true)
    {
        if (url is null)
        {
            return null;
        }

        Regex pattern = strict ? StrictPattern : LoosePattern;
        Match match = pattern.Match(url);
        if (!match.Success)
        {
            return null;
        }

        long gid = NumberUtils.ParseLongSafely(match.Groups[2].Value, -1L);
        string pToken = match.Groups[1].Value;
        int page = NumberUtils.ParseIntSafely(match.Groups[3].Value, 0) - 1;
        if (gid < 0 || page < 0)
        {
            return null;
        }

        return new Result(gid, pToken, page);
    }

    [GeneratedRegex(
        @"https?://(?:exhentai\.org|e-hentai\.org|lofi\.e-hentai\.org)/s/([0-9a-f]{10})/(\d+)-(\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateStrictPattern();

    [GeneratedRegex(
        @"([0-9a-f]{10})/(\d+)-(\d+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateLoosePattern();
}
