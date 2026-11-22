using EhViewer.Core.Client.Data;
using EhViewer.Desktop.Services;
using Microsoft.UI.Xaml.Controls;

namespace EhViewer.Desktop.ViewModels;

/// <summary>
/// Runtime state for a single workspace tab inside <see cref="MainWindow"/>, holding either a
/// gallery list view or a navigation stack of <see cref="Page"/> views.
/// </summary>
public sealed class BrowserTabState
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; } = "首页";
    public NavigationKind NavKind { get; set; } = NavigationKind.Home;
    public ListUrlBuilder ListBuilder { get; set; } = new() { Mode = ListUrlBuilder.ModeNormal };
    public List<GalleryItemViewModel> Items { get; set; } = [];
    public string? FirstHref { get; set; }
    public string? PrevHref { get; set; }
    public string? NextHref { get; set; }
    public string? LastHref { get; set; }
    public int PageIndex { get; set; }
    public int TotalPages { get; set; }
    public string? ResultCountText { get; set; }
    public string StatusText { get; set; } = "就绪";

    public Page? ActivePage { get; set; }
    public Stack<Page> PageStack { get; } = new();

    /// <summary>Optional detail target remembered for lightweight session restoration.</summary>
    public long DetailGid { get; set; }
    public string? DetailToken { get; set; }

    public PersistedTabState ToPersisted()
    {
        string kind = DetailGid > 0 && !string.IsNullOrEmpty(DetailToken)
            ? "Detail"
            : NavKind.ToString();

        return new PersistedTabState
        {
            Title = Title,
            Kind = kind,
            Keyword = ListBuilder.Keyword,
            Mode = ListBuilder.Mode,
            Category = ListBuilder.Category,
            PageIndex = PageIndex,
            Gid = DetailGid,
            Token = DetailToken,
            FavCat = FavListUrlBuilder.FavCatAll
        };
    }

    public static BrowserTabState FromPersisted(PersistedTabState persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);

        NavigationKind navKind = Enum.TryParse(persisted.Kind, out NavigationKind parsed)
            ? parsed
            : NavigationKind.Home;

        return new BrowserTabState
        {
            Title = string.IsNullOrWhiteSpace(persisted.Title) ? "首页" : persisted.Title,
            NavKind = navKind,
            DetailGid = string.Equals(persisted.Kind, "Detail", StringComparison.Ordinal) ? persisted.Gid : 0,
            DetailToken = string.Equals(persisted.Kind, "Detail", StringComparison.Ordinal) ? persisted.Token : null,
            ListBuilder = new ListUrlBuilder
            {
                Mode = persisted.Mode,
                Category = persisted.Category,
                Keyword = persisted.Keyword,
                PageIndex = Math.Max(0, persisted.PageIndex)
            }
        };
    }
}
