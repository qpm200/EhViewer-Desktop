using System.Collections.Concurrent;
using System.Threading.Channels;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Network;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Downloads;

/// <summary>Download lifecycle states matching upstream <c>DownloadManager.STATE_*</c>.</summary>
public enum DownloadState
{
    Invalid = -1,
    None = 0,
    Wait = 1,
    Download = 2,
    Finish = 3,
    Failed = 4,
    Update = 5
}

/// <summary>Snapshot of one gallery download.</summary>
public sealed record DownloadProgress(
    long Gid,
    string Title,
    DownloadState State,
    int Done,
    int Total,
    long Bytes,
    string? Error,
    string? Label = null,
    bool IsPaused = false,
    string? DirectoryPath = null);

/// <summary>One incomplete or empty local download folder candidate for cleanup.</summary>
public sealed record InvalidDownloadCandidate(
    string DirectoryPath,
    string FolderName,
    long? Gid,
    string Reason,
    int ExpectedPages,
    int ActualImages);

/// <summary>
/// Gallery download queue. Downloads are enqueued onto an unbounded
/// <see cref="Channel{T}"/> and executed by a bounded concurrent worker pool
/// (<see cref="Concurrency"/> clamped to 1..16); each gallery writes images with the upstream
/// <c>%08d.ext</c> naming scheme, and progress is persisted to <see cref="EhDatabase"/> so a restart
/// resumes instead of restarting.
/// </summary>
public sealed partial class DownloadManager : IDisposable, IAsyncDisposable
{
    private const int MaxPageDownloadAttempts = 4;

    private readonly EhEngine _engine;
    private readonly Func<HttpClient> _getImageClient;
    private readonly EhDatabase _database;
    private readonly AppPaths _paths;
    private readonly IEhClientSettings? _settings;
    private readonly ILogger<DownloadManager> _logger;
    private readonly Func<string>? _resolveDownloadRoot;
    private readonly Func<DesktopSettings?>? _resolveDesktopSettings;
    private readonly Func<bool> _isOnBattery;

    private readonly Channel<DownloadTask> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<long, DownloadProgress> _progress = new();
    private readonly ConcurrentDictionary<long, DownloadTask> _tasks = new();
    private readonly ConcurrentDictionary<long, string?> _labels = new();
    private readonly List<Task> _workers = [];
    private int _disposed;

    public DownloadManager(
        EhEngine engine,
        EhHttpClientFactory clientFactory,
        EhDatabase database,
        AppPaths paths,
        ILogger<DownloadManager> logger,
        int concurrency = 3,
        IEhClientSettings? settings = null,
        Func<string>? resolveDownloadRoot = null,
        Func<DesktopSettings?>? resolveDesktopSettings = null,
        Func<bool>? isOnBattery = null)
        : this(
            engine,
            () => (clientFactory ?? throw new ArgumentNullException(nameof(clientFactory))).ImageClient,
            database,
            paths,
            logger,
            concurrency,
            settings,
            resolveDownloadRoot,
            resolveDesktopSettings,
            isOnBattery)
    {
    }

    internal DownloadManager(
        EhEngine engine,
        Func<HttpClient> getImageClient,
        EhDatabase database,
        AppPaths paths,
        ILogger<DownloadManager> logger,
        int concurrency = 3,
        IEhClientSettings? settings = null,
        Func<string>? resolveDownloadRoot = null,
        Func<DesktopSettings?>? resolveDesktopSettings = null,
        Func<bool>? isOnBattery = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _getImageClient = getImageClient ?? throw new ArgumentNullException(nameof(getImageClient));
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings;
        _resolveDownloadRoot = resolveDownloadRoot;
        _resolveDesktopSettings = resolveDesktopSettings;
        _isOnBattery = isOnBattery ?? WindowsPowerStatus.IsOnBatteryOrSaver;

        Concurrency = Math.Clamp(concurrency, 1, 16);
        _queue = Channel.CreateUnbounded<DownloadTask>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        for (int i = 0; i < Concurrency; i++)
        {
            _workers.Add(Task.Run(() => WorkerLoopAsync(_shutdown.Token)));
        }
    }

    public int Concurrency { get; }

    /// <summary>Raised whenever any download's progress changes.</summary>
    public event Action<DownloadProgress>? ProgressChanged;

    public IReadOnlyCollection<DownloadProgress> Snapshot() => [.. _progress.Values];

