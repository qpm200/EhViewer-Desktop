using System.Text.RegularExpressions;
using EhViewer.Core.Util;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses gallery detail/mpv URLs (<c>/g/{gid}/{token}</c> or <c>/mpv/{gid}/{token}</c>).
/// </summary>
public static partial class GalleryDetailUrlParser
{
    public sealed record Result(long Gid, string Token);

    private static readonly Regex StrictPattern = CreateStrictPattern();
    private static readonly Regex LoosePattern = CreateLoosePattern();

    public static Result? Parse(string? url, bool strict = true)
    {
        if (url is null)
        {
            return null;
        }

        Regex regex = strict ? StrictPattern : LoosePattern;
        Match match = regex.Match(url);
        if (!match.Success)
        {
            return null;
        }

        long gid = NumberUtils.ParseLongSafely(match.Groups[1].Value, -1L);
        if (gid < 0)
        {
            return null;
        }

        return new Result(gid, match.Groups[2].Value);
    }

    [GeneratedRegex(
        @"https?://(?:exhentai\.org|e-hentai\.org|lofi\.e-hentai\.org)/(?:g|mpv)/(\d+)/([0-9a-f]{10})",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateStrictPattern();

    [GeneratedRegex(
        @"(\d+)/([0-9a-f]{10})(?:[^0-9a-f]|$)",
        RegexOptions.CultureInvariant)]
    private static partial Regex CreateLoosePattern();
}
