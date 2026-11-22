using System.Text.RegularExpressions;
using EhViewer.Core.Client.Exceptions;
using EhViewer.Core.Util;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses HTML viewer page (<c>/s/{ptoken}/{gid}-{page}</c>) image URL, H@H skip key, and showkey.
/// </summary>
public static partial class GalleryPageParser
{
    public sealed record Result(string ImageUrl, string? SkipHathKey, string? OriginImageUrl, string ShowKey);

    internal static readonly Regex PatternImageUrl = CreateImageUrlPattern();
    internal static readonly Regex PatternSkipHathKey = CreateSkipHathKeyPattern();
    internal static readonly Regex PatternOriginImageUrl = CreateOriginImageUrlPattern();
    private static readonly Regex PatternShowKey = CreateShowKeyPattern();

    public static Result Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        (string? imageUrl, string? skipHathKey, string? originImageUrl) = ExtractCommonImageFields(body, body, body);

        Match showKeyMatch = PatternShowKey.Match(body);
        string? showKey = showKeyMatch.Success ? showKeyMatch.Groups[1].Value : null;

        if (!string.IsNullOrEmpty(imageUrl) && !string.IsNullOrEmpty(showKey))
        {
            return new Result(imageUrl, skipHathKey, originImageUrl, showKey);
        }

        throw new ParseException("Parse image url and show error", body);
    }

    internal static (string? ImageUrl, string? SkipHathKey, string? OriginImageUrl) ExtractCommonImageFields(
        string imageSection,
        string infoSection,
        string originSection)
    {
        string? imageUrl = null;
        Match m = PatternImageUrl.Match(imageSection);
        if (m.Success)
        {
            imageUrl = StringUtils.UnescapeXml(StringUtils.Trim(m.Groups[1].Value));
        }

        string? skipHathKey = null;
        m = PatternSkipHathKey.Match(infoSection);
        if (m.Success)
        {
            skipHathKey = StringUtils.UnescapeXml(StringUtils.Trim(m.Groups[1].Value));
        }

        string? originImageUrl = null;
        m = PatternOriginImageUrl.Match(originSection);
        if (m.Success)
        {
            originImageUrl = StringUtils.UnescapeXml(m.Groups[1].Value)
                + "fullimg"
                + StringUtils.UnescapeXml(m.Groups[2].Value);
        }

        return (imageUrl, skipHathKey, originImageUrl);
    }

    [GeneratedRegex(@"<img[^>]*src=""([^""]+)"" style", RegexOptions.CultureInvariant)]
    private static partial Regex CreateImageUrlPattern();

    [GeneratedRegex(@"onclick=""return nl\('([^\)]+)'\)", RegexOptions.CultureInvariant)]
    private static partial Regex CreateSkipHathKeyPattern();

    [GeneratedRegex(@"<a href=""([^""]+)fullimg([^""]+)"">", RegexOptions.CultureInvariant)]
    private static partial Regex CreateOriginImageUrlPattern();

    [GeneratedRegex(@"var showkey=""([0-9a-z]+)"";", RegexOptions.CultureInvariant)]
    private static partial Regex CreateShowKeyPattern();
}
