using System.Collections.ObjectModel;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Controls;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Theming;
using EhViewer.Desktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.System;

namespace EhViewer.Desktop.Views;

/// <summary>
/// Primary browse scene matching Android EhViewer GalleryListScene:
/// floating SearchBar + expandable SearchLayout + horizontal/grid cards + endless scroll + corner FabLayout + QuickSearch drawer.
/// </summary>
public sealed partial class GalleryListScenePage : Page
{
    private const int CardThumbnailWidth = 320;
    private const int SuggestDebounceMs = 120;
    private static readonly Random RandomGenerator = new();

    private readonly IShellCoordinator _shell;
    private readonly AppServices _services;
    private readonly ObservableCollection<GalleryItemViewModel> _items = [];
    private readonly ObservableCollection<SearchSuggestionEntry> _suggestions = [];
    private readonly HashSet<long> _seenGids = [];
    private readonly bool _showBackButton;

    private ListUrlBuilder _listBuilder;
    private string _sceneTitle;
    private string? _firstHref;
    private string? _prevHref;
    private string? _nextHref;
    private string? _currentListUrl;
    private int _pageIndex;
    private int _totalPages;
    private string? _resultCountText;
    private bool _hasMore;
    private bool _isLoading;
    private bool _isGridView;
    private bool _isPointerOverSuggestions;
    private bool _suppressTextChanged;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _suggestCts;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _suggestTimer;
    private string _pendingSuggestInput = string.Empty;
    private bool _pendingSuggestOpen;
    private int _suggestVersion;
    private double _savedVerticalOffset;
    private QuickSearchDrawerContent? _quickSearchDrawer;
    private ImageSearchRequest? _pendingImageSearch;

    public GalleryListScenePage(
        IShellCoordinator shell,
        ListUrlBuilder builder,
        string sceneTitle,
        bool showBackButton = false,
        ImageSearchRequest? initialImageSearch = null)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _services = shell.Services;
        _listBuilder = builder ?? throw new ArgumentNullException(nameof(builder));
        _sceneTitle = sceneTitle;
        _showBackButton = showBackButton;
        _pendingImageSearch = initialImageSearch;

        InitializeComponent();

        GalleryRepeater.ItemsSource = _items;
        SuggestionsList.ItemsSource = _suggestions;
        ApplyCardTemplate();

        SearchBarLeftIcon.Glyph = _showBackButton ? "\uE72B" : "\uE721";
        SetSearchBoxText(_listBuilder.Keyword ?? string.Empty);
        UpdateSearchPlaceholder();

        SearchOverlay.CurrentKeywordAccessor = () => SearchBox.Text ?? string.Empty;
        SearchOverlay.OwnerWindowAccessor = () => _shell.HostWindow;
        SearchOverlay.NormalSearchSubmitted += OnOverlayNormalSearchSubmitted;
        SearchOverlay.ImageSearchSubmitted += OnOverlayImageSearchSubmitted;
        SearchOverlay.SaveQuickSearchRequested += OnOverlaySaveQuickSearchRequested;

        CornerFab.FilterRequested += async (_, _) => await _shell.ShowFilterManagerDialogAsync().ConfigureAwait(true);
        CornerFab.GoToRequested += (_, _) => _ = ShowGoToDialogAsync();
        CornerFab.RefreshRequested += (_, _) => _ = RefreshAsync();
        CornerFab.RandomRequested += (_, _) => _ = OpenRandomFromCurrentAsync();

