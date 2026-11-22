using System.Text;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EhViewer.Desktop.Views;

/// <summary>Key-value metadata row shown in Android-style GalleryInfoPage.</summary>
public sealed record GalleryInfoRow(string Label, string Value, bool IsLink = false);

/// <summary>
/// Full gallery metadata scene mirroring Android GalleryInfoScene.
/// Every row supports tap-to-copy and link rows support direct navigation.
/// </summary>
public sealed partial class GalleryInfoPage : Page, ISceneAware
{
    private readonly GalleryDetail _detail;
    private readonly List<GalleryInfoRow> _rows = [];
    private IShellCoordinator? _shell;

    public GalleryInfoPage(GalleryDetail detail, IShellCoordinator? shell = null)
    {
        ArgumentNullException.ThrowIfNull(detail);
        _detail = detail;
        _shell = shell;

        InitializeComponent();
        BuildRows();
    }

    public void AttachCoordinator(IShellCoordinator coordinator)
    {
        _shell = coordinator;
        BuildRows();
    }

    private void BuildRows()
    {
        EhSite site = _shell?.Services.Settings.GallerySite ?? EhSite.E;
        string detailUrl = EhUrl.GetGalleryDetailUrl(_detail.Gid, _detail.Token ?? string.Empty, 0, false, site);
        Toolbar.Subtitle = EhUtils.GetSuitableTitle(_detail, _shell?.Services.Settings.ShowJpnTitle ?? false) ?? $"画廊 {_detail.Gid}";

        _rows.Clear();
        AddRow("画廊 ID (GID)", _detail.Gid.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddRow("Token", _detail.Token);
        AddRow("画廊链接", detailUrl, isLink: true);
        AddRow("标题", _detail.Title);
        AddRow("日文标题", _detail.TitleJpn);
        AddRow("封面链接", _detail.Thumb, isLink: !string.IsNullOrWhiteSpace(_detail.Thumb));
        AddRow("分类", EhUtils.GetCategory(_detail.Category));
        AddRow("上传者", _detail.Uploader);
        AddRow("发布时间", _detail.Posted);
        AddRow("可见状态", _detail.Visible);
        AddRow("语言", _detail.Language);
        AddRow("页数", _detail.Pages > 0 ? $"{_detail.Pages} 页" : string.Empty);
        AddRow("文件大小", _detail.Size);
        AddRow("收藏数", _detail.FavoriteCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AddRow("收藏夹", _detail.IsFavorited ? (_detail.FavoriteName ?? $"收藏槽 {_detail.FavoriteSlot}") : "未收藏");
        AddRow("评分", _detail.Rating > 0 ? $"{_detail.Rating:0.00} ({_detail.RatingCount} 次评分)" : "暂无评分");
        AddRow("父画廊", _detail.Parent, isLink: !string.IsNullOrWhiteSpace(_detail.Parent));
        AddRow("种子数量", _detail.TorrentCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

        InfoRowsRepeater.ItemsSource = null;
        InfoRowsRepeater.ItemsSource = _rows;
    }

    private void AddRow(string label, string? value, bool isLink = false)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _rows.Add(new GalleryInfoRow(label, value!, isLink));
        }
    }

    private void OnBackRequested(object sender, RoutedEventArgs e) => _shell?.GoBack();

    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GalleryInfoRow row })
        {
            CopyRow(row);
        }
    }

    private void OnCopyRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GalleryInfoRow row })
        {
            CopyRow(row);
        }
    }

    private void OnOpenLinkRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: GalleryInfoRow { Value: { Length: > 0 } url } } && _shell is not null)
        {
            _ = _shell.OpenExternalOrInAppUrlAsync(url);
        }
    }

    private void OnCopyAllClick(object sender, RoutedEventArgs e)
    {
        var sb = new StringBuilder();
        foreach (GalleryInfoRow row in _rows)
        {
            sb.Append(row.Label).Append(": ").AppendLine(row.Value);
        }

        ClipboardService.CopyText(sb.ToString().TrimEnd());
        _shell?.SetStatus("已复制全部画廊元数据。");
    }

    private void CopyRow(GalleryInfoRow row)
    {
        ClipboardService.CopyText(row.Value);
        _shell?.SetStatus($"已复制 {row.Label}：{row.Value}");
    }
}
