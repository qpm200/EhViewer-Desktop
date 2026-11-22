using System.Diagnostics;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Network;
using EhViewer.Core.Storage;
using EhViewer.Core.Util;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Reader;
using EhViewer.Desktop.ViewModels;
using EhViewer.Desktop.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Centralizes reusable gallery/detail/favorite/filter/thumbnail operations so MainWindow and
/// detached scene windows share identical behavior without duplicating business logic.
/// </summary>
internal static class SharedGalleryActions
{
    public static FavoritesViewModel CreateFavoritesViewModel(AppServices services)
    {
        var settings = services.GetRequiredService<IEhClientSettings>();
        var db = services.GetRequiredService<EhDatabase>();
        var cookies = services.GetRequiredService<ICookieStore>();
        var engine = services.GetRequiredService<EhEngine>();
        var browse = services.GetRequiredService<GalleryBrowseService>();
        var uconfigSync = services.GetRequiredService<UConfigSyncService>();

        static FavoritesPageData ToPageData(FavoritesParser.Result result, int localCount)
        {
            var names = new List<string>(result.CatArray.Length);
            var counts = new List<int>(result.CountArray.Length);
            for (int i = 0; i < result.CatArray.Length; i++)
            {
                names.Add(result.CatArray[i] ?? $"分类 {i}");
                counts.Add(result.CountArray[i]);
            }

            return new FavoritesPageData(
                result.GalleryInfoList ?? [],
                names,
                counts,
                result.ResultCount,
                !string.IsNullOrEmpty(result.NextHref),
                RequiresSignIn: false,
                PrevHref: result.PrevHref,
                NextHref: result.NextHref,
                LocalCount: localCount);
        }

        int initialSlot = cookies.HasSignedIn ? FavListUrlBuilder.FavCatAll : FavListUrlBuilder.FavCatLocal;
        string initialSort = services.Settings.FavoritesSortOrder;

        return new FavoritesViewModel(
            loadPage: async (req, ct) =>
            {
                if (req.Slot == FavListUrlBuilder.FavCatLocal)
                {
                    IReadOnlyList<GalleryInfo> filtered;
                    int localCount;
                    if (string.IsNullOrWhiteSpace(req.Keyword))
                    {
                        List<GalleryInfo> localAll = db.GetAllLocalFavorites();
                        filtered = localAll;
                        localCount = localAll.Count;
                    }
                    else
                    {
                        filtered = db.SearchLocalFavorites(req.Keyword!);
                        localCount = db.GetLocalFavoritesCount();
                    }

                    return new FavoritesPageData(
                        filtered,
                        SlotNames: [],
                        SlotCounts: [],
                        ResultCount: $"本地共 {filtered.Count} 项",
                        HasMore: false,
                        RequiresSignIn: false,
                        LocalCount: localCount);
                }

                int currentLocalCount = db.GetLocalFavoritesCount();
                string url = !string.IsNullOrEmpty(req.ExplicitHref)
                    ? req.ExplicitHref!
                    : new FavListUrlBuilder
                    {
                        FavCat = req.Slot,
                        Index = req.PageIndex,
                        Keyword = req.Keyword
                    }.Build(settings.GallerySite);

                FavoritesParser.Result result = await engine
                    .GetFavoritesAsync(url, callApi: true, ct)
                    .ConfigureAwait(false);
                return ToPageData(result, currentLocalCount);
            },
            batchModify: async (currentSlot, galleries, dstCat, ct) =>
            {
                long[] gids = galleries.Select(static g => g.Gid).ToArray();

                if (currentSlot == FavListUrlBuilder.FavCatLocal)
                {
                    if (dstCat < 0)
                    {
                        db.RemoveLocalFavorites(gids);
                        return null;
                    }

                    string[] tokens = galleries.Select(static g => g.Token ?? string.Empty).ToArray();
                    await engine.AddFavoritesRangeAsync(gids, tokens, dstCat, ct).ConfigureAwait(false);
                    return null;
                }

                if (dstCat == FavListUrlBuilder.FavCatLocal)
                {
                    db.PutLocalFavorites(galleries);
                    return null;
                }

                string url = new FavListUrlBuilder { FavCat = currentSlot }.Build(settings.GallerySite);
                FavoritesParser.Result modified = await browse
                    .ModifyFavoritesAsync(url, gids, dstCat, callApi: true, ct)
                    .ConfigureAwait(false);
                int localCount = db.GetLocalFavoritesCount();
                return ToPageData(modified, localCount);
            },
            batchDownload: selected =>
            {
                DownloadManager dl = services.GetRequiredService<DownloadManager>();
                foreach (GalleryInfo info in selected)
                {
                    dl.Enqueue(info);
                }
            },
            applySortOrder: sort =>
            {
                services.UpdateSettings(s => s.FavoritesSortOrder = sort);
                uconfigSync.ApplySettings(services.Settings);
            },
            initialSlot: initialSlot,
            initialSortOrder: initialSort);
    }

