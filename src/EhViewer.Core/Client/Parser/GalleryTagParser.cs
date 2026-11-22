using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using EhViewer.Core.Client.Data;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Shared DOM tag-group extraction helper used by both <see cref="GalleryListParser"/> and <see cref="GalleryDetailParser"/>.
/// </summary>
public static class GalleryTagParser
{
    public static GalleryTagGroup[] ParseTagGroups(IHtmlDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        IElement? tagList = document.GetElementById("taglist");
        IElement? tbody = EhHtmlHelper.GetChildElement(EhHtmlHelper.GetChildElement(tagList, 0), 0);
        return tbody is null ? [] : ParseTagGroups(tbody.Children);
    }

    public static GalleryTagGroup[] ParseTagGroups(IHtmlCollection<IElement> trs)
    {
        ArgumentNullException.ThrowIfNull(trs);

        var list = new List<GalleryTagGroup>(trs.Length);
        for (int i = 0; i < trs.Length; i++)
        {
            GalleryTagGroup? group = ParseTagGroup(trs[i]);
            if (group is not null)
            {
                list.Add(group);
            }
        }
        return [.. list];
    }

    private static GalleryTagGroup? ParseTagGroup(IElement tr)
    {
        if (tr.Children.Length < 2)
        {
            return null;
        }

        string nameSpace = EhHtmlHelper.GetNormalizedText(tr.Children[0]);
        if (nameSpace.Length == 0)
        {
            return null;
        }

        if (nameSpace.EndsWith(':'))
        {
            nameSpace = nameSpace[..^1];
        }

        var group = new GalleryTagGroup
        {
            GroupName = nameSpace
        };

        IHtmlCollection<IElement> tags = tr.Children[1].Children;
        for (int i = 0; i < tags.Length; i++)
        {
            string tag = EhHtmlHelper.GetNormalizedText(tags[i]);
            // Parody/translated tags may end with " | english"; keep only the primary tag name.
            int pipeIndex = tag.IndexOf('|');
            if (pipeIndex >= 0)
            {
                tag = tag[..pipeIndex].Trim();
            }
            group.AddTag(tag);
        }

        return group.Size > 0 ? group : null;
    }
}
