using AngleSharp.Dom;
using AngleSharp.Html.Dom;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses dawn-of-a-new-day event HTML from <c>news.php</c> (upstream <c>EhEventParse</c>).
/// </summary>
public static class EhEventParser
{
    public static string? Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        IHtmlDocument document = EhHtmlHelper.ParseDocument(body);
        IElement? eventPane = document.GetElementById("eventpane");
        return eventPane?.InnerHtml;
    }
}
