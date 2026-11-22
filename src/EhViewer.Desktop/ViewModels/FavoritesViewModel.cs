using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;

namespace EhViewer.Desktop.ViewModels;

/// <summary>Parameters passed to the favorites page loader callback.</summary>
public sealed record FavoritesLoadRequest(
    int Slot,
    int PageIndex,
    string? Keyword = null,
    string? ExplicitHref = null);

/// <summary>
/// Favorites list view model supporting local favorites (<c>-2</c>), all cloud favorites (<c>-1</c>),
/// the ten cloud slots (<c>0..9</c>), keyword search, sort toggle (<c>f</c>/<c>p</c>), and batch
/// selection/move/delete/download operations.
/// </summary>
public sealed partial class FavoritesViewModel : ObservableObject
{
    private readonly Func<FavoritesLoadRequest, CancellationToken, Task<FavoritesPageData>> _loadPage;
    private readonly Func<int, IReadOnlyList<GalleryInfo>, int, CancellationToken, Task<FavoritesPageData?>>? _batchModify;
    private readonly Action<IReadOnlyList<GalleryInfo>>? _batchDownload;
    private readonly Action<string>? _applySortOrder;

    public FavoritesViewModel(Func<int, int, CancellationToken, Task<FavoritesPageData>> loadPage)
        : this(
            (req, ct) => loadPage(req.Slot, req.PageIndex, ct),
            batchModify: null,
            batchDownload: null,
            applySortOrder: null)
    {
        ArgumentNullException.ThrowIfNull(loadPage);
    }

    public FavoritesViewModel(
        Func<FavoritesLoadRequest, CancellationToken, Task<FavoritesPageData>> loadPage,
        Func<int, IReadOnlyList<GalleryInfo>, int, CancellationToken, Task<FavoritesPageData?>>? batchModify = null,
        Action<IReadOnlyList<GalleryInfo>>? batchDownload = null,
        Action<string>? applySortOrder = null,
        int initialSlot = FavListUrlBuilder.FavCatAll,
        string initialSortOrder = EhConfig.FavoritesSortFavoritedTime)
    {
        _loadPage = loadPage ?? throw new ArgumentNullException(nameof(loadPage));
        _batchModify = batchModify;
        _batchDownload = batchDownload;
        _applySortOrder = applySortOrder;
        SelectedSlot = initialSlot;
        SortOrder = string.IsNullOrWhiteSpace(initialSortOrder)
            ? EhConfig.FavoritesSortFavoritedTime
            : initialSortOrder;

        InitializeDefaultSlots();
    }

    [ObservableProperty]
    public partial int SelectedSlot { get; set; }

    [ObservableProperty]
    public partial string SearchKeyword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SortOrder { get; set; } = EhConfig.FavoritesSortFavoritedTime;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string InfoMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial bool IsSignedInRequired { get; set; }

    [ObservableProperty]
    public partial string ResultCountText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSelectionMode { get; set; }

    partial void OnIsSelectionModeChanged(bool value)
    {
        foreach (GalleryItemViewModel item in Items)
        {
            item.IsSelectionVisible = value;
        }
    }

    [ObservableProperty]
    public partial int SelectedCount { get; set; }

    [ObservableProperty]
    public partial bool HasMore { get; set; } = true;

    public ObservableCollection<FavoriteSlot> Slots { get; } = [];

    public ObservableCollection<GalleryItemViewModel> Items { get; } = [];

    public int PageIndex { get; private set; }

    public string? PrevHref { get; private set; }
    public string? NextHref { get; private set; }
    public bool CanGoPrev => !string.IsNullOrEmpty(PrevHref);

    public IReadOnlyList<GalleryInfo> GetSelectedGalleries() =>
        [.. Items.Where(static i => i.IsSelected).Select(static i => i.Info)];

    public void ToggleItemSelection(GalleryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.IsSelected = !item.IsSelected;
        RecountSelected();
    }

