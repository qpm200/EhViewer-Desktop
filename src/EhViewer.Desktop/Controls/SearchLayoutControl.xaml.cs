using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Desktop.Services;
using EhViewer.Desktop.Services.Theming;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace EhViewer.Desktop.Controls;

public sealed record NormalSearchRequest(
    string Keyword,
    int Mode,
    int CategoryMask,
    AdvanceSearchOptions? AdvanceOptions);

public sealed record ImageSearchRequest(
    string ImagePath,
    bool UseSimilarityScan,
    bool OnlySearchCovers,
    bool ShowExpunged);

/// <summary>
/// Faithful Windows adaptation of Android EhViewer's SearchLayout overlay: 10 color-coded categories,
/// search scope selector, collapsible AdvanceSearchTable, and image similarity search panel.
/// </summary>
public sealed partial class SearchLayoutControl : UserControl
{
    private static readonly (int Bit, string Label, uint ColorArgb)[] CategorySpecs =
    [
        (EhConfig.Doujinshi, "DOUJINSHI", 0xFFF44336),
        (EhConfig.Manga, "MANGA", 0xFFFF9800),
        (EhConfig.ArtistCg, "ARTIST CG", 0xFFFBC02D),
        (EhConfig.GameCg, "GAME CG", 0xFF4CAF50),
        (EhConfig.Western, "WESTERN", 0xFF8BC34A),
        (EhConfig.NonH, "NON-H", 0xFF2196F3),
        (EhConfig.ImageSet, "IMAGE SET", 0xFF3F51B5),
        (EhConfig.Cosplay, "COSPLAY", 0xFF9C27B0),
        (EhConfig.AsianPorn, "ASIAN PORN", 0xFF9575CD),
        (EhConfig.Misc, "MISC", 0xFFF06292)
    ];

    private readonly List<(int Bit, ToggleButton Button, uint ColorArgb)> _categoryButtons = [];
    private string? _selectedImagePath;
    private int _activeTab;
    private int _overlayAnimVersion;
    private bool _isOpen;
    private bool _suppressAdvanceAnimation;

    public SearchLayoutControl()
    {
        InitializeComponent();
        BuildCategoryButtons();
    }

    public Func<string>? CurrentKeywordAccessor { get; set; }
    public Func<Window?>? OwnerWindowAccessor { get; set; }

    public event EventHandler<NormalSearchRequest>? NormalSearchSubmitted;
    public event EventHandler<ImageSearchRequest>? ImageSearchSubmitted;
    public event EventHandler<NormalSearchRequest>? SaveQuickSearchRequested;
    public event EventHandler? Closed;

    public bool IsOpen => _isOpen;

    public void Open(ListUrlBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        bool shouldAnimateShow = !_isOpen || OverlayRoot.Visibility != Visibility.Visible;
        _isOpen = true;
        _overlayAnimVersion++;

        _suppressAdvanceAnimation = true;
        try
        {
            SyncFromBuilder(builder);
        }
        finally
        {
            _suppressAdvanceAnimation = false;
        }

        OverlayRoot.Visibility = Visibility.Visible;
        if (shouldAnimateShow)
        {
            ThemeMotionHelper.AnimateOverlayShow(
                SearchPanelCard,
                SearchOverlayScrim,
                MaterialThemeService.ActiveBehavior);
        }
    }

    public void Close()
    {
        if (!_isOpen)
        {
            return;
        }

        _isOpen = false;
        if (OverlayRoot.Visibility == Visibility.Visible)
        {
            int version = ++_overlayAnimVersion;
            ThemeMotionHelper.AnimateOverlayHide(
                SearchPanelCard,
                SearchOverlayScrim,
                MaterialThemeService.ActiveBehavior,
                () =>
                {
                    if (version == _overlayAnimVersion && !_isOpen && OverlayRoot.Visibility == Visibility.Visible)
                    {
                        OverlayRoot.Visibility = Visibility.Collapsed;
                        Closed?.Invoke(this, EventArgs.Empty);
                    }
                });
        }
    }

