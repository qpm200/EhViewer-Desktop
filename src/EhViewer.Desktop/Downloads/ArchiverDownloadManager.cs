using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using EhViewer.Core.Util;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EhViewer.Desktop.Downloads;

public enum ArchiverTaskState
{
    Queued,
    Running,
    Paused,
    Completed,
    Failed,
    Cancelled
}

public sealed record ArchiverTaskSnapshot(
    string TaskId,
    long Gid,
    string Title,
    string DownloadUrl,
    string ResolutionLabel,
    string FilePath,
    ArchiverTaskState State,
    long BytesReceived,
    long? TotalBytes,
    string? ErrorMessage,
    bool AutoImportToLibrary,
    string? ImportedDirectory)
{
    public double ProgressFraction =>
        TotalBytes is > 0
            ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0.0, 1.0)
            : (State == ArchiverTaskState.Completed ? 1.0 : 0.0);

    public bool IsActive => State is ArchiverTaskState.Queued or ArchiverTaskState.Running;

    public bool CanPause => State is ArchiverTaskState.Queued or ArchiverTaskState.Running;

    public bool CanResume => State is ArchiverTaskState.Paused or ArchiverTaskState.Failed or ArchiverTaskState.Cancelled;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public double ProgressPercent => ProgressFraction * 100.0;

    public string StateDisplay => State switch
    {
        ArchiverTaskState.Queued => "排队中",
        ArchiverTaskState.Running => "下载中",
        ArchiverTaskState.Paused => "已暂停",
        ArchiverTaskState.Completed => "已完成",
        ArchiverTaskState.Failed => "失败",
        ArchiverTaskState.Cancelled => "已取消",
        _ => State.ToString()
    };

    public string ProgressDisplay
    {
        get
        {
            string recv = FormatBytes(BytesReceived);
            return TotalBytes is > 0 ? $"{recv} / {FormatBytes(TotalBytes.Value)}" : recv;
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024)
        {
            return $"{kb:F1} KB";
        }

        double mb = kb / 1024.0;
        if (mb < 1024)
        {
            return $"{mb:F1} MB";
        }

        return $"{mb / 1024.0:F2} GB";
    }
}

/// <summary>
/// Manages HTTP-Range resumable downloads for E-Hentai Archiver <c>.zip</c> packages,
/// supporting pause/resume via <c>.part</c> files and optional extraction into the local library.
/// </summary>
public sealed class ArchiverDownloadManager : IDisposable
{
    private const int BufferSize = 64 * 1024;
    private readonly Func<HttpClient> _clientFactory;
    private readonly Func<string> _downloadRootResolver;
    private readonly LocalLibraryScanner? _scanner;
    private readonly ILogger<ArchiverDownloadManager> _logger;
    private readonly SemaphoreSlim _concurrencyGate = new(2, 2);
    private readonly object _gate = new();
    private readonly List<TaskEntry> _orderedEntries = [];
    private readonly ConcurrentDictionary<string, TaskEntry> _entries = new(StringComparer.Ordinal);
    private bool _disposed;

    public ArchiverDownloadManager(
        Func<HttpClient> clientFactory,
        Func<string> downloadRootResolver,
        LocalLibraryScanner? scanner = null,
        ILogger<ArchiverDownloadManager>? logger = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _downloadRootResolver = downloadRootResolver ?? throw new ArgumentNullException(nameof(downloadRootResolver));
        _scanner = scanner;
        _logger = logger ?? NullLogger<ArchiverDownloadManager>.Instance;
    }

    public event EventHandler<ArchiverTaskSnapshot>? TaskUpdated;

    public event EventHandler? TasksChanged;

    public IReadOnlyList<ArchiverTaskSnapshot> GetSnapshots()
    {
        lock (_gate)
        {
            return _orderedEntries.Select(static e => e.ToSnapshot()).ToArray();
        }
    }

    public ArchiverTaskSnapshot? TryGetSnapshot(string taskId)
    {
        if (string.IsNullOrEmpty(taskId) || !_entries.TryGetValue(taskId, out TaskEntry? entry))
        {
            return null;
        }

        lock (_gate)
        {
            return entry.ToSnapshot();
        }
    }

    public string Enqueue(
        long gid,
        string title,
        string downloadUrl,
        string resolutionLabel,
        bool autoImportToLibrary = false,
        string? customFileName = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("无效的归档下载地址。", nameof(downloadUrl));
        }

        string downloadRoot = _downloadRootResolver();
        string archiveDir = Path.Combine(downloadRoot, "_archives");
        Directory.CreateDirectory(archiveDir);

