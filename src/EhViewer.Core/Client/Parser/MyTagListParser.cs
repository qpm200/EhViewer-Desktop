using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using EhViewer.Core.Client.Data.UserTag;
using EhViewer.Core.Util;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses user watched/hidden tag rules from <c>mytags</c> HTML (upstream <c>MyTagLitParser</c>).
/// </summary>
public static class MyTagListParser
{
    public static UserTagList Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        EhHtmlHelper.ThrowIfSiteError(body);

        var list = new UserTagList();
        IHtmlDocument document = EhHtmlHelper.ParseDocument(body);
        IElement? outer = document.GetElementById("usertags_outer");
        if (outer is null)
        {
            return list;
        }

        IHtmlCollection<IElement> tags = outer.Children;
        // Row 0 is the "Add New Tag" input row.
        for (int i = 1; i < tags.Length; i++)
        {
            UserTag? parsed = ParseUserTag(document, tags[i]);
            if (parsed is not null)
            {
                list.UserTags.Add(parsed);
            }
        }

        return list;
    }

    private static UserTag? ParseUserTag(IHtmlDocument document, IElement tagRow)
    {
        string rowId = tagRow.Id ?? string.Empty;
        if (rowId.Length <= 7)
        {
            return null;
        }

        string id = rowId[7..];
        var userTag = new UserTag
        {
            UserTagId = rowId
        };

        string? tagName = document.GetElementById("tagpreview" + id)?.GetAttribute("title");
        if (tagName is not null)
        {
            userTag.TagName = tagName;
        }

        IElement? watchInput = document.GetElementById("tagwatch" + id);
        IElement? hideInput = document.GetElementById("taghide" + id);
        userTag.Watched = watchInput?.GetAttribute("checked") == "checked";
        userTag.Hidden = hideInput?.GetAttribute("checked") == "checked";

        userTag.Color = document.GetElementById("tagcolor" + id)?.GetAttribute("placeholder");

        string? weightStr = document.GetElementById("tagweight" + id)?.GetAttribute("value");
        userTag.TagWeight = string.IsNullOrEmpty(weightStr) ? 0 : NumberUtils.ParseIntSafely(weightStr, 0);

        return userTag;
    }
}