    public void SyncFromBuilder(ListUrlBuilder builder)
    {
        int mask = builder.Category == EhUtils.None ? CategoryTable.All : builder.Category;
        if (mask == 0)
        {
            mask = CategoryTable.All;
        }

        foreach ((int bit, ToggleButton btn, uint argb) in _categoryButtons)
        {
            btn.IsChecked = (mask & bit) != 0;
            UpdateCategoryVisual(btn, argb);
        }

        if (builder.Mode == ListUrlBuilder.ModeSubscription)
        {
            ModeSubscriptionRadio.IsChecked = true;
        }
        else if (builder.Mode == ListUrlBuilder.ModeUploader)
        {
            ModeUploaderRadio.IsChecked = true;
        }
        else if (builder.Mode == ListUrlBuilder.ModeTag)
        {
            ModeTagRadio.IsChecked = true;
        }
        else
        {
            ModeNormalRadio.IsChecked = true;
        }

        if (builder.AdvanceSearch > 0)
        {
            AdvanceToggle.IsOn = true;
            AdvSearchName.IsChecked = (builder.AdvanceSearch & AdvanceSearchOptions.SName) != 0;
            AdvSearchTags.IsChecked = (builder.AdvanceSearch & AdvanceSearchOptions.STags) != 0;
            AdvSearchDesc.IsChecked = (builder.AdvanceSearch & AdvanceSearchOptions.SDesc) != 0;
            AdvSearchTorr.IsChecked = (builder.AdvanceSearch & AdvanceSearchOptions.STorr) != 0;
            AdvOnlyTorr.IsChecked = (builder.AdvanceSearch & AdvanceSearchOptions.STo) != 0;
            AdvLowPower.IsChecked = (builder.AdvanceSearch & AdvanceSearchOptions.SDt1) != 0;
            AdvDownvoted.IsChecked = (builder.AdvanceSearch & AdvanceSearchOptions.SDt2) != 0;
            AdvExpunged.IsChecked = (builder.AdvanceSearch & AdvanceSearchOptions.SH) != 0;
            AdvDisableCustom.IsChecked = (builder.AdvanceSearch & AdvanceSearchOptions.SFl) != 0;
            AdvDisableDefault.IsChecked = (builder.AdvanceSearch & AdvanceSearchOptions.SFu) != 0;
            AdvDisableLang.IsChecked = (builder.AdvanceSearch & AdvanceSearchOptions.SFt) != 0;

            AdvMinRatingCheck.IsChecked = builder.MinRating >= 2;
            if (builder.MinRating >= 2)
            {
                AdvMinRatingCombo.SelectedIndex = Math.Clamp(builder.MinRating - 2, 0, 3);
            }

            AdvPagesCheck.IsChecked = builder.PageFrom > 0 || builder.PageTo > 0;
            AdvPageFromBox.Text = builder.PageFrom > 0 ? builder.PageFrom.ToString() : string.Empty;
            AdvPageToBox.Text = builder.PageTo > 0 ? builder.PageTo.ToString() : string.Empty;
        }
        else
        {
            AdvanceToggle.IsOn = false;
        }
    }