    public DownloadProgress? GetProgress(long gid)
        => _progress.TryGetValue(gid, out DownloadProgress? p) ? p : null;

    public string EffectiveDownloadRoot => _resolveDownloadRoot?.Invoke() ?? _paths.DownloadDirectory;

    /// <summary>Queues a gallery for download. Re-queuing a finished gallery resumes from disk.</summary>
    public bool Enqueue(GalleryInfo info) => Enqueue(info, label: null);

    /// <summary>Queues a gallery for download with an optional download label.</summary>
    public bool Enqueue(GalleryInfo info, string? label)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (_tasks.ContainsKey(info.Gid))
        {
            return false;
        }

        string? resolvedLabel = label;
        if (resolvedLabel is null)
        {
            if (info is DownloadInfo di && !string.IsNullOrWhiteSpace(di.Label))
            {
                resolvedLabel = di.Label;
            }
            else if (_labels.TryGetValue(info.Gid, out string? cachedLabel))
            {
                resolvedLabel = cachedLabel;
            }
            else
            {
                try
                {
                    resolvedLabel = _database.GetDownloadInfo(info.Gid)?.Label;
                }
                catch
                {
                }
            }
        }

        _labels[info.Gid] = resolvedLabel;
        var task = new DownloadTask(info.Gid, info.Token ?? string.Empty, info)
        {
            Label = resolvedLabel
        };
        if (!_tasks.TryAdd(info.Gid, task))
        {
            return false;
        }

        int knownFinished = info is DownloadInfo rowInfo ? Math.Max(0, rowInfo.Finished) : 0;
        int knownTotal = info is DownloadInfo rowTotal && rowTotal.Total > 0 ? rowTotal.Total : Math.Max(0, info.Pages);
        task.LastFinished = knownFinished;
        task.LastTotal = knownTotal;

        Publish(new DownloadProgress(
            info.Gid,
            info.Title ?? string.Empty,
            DownloadState.Wait,
            knownFinished,
            knownTotal,
            0,
            null,
            resolvedLabel,
            IsPaused: false,
            DirectoryPath: BuildGalleryFolder(EffectiveDownloadRoot, info)));

        // Persist the row immediately so a crash still shows the gallery as waiting.
        SaveRow(info, DownloadState.Wait, finished: knownFinished, total: knownTotal, label: resolvedLabel);