        Loaded += OnPageLoaded;
        Unloaded += OnPageUnloaded;
    }

    private void OnPageLoaded(object sender, RoutedEventArgs e)
    {
        if (_items.Count > 0 && _savedVerticalOffset > 1 && ListScroller.VerticalOffset < 1)
        {
            double target = _savedVerticalOffset;
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_items.Count > 0 && _savedVerticalOffset > 1 && ListScroller.VerticalOffset < 1)
                {
                    ListScroller.ChangeView(null, target, null, disableAnimation: true);
                }
            });
        }
    }

    private void OnPageUnloaded(object sender, RoutedEventArgs e) => CloseSuggestions();

    public string SceneTitle => _sceneTitle;
    public ListUrlBuilder CurrentBuilder => _listBuilder;

    public FrameworkElement GetOrCreateQuickSearchDrawer()
    {
        if (_quickSearchDrawer is null)
        {
            _quickSearchDrawer = new QuickSearchDrawerContent(
                _services.GetRequiredService<EhDatabase>(),
                () => _listBuilder);
            _quickSearchDrawer.QuickSearchSelected += OnQuickSearchSelected;
        }
        else
        {
            _quickSearchDrawer.Reload();
        }

        return _quickSearchDrawer;
    }

    public Task InitializeLoadAsync()
    {
        if (_pendingImageSearch is { } imgReq)
        {
            _pendingImageSearch = null;
            return RunImageSearchAsync(imgReq);
        }

        return LoadPageAsync(reset: true);
    }

    public void FocusSearchInput()
    {
        SearchBox.Focus(FocusState.Programmatic);
    }

    public Task RefreshAsync() => LoadPageAsync(reset: true);

    public async Task ApplyListBuilderAsync(ListUrlBuilder builder, string? title = null)
    {
        _listBuilder = builder ?? throw new ArgumentNullException(nameof(builder));
        if (!string.IsNullOrWhiteSpace(title))
        {
            _sceneTitle = title;
        }

        SetSearchBoxText(_listBuilder.Keyword ?? string.Empty);
        CloseSuggestions();
        UpdateSearchPlaceholder();
        await LoadPageAsync(reset: true).ConfigureAwait(true);
    }

    public void ApplyQuickSearch(QuickSearch qs)
    {
        _listBuilder = new ListUrlBuilder
        {
            Mode = qs.Mode,
            Category = qs.Category,
            Keyword = qs.Keyword,
            AdvanceSearch = qs.AdvanceSearch,
            MinRating = qs.MinRating,
            PageFrom = qs.PageFrom,
            PageTo = qs.PageTo
        };
        _sceneTitle = string.IsNullOrWhiteSpace(qs.Name) ? "快速搜索" : qs.Name!;
        SetSearchBoxText(qs.Keyword ?? string.Empty);
        CloseSuggestions();
        UpdateSearchPlaceholder();
        _shell.CloseRightDrawer();
        _ = LoadPageAsync(reset: true);
    }

    public void ReleaseCts()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        if (_suggestTimer is not null)
        {
            _suggestTimer.Stop();
            _suggestTimer.Tick -= OnSuggestTimerTick;
            _suggestTimer = null;
        }

        _suggestCts?.Cancel();
        _suggestCts?.Dispose();
        _suggestCts = null;
        _suggestVersion++;
    }

    private void UpdateSearchPlaceholder()
    {
        string prefix = _listBuilder.Mode switch
        {
            ListUrlBuilder.ModeWhatsHot => "热门画廊 · ",
            ListUrlBuilder.ModeSubscription => "我的订阅 · ",
            ListUrlBuilder.ModeUploader => "上传者 · ",
            ListUrlBuilder.ModeTag => "精确标签 · ",
            ListUrlBuilder.ModeImageSearch => "以图搜图 · ",
            _ => string.Empty
        };
        SearchBox.PlaceholderText = $"{prefix}搜索关键词或点击右侧漏斗图标展开过滤...";
    }

    private void ApplyCardTemplate()
    {
        if (_isGridView)
        {
            GalleryRepeater.ItemTemplate = (DataTemplate)Resources["GridGalleryCardTemplate"];
            RepeaterLayout.MinItemWidth = 204;
            RepeaterLayout.MinItemHeight = 336;
            ViewModeIcon.Glyph = "\uE8FD";
        }
        else
        {
            GalleryRepeater.ItemTemplate = (DataTemplate)Resources["HorizontalGalleryCardTemplate"];
            RepeaterLayout.MinItemWidth = 460;
            RepeaterLayout.MinItemHeight = 138;
            ViewModeIcon.Glyph = "\uF0E2";
        }
    }

    private async Task LoadPageAsync(bool reset)
    {
        if (reset)
        {
            _listBuilder.PageIndex = 0;
        }

        string url = _listBuilder.Build(_services.Settings.GallerySite);
        await LoadUrlAsync(url, append: !reset).ConfigureAwait(true);
    }

    private async Task LoadNextPageAsync()
    {
        if (_isLoading || !_hasMore)
        {
            return;
        }

        if (!string.IsNullOrEmpty(_nextHref))
        {
            await LoadUrlAsync(_nextHref!, append: true).ConfigureAwait(true);
            return;
        }

        _listBuilder.PageIndex = _pageIndex;
        string url = _listBuilder.Build(_services.Settings.GallerySite);
        await LoadUrlAsync(url, append: true).ConfigureAwait(true);
    }

    private async Task LoadUrlAsync(string url, bool append)
    {
        if (_isLoading && append)
        {
            return;
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(_shell.LifetimeToken);
        CancellationToken ct = _loadCts.Token;

        _isLoading = true;
        SetLoadingVisuals(append, loading: true);

        try
        {
            var browse = _services.GetRequiredService<GalleryBrowseService>();
            GalleryListParser.Result result = await browse
                .LoadRawAsync(url, _listBuilder.Mode, ct)
                .ConfigureAwait(true);

            ApplyListResult(result, append, url);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            string msg = GalleryBrowseService.DescribeFailure(ex);
            _shell.SetStatus(msg);
            if (_items.Count == 0)
            {
                EmptyErrorText.Text = msg;
                EmptyErrorPanel.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _isLoading = false;
            SetLoadingVisuals(append, loading: false);
        }
    }

    private void ApplyListResult(GalleryListParser.Result result, bool append, string currentUrl)
    {
        _currentListUrl = currentUrl;
        _firstHref = result.FirstHref;
        _prevHref = result.PrevHref;
        _nextHref = result.NextHref;
        _pageIndex = result.Pages > 0 ? Math.Max(1, result.NextPage > 0 ? result.NextPage - 1 : _pageIndex + 1) : 1;
        if (!append && result.NextPage <= 0 && string.IsNullOrEmpty(result.PrevHref))
        {
            _pageIndex = 1;
        }

        _totalPages = result.Pages;
        _resultCountText = result.ResultCount;
        _hasMore = !string.IsNullOrEmpty(result.NextHref) || (result.NextPage > 0 && result.NextPage <= Math.Max(1, result.Pages));

        if (!append)
        {
            _items.Clear();
            _seenGids.Clear();
            _savedVerticalOffset = 0;
            ListScroller.ChangeView(null, 0, null, disableAnimation: true);
        }

        var filter = _services.GetRequiredService<EhFilter>();
        var downloads = _services.GetRequiredService<DownloadManager>();
        bool showJpn = _services.Settings.ShowJpnTitle;

        if (result.GalleryInfoList is { Count: > 0 } rawList)
        {
            foreach (GalleryInfo info in rawList)
            {
                if (!filter.FilterTitle(info) ||
                    !filter.FilterUploader(info) ||
                    !filter.FilterTag(info) ||
                    !filter.FilterTagNamespace(info) ||
                    !_seenGids.Add(info.Gid))
                {
                    continue;
                }

                var vm = new GalleryItemViewModel(info, _ => showJpn)
                {
                    IsDownloaded = downloads.GetProgress(info.Gid) is not null
                };
                _items.Add(vm);
            }
        }

        bool hasErrorOrEmpty = _items.Count == 0;
        if (hasErrorOrEmpty)
        {
            EmptyErrorText.Text = !string.IsNullOrEmpty(result.CustomErrorString)
                ? result.CustomErrorString!
                : "没有找到符合条件的画廊。";
            EmptyErrorPanel.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyErrorPanel.Visibility = Visibility.Collapsed;
        }

        UpdatePaginationHeader();
        string countSuffix = string.IsNullOrWhiteSpace(_resultCountText) ? $"{_items.Count} 项" : _resultCountText!;
        _shell.SetStatus(!string.IsNullOrEmpty(result.CustomErrorString) ? result.CustomErrorString! : $"{_sceneTitle} · {countSuffix}");
    }

    private void SetLoadingVisuals(bool append, bool loading)
    {
        TopLoadingBar.Visibility = loading && !append ? Visibility.Visible : Visibility.Collapsed;
        FooterBusyRing.IsActive = loading && append;
        FooterBusyRing.Visibility = loading && append ? Visibility.Visible : Visibility.Collapsed;
        LoadMoreBtn.Visibility = !loading && _hasMore && _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePaginationHeader()
    {
        if (_items.Count == 0 && string.IsNullOrWhiteSpace(_resultCountText))
        {
            ListSummaryRow.Visibility = Visibility.Collapsed;
            return;
        }

        ListSummaryRow.Visibility = Visibility.Visible;
        string summary = string.IsNullOrWhiteSpace(_resultCountText)
            ? $"已加载 {_items.Count} 本画廊"
            : $"{_sceneTitle} · {_resultCountText}";
        ListSummaryText.Text = summary;

        FirstPageBtn.IsEnabled = !string.IsNullOrEmpty(_firstHref);
        PrevPageBtn.IsEnabled = !string.IsNullOrEmpty(_prevHref);
        NextPageBtn.IsEnabled = !string.IsNullOrEmpty(_nextHref);
        PageStatusBadge.Text = _totalPages > 0 ? $"{_pageIndex} / {_totalPages}" : $"第 {_pageIndex} 页";
        LoadMoreBtn.Visibility = _hasMore && !_isLoading && _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnListScrollViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        _shell.DismissHoverPreview();
        if (ListScroller.ScrollableHeight > 0)
        {
            _savedVerticalOffset = ListScroller.VerticalOffset;
        }

        if (SuggestionsCard.Visibility == Visibility.Visible)
        {
            CloseSuggestions();
        }

        if (!_hasMore || _isLoading || _items.Count == 0)
        {
            return;
        }

        if (ListScroller.ScrollableHeight > 0 && ListScroller.VerticalOffset >= ListScroller.ScrollableHeight - 240)
        {
            _ = LoadNextPageAsync();
        }
    }

    private void OnPagePreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        if (SuggestionsCard.Visibility == Visibility.Visible)
        {
            CloseSuggestions();
            e.Handled = true;
            return;
        }

        if (SearchOverlay.IsOpen)
        {
            SearchOverlay.Close();
            e.Handled = true;
        }
    }

    // --- Search Bar & Overlay Handlers -------------------------------------

    private void OnSearchBarLeftClick(object sender, RoutedEventArgs e)
    {
        if (_showBackButton)
        {
            _shell.GoBack();
            return;
        }

        if (SearchOverlay.IsOpen)
        {
            SearchOverlay.Close();
        }
        else
        {
            SearchOverlay.Open(_listBuilder);
        }
    }

    private void OnToggleSearchLayoutClick(object sender, RoutedEventArgs e)
    {
        if (SearchOverlay.IsOpen)
        {
            SearchOverlay.Close();
        }
        else
        {
            SearchOverlay.Open(_listBuilder);
        }
    }

    private void SetSearchBoxText(string text)
    {
        _suppressTextChanged = true;
        try
        {
            SearchBox.Text = text;
        }
        finally
        {
            _suppressTextChanged = false;
        }
    }

    private void OnSearchBoxGotFocus(object sender, RoutedEventArgs e)
    {
        ScheduleSuggestionsUpdate(SearchBox.Text, openDropdown: true, immediate: true);
    }

    private void OnSearchBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (!_isPointerOverSuggestions)
        {
            CloseSuggestions();
        }
    }

    private void OnSuggestionsPointerEntered(object sender, PointerRoutedEventArgs e) => _isPointerOverSuggestions = true;

    private void OnSuggestionsPointerExited(object sender, PointerRoutedEventArgs e) => _isPointerOverSuggestions = false;

    private void OnListScrollerPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (SuggestionsCard.Visibility == Visibility.Visible)
        {
            CloseSuggestions();
        }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged)
        {
            return;
        }

        ScheduleSuggestionsUpdate(SearchBox.Text, openDropdown: SearchBox.FocusState != FocusState.Unfocused, immediate: false);
    }

    private void OnSearchBoxPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            string query = SuggestionsCard.Visibility == Visibility.Visible &&
                           SuggestionsList.SelectedItem is SearchSuggestionEntry selected
                ? selected.Text
                : SearchBox.Text;
            if (SuggestionsList.SelectedItem is SearchSuggestionEntry entry)
            {
                SetSearchBoxText(entry.Text);
            }

            CloseSuggestions();
            SubmitSearchQuery(query);
            e.Handled = true;
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            CloseSuggestions();
            e.Handled = true;
            return;
        }

        if (SuggestionsCard.Visibility != Visibility.Visible || _suggestions.Count == 0)
        {
            return;
        }

        if (e.Key == VirtualKey.Down)
        {
            int next = SuggestionsList.SelectedIndex + 1;
            if (next >= _suggestions.Count)
            {
                next = 0;
            }
            SuggestionsList.SelectedIndex = next;
            SuggestionsList.ScrollIntoView(_suggestions[next]);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Up)
        {
            int prev = SuggestionsList.SelectedIndex <= 0 ? _suggestions.Count - 1 : SuggestionsList.SelectedIndex - 1;
            SuggestionsList.SelectedIndex = prev;
            SuggestionsList.ScrollIntoView(_suggestions[prev]);
            e.Handled = true;
        }
    }

    private void ScheduleSuggestionsUpdate(string? input, bool openDropdown, bool immediate)
    {
        _pendingSuggestInput = input ?? string.Empty;
        _pendingSuggestOpen = openDropdown;

        if (immediate)
        {
            _suggestTimer?.Stop();
            _ = RefreshSuggestionsAsync(_pendingSuggestInput, openDropdown);
            return;
        }

        if (_suggestTimer is null)
        {
            if (DispatcherQueue is null)
            {
                _ = RefreshSuggestionsAsync(_pendingSuggestInput, openDropdown);
                return;
            }

            _suggestTimer = DispatcherQueue.CreateTimer();
            _suggestTimer.Interval = TimeSpan.FromMilliseconds(SuggestDebounceMs);
            _suggestTimer.IsRepeating = false;
            _suggestTimer.Tick += OnSuggestTimerTick;
        }

        _suggestTimer.Stop();
        _suggestTimer.Start();
    }

    private void OnSuggestTimerTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        => _ = RefreshSuggestionsAsync(_pendingSuggestInput, _pendingSuggestOpen);

    private async Task RefreshSuggestionsAsync(string rawInput, bool openDropdown)
    {
        _suggestCts?.Cancel();
        _suggestCts?.Dispose();
        _suggestCts = CancellationTokenSource.CreateLinkedTokenSource(_shell.LifetimeToken);
        CancellationToken ct = _suggestCts.Token;
        int version = ++_suggestVersion;

        string text = rawInput.Trim();
        var historyDb = _services.GetRequiredService<SearchHistoryDatabase>();
        var tagSource = _services.GetRequiredService<TagTranslationSource>();

        List<SearchSuggestionEntry> entries;
        try
        {
            entries = await Task.Run(() => BuildSuggestions(historyDb, tagSource, text, ct), ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            return;
        }

        if (ct.IsCancellationRequested || version != _suggestVersion)
        {
            return;
        }

        _suggestions.Clear();
        foreach (SearchSuggestionEntry entry in entries)
        {
            _suggestions.Add(entry);
        }

        SuggestionsList.SelectedIndex = -1;
        SuggestionsCard.Visibility = openDropdown && _suggestions.Count > 0 && SearchBox.FocusState != FocusState.Unfocused
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static List<SearchSuggestionEntry> BuildSuggestions(
        SearchHistoryDatabase historyDb,
        TagTranslationSource tagSource,
        string text,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entries = new List<SearchSuggestionEntry>(14);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string h in historyDb.GetSuggestions(text, 6))
        {
            if (seen.Add(h))
            {
                entries.Add(new SearchSuggestionEntry(h, IsHistory: true));
            }
        }

        ct.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(text))
        {
            foreach (TagSuggestionItem item in tagSource.SuggestItems(text, 8))
            {
                if (seen.Add(item.Query))
                {
                    entries.Add(new SearchSuggestionEntry(item.Query, IsHistory: false, Subtitle: item.Chinese));
                }
            }
        }

        return entries;
    }

    private void CloseSuggestions()
    {
        _suggestTimer?.Stop();
        _suggestCts?.Cancel();
        _suggestCts?.Dispose();
        _suggestCts = null;
        _suggestVersion++;
        _isPointerOverSuggestions = false;
        SuggestionsList.SelectedIndex = -1;
        SuggestionsCard.Visibility = Visibility.Collapsed;
    }

    private void OnSuggestionItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SearchSuggestionEntry entry)
        {
            SetSearchBoxText(entry.Text);
            CloseSuggestions();
            SubmitSearchQuery(entry.Text);
        }
    }

    private void SubmitSearchQuery(string? rawQuery)
    {
        string query = (rawQuery ?? string.Empty).Trim();
        SearchOverlay.Close();
        if (!string.IsNullOrEmpty(query))
        {
            _services.GetRequiredService<SearchHistoryDatabase>().AddQuery(query);
        }

        if (_listBuilder.Mode == ListUrlBuilder.ModeWhatsHot || _listBuilder.Mode == ListUrlBuilder.ModeImageSearch)
        {
            _listBuilder.Mode = ListUrlBuilder.ModeNormal;
        }

        _listBuilder.Keyword = query;
        _listBuilder.PageIndex = 0;
        _sceneTitle = string.IsNullOrWhiteSpace(query) ? "画廊列表" : $"搜索: {query}";
        UpdateSearchPlaceholder();
        _ = LoadPageAsync(reset: true);
    }

    private void OnOverlayNormalSearchSubmitted(object? sender, NormalSearchRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Keyword))
        {
            _services.GetRequiredService<SearchHistoryDatabase>().AddQuery(req.Keyword);
        }

        _listBuilder = new ListUrlBuilder
        {
            Mode = req.Mode,
            Category = req.CategoryMask,
            Keyword = req.Keyword,
            AdvanceSearch = req.AdvanceOptions?.AdvanceSearch ?? -1,
            MinRating = req.AdvanceOptions?.EffectiveMinRating ?? -1,
            PageFrom = req.AdvanceOptions?.EffectivePageFrom ?? -1,
            PageTo = req.AdvanceOptions?.EffectivePageTo ?? -1
        };

        _sceneTitle = string.IsNullOrWhiteSpace(req.Keyword)
            ? (req.Mode == ListUrlBuilder.ModeSubscription ? "订阅筛选" : "分类筛选")
            : $"搜索: {req.Keyword}";
        SetSearchBoxText(req.Keyword);
        CloseSuggestions();
        UpdateSearchPlaceholder();
        _ = LoadPageAsync(reset: true);
    }

    private void OnOverlayImageSearchSubmitted(object? sender, ImageSearchRequest req)
    {
        _ = RunImageSearchAsync(req);
    }

    private async void OnOverlaySaveQuickSearchRequested(object? sender, NormalSearchRequest req)
    {
        var tempBuilder = new ListUrlBuilder
        {
            Mode = req.Mode,
            Category = req.CategoryMask,
            Keyword = req.Keyword,
            AdvanceSearch = req.AdvanceOptions?.AdvanceSearch ?? -1,
            MinRating = req.AdvanceOptions?.EffectiveMinRating ?? -1,
            PageFrom = req.AdvanceOptions?.EffectivePageFrom ?? -1,
            PageTo = req.AdvanceOptions?.EffectivePageTo ?? -1
        };

        GetOrCreateQuickSearchDrawer();
        await _quickSearchDrawer!.PromptSaveAsync(tempBuilder, req.Keyword).ConfigureAwait(true);
        _shell.SetStatus("已保存到快速搜索。");
    }

    private void OnQuickSearchSelected(object? sender, QuickSearch qs) => ApplyQuickSearch(qs);

    private void OnOpenQuickSearchDrawerClick(object sender, RoutedEventArgs e)
    {
        _shell.SetRightDrawer(GetOrCreateQuickSearchDrawer(), "快速搜索");
        _shell.OpenRightDrawer();
    }

    private void OnToggleViewModeClick(object sender, RoutedEventArgs e)
    {
        _isGridView = !_isGridView;
        ApplyCardTemplate();
    }

    private async Task RunImageSearchAsync(ImageSearchRequest req)
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(_shell.LifetimeToken);
        CancellationToken ct = _loadCts.Token;

        _isLoading = true;
        SetLoadingVisuals(append: false, loading: true);

        try
        {
            _listBuilder.Mode = ListUrlBuilder.ModeImageSearch;
            _sceneTitle = $"以图搜图: {Path.GetFileName(req.ImagePath)}";
            SetSearchBoxText(string.Empty);
            CloseSuggestions();
            UpdateSearchPlaceholder();

            string lookupUrl = EhUrl.GetImageSearchUrl(_services.Settings.GallerySite);
            var browse = _services.GetRequiredService<GalleryBrowseService>();
            GalleryListParser.Result result = await browse
                .ImageSearchAsync(
                    req.ImagePath,
                    req.UseSimilarityScan,
                    req.OnlySearchCovers,
                    req.ShowExpunged,
                    ct)
                .ConfigureAwait(true);

            ApplyListResult(result, append: false, lookupUrl);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            string msg = GalleryBrowseService.DescribeFailure(ex);
            _shell.SetStatus(msg);
            if (_items.Count == 0)
            {
                EmptyErrorText.Text = msg;
                EmptyErrorPanel.Visibility = Visibility.Visible;
            }
        }
        finally
        {
            _isLoading = false;
            SetLoadingVisuals(append: false, loading: false);
        }
    }

    // --- FAB & Pagination Handlers ------------------------------------------

    private void OnRefreshClick(object sender, RoutedEventArgs e) => _ = RefreshAsync();
    private void OnLoadMoreClick(object sender, RoutedEventArgs e) => _ = LoadNextPageAsync();

    private async void OnFirstPageClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_firstHref))
        {
            await LoadUrlAsync(_firstHref!, append: false).ConfigureAwait(true);
        }
    }

    private async void OnPrevPageClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_prevHref))
        {
            await LoadUrlAsync(_prevHref!, append: false).ConfigureAwait(true);
        }
    }

    private async void OnNextPageClick(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_nextHref))
        {
            await LoadUrlAsync(_nextHref!, append: false).ConfigureAwait(true);
        }
    }

    private Task OpenRandomFromCurrentAsync()
    {
        if (_items.Count == 0)
        {
            _shell.SetStatus("当前列表中没有可随机打开的画廊。");
            return Task.CompletedTask;
        }

        GalleryItemViewModel picked = _items[RandomGenerator.Next(_items.Count)];
        return _shell.OpenDetailAsync(picked.Info);
    }

    private async Task ShowGoToDialogAsync()
    {
        if (XamlRoot is null)
        {
            return;
        }

        int maxPage = Math.Max(1, _totalPages);
        var pageBox = new NumberBox
        {
            Header = $"跳转到指定页码 (1 – {maxPage})",
            Minimum = 1,
            Maximum = maxPage,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = Math.Clamp(_pageIndex, 1, maxPage)
        };

        var datePicker = new CalendarDatePicker
        {
            Header = "或按发布日期回溯 (seek=YYYY-MM-DD)",
            PlaceholderText = "选择目标日期..."
        };

        string? quickJumpParam = null;
        var jumpPanel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach ((string label, string param) in new[] { ("1天前", "jump=1d"), ("1周前", "jump=1w"), ("1月前", "jump=1m"), ("1年前", "jump=1y") })
        {
            var btn = new Button { Content = label, FontSize = 12 };
            btn.Click += (_, _) =>
            {
                quickJumpParam = param;
                pageBox.IsEnabled = false;
            };
            jumpPanel.Children.Add(btn);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "页码与日期跳转",
            PrimaryButtonText = "跳转",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { pageBox, datePicker, jumpPanel }
            }
        };

        if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) != ContentDialogResult.Primary)
        {
            return;
        }

        string baseUrl = !string.IsNullOrEmpty(_currentListUrl)
            ? _currentListUrl!
            : _listBuilder.Build(_services.Settings.GallerySite);

        if (!string.IsNullOrEmpty(quickJumpParam))
        {
            await LoadUrlAsync(ListUrlBuilder.JumpHrefBuild(baseUrl, quickJumpParam!), append: false).ConfigureAwait(true);
            return;
        }

        if (datePicker.Date is { } selectedDate)
        {
            await LoadUrlAsync(ListUrlBuilder.JumpHrefBuild(baseUrl, $"seek={selectedDate:yyyy-MM-dd}"), append: false).ConfigureAwait(true);
            return;
        }

        int zeroBasedPage = double.IsNaN(pageBox.Value) ? 0 : Math.Max(0, (int)Math.Round(pageBox.Value) - 1);
        _listBuilder.PageIndex = zeroBasedPage;
        await LoadPageAsync(reset: true).ConfigureAwait(true);
    }

    // --- Gallery Card Interactions ------------------------------------------

    private void OnGalleryElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        GalleryItemViewModel? item = RepeaterSupport.ItemAt<GalleryItemViewModel>(sender, args.Index);
        if (item is null || !item.HasThumb)
        {
            return;
        }

        Border? thumbHost = RepeaterSupport.FindThumbHost(args.Element);
        if (thumbHost is null)
        {
            return;
        }

        _ = RepeaterSupport.SetBorderImageAsync(
            thumbHost,
            item,
            ct => _shell.LoadThumbnailAsync(item.ThumbUrl, CardThumbnailWidth, ct));
    }

    private void OnGalleryElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        if (args.Element is Border card)
        {
            card.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EhCardBorderBrush"];
        }

        Border? thumbHost = RepeaterSupport.FindThumbHost(args.Element);
        RepeaterSupport.CancelBorderImage(thumbHost);
    }

    private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            var props = e.GetCurrentPoint(element).Properties;
            if (element.Tag is GalleryItemViewModel item && props.IsMiddleButtonPressed)
            {
                e.Handled = true;
                _shell.DismissHoverPreview();
                _ = _shell.OpenGalleryInNewWindowAsync(item.Info);
                return;
            }

            bool isPrimary = props.IsLeftButtonPressed || e.Pointer.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse;
            ThemeMotionHelper.HandleCardPointerPressed(element, isPrimary, MaterialThemeService.ActiveBehavior);
        }
    }

    private void OnCardPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            ThemeMotionHelper.HandleCardPointerReleased(element, MaterialThemeService.ActiveBehavior);
        }
    }

    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card)
        {
            card.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EhPrimaryBrush"];
        }

        if (sender is FrameworkElement element)
        {
            ThemeMotionHelper.HandleCardPointerEntered(element, MaterialThemeService.ActiveBehavior);
            if (element.Tag is GalleryItemViewModel item)
            {
                _shell.ShowHoverPreview(item, element);
            }
        }
    }

    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border card)
        {
            card.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["EhCardBorderBrush"];
        }

        if (sender is FrameworkElement element)
        {
            ThemeMotionHelper.HandleCardPointerExited(element, MaterialThemeService.ActiveBehavior);
        }

        _shell.DismissHoverPreview();
    }

    private void OnCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GalleryItemViewModel item })
        {
            _shell.DismissHoverPreview();
            _ = _shell.OpenDetailAsync(item.Info);
        }
    }

    private void OnCardContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: GalleryItemViewModel item } element)
        {
            return;
        }

        args.Handled = true;
        _shell.DismissHoverPreview();

        var flyout = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "打开详情", Icon = new FontIcon { Glyph = "\uE8A7" } };
        openItem.Click += (_, _) => _ = _shell.OpenDetailAsync(item.Info);
        flyout.Items.Add(openItem);

        var openWinItem = new MenuFlyoutItem { Text = "在新窗口打开画廊", Icon = new FontIcon { Glyph = "\uE8A7" } };
        openWinItem.Click += (_, _) => _ = _shell.OpenGalleryInNewWindowAsync(item.Info);
        flyout.Items.Add(openWinItem);

        var readNowItem = new MenuFlyoutItem { Text = "直接开始阅读", Icon = new FontIcon { Glyph = "\uE736" } };
        readNowItem.Click += async (_, _) =>
        {
            try
            {
                GalleryDetail detail = await _services.GetRequiredService<GalleryBrowseService>()
                    .GetDetailAsync(item.Info, _shell.LifetimeToken)
                    .ConfigureAwait(true);
                await _shell.OpenReaderAsync(detail, 0).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _shell.SetStatus(GalleryBrowseService.DescribeFailure(ex));
            }
        };
        flyout.Items.Add(readNowItem);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var dlItem = new MenuFlyoutItem { Text = "加入下载队列", Icon = new FontIcon { Glyph = "\uE896" } };
        dlItem.Click += (_, _) =>
        {
            _shell.EnqueueDownload(item.Info);
            item.IsDownloaded = true;
        };
        flyout.Items.Add(dlItem);

        var favItem = new MenuFlyoutItem { Text = "添加 / 管理收藏...", Icon = new FontIcon { Glyph = "\uE734" } };
        favItem.Click += (_, _) => _ = _shell.PickFavoriteSlotDialogAsync(item.Info);
        flyout.Items.Add(favItem);

        if (!string.IsNullOrWhiteSpace(item.Uploader))
        {
            var uploaderItem = new MenuFlyoutItem { Text = $"搜索上传者：{item.Uploader}", Icon = new FontIcon { Glyph = "\uE77B" } };
            uploaderItem.Click += (_, _) => _ = _shell.OpenSearchKeywordAsync($"uploader:\"{item.Uploader}\"");
            flyout.Items.Add(uploaderItem);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        var copyUrlItem = new MenuFlyoutItem { Text = "复制画廊链接", Icon = new FontIcon { Glyph = "\uE8C8" } };
        copyUrlItem.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(item.Info.Token))
            {
                string url = EhUrl.GetGalleryDetailUrl(item.Info.Gid, item.Info.Token!, 0, false, _services.Settings.GallerySite);
                ClipboardService.CopyText(url);
                _shell.SetStatus($"已复制链接：{url}");
            }
        };
        flyout.Items.Add(copyUrlItem);

        if (args.TryGetPosition(element, out Windows.Foundation.Point pt))
        {
            flyout.ShowAt(element, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions { Position = pt });
        }
        else
        {
            flyout.ShowAt(element);
        }
    }
}

public sealed record SearchSuggestionEntry(string Text, bool IsHistory, string Subtitle = "")
{
    public string Glyph => IsHistory ? "\uE81C" : "\uE8EC";
    public string Badge => IsHistory ? "历史" : "标签";
    public Visibility SubtitleVisibility => string.IsNullOrEmpty(Subtitle) ? Visibility.Collapsed : Visibility.Visible;
}
