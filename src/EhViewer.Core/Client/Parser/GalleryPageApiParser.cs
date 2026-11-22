using System.Text.Json;
using System.Text.RegularExpressions;
using EhViewer.Core.Client.Exceptions;
using EhViewer.Core.Util;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses <c>showpage</c> JSON API responses for next-image URLs.
/// </summary>
public static partial class GalleryPageApiParser
{
    public sealed record Result(string ImageUrl, string? OtherImageUrl, string? SkipHathKey, string? OriginImageUrl);

    private static readonly Regex PatternOriginImageUrlNew = CreateOriginImageUrlNewPattern();

    public static Result Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        try
        {
            JsonElement root = ParserUtils.ParseJsonObject(body);
            if (root.TryGetProperty("error", out _))
            {
                throw new ParseException(ParserUtils.GetRequiredString(root, "error"), body);
            }

            string i3 = ParserUtils.GetRequiredString(root, "i3");
            string i6 = ParserUtils.GetRequiredString(root, "i6");
            string originSource = root.TryGetProperty("i7", out JsonElement i7El) && i7El.ValueKind != JsonValueKind.Null
                ? ParserUtils.GetRequiredString(root, "i7")
                : i6;

            (string? imageUrl, string? skipHathKey, string? originImageUrl) =
                GalleryPageParser.ExtractCommonImageFields(i3, i6, originSource);

            string? otherImageUrl = null;
            Match newOriginMatch = PatternOriginImageUrlNew.Match(i6);
            if (newOriginMatch.Success)
            {
                otherImageUrl = StringUtils.UnescapeXml(newOriginMatch.Groups[1].Value);
            }

            if (!string.IsNullOrEmpty(imageUrl))
            {
                return new Result(imageUrl, otherImageUrl, skipHathKey, originImageUrl);
            }

            throw new ParseException("Parse image url and skip hath key error", body);
        }
        catch (JsonException ex)
        {
            throw new ParseException("Can't parse json", body, ex);
        }
    }

    [GeneratedRegex(@"<a href=""#"" onclick=""prompt\('Copy the URL below.', '([^""]+)'\)", RegexOptions.CultureInvariant)]
    private static partial Regex CreateOriginImageUrlNewPattern();
}