    public NormalSearchRequest BuildNormalRequest()
    {
        int mask = 0;
        foreach ((int bit, ToggleButton btn, _) in _categoryButtons)
        {
            if (btn.IsChecked == true)
            {
                mask |= bit;
            }
        }

        if (mask == 0)
        {
            mask = CategoryTable.All;
        }

        int mode = ListUrlBuilder.ModeNormal;
        if (ModeSubscriptionRadio.IsChecked == true)
        {
            mode = ListUrlBuilder.ModeSubscription;
        }
        else if (ModeUploaderRadio.IsChecked == true)
        {
            mode = ListUrlBuilder.ModeUploader;
        }
        else if (ModeTagRadio.IsChecked == true)
        {
            mode = ListUrlBuilder.ModeTag;
        }

        AdvanceSearchOptions? adv = null;
        if (AdvanceToggle.IsOn)
        {
            int flags = 0;
            if (AdvSearchName.IsChecked == true) flags |= AdvanceSearchOptions.SName;
            if (AdvSearchTags.IsChecked == true) flags |= AdvanceSearchOptions.STags;
            if (AdvSearchDesc.IsChecked == true) flags |= AdvanceSearchOptions.SDesc;
            if (AdvSearchTorr.IsChecked == true) flags |= AdvanceSearchOptions.STorr;
            if (AdvOnlyTorr.IsChecked == true) flags |= AdvanceSearchOptions.STo;
            if (AdvLowPower.IsChecked == true) flags |= AdvanceSearchOptions.SDt1;
            if (AdvDownvoted.IsChecked == true) flags |= AdvanceSearchOptions.SDt2;
            if (AdvExpunged.IsChecked == true) flags |= AdvanceSearchOptions.SH;
            if (AdvDisableCustom.IsChecked == true) flags |= AdvanceSearchOptions.SFl;
            if (AdvDisableDefault.IsChecked == true) flags |= AdvanceSearchOptions.SFu;
            if (AdvDisableLang.IsChecked == true) flags |= AdvanceSearchOptions.SFt;

            bool minRatingEnabled = AdvMinRatingCheck.IsChecked == true;
            int minRatingIndex = Math.Clamp(AdvMinRatingCombo.SelectedIndex, 0, 3);
            bool pagesEnabled = AdvPagesCheck.IsChecked == true;
            int pageFrom = pagesEnabled && int.TryParse(AdvPageFromBox.Text.Trim(), out int pf) ? pf : -1;
            int pageTo = pagesEnabled && int.TryParse(AdvPageToBox.Text.Trim(), out int pt) ? pt : -1;

            adv = new AdvanceSearchOptions
            {
                AdvanceSearch = flags,
                MinRatingEnabled = minRatingEnabled,
                MinRatingIndex = minRatingIndex,
                PagesEnabled = pagesEnabled,
                PageFrom = pageFrom,
                PageTo = pageTo
            };
        }

        string keyword = CurrentKeywordAccessor?.Invoke().Trim() ?? string.Empty;
        return new NormalSearchRequest(keyword, mode, mask, adv);
    }