        string safeBase = FileUtils.SanitizeFilename(string.IsNullOrWhiteSpace(customFileName)
            ? $"{gid}-{title}-{resolutionLabel}"
            : Path.GetFileNameWithoutExtension(customFileName));
        if (string.IsNullOrWhiteSpace(safeBase))
        {
            safeBase = gid.ToString(CultureInfo.InvariantCulture);
        }

        string targetPath = Path.Combine(archiveDir, $"{safeBase}.zip");
        string taskId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var entry = new TaskEntry
        {
            TaskId = taskId,
            Gid = gid,
            Title = string.IsNullOrWhiteSpace(title) ? $"画廊 {gid}" : title,
            DownloadUrl = downloadUrl,
            ResolutionLabel = string.IsNullOrWhiteSpace(resolutionLabel) ? "archiver" : resolutionLabel,
            FilePath = targetPath,
            State = ArchiverTaskState.Queued,
            AutoImportToLibrary = autoImportToLibrary
        };

        lock (_gate)
        {
            _entries[taskId] = entry;
            _orderedEntries.Insert(0, entry);
        }

        TasksChanged?.Invoke(this, EventArgs.Empty);
        StartEntryWorker(entry);
        return taskId;
    }

    public Task<ArchiverTaskSnapshot> WaitForCompletionAsync(string taskId, CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(taskId, out TaskEntry? entry))
        {
            throw new KeyNotFoundException($"找不到归档任务 {taskId}。");
        }

        TaskCompletionSource<ArchiverTaskSnapshot>? tcs;
        lock (_gate)
        {
            tcs = entry.CompletionSource;
            if (tcs is null)
            {
                return Task.FromResult(entry.ToSnapshot());
            }
        }

        return tcs.Task.WaitAsync(cancellationToken);
    }

    public bool Pause(string taskId)
    {
        if (!_entries.TryGetValue(taskId, out TaskEntry? entry))
        {
            return false;
        }

        CancellationTokenSource? cts;
        lock (_gate)
        {
            if (!entry.IsActiveState)
            {
                return false;
            }

            entry.PauseRequested = true;
            entry.CancelRequested = false;
            cts = entry.ActiveCts;
            if (entry.State == ArchiverTaskState.Queued)
            {
                entry.State = ArchiverTaskState.Paused;
                NotifyEntryLocked(entry);
            }
        }

        cts?.Cancel();
        return true;
    }

    public bool Resume(string taskId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_entries.TryGetValue(taskId, out TaskEntry? entry))
        {
            return false;
        }

        lock (_gate)
        {
            if (entry.IsActiveState || entry.State == ArchiverTaskState.Completed)
            {
                return false;
            }

            entry.PauseRequested = false;
            entry.CancelRequested = false;
            entry.ErrorMessage = null;
            entry.State = ArchiverTaskState.Queued;
            NotifyEntryLocked(entry);
        }

        StartEntryWorker(entry);
        return true;
    }

    public bool Cancel(string taskId, bool deletePartialFile = true)
    {
        if (!_entries.TryGetValue(taskId, out TaskEntry? entry))
        {
            return false;
        }

        CancellationTokenSource? cts;
        string partPath = GetPartPath(entry.FilePath);
        bool deleteNow;
        lock (_gate)
        {
            entry.CancelRequested = true;
            entry.PauseRequested = false;
            entry.DeletePartialOnCancel = deletePartialFile;
            cts = entry.ActiveCts;
            deleteNow = entry.State != ArchiverTaskState.Running;
            if (deleteNow)
            {
                entry.State = ArchiverTaskState.Cancelled;
                if (deletePartialFile)
                {
                    entry.BytesReceived = 0;
                }

                NotifyEntryLocked(entry);
            }
        }

        cts?.Cancel();
        if (deleteNow && deletePartialFile)
        {
            TryDeleteFile(partPath);
        }

        return true;
    }

    public void Remove(string taskId)
    {
        if (!_entries.TryRemove(taskId, out TaskEntry? entry))
        {
            return;
        }

        CancellationTokenSource? cts;
        lock (_gate)
        {
            _orderedEntries.Remove(entry);
            entry.CancelRequested = true;
            cts = entry.ActiveCts;
        }

        cts?.Cancel();
        TryDeleteFile(GetPartPath(entry.FilePath));
        TasksChanged?.Invoke(this, EventArgs.Empty);
    }

    private void StartEntryWorker(TaskEntry entry)
    {
        lock (_gate)
        {
            entry.ActiveCts?.Dispose();
            entry.ActiveCts = new CancellationTokenSource();
            entry.CompletionSource = new TaskCompletionSource<ArchiverTaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _ = Task.Run(() => ExecuteEntryAsync(entry));
    }

    private async Task ExecuteEntryAsync(TaskEntry entry)
    {
        CancellationToken token;
        TaskCompletionSource<ArchiverTaskSnapshot> tcs;
        lock (_gate)
        {
            token = entry.ActiveCts?.Token ?? CancellationToken.None;
            tcs = entry.CompletionSource ?? new TaskCompletionSource<ArchiverTaskSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (entry.PauseRequested || entry.CancelRequested)
            {
                entry.State = entry.PauseRequested ? ArchiverTaskState.Paused : ArchiverTaskState.Cancelled;
                ArchiverTaskSnapshot snap = entry.ToSnapshot();
                NotifyEntryLocked(entry, snap);
                tcs.TrySetResult(snap);
                return;
            }
        }

        bool gateAcquired = false;
        try
        {
            await _concurrencyGate.WaitAsync(token).ConfigureAwait(false);
            gateAcquired = true;

            lock (_gate)
            {
                if (entry.PauseRequested || entry.CancelRequested || token.IsCancellationRequested)
                {
                    entry.State = entry.PauseRequested ? ArchiverTaskState.Paused : ArchiverTaskState.Cancelled;
                    ArchiverTaskSnapshot snap = entry.ToSnapshot();
                    NotifyEntryLocked(entry, snap);
                    tcs.TrySetResult(snap);
                    return;
                }

                entry.State = ArchiverTaskState.Running;
                entry.ErrorMessage = null;
                NotifyEntryLocked(entry);
            }

            await DownloadWithRangeAsync(entry, token).ConfigureAwait(false);

            string? importedDir = null;
            if (entry.AutoImportToLibrary && _scanner is not null)
            {
                string root = _downloadRootResolver();
                importedDir = _scanner.ImportArchive(entry.FilePath, root, entry.Gid, entry.Title);
            }

            ArchiverTaskSnapshot completed;
            lock (_gate)
            {
                entry.State = ArchiverTaskState.Completed;
                entry.ImportedDirectory = importedDir;
                if (entry.TotalBytes is null or <= 0)
                {
                    entry.TotalBytes = entry.BytesReceived;
                }

                completed = entry.ToSnapshot();
                NotifyEntryLocked(entry, completed);
            }

            tcs.TrySetResult(completed);
        }
        catch (OperationCanceledException)
        {
            string partPath = GetPartPath(entry.FilePath);
            bool deletePart;
            ArchiverTaskSnapshot snapshot;
            lock (_gate)
            {
                if (entry.PauseRequested)
                {
                    entry.State = ArchiverTaskState.Paused;
                    deletePart = false;
                }
                else
                {
                    entry.State = ArchiverTaskState.Cancelled;
                    deletePart = entry.DeletePartialOnCancel;
                    if (deletePart)
                    {
                        entry.BytesReceived = 0;
                    }
                }

                snapshot = entry.ToSnapshot();
                NotifyEntryLocked(entry, snapshot);
            }

            if (deletePart)
            {
                TryDeleteFile(partPath);
            }

            tcs.TrySetResult(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "归档下载失败：gid={Gid}, url={Url}", entry.Gid, entry.DownloadUrl);
            ArchiverTaskSnapshot failed;
            lock (_gate)
            {
                entry.State = ArchiverTaskState.Failed;
                entry.ErrorMessage = ex.Message;
                failed = entry.ToSnapshot();
                NotifyEntryLocked(entry, failed);
            }

            tcs.TrySetResult(failed);
        }
        finally
        {
            if (gateAcquired)
            {
                _concurrencyGate.Release();
            }
        }
    }

    private async Task DownloadWithRangeAsync(TaskEntry entry, CancellationToken token)
    {
        string partPath = GetPartPath(entry.FilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(entry.FilePath)!);

        long existingBytes = 0;
        if (File.Exists(partPath))
        {
            existingBytes = new FileInfo(partPath).Length;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, entry.DownloadUrl);
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        HttpClient client = _clientFactory();
        using HttpResponseMessage response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingBytes > 0)
        {
            long? rangeTotal = response.Content.Headers.ContentRange?.Length;
            if (rangeTotal.HasValue && rangeTotal.Value == existingBytes)
            {
                lock (_gate)
                {
                    entry.BytesReceived = existingBytes;
                    entry.TotalBytes = rangeTotal.Value;
                }

                File.Move(partPath, entry.FilePath, overwrite: true);
                return;
            }

            TryDeleteFile(partPath);
            existingBytes = 0;
            using var restartReq = new HttpRequestMessage(HttpMethod.Get, entry.DownloadUrl);
            using HttpResponseMessage restartResp = await client
                .SendAsync(restartReq, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            restartResp.EnsureSuccessStatusCode();
            await StreamToFileAsync(entry, restartResp, partPath, appendOffset: 0, token).ConfigureAwait(false);
            File.Move(partPath, entry.FilePath, overwrite: true);
            return;
        }

        response.EnsureSuccessStatusCode();

        bool isPartial = response.StatusCode == HttpStatusCode.PartialContent && existingBytes > 0;
        long offset = isPartial ? existingBytes : 0;
        await StreamToFileAsync(entry, response, partPath, offset, token).ConfigureAwait(false);

        File.Move(partPath, entry.FilePath, overwrite: true);
    }

    private async Task StreamToFileAsync(
        TaskEntry entry,
        HttpResponseMessage response,
        string partPath,
        long appendOffset,
        CancellationToken token)
    {
        long? totalBytes = response.Content.Headers.ContentRange?.Length;
        if (!totalBytes.HasValue && response.Content.Headers.ContentLength is { } contentLen)
        {
            totalBytes = appendOffset + contentLen;
        }

        long received = appendOffset;
        lock (_gate)
        {
            entry.BytesReceived = received;
            if (totalBytes.HasValue)
            {
                entry.TotalBytes = totalBytes.Value;
            }

            NotifyEntryLocked(entry);
        }

        await using var networkStream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            partPath,
            appendOffset > 0 ? FileMode.OpenOrCreate : FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            BufferSize,
            useAsync: true);

        if (appendOffset > 0)
        {
            fileStream.Seek(appendOffset, SeekOrigin.Begin);
        }

        byte[] buffer = new byte[BufferSize];
        long lastNotifiedBytes = received;

        try
        {
            while (true)
            {
                int read = await networkStream.ReadAsync(buffer, token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;

                lock (_gate)
                {
                    entry.BytesReceived = received;
                    if (received - lastNotifiedBytes >= 64 * 1024)
                    {
                        lastNotifiedBytes = received;
                        NotifyEntryLocked(entry);
                    }
                }
            }
        }
        finally
        {
            try
            {
                await fileStream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }

            lock (_gate)
            {
                entry.BytesReceived = received;
                NotifyEntryLocked(entry);
            }
        }
    }

    private void NotifyEntryLocked(TaskEntry entry, ArchiverTaskSnapshot? snapshot = null)
    {
        ArchiverTaskSnapshot snap = snapshot ?? entry.ToSnapshot();
        TaskUpdated?.Invoke(this, snap);
    }

    private static string GetPartPath(string finalPath) => finalPath + ".part";

    private static void TryDeleteFile(string path)
    {
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

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        List<CancellationTokenSource?> ctsList = [];
        lock (_gate)
        {
            foreach (TaskEntry entry in _orderedEntries)
            {
                entry.CancelRequested = true;
                ctsList.Add(entry.ActiveCts);
                entry.ActiveCts = null;
            }
        }

        foreach (CancellationTokenSource? cts in ctsList)
        {
            try
            {
                cts?.Cancel();
                cts?.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        _concurrencyGate.Dispose();
    }

    private sealed class TaskEntry
    {
        public required string TaskId { get; init; }
        public required long Gid { get; init; }
        public required string Title { get; init; }
        public required string DownloadUrl { get; init; }
        public required string ResolutionLabel { get; init; }
        public required string FilePath { get; init; }
        public ArchiverTaskState State { get; set; }
        public long BytesReceived { get; set; }
        public long? TotalBytes { get; set; }
        public string? ErrorMessage { get; set; }
        public bool AutoImportToLibrary { get; init; }
        public string? ImportedDirectory { get; set; }
        public bool PauseRequested { get; set; }
        public bool CancelRequested { get; set; }
        public bool DeletePartialOnCancel { get; set; } = true;
        public CancellationTokenSource? ActiveCts { get; set; }
        public TaskCompletionSource<ArchiverTaskSnapshot>? CompletionSource { get; set; }

        public bool IsActiveState => State is ArchiverTaskState.Queued or ArchiverTaskState.Running;

        public ArchiverTaskSnapshot ToSnapshot() => new(
            TaskId,
            Gid,
            Title,
            DownloadUrl,
            ResolutionLabel,
            FilePath,
            State,
            BytesReceived,
            TotalBytes,
            ErrorMessage,
            AutoImportToLibrary,
            ImportedDirectory);
    }
}