    public static TopListViewModel CreateTopListViewModel(AppServices services)
    {
        var engine = services.GetRequiredService<EhEngine>();
        return new TopListViewModel(async ct =>
            await engine.GetTopListAsync(EhUrl.GetTopListUrl(), ct).ConfigureAwait(false));
    }

    public static GalleryDetailActions BuildDetailActions(
        IShellCoordinator shell,
        HashSet<long> favoriteOverrides)
    {
        AppServices services = shell.Services;
        var browse = services.GetRequiredService<GalleryBrowseService>();
        CancellationToken ct = shell.LifetimeToken;

        return new GalleryDetailActions(
            ReadAsync: shell.OpenReaderAsync,
            DownloadAsync: detail =>
            {
                shell.EnqueueDownload(detail);
                return Task.CompletedTask;
            },
            ToggleFavoriteAsync: detail =>
            {
                bool isFav = detail.IsFavorited || favoriteOverrides.Contains(detail.Gid);
                return SaveFavoriteAsync(shell, favoriteOverrides, detail, isFav ? -1 : 0, null);
            },
            LoadPreviewAsync: async (tile, tileCt) =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, tileCt);
                return await LoadPreviewTileAsync(services, tile, linked.Token).ConfigureAwait(true);
            },
            SearchTagAsync: chip => shell.OpenSearchKeywordAsync(chip.SearchQuery, pushToBackstack: true),
            OpenInNewWindowAsync: detail => shell.OpenReaderInNewWindowAsync(detail),
            RateAsync: (detail, rating) => browse.RateGalleryAsync(detail, rating, ct),
            CommentAsync: (detail, comment) => browse.CommentGalleryAsync(detail, comment, ct),
            SaveFavoriteAsync: (detail, dstCat, note) => SaveFavoriteAsync(shell, favoriteOverrides, detail, dstCat, note),
            GetArchiverAsync: detail => browse.GetArchiverAsync(detail, ct),
            DownloadArchiverAsync: (postUrl, refererUrl, dltype, dlcheck) =>
                browse.RequestArchiverDownloadUrlAsync(postUrl, refererUrl, dltype, dlcheck, ct),
            VoteCommentAsync: (detail, commentId, vote) =>
                browse.VoteCommentAsync(detail, commentId, vote, ct),
            GetEditCommentAsync: (detail, commentId) =>
                browse.GetEditCommentAsync(detail, commentId, ct),
            EditCommentAsync: (detail, commentId, body) =>
                browse.EditCommentAsync(detail, commentId, body, ct),
            GetTorrentListAsync: detail => browse.GetTorrentListAsync(detail, ct),
            DownloadTorrentAsync: (detail, torrent) => DownloadTorrentAsync(shell, detail, torrent, ct),
            StartArchiverDownloadAsync: (detail, url, res, autoImport) =>
            {
                var archiver = services.GetRequiredService<ArchiverDownloadManager>();
                string title = EhUtils.GetSuitableTitle(detail) ?? $"画廊 {detail.Gid}";
                archiver.Enqueue(detail.Gid, title, url, res, autoImportToLibrary: autoImport);
                shell.SetStatus($"已加入归档下载队列：{title}");
                return Task.CompletedTask;
            },
            GetArchiveListAsync: detail => browse.GetArchiveListAsync(detail, ct),
            DownloadHathArchiveAsync: async (detail, paramOr, res) =>
            {
                await browse.DownloadHathArchiveAsync(detail, paramOr, res, ct).ConfigureAwait(true);
                shell.SetStatus($"已发送 H@H 归档任务：{detail.Title} ({res})");
            },
            SearchSimilarAsync: detail =>
            {
                string raw = EhUtils.GetSuitableTitle(detail) ?? detail.Title ?? string.Empty;
                string? extracted = EhUtils.ExtractTitle(raw);
                string query = string.IsNullOrWhiteSpace(extracted) ? raw : extracted;
                if (string.IsNullOrWhiteSpace(query))
                {
                    shell.SetStatus("无法从当前画廊标题提取搜索关键词。");
                    return Task.CompletedTask;
                }

                return shell.OpenSearchKeywordAsync(query, pushToBackstack: true);
            },
            OpenGalleryDetailAsync: shell.OpenDetailAsync,
            OpenUrlAsync: shell.OpenExternalOrInAppUrlAsync);
    }

    public static async Task SaveFavoriteAsync(
        IShellCoordinator shell,
        HashSet<long> favoriteOverrides,
        GalleryDetail detail,
        int dstCat,
        string? note)
    {
        AppServices services = shell.Services;
        var db = services.GetRequiredService<EhDatabase>();

        if (dstCat == FavListUrlBuilder.FavCatLocal)
        {
            db.PutLocalFavorite(detail);
            favoriteOverrides.Add(detail.Gid);
            detail.IsFavorited = true;
            detail.FavoriteSlot = FavListUrlBuilder.FavCatLocal;
            shell.SetStatus("已保存到本地收藏。");
            return;
        }

        if (dstCat < 0)
        {
            db.RemoveLocalFavorites(detail.Gid);
            favoriteOverrides.Remove(detail.Gid);
            bool wasCloudFavorite = detail.IsFavorited && detail.FavoriteSlot >= 0;
            detail.IsFavorited = false;
            detail.FavoriteSlot = -1;

            if (wasCloudFavorite && services.GetRequiredService<ICookieStore>().HasSignedIn)
            {
                var browse = services.GetRequiredService<GalleryBrowseService>();
                await browse.AddFavoritesAsync(detail, -1, note, shell.LifetimeToken).ConfigureAwait(true);
            }

            shell.SetStatus("已取消收藏。");
            return;
        }

        var cloudBrowse = services.GetRequiredService<GalleryBrowseService>();
        await cloudBrowse.AddFavoritesAsync(detail, dstCat, note, shell.LifetimeToken).ConfigureAwait(true);

        favoriteOverrides.Add(detail.Gid);
        detail.IsFavorited = true;
        detail.FavoriteSlot = dstCat;
        shell.SetStatus($"已加入收藏夹 {dstCat}。");
    }

    public static async Task<int?> PickFavoriteSlotDialogAsync(
        IShellCoordinator shell,
        HashSet<long> favoriteOverrides,
        GalleryInfo info)
    {
        XamlRoot? xamlRoot = shell.HostWindow.Content?.XamlRoot;
        if (xamlRoot is null)
        {
            return null;
        }

        bool signedIn = shell.Services.GetRequiredService<ICookieStore>().HasSignedIn;
        var items = new List<(string Label, int Slot)> { ("本地收藏 (无需登录)", FavListUrlBuilder.FavCatLocal) };
        if (signedIn)
        {
            for (int i = 0; i <= 9; i++)
            {
                items.Add(($"云端收藏夹 {i}", i));
            }
        }
        items.Add(("取消收藏 / 移除", -2));

        var slotCombo = new ComboBox
        {
            Header = "目标收藏夹",
            ItemsSource = items.Select(static x => x.Label).ToList(),
            SelectedIndex = 0,
            MinWidth = 280
        };
        var noteBox = new TextBox
        {
            Header = "收藏备注 (可选)",
            PlaceholderText = "输入备注..."
        };

        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = EhUtils.GetSuitableTitle(info, shell.Services.Settings.ShowJpnTitle) ?? "添加收藏",
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel { Spacing = 10, Children = { slotCombo, noteBox } }
        };

        if (await dialog.ShowAsync().AsTask().ConfigureAwait(true) != ContentDialogResult.Primary)
        {
            return null;
        }

        int slot = items[Math.Clamp(slotCombo.SelectedIndex, 0, items.Count - 1)].Slot;
        var db = shell.Services.GetRequiredService<EhDatabase>();
        if (slot == FavListUrlBuilder.FavCatLocal)
        {
            db.PutLocalFavorite(info);
            info.FavoriteSlot = FavListUrlBuilder.FavCatLocal;
            favoriteOverrides.Add(info.Gid);
            shell.SetStatus("已添加到本地收藏。");
        }
        else if (slot < -1)
        {
            db.RemoveLocalFavorites(info.Gid);
            favoriteOverrides.Remove(info.Gid);
            info.FavoriteSlot = -2;
            shell.SetStatus("已移除本地收藏。");
        }
        else
        {
            var browse = shell.Services.GetRequiredService<GalleryBrowseService>();
            var detail = new GalleryDetail
            {
                Gid = info.Gid,
                Token = info.Token,
                Title = info.Title,
                TitleJpn = info.TitleJpn,
                Thumb = info.Thumb,
                Category = info.Category,
                Posted = info.Posted,
                Uploader = info.Uploader,
                Rating = info.Rating,
                Pages = info.Pages
            };
            await browse.AddFavoritesAsync(detail, slot, noteBox.Text.Trim(), shell.LifetimeToken).ConfigureAwait(true);
            info.FavoriteSlot = slot;
            favoriteOverrides.Add(info.Gid);
            shell.SetStatus($"已加入云端收藏夹 {slot}。");
        }

        return slot;
    }

    public static async Task<string?> DownloadTorrentAsync(
        IShellCoordinator shell,
        GalleryDetail detail,
        TorrentInfo torrent,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(torrent.Url) ||
            !Uri.TryCreate(torrent.Url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("无效的种子下载地址。");
        }

        HttpClient client = shell.Services.GetRequiredService<EhHttpClientFactory>().StandardClient;
        byte[] bytes = await client.GetByteArrayAsync(uri, ct).ConfigureAwait(true);

        string downloadRoot = shell.Services.Paths.GetEffectiveDownloadDirectory(shell.Services.Settings);
        string torrentDir = Path.Combine(downloadRoot, "_torrents");
        Directory.CreateDirectory(torrentDir);

        string safeName = FileUtils.SanitizeFilename(string.IsNullOrWhiteSpace(torrent.Name)
            ? $"{detail.Gid}.torrent"
            : torrent.Name);
        if (!safeName.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
        {
            safeName += ".torrent";
        }

        string destPath = Path.Combine(torrentDir, safeName);
        await File.WriteAllBytesAsync(destPath, bytes, ct).ConfigureAwait(true);
        shell.SetStatus($"种子已保存：{destPath}");
        return destPath;
    }

    public static async Task OpenExternalOrInAppUrlAsync(IShellCoordinator shell, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        if (GalleryDetailUrlParser.Parse(url, strict: false) is { } detailTarget)
        {
            await shell.OpenDetailAsync(new GalleryInfo { Gid = detailTarget.Gid, Token = detailTarget.Token }).ConfigureAwait(true);
            return;
        }

        if (GalleryPageUrlParser.Parse(url, strict: false) is { } pageTarget)
        {
            var engine = shell.Services.GetRequiredService<EhEngine>();
            string token = await engine
                .GetGalleryTokenAsync(pageTarget.Gid, pageTarget.PToken, pageTarget.Page, shell.LifetimeToken)
                .ConfigureAwait(true);
            await shell.OpenReaderAsync(new GalleryDetail { Gid = pageTarget.Gid, Token = token }, pageTarget.Page).ConfigureAwait(true);
            return;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = uri.ToString(), UseShellExecute = true });
            }
            catch
            {
                ClipboardService.CopyText(uri.ToString());
                shell.SetStatus("无法启动浏览器，链接已复制到剪贴板。");
            }
        }
    }

    public static async Task<WriteableBitmap?> LoadThumbnailAsync(
        AppServices services,
        string? thumbUrl,
        int decodeWidth,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(thumbUrl))
        {
            return null;
        }

        var thumbnails = services.GetRequiredService<ThumbnailService>();
        DecodedPixelBuffer? pixels = await thumbnails
            .LoadPixelsAsync(thumbUrl, decodeWidth, clip: null, ct)
            .ConfigureAwait(true);

        return pixels is null ? null : SkiaBitmapHelper.ToWriteableBitmap(pixels);
    }

    public static async Task<WriteableBitmap?> LoadPreviewTileAsync(
        AppServices services,
        PreviewTileViewModel tile,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tile.ImageUrl))
        {
            return null;
        }

        SKRectI? clip = null;
        if (tile.IsClipped)
        {
            clip = new SKRectI(
                tile.OffsetX,
                tile.OffsetY,
                tile.OffsetX + tile.ClipWidth,
                tile.OffsetY + tile.ClipHeight);
        }

        var thumbnails = services.GetRequiredService<ThumbnailService>();
        DecodedPixelBuffer? buffer = await thumbnails
            .LoadPixelsAsync(tile.ImageUrl, tile.IsClipped ? int.MaxValue : 240, clip, ct)
            .ConfigureAwait(true);

        return buffer is null ? null : SkiaBitmapHelper.ToWriteableBitmap(buffer);
    }

    public static async Task ShowFilterManagerDialogAsync(IShellCoordinator shell)
    {
        XamlRoot? xamlRoot = shell.HostWindow.Content?.XamlRoot;
        if (xamlRoot is null)
        {
            return;
        }

        try
        {
            EhFilter filterService = shell.Services.GetRequiredService<EhFilter>();
            var ruleList = new StackPanel { Spacing = 6 };

            static string ModeLabel(int mode) => mode switch
            {
                EhFilter.ModeTitle => "标题",
                EhFilter.ModeUploader => "上传者",
                EhFilter.ModeTag => "标签",
                EhFilter.ModeTagNamespace => "标签命名空间",
                _ => "未知"
            };

            void RebuildRules()
            {
                ruleList.Children.Clear();
                var allRules = filterService.TitleFilters
                    .Concat(filterService.UploaderFilters)
                    .Concat(filterService.TagFilters)
                    .Concat(filterService.TagNamespaceFilters)
                    .ToList();

                if (allRules.Count == 0)
                {
                    ruleList.Children.Add(new TextBlock
                    {
                        Text = "暂无屏蔽规则。",
                        Foreground = Application.Current.Resources["EhTextSecondaryBrush"] as Brush
                    });
                    return;
                }

                foreach (FilterRule rule in allRules)
                {
                    var row = new Grid { ColumnSpacing = 8 };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var toggle = new CheckBox
                    {
                        Content = $"[{ModeLabel(rule.Mode)}]",
                        IsChecked = rule.IsEnabled
                    };
                    toggle.Click += (_, _) => filterService.TriggerFilter(rule);

                    var textBlock = new TextBlock
                    {
                        Text = rule.Text ?? string.Empty,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    var delBtn = new Button { Content = "删除" };
                    delBtn.Click += (_, _) =>
                    {
                        filterService.DeleteFilter(rule);
                        RebuildRules();
                    };

                    Grid.SetColumn(toggle, 0);
                    Grid.SetColumn(textBlock, 1);
                    Grid.SetColumn(delBtn, 2);
                    row.Children.Add(toggle);
                    row.Children.Add(textBlock);
                    row.Children.Add(delBtn);
                    ruleList.Children.Add(row);
                }
            }

            RebuildRules();

            var modePicker = new ComboBox
            {
                MinWidth = 140,
                SelectedIndex = 0,
                ItemsSource = new[] { "标题", "上传者", "标签", "标签命名空间" }
            };
            var textInput = new TextBox { PlaceholderText = "输入要屏蔽的关键词或标签...", MinWidth = 220 };
            var addBtn = new Button { Content = "添加" };
            addBtn.Click += (_, _) =>
            {
                string value = textInput.Text.Trim();
                if (value.Length == 0)
                {
                    return;
                }

                filterService.AddFilter(new FilterRule
                {
                    Mode = Math.Clamp(modePicker.SelectedIndex, 0, 3),
                    Text = value,
                    Enable = true
                });
                textInput.Text = string.Empty;
                RebuildRules();
            };

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "本地过滤规则 (EhFilter)",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Close,
                Content = new ScrollViewer
                {
                    MaxHeight = 480,
                    MinWidth = 480,
                    Content = new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { modePicker, textInput, addBtn } },
                            ruleList
                        }
                    }
                }
            };

            await dialog.ShowAsync().AsTask().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            shell.SetStatus($"过滤规则管理失败：{ex.Message}");
        }
    }
}