    private void BuildCategoryButtons()
    {
        for (int i = 0; i < CategorySpecs.Length; i++)
        {
            (int bit, string label, uint argb) = CategorySpecs[i];
            var btn = new ToggleButton
            {
                Content = label,
                IsChecked = true,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 34,
                CornerRadius = new CornerRadius(4),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Colors.White),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(4, 2, 4, 2)
            };
            UpdateCategoryVisual(btn, argb);
            btn.Click += (_, _) => UpdateCategoryVisual(btn, argb);

            Grid.SetRow(btn, i / 5);
            Grid.SetColumn(btn, i % 5);
            CategoryGrid.Children.Add(btn);
            _categoryButtons.Add((bit, btn, argb));
        }
    }

    private static void UpdateCategoryVisual(ToggleButton btn, uint argb)
    {
        bool checkedState = btn.IsChecked == true;
        byte alpha = checkedState ? (byte)255 : (byte)80;
        var color = Color.FromArgb(
            alpha,
            (byte)((argb >> 16) & 0xFF),
            (byte)((argb >> 8) & 0xFF),
            (byte)(argb & 0xFF));
        btn.Background = new SolidColorBrush(color);
        btn.Opacity = checkedState ? 1.0 : 0.65;
    }

    private void OnSelectAllCategoriesClick(object sender, RoutedEventArgs e)
    {
        foreach ((_, ToggleButton btn, uint argb) in _categoryButtons)
        {
            btn.IsChecked = true;
            UpdateCategoryVisual(btn, argb);
        }
    }

    private void OnClearAllCategoriesClick(object sender, RoutedEventArgs e)
    {
        foreach ((_, ToggleButton btn, uint argb) in _categoryButtons)
        {
            btn.IsChecked = false;
            UpdateCategoryVisual(btn, argb);
        }
    }

    private void OnTabNormalClick(object sender, RoutedEventArgs e)
    {
        if (_activeTab == 0)
        {
            NormalTabBtn.IsChecked = true;
            return;
        }

        _activeTab = 0;
        NormalTabBtn.IsChecked = true;
        ImageTabBtn.IsChecked = false;
        ImagePanel.Visibility = Visibility.Collapsed;
        ThemeMotionHelper.AnimatePopoverShow(
            NormalPanel,
            fromOffsetY: 8f,
            fromScale: 0.985f,
            durationMs: 160);
    }

    private void OnTabImageClick(object sender, RoutedEventArgs e)
    {
        if (_activeTab == 1)
        {
            ImageTabBtn.IsChecked = true;
            return;
        }

        _activeTab = 1;
        NormalTabBtn.IsChecked = false;
        ImageTabBtn.IsChecked = true;
        NormalPanel.Visibility = Visibility.Collapsed;
        ThemeMotionHelper.AnimatePopoverShow(
            ImagePanel,
            fromOffsetY: 8f,
            fromScale: 0.985f,
            durationMs: 160);
    }

    private void OnAdvanceToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressAdvanceAnimation)
        {
            if (AdvanceToggle.IsOn)
            {
                ThemeMotionHelper.AnimatePopoverShow(
                    AdvancePanel,
                    fromOffsetY: 0f,
                    fromScale: 1f,
                    durationMs: 1);
            }
            else
            {
                AdvancePanel.Visibility = Visibility.Collapsed;
            }
            return;
        }

        if (AdvanceToggle.IsOn)
        {
            ThemeMotionHelper.AnimatePopoverShow(
                AdvancePanel,
                fromOffsetY: -6f,
                fromScale: 0.985f,
                durationMs: 160);
        }
        else
        {
            ThemeMotionHelper.AnimatePopoverHide(
                AdvancePanel,
                toOffsetY: -6f,
                toScale: 0.985f,
                durationMs: 120);
        }
    }

    private async void OnPickSearchImageClick(object sender, RoutedEventArgs e)
    {
        Window? owner = OwnerWindowAccessor?.Invoke();
        if (owner is null)
        {
            return;
        }

        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".gif");
        picker.FileTypeFilter.Add(".bmp");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));

        StorageFile? file = await picker.PickSingleFileAsync().AsTask().ConfigureAwait(true);
        if (file is null || string.IsNullOrEmpty(file.Path))
        {
            return;
        }

        _selectedImagePath = file.Path;
        SelectedImagePathText.Text = file.Name;
        try
        {
            ImagePreview.Source = new BitmapImage(new Uri(file.Path));
            ImagePreview.Visibility = Visibility.Visible;
        }
        catch
        {
            ImagePreview.Visibility = Visibility.Collapsed;
        }
    }

    private void OnSaveQuickSearchClick(object sender, RoutedEventArgs e)
    {
        NormalSearchRequest req = BuildNormalRequest();
        SaveQuickSearchRequested?.Invoke(this, req);
    }

    private void OnSubmitSearchClick(object sender, RoutedEventArgs e)
    {
        if (_activeTab == 1)
        {
            if (string.IsNullOrWhiteSpace(_selectedImagePath))
            {
                SelectedImagePathText.Text = "请先选择一张本地图片！";
                return;
            }

            Close();
            ImageSearchSubmitted?.Invoke(
                this,
                new ImageSearchRequest(
                    _selectedImagePath!,
                    ImgUssCheck.IsChecked == true,
                    ImgOscCheck.IsChecked == true,
                    ImgSeCheck.IsChecked == true));
            return;
        }

        NormalSearchRequest req = BuildNormalRequest();
        Close();
        NormalSearchSubmitted?.Invoke(this, req);
    }

    private void OnClosePanelClick(object sender, RoutedEventArgs e) => Close();
    private void OnScrimPressed(object sender, PointerRoutedEventArgs e) => Close();
}
