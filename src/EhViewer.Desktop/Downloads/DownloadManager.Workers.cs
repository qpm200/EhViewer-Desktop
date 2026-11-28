using System.Net;
using System.Net.Http.Headers;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Exceptions;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Network;
using EhViewer.Core.Spider;
using EhViewer.Desktop.Reader;
using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Downloads;

public sealed partial class DownloadManager
{
    private async Task WorkerLoopAsync(CancellationToken shutdownToken)
    {
        try
        {
            await foreach (DownloadTask task in _queue.Reader.ReadAllAsync(shutdownToken).ConfigureAwait(false))
            {
                lock (task)
                {
                    if (task.Cancellation.IsCancellationRequested || task.PauseRequested)
                    {
                        _tasks.TryRemove(task.Gid, out _);
                        continue;
                    }

                    task.IsActiveInWorker = true;
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, task.Cancellation.Token);
                try
                {
                    await RunTaskAsync(task, linked.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is OperationCanceledException or CancelledException)
                {
                    if (task.PauseRequested)
                    {
                        Publish(new DownloadProgress(
                            task.Gid,
                            task.Info.Title ?? string.Empty,
                            DownloadState.None,
                            task.LastFinished,
                            task.LastTotal,
                            task.LastBytes,
                            task.PauseReason ?? "已暂停",
                            task.Label,
                            IsPaused: true,
                            DirectoryPath: BuildGalleryFolder(EffectiveDownloadRoot, task.Info)));
                        SaveRow(task.Info, DownloadState.None, task.LastFinished, task.LastTotal, task.Label);
                    }
                    else
                    {
                        Publish(new DownloadProgress(
                            task.Gid,
                            task.Info.Title ?? string.Empty,
                            DownloadState.None,
                            0,
                            0,
                            0,
                            "已取消",
                            task.Label));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "下载失败：{Gid}", task.Gid);
                    Publish(new DownloadProgress(
                        task.Gid,
                        task.Info.Title ?? string.Empty,
                        DownloadState.Failed,
                        task.LastFinished,
                        task.LastTotal,
                        task.LastBytes,
                        ex.Message,
                        task.Label,
                        IsPaused: false,
                        DirectoryPath: BuildGalleryFolder(EffectiveDownloadRoot, task.Info)));
                    SaveRow(task.Info, DownloadState.Failed, finished: task.LastFinished, total: task.LastTotal, label: task.Label);
                }
                finally
                {
                    task.IsActiveInWorker = false;
                    _tasks.TryRemove(task.Gid, out _);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private async Task RunTaskAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        if (_resolveDesktopSettings?.Invoke()?.DownloadPauseOnBattery == true && _isOnBattery())
        {
            task.PauseRequested = true;
            task.PauseReason = "电池供电已暂停";
            throw new OperationCanceledException(cancellationToken);
        }

        EhSite site = _settings?.GallerySite ?? EhSite.E;
        GalleryDetail detail = await _engine
            .GetGalleryDetailAsync(EhUrl.GetGalleryDetailUrl(task.Gid, task.Token, 0, false, site), cancellationToken)
            .ConfigureAwait(false);

        string folder = BuildGalleryFolder(EffectiveDownloadRoot, detail);
        Directory.CreateDirectory(folder);

        int total = detail.Pages > 0 ? detail.Pages : 0;
        task.LastTotal = total;
        Publish(new DownloadProgress(
            task.Gid,
            detail.Title ?? string.Empty,
            DownloadState.Download,
            task.LastFinished,
            total,
            0,
            null,
            task.Label,
            IsPaused: false,
            DirectoryPath: folder));

        string progressFile = Path.Combine(folder, ".ehviewer");

        // Reuse the existing progress document so a resume keeps the pToken map written by a
        // previous run; overwriting it would force every already-downloaded page to be re-resolved.
        SpiderInfo? existing = ReadProgress(progressFile);
        int startPage = existing is not null && existing.StartPage >= 0 ? existing.StartPage : 0;

        var spiderInfo = existing ?? new SpiderInfo();
        spiderInfo.StartPage = startPage;
        spiderInfo.Gid = detail.Gid;
        spiderInfo.Token = detail.Token;
        spiderInfo.Pages = total;
        spiderInfo.PreviewPages = detail.PreviewPages;
        if (spiderInfo.PreviewPerPage < 0)
        {
            spiderInfo.PreviewPerPage = 0;
        }

        var urlSource = new GalleryPageUrlSource(_engine, detail, fallbackPageUrls: null, site);
        int totalPages = total > 0 ? total : urlSource.KnownCount;
        task.LastTotal = totalPages;
        string referer = EhUrl.GetReferer(site);
        int finished = 0;
        for (int existingIdx = 0; existingIdx < startPage; existingIdx++)
        {
            if (TryFindExistingPageFile(folder, existingIdx, out _))
            {
                finished++;
            }
        }

        task.LastFinished = finished;
        long bytes = 0;

        for (int i = startPage; i < totalPages; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_resolveDesktopSettings?.Invoke()?.DownloadPauseOnBattery == true && _isOnBattery())
            {
                task.PauseRequested = true;
                task.PauseReason = "电池供电已暂停";
                throw new OperationCanceledException(cancellationToken);
            }

            string targetPath = BuildImagePath(folder, i);
            if (TryFindExistingPageFile(folder, i, out _))
            {
                finished++;
                task.LastFinished = finished;
                continue;
            }

            string? basePageUrl = await urlSource.ResolvePageUrlAsync(i, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(basePageUrl))
            {
                continue;
            }

            string partPath = Path.ChangeExtension(targetPath, ".part");
            (string? resolvedImageUrl, byte[] payload) = await DownloadPageWithSkipRetryAsync(
                basePageUrl,
                detail,
                i,
                referer,
                partPath,
                cancellationToken).ConfigureAwait(false);

            if (payload.Length == 0 || string.IsNullOrEmpty(resolvedImageUrl))
            {
                continue;
            }

            string extension = GuessExtension(resolvedImageUrl, payload);
            string finalPath = Path.ChangeExtension(targetPath, extension);
            if (File.Exists(partPath))
            {
                File.Move(partPath, finalPath, overwrite: true);
            }
            else
            {
                await File.WriteAllBytesAsync(finalPath, payload, cancellationToken).ConfigureAwait(false);
            }

            bytes += payload.Length;
            finished++;
            task.LastFinished = finished;
            task.LastBytes = bytes;

            spiderInfo.PTokenMap[i] = resolvedImageUrl;
            WriteProgress(progressFile, spiderInfo);

            Publish(new DownloadProgress(
                task.Gid,
                detail.Title ?? string.Empty,
                DownloadState.Download,
                finished,
                totalPages,
                bytes,
                null,
                task.Label,
                IsPaused: false,
                DirectoryPath: folder));
            SaveRow(detail, DownloadState.Download, finished, totalPages, task.Label);
        }

        spiderInfo.StartPage = 0;
        WriteProgress(progressFile, spiderInfo);

        Publish(new DownloadProgress(
            task.Gid,
            detail.Title ?? string.Empty,
            DownloadState.Finish,
            finished,
            totalPages,
            bytes,
            null,
            task.Label,
            IsPaused: false,
            DirectoryPath: folder));
        SaveRow(detail, DownloadState.Finish, finished, totalPages, task.Label);
    }

    private async Task<(string? ImageUrl, byte[] Payload)> DownloadPageWithSkipRetryAsync(
        string basePageUrl,
        GalleryDetail detail,
        int pageIndex,
        string referer,
        string partPath,
        CancellationToken cancellationToken)
    {
        var seenSkipKeys = new HashSet<string>(StringComparer.Ordinal);
        string? skipHathKey = null;

        for (int attempt = 0; attempt < MaxPageDownloadAttempts; attempt++)
        {
            string pageUrl = GalleryPageUrlSource.AppendSkipHathKey(basePageUrl, skipHathKey);
            GalleryPageParser.Result page = await _engine
                .GetGalleryPageAsync(pageUrl, detail.Gid, detail.Token ?? string.Empty, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(page.ImageUrl))
            {
                _logger.LogDebug("第 {Page} 页无图片地址，跳过", pageIndex);
                return (null, []);
            }

            byte[] payload = await DownloadImageAsync(page.ImageUrl, referer, partPath, cancellationToken).ConfigureAwait(false);
            if (payload.Length > 0)
            {
                return (page.ImageUrl, payload);
            }

            if (string.IsNullOrEmpty(page.SkipHathKey) || !seenSkipKeys.Add(page.SkipHathKey))
            {
                break;
            }

            skipHathKey = page.SkipHathKey;
            _logger.LogDebug("第 {Page} 页尝试切换 H@H 节点：nl={Key}", pageIndex, skipHathKey);
        }

        return (null, []);
    }

    private async Task<byte[]> DownloadImageAsync(
        string imageUrl,
        string referer,
        string? partPath,
        CancellationToken cancellationToken)
    {
        int timeoutSeconds = Math.Clamp(_resolveDesktopSettings?.Invoke()?.DownloadTimeoutSeconds ?? 30, 5, 600);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        CancellationToken effectiveToken = timeoutCts.Token;

        try
        {
            long existingLen = 0;
            if (!string.IsNullOrEmpty(partPath) && File.Exists(partPath))
            {
                existingLen = new FileInfo(partPath).Length;
            }

            using HttpRequestMessage request = EhRequestBuilder.Get(imageUrl, referer);
            if (existingLen > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingLen, null);
            }

            HttpClient client = _getImageClient();
            using HttpResponseMessage response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, effectiveToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingLen > 0 && !string.IsNullOrEmpty(partPath))
            {
                long? rangeLength = response.Content.Headers.ContentRange?.Length;
                if (rangeLength.HasValue && rangeLength.Value == existingLen)
                {
                    byte[] existingPayload = await File.ReadAllBytesAsync(partPath, effectiveToken).ConfigureAwait(false);
                    if (!IsPlainText(existingPayload) && !LooksLikeHtmlPayload(existingPayload))
                    {
                        return existingPayload;
                    }
                }

                TryDeleteFileQuietly(partPath);
                return await DownloadImageAsync(imageUrl, referer, partPath, cancellationToken).ConfigureAwait(false);
            }

            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            // Reject unexpected HTML/text responses (plain-text errors, proxy/ISP hijack pages).
            string? mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrEmpty(mediaType)
                && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)))
            {
                TryDeleteFileQuietly(partPath);
                return [];
            }