        _queue.Writer.TryWrite(task);
        return true;
    }

    /// <summary>
    /// Scans persisted download rows on startup and re-queues tasks that were still waiting or
    /// actively downloading when the previous process exited.
    /// </summary>
    public int ResumePending()
    {
        int resumed = 0;
        List<DownloadInfo> rows;
        try
        {
            rows = _database.GetPendingDownloadInfo();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取未完成下载列表失败");
            return 0;
        }

        // Oldest queued items first (ORDER BY TIME ASC) so startup preserves user queue order.
        foreach (DownloadInfo row in rows)
        {
            if (row.Gid > 0 && Enqueue(row))
            {
                resumed++;
            }
        }

        if (resumed > 0)
        {
            _logger.LogInformation("启动恢复 {Count} 个未完成下载任务", resumed);
        }

        return resumed;
    }

    /// <summary>Pauses an active or queued download while preserving partial progress and <c>.part</c> files.</summary>
    public bool Pause(long gid, string? reason = null)
    {
        if (!_tasks.TryRemove(gid, out DownloadTask? task))
        {
            return false;
        }

        bool publishNow;
        lock (task)
        {
            task.PauseRequested = true;
            task.PauseReason = string.IsNullOrWhiteSpace(reason) ? "已暂停" : reason;
            publishNow = !task.IsActiveInWorker;
        }

        task.Cancellation.Cancel();

        if (publishNow)
        {
            Publish(new DownloadProgress(
                gid,
                task.Info.Title ?? string.Empty,
                DownloadState.None,
                task.LastFinished,
                task.LastTotal,
                task.LastBytes,
                task.PauseReason,
                task.Label,
                IsPaused: true,
                DirectoryPath: BuildGalleryFolder(EffectiveDownloadRoot, task.Info)));
            SaveRow(task.Info, DownloadState.None, task.LastFinished, task.LastTotal, task.Label);
        }

        return true;
    }

    /// <summary>Resumes a previously paused or failed download row from SQLite / disk.</summary>
    public bool Resume(long gid)
    {
        if (_tasks.ContainsKey(gid))
        {
            return false;
        }

        DownloadInfo? row = _database.GetDownloadInfo(gid);
        if (row is null)
        {
            return false;
        }

        return Enqueue(row, row.Label);
    }

    /// <summary>Pauses all currently queued or running downloads.</summary>
    public int PauseAll()
    {
        int count = 0;
        foreach (long gid in _tasks.Keys.ToArray())
        {
            if (Pause(gid))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Starts or resumes every incomplete download stored in SQLite.</summary>
    public int StartAll()
    {
        int started = 0;
        foreach (DownloadInfo row in _database.GetAllDownloadInfo())
        {
            if (row.Gid > 0 && row.State != DownloadInfo.StateFinish && Enqueue(row, row.Label))
            {
                started++;
            }
        }

        return started;
    }

    /// <summary>Assigns or clears a download label for <paramref name="gid"/> and persists it.</summary>
    public void AssignLabel(long gid, string? label)
    {
        string? normalized = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        _labels[gid] = normalized;
        if (_tasks.TryGetValue(gid, out DownloadTask? task))
        {
            task.Label = normalized;
        }

        if (_database.GetDownloadInfo(gid) is { } row)
        {
            row.Label = normalized;
            _database.PutDownloadInfo(row);
        }

        if (_progress.TryGetValue(gid, out DownloadProgress? current))
        {
            Publish(current with { Label = normalized });
        }
    }

    /// <summary>Deletes persisted download records and optionally deletes gallery folders from disk.</summary>
    public int DeleteDownloads(IEnumerable<long> gids, bool deleteFiles)
    {
        ArgumentNullException.ThrowIfNull(gids);

        int deleted = 0;
        string root = Path.GetFullPath(EffectiveDownloadRoot);

        foreach (long gid in gids.Distinct())
        {
            Cancel(gid);
            _progress.TryRemove(gid, out _);
            _labels.TryRemove(gid, out _);

            string? dir = _database.GetDownloadDirname(gid);
            if (string.IsNullOrWhiteSpace(dir) && _database.GetDownloadInfo(gid) is { } row)
            {
                dir = BuildGalleryFolder(root, row);
            }

            _database.RemoveDownloadInfo(gid);
            _database.RemoveDownloadDirname(gid);

            if (deleteFiles && !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                try
                {
                    string fullDir = Path.GetFullPath(dir);
                    if (fullDir.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(fullDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.Delete(fullDir, recursive: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "删除下载目录失败：{Dir}", dir);
                }
            }

            deleted++;
        }

        return deleted;
    }

    /// <summary>Cancels an in-flight or queued download.</summary>
    public bool Cancel(long gid)
    {
        if (!_tasks.TryRemove(gid, out DownloadTask? task))
        {
            return false;
        }

        task.PauseRequested = false;
        task.Cancellation.Cancel();
        Publish(new DownloadProgress(gid, task.Info.Title ?? string.Empty, DownloadState.None, 0, 0, 0, "已取消", task.Label));
        SaveRow(task.Info, DownloadState.None, finished: 0, total: 0, label: task.Label);
        return true;
    }

    private void Publish(DownloadProgress progress)
    {
        _progress[progress.Gid] = progress;
        try
        {
            ProgressChanged?.Invoke(progress);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "下载进度订阅者抛出异常");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        _shutdown.Cancel();
        try
        {
            Task.WhenAll(_workers).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(static e => e is OperationCanceledException))
        {
            // Expected during shutdown.
        }

        _shutdown.Dispose();
        foreach (DownloadTask task in _tasks.Values)
        {
            task.Cancellation.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(_workers).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown.
        }

        _shutdown.Dispose();
        foreach (DownloadTask task in _tasks.Values)
        {
            task.Cancellation.Dispose();
        }
    }

    private sealed class DownloadTask
    {
        public DownloadTask(long gid, string token, GalleryInfo info)
        {
            Gid = gid;
            Token = token;
            Info = info;
        }

        public long Gid { get; }
        public string Token { get; }
        public GalleryInfo Info { get; }
        public string? Label { get; set; }
        public bool PauseRequested { get; set; }
        public string? PauseReason { get; set; }
        public bool IsActiveInWorker { get; set; }
        public int LastFinished { get; set; }
        public int LastTotal { get; set; }
        public long LastBytes { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
    }
}