    public void SelectAll()
    {
        foreach (GalleryItemViewModel item in Items)
        {
            item.IsSelected = true;
        }
        RecountSelected();
    }

    public void InvertSelection()
    {
        foreach (GalleryItemViewModel item in Items)
        {
            item.IsSelected = !item.IsSelected;
        }
        RecountSelected();
    }

    public void ClearSelection()
    {
        foreach (GalleryItemViewModel item in Items)
        {
            item.IsSelected = false;
        }
        RecountSelected();
    }

    public void RecountSelected()
    {
        SelectedCount = Items.Count(static i => i.IsSelected);
    }

    /// <summary>Updates favorites sort order (<c>f</c> or <c>p</c>) and notifies persistence hook.</summary>
    public Task ChangeSortOrderAsync(string sortOrder, CancellationToken cancellationToken = default)
    {
        SortOrder = string.Equals(sortOrder, EhConfig.OrderByPubTime, StringComparison.Ordinal)
            ? EhConfig.OrderByPubTime
            : EhConfig.FavoritesSortFavoritedTime;
        _applySortOrder?.Invoke(SortOrder);
        return SelectedSlot == FavListUrlBuilder.FavCatLocal
            ? Task.CompletedTask
            : LoadFirstPageAsync(cancellationToken);
    }

    /// <summary>Rebuilds slot pills while keeping local (-2) and all-cloud (-1) headers first.</summary>
    public void ApplySlotCounts(IReadOnlyList<string> names, IReadOnlyList<int> counts, int? localCount = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(counts);

        Slots.Clear();
        Slots.Add(new FavoriteSlot(FavListUrlBuilder.FavCatLocal, "本地收藏", localCount ?? -1));
        int totalCloud = counts.Count > 0 ? counts.Sum() : -1;
        Slots.Add(new FavoriteSlot(FavListUrlBuilder.FavCatAll, "全部云端", totalCloud));

        int slotCount = Math.Min(names.Count, counts.Count);
        for (int i = 0; i < slotCount; i++)
        {
            Slots.Add(new FavoriteSlot(i, names[i], counts[i]));
        }
    }

    public Task LoadFirstPageAsync(CancellationToken cancellationToken = default)
        => LoadAsync(reset: true, explicitHref: null, cancellationToken);

    public Task LoadNextPageAsync(CancellationToken cancellationToken = default)
        => HasMore ? LoadAsync(reset: false, explicitHref: NextHref, cancellationToken) : Task.CompletedTask;

    public Task LoadPrevPageAsync(CancellationToken cancellationToken = default)
        => !string.IsNullOrEmpty(PrevHref) ? LoadAsync(reset: true, explicitHref: PrevHref, cancellationToken) : Task.CompletedTask;

    public async Task<bool> DeleteSelectedAsync(CancellationToken cancellationToken = default)
        => await ModifySelectedAsync(dstCat: -1, cancellationToken).ConfigureAwait(true);

    public async Task<bool> MoveSelectedAsync(int dstCat, CancellationToken cancellationToken = default)
        => await ModifySelectedAsync(dstCat, cancellationToken).ConfigureAwait(true);

    public int DownloadSelected()
    {
        IReadOnlyList<GalleryInfo> selected = GetSelectedGalleries();
        if (selected.Count == 0 || _batchDownload is null)
        {
            return 0;
        }

        _batchDownload(selected);
        InfoMessage = $"已将 {selected.Count} 个收藏加入下载队列。";
        ClearSelection();
        return selected.Count;
    }

