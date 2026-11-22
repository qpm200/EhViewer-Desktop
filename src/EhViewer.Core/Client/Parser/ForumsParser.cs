using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using EhViewer.Core.Client.Exceptions;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Extracts the current signed-in user's profile link from forums index HTML.
/// </summary>
public static class ForumsParser
{
    public static string Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        IHtmlDocument document = EhHtmlHelper.ParseDocument(body);
        IElement? userLinks = document.GetElementById("userlinks");
        IElement? anchor = EhHtmlHelper.GetChildElement(
            EhHtmlHelper.GetChildElement(
                EhHtmlHelper.GetChildElement(userLinks, 0), 0), 0);
        string? href = anchor?.GetAttribute("href");
        if (href is null)
        {
            throw new ParseException("Parse forums error", body);
        }

        return href;
    }
}
