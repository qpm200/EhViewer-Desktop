using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using EhViewer.Core.Client.Parser;

namespace EhViewer.Core.Client.Data;

public sealed class EhNewsDetail
{
    public EhNewsDetail()
    {
    }

    public EhNewsDetail(string? webData)
    {
        WebData = webData;
    }

    public string? WebData { get; set; }
    public string? EventPane { get; set; }

    public string? GetEventPane()
    {
        if (EventPane is not null)
        {
            return EventPane;
        }

        if (string.IsNullOrEmpty(WebData))
        {
            return null;
        }

        IHtmlDocument document = EhHtmlHelper.ParseDocument(WebData);
        IElement? eventPane = document.GetElementById("eventpane");
        if (eventPane is not null && eventPane.ChildElementCount == 3)
        {
            EventPane = eventPane.InnerHtml;
        }

        return EventPane;
    }
}