    private async Task<bool> ModifySelectedAsync(int dstCat, CancellationToken cancellationToken)
    {
        if (_batchModify is null)
        {
            return false;
        }

        IReadOnlyList<GalleryInfo> selected = GetSelectedGalleries();
        if (selected.Count == 0)
        {
            return false;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;

        try
        {
            FavoritesPageData? updated = await _batchModify(SelectedSlot, selected, dstCat, cancellationToken)
                .ConfigureAwait(true);
            if (updated is not null)
            {
                ApplyPageData(updated, reset: true);
            }
            else
            {
                var removeSet = selected.Select(static g => g.Gid).ToHashSet();
                for (int i = Items.Count - 1; i >= 0; i--)
                {
                    if (removeSet.Contains(Items[i].Gid))
                    {
                        Items.RemoveAt(i);
                    }
                }
            }

            ClearSelection();
            InfoMessage = dstCat < 0 ? $"已移除 {selected.Count} 个收藏。" : $"已移动 {selected.Count} 个收藏。";
            return true;
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "已取消";
            return false;
        }
        catch (Exception ex)
        {
            ErrorMessage = Services.GalleryBrowseService.DescribeFailure(ex);
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void InitializeDefaultSlots()
    {
        Slots.Clear();
        Slots.Add(new FavoriteSlot(FavListUrlBuilder.FavCatLocal, "本地收藏", -1));
        Slots.Add(new FavoriteSlot(FavListUrlBuilder.FavCatAll, "全部云端", -1));
        for (int i = 0; i < 10; i++)
        {
            Slots.Add(new FavoriteSlot(i, $"Favorites {i}", -1));
        }
    }

    private async Task LoadAsync(bool reset, string? explicitHref, CancellationToken cancellationToken)
    {
        if (IsLoading)
        {
            return;
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        InfoMessage = string.Empty;

        try
        {
            string? keyword = string.IsNullOrWhiteSpace(SearchKeyword) ? null : SearchKeyword.Trim();
            var request = new FavoritesLoadRequest(SelectedSlot, reset ? 0 : PageIndex, keyword, explicitHref);
            FavoritesPageData page = await _loadPage(request, cancellationToken).ConfigureAwait(true);
            ApplyPageData(page, reset);
        }
        catch (OperationCanceledException)
        {
            ErrorMessage = "已取消";
        }
        catch (Exception ex)
        {
            ErrorMessage = Services.GalleryBrowseService.DescribeFailure(ex);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyPageData(FavoritesPageData page, bool reset)
    {
        if (reset)
        {
            Items.Clear();
            PageIndex = 0;
            if (page.SlotNames.Count > 0 && page.SlotCounts.Count > 0)
            {
                ApplySlotCounts(page.SlotNames, page.SlotCounts, page.LocalCount);
            }
            else if (page.LocalCount.HasValue && Slots.Count > 0 && Slots[0].Index == FavListUrlBuilder.FavCatLocal)
            {
                Slots[0] = Slots[0] with { Count = page.LocalCount.Value };
            }
        }

        var existing = new HashSet<long>(Items.Select(static i => i.Gid));
        foreach (GalleryInfo info in page.Galleries)
        {
            if (existing.Add(info.Gid))
            {
                Items.Add(new GalleryItemViewModel(info, static _ => false)
                {
                    IsSelectionVisible = IsSelectionMode
                });
            }
        }

        ResultCountText = page.ResultCount ?? string.Empty;
        HasMore = page.HasMore;
        PrevHref = page.PrevHref;
        NextHref = page.NextHref;
        OnPropertyChanged(nameof(CanGoPrev));
        PageIndex++;
        IsSignedInRequired = page.RequiresSignIn;
        RecountSelected();
    }
}

/// <summary>One favourites category slot (local <c>-2</c>, all cloud <c>-1</c>, or <c>0..9</c>).</summary>
public sealed record FavoriteSlot(int Index, string Name, int Count)
{
    public string Display => Count >= 0 ? $"{Name} ({Count})" : Name;
}

/// <summary>One page of favourites.</summary>
public sealed record FavoritesPageData(
    IReadOnlyList<GalleryInfo> Galleries,
    IReadOnlyList<string> SlotNames,
    IReadOnlyList<int> SlotCounts,
    string? ResultCount,
    bool HasMore,
    bool RequiresSignIn,
    string? PrevHref = null,
    string? NextHref = null,
    int? LocalCount = null);
