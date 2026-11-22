using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EhViewer.Core.Client.Data;

namespace EhViewer.Desktop.ViewModels;

/// <summary>
/// Row view model for a gallery in a list. Wraps a <see cref="GalleryInfo"/> and exposes the
/// display strings and colour the list template binds to.
/// </summary>
public sealed partial class GalleryItemViewModel : ObservableObject
{
    private readonly Func<GalleryInfo, bool> _showJpnTitle;

    public GalleryItemViewModel(GalleryInfo info, Func<GalleryInfo, bool> showJpnTitle)
    {
        Info = info ?? throw new ArgumentNullException(nameof(info));
        _showJpnTitle = showJpnTitle ?? throw new ArgumentNullException(nameof(showJpnTitle));
    }

    public GalleryInfo Info { get; }

    public long Gid => Info.Gid;

    public string Title => EhViewer.Core.Client.EhUtils.GetSuitableTitle(Info, _showJpnTitle(Info)) ?? string.Empty;

    public string CategoryName => EhViewer.Core.Client.EhUtils.GetCategory(Info.Category);

    public int CategoryColor => EhViewer.Core.Client.EhUtils.GetCategoryColor(Info.Category);

    public string Posted => Info.Posted ?? string.Empty;

    public string Uploader => Info.Uploader ?? string.Empty;

    public string RatingText => Info.Rating > 0 ? Info.Rating.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : string.Empty;

    public string ThumbUrl => Info.Thumb ?? string.Empty;

    public bool HasThumb => !string.IsNullOrEmpty(Info.Thumb);

    public string PagesText => Info.Pages > 0 ? $"{Info.Pages} 页" : string.Empty;

    public string ShortPagesText => Info.Pages > 0 ? $"{Info.Pages}P" : string.Empty;

    public float Rating => Info.Rating;

    public string SimpleLanguage => string.IsNullOrWhiteSpace(Info.SimpleLanguage) ? string.Empty : Info.SimpleLanguage!.ToUpperInvariant();

    public bool HasSimpleLanguage => !string.IsNullOrWhiteSpace(Info.SimpleLanguage);

    public bool IsFavorited => Info.FavoriteSlot != -2;

    public string FavoriteBadgeText => Info.FavoriteSlot switch
    {
        -1 => "本地",
        >= 0 => !string.IsNullOrWhiteSpace(Info.FavoriteName) ? Info.FavoriteName! : $"#{Info.FavoriteSlot}",
        _ => string.Empty
    };

    [ObservableProperty]
    public partial bool IsDownloaded { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsSelectionVisible { get; set; }
}

/// <summary>
/// Paged gallery list view model. Appends pages, de-duplicates by gid (upstream
/// <c>ContentHelper</c> semantics), and keeps the next-page url returned by the site.
/// </summary>
public sealed partial class GalleryListViewModel : ObservableObject
{
    private readonly Func<PageRequest, CancellationToken, Task<PageResult>> _loadPage;
    private readonly Func<GalleryInfo, bool> _showJpnTitle;
    private readonly HashSet<long> _seenGids = [];
    private readonly object _gate = new();

    private bool _loading;

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    public GalleryListViewModel(
        Func<PageRequest, CancellationToken, Task<PageResult>> loadPage,
        Func<GalleryInfo, bool> showJpnTitle)
    {
        _loadPage = loadPage ?? throw new ArgumentNullException(nameof(loadPage));
        _showJpnTitle = showJpnTitle ?? throw new ArgumentNullException(nameof(showJpnTitle));
    }

    public ObservableCollection<GalleryItemViewModel> Items { get; } = [];

    public bool IsLoading => _loading;

    public bool HasMore { get; private set; } = true;

    public int PageIndex { get; private set; }

    public string? ResultCount { get; private set; }

    public Task LoadFirstPageAsync(CancellationToken cancellationToken = default)
        => LoadPageAsync(reset: true, cancellationToken);

    public Task LoadNextPageAsync(CancellationToken cancellationToken = default)
    {
        if (!HasMore)
        {
            return Task.CompletedTask;
        }

        return LoadPageAsync(reset: false, cancellationToken);
    }

    private async Task LoadPageAsync(bool reset, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_loading || (!reset && !HasMore))
            {
                return;
            }

            _loading = true;
        }

        OnPropertyChanged(nameof(IsLoading));

        try
        {
            if (reset)
            {
                PageIndex = 0;
                HasMore = true;
                ErrorMessage = null;
            }

            PageResult result = await _loadPage(new PageRequest(PageIndex), cancellationToken).ConfigureAwait(true);

            if (reset)
            {
                Items.Clear();
                _seenGids.Clear();
            }

            foreach (GalleryInfo info in result.Galleries)
            {
                if (!_seenGids.Add(info.Gid))
                {
                    continue;
                }

                Items.Add(new GalleryItemViewModel(info, _showJpnTitle));
            }

            ResultCount = result.ResultCount;
            HasMore = result.HasMore;
            PageIndex++;
            IsEmpty = Items.Count == 0;
            ErrorMessage = result.ErrorMessage;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            HasMore = false;
        }
        finally
        {
            lock (_gate)
            {
                _loading = false;
            }

            OnPropertyChanged(nameof(IsLoading));
        }
    }
}

/// <summary>Request for one page of galleries.</summary>
public readonly record struct PageRequest(int PageIndex);

/// <summary>Parsed page of galleries plus its pagination state.</summary>
public sealed record PageResult(
    IReadOnlyList<GalleryInfo> Galleries,
    string? ResultCount,
    bool HasMore,
    string? ErrorMessage);
