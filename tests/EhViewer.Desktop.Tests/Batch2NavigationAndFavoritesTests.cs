using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Windows.Graphics;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class Batch2NavigationAndFavoritesTests
{
    [Fact]
    public void ShellViewModel_IncludesWhatsHotNavigationEntry()
    {
        var vm = new ShellViewModel();
        Assert.Contains(vm.Entries, static e => e.Kind == NavigationKind.WhatsHot && e.Label == "热门");
        Assert.Equal(NavigationKind.Home, vm.Entries[0].Kind);
    }

    [Fact]
    public void WindowPlacementHelper_ClampToWorkArea_KeepsWindowOnScreen()
    {
        var workArea = new RectInt32(0, 0, 1920, 1040);
        RectInt32 offScreen = WindowPlacementHelper.ClampToWorkArea(new RectInt32(-500, 2000, 3000, 400), workArea);

        Assert.Equal(0, offScreen.X);
        Assert.Equal(1040 - WindowPlacementHelper.MinHeight, offScreen.Y);
        Assert.Equal(1920, offScreen.Width);
        Assert.Equal(WindowPlacementHelper.MinHeight, offScreen.Height);
    }

    [Fact]
    public void BrowserTabState_PersistsAndRestoresBrowseAndDetailState()
    {
        var browseTab = new BrowserTabState
        {
            Title = "热门",
            NavKind = NavigationKind.WhatsHot,
            ListBuilder = new ListUrlBuilder { Mode = ListUrlBuilder.ModeWhatsHot, Keyword = "tag:test", PageIndex = 2 }
        };
        PersistedTabState persisted = browseTab.ToPersisted();
        BrowserTabState restored = BrowserTabState.FromPersisted(persisted);
        Assert.Equal(NavigationKind.WhatsHot, restored.NavKind);
        Assert.Equal(ListUrlBuilder.ModeWhatsHot, restored.ListBuilder.Mode);
        Assert.Equal("tag:test", restored.ListBuilder.Keyword);

        var detailTab = new BrowserTabState { Title = "Detail", DetailGid = 12345, DetailToken = "abc123def0" };
        BrowserTabState restoredDetail = BrowserTabState.FromPersisted(detailTab.ToPersisted());
        Assert.Equal(12345, restoredDetail.DetailGid);
        Assert.Equal("abc123def0", restoredDetail.DetailToken);
    }

    [Fact]
    public async Task FavoritesViewModel_SupportsLocalSlotSearchSortAndBatchOperations()
    {
        var g1 = new GalleryInfo { Gid = 101, Token = "aaa", Title = "Alpha" };
        var g2 = new GalleryInfo { Gid = 102, Token = "bbb", Title = "Beta" };
        FavoritesLoadRequest? lastRequest = null;
        string? appliedSort = null;
        int? batchTarget = null;
        var downloaded = new List<GalleryInfo>();

        var vm = new FavoritesViewModel(
            loadPage: (req, _) =>
            {
                lastRequest = req;
                return Task.FromResult(new FavoritesPageData(
                    Galleries: [g1, g2],
                    SlotNames: ["Fav0", "Fav1"],
                    SlotCounts: [2, 0],
                    ResultCount: "2 items",
                    HasMore: false,
                    RequiresSignIn: false,
                    LocalCount: 5));
            },
            batchModify: (_, items, dstCat, _) =>
            {
                batchTarget = dstCat;
                return Task.FromResult<FavoritesPageData?>(null);
            },
            batchDownload: items => downloaded.AddRange(items),
            applySortOrder: sort => appliedSort = sort,
            initialSlot: FavListUrlBuilder.FavCatLocal);

        vm.SearchKeyword = "Alpha";
        await vm.LoadFirstPageAsync();

        Assert.NotNull(lastRequest);
        Assert.Equal(FavListUrlBuilder.FavCatLocal, lastRequest!.Slot);
        Assert.Equal("Alpha", lastRequest.Keyword);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal("本地收藏 (5)", vm.Slots[0].Display);

        vm.SelectedSlot = 0;
        await vm.ChangeSortOrderAsync(EhConfig.OrderByPubTime);
        Assert.Equal(EhConfig.OrderByPubTime, appliedSort);

        vm.IsSelectionMode = true;
        vm.SelectAll();
        Assert.Equal(2, vm.SelectedCount);
        vm.ToggleItemSelection(vm.Items[1]);
        Assert.Equal(1, vm.SelectedCount);

        int dlCount = vm.DownloadSelected();
        Assert.Equal(1, dlCount);
        Assert.Single(downloaded);
        Assert.Equal(101, downloaded[0].Gid);

        vm.SelectAll();
        bool modified = await vm.DeleteSelectedAsync();
        Assert.True(modified);
        Assert.Equal(-1, batchTarget);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public void ListUrlBuilder_JumpHrefBuild_HandlesRootUrlWithoutQuestionMark()
    {
        Assert.Equal("https://e-hentai.org/?seek=2025-02-01",
            ListUrlBuilder.JumpHrefBuild("https://e-hentai.org/", "seek=2025-02-01"));
    }
}