            byte[] payload;
            if (!string.IsNullOrEmpty(partPath))
            {
                bool append = response.StatusCode == HttpStatusCode.PartialContent && existingLen > 0;
                Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
                await using (Stream netStream = await response.Content.ReadAsStreamAsync(effectiveToken).ConfigureAwait(false))
                await using (var fs = new FileStream(
                    partPath,
                    append ? FileMode.OpenOrCreate : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 65536,
                    useAsync: true))
                {
                    if (append)
                    {
                        fs.Seek(existingLen, SeekOrigin.Begin);
                    }

                    await netStream.CopyToAsync(fs, effectiveToken).ConfigureAwait(false);
                    await fs.FlushAsync(effectiveToken).ConfigureAwait(false);
                }

                payload = await File.ReadAllBytesAsync(partPath, effectiveToken).ConfigureAwait(false);
            }
            else
            {
                payload = await response.Content.ReadAsByteArrayAsync(effectiveToken).ConfigureAwait(false);
            }

            // Upstream step 7: a payload of only bytes <= 126 is an error page, not an image.
            if (IsPlainText(payload) || LooksLikeHtmlPayload(payload))
            {
                TryDeleteFileQuietly(partPath);
                return [];
            }

            return payload;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogDebug(ex, "图片下载超时（{Seconds}s）：{Url}", timeoutSeconds, imageUrl);
            return [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "图片下载失败：{Url}", imageUrl);
            return [];
        }
    }

    private static void TryDeleteFileQuietly(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    internal static bool LooksLikeHtmlPayload(ReadOnlySpan<byte> payload)
    {
        int i = 0;
        while (i < payload.Length && payload[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            i++;
        }

        if (payload.Length - i < 5)
        {
            return false;
        }

        ReadOnlySpan<byte> head = payload.Slice(i, Math.Min(16, payload.Length - i));
        return head.StartsWith("<!DOC"u8)
            || head.StartsWith("<!doc"u8)
            || head.StartsWith("<html"u8)
            || head.StartsWith("<HTML"u8)
            || head.StartsWith("<?xml"u8);
    }

    /// <summary>Upstream anti-error-page check: every byte printable ASCII means it is not an image.</summary>
    internal static bool IsPlainText(ReadOnlySpan<byte> payload)
    {
        if (payload.Length == 0)
        {
            return true;
        }

        foreach (byte b in payload)
        {
            if (b > 126)
            {
                return false;
            }
        }

        return true;
    }

    internal static string GuessExtension(string url, ReadOnlySpan<byte> payload)
    {
        if (payload.Length >= 3 && payload[0] == 0xFF && payload[1] == 0xD8 && payload[2] == 0xFF)
        {
            return ".jpg";
        }

        if (payload.Length >= 8 && payload[0] == 0x89 && payload[1] == 0x50 && payload[2] == 0x4E && payload[3] == 0x47)
        {
            return ".png";
        }

        if (payload.Length >= 12 && payload[0] == (byte)'R' && payload[1] == (byte)'I' && payload[2] == (byte)'F' && payload[3] == (byte)'F')
        {
            return ".webp";
        }

        if (payload.Length >= 3 && payload[0] == (byte)'G' && payload[1] == (byte)'I' && payload[2] == (byte)'F')
        {
            return ".gif";
        }

        string lower = url.ToLowerInvariant();
        foreach (string ext in (string[])[".jpg", ".jpeg", ".png", ".webp", ".gif"])
        {
            if (lower.Contains(ext, StringComparison.Ordinal))
            {
                return ext == ".jpeg" ? ".jpg" : ext;
            }
        }

        return ".jpg";
    }
}
