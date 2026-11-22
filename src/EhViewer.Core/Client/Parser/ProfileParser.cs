using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using EhViewer.Core.Client.Exceptions;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Extracts display name and optional avatar URL from forums user profile HTML.
/// </summary>
public static class ProfileParser
{
    public sealed record Result(string? DisplayName, string? Avatar);

    public static Result Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        IHtmlDocument document = EhHtmlHelper.ParseDocument(body);
        IElement? profileName = document.GetElementById("profilename");
        IElement? nameElement = EhHtmlHelper.GetChildElement(profileName, 0);
        if (nameElement is null)
        {
            throw new ParseException("Parse forums error", body);
        }

        string displayName = EhHtmlHelper.GetNormalizedText(nameElement);
        string? avatar = ExtractAvatarUrl(profileName);
        return new Result(displayName, avatar);
    }

    public static Result ParseNew(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        string htmlString = body
            .Replace(@"\u003C", "<", StringComparison.Ordinal)
            .Replace(@"\n", string.Empty, StringComparison.Ordinal)
            .Replace(@"\""", string.Empty, StringComparison.Ordinal)
            .Replace(@"\t", string.Empty, StringComparison.Ordinal);

        if (htmlString.Contains("ERR_CONNECTION_REFUSED", StringComparison.Ordinal))
        {
            return new Result(null, null);
        }

        IHtmlDocument document = EhHtmlHelper.ParseDocument(htmlString);
        IElement? userLinks = document.GetElementById("userlinks");
        IElement? nameElement = EhHtmlHelper.GetChildElement(
            EhHtmlHelper.GetChildElement(
                EhHtmlHelper.GetChildElement(userLinks, 0), 0), 0);
        if (nameElement is null)
        {
            throw new ParseException("Parse forums error", body);
        }

        string displayName = EhHtmlHelper.GetNormalizedText(nameElement);
        string? avatar = ExtractAvatarUrl(userLinks);
        return new Result(displayName, avatar);
    }

    private static string? ExtractAvatarUrl(IElement? anchorSiblingBase)
    {
        IElement? img = EhHtmlHelper.GetChildElement(
            anchorSiblingBase?.NextElementSibling?.NextElementSibling, 0);
        string? src = img?.GetAttribute("src");
        if (string.IsNullOrEmpty(src))
        {
            return null;
        }

        return src.StartsWith("http", StringComparison.Ordinal)
            ? src
            : EhUrl.UrlForums + src;
    }
}
