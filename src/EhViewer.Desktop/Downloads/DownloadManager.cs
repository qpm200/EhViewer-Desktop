using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Channels;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Exceptions;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Network;
using EhViewer.Core.Spider;
using EhViewer.Core.Storage;
using EhViewer.Core.Util;
using EhViewer.Desktop.Reader;
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
/// Gallery download queue. Downloads run through a bounded
/// <see cref="Channel{T}"/> worker pool, each gallery writes images with the upstream
/// <c>%08d.ext</c> naming scheme, and progress is persisted to <see cref="EhDatabase"/> so a restart
/// resumes instead of restarting.
/// </summary>
public sealed class DownloadManager : IDisposable, IAsyncDisposable
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

    /// <summary>Exports all persisted downloads and labels as RFC-4180 CSV.</summary>
    public string ExportDownloadsCsv()
    {
        var sb = new StringBuilder();
        sb.AppendLine("gid,token,title,titleJpn,thumb,category,posted,uploader,rating,rated,simpleLanguage,thumbWidth,thumbHeight,favoriteSlot,favoriteName,pages,state,legacy,time,label");
        foreach (DownloadInfo d in _database.GetAllDownloadInfo())
        {
            string[] cols =
            [
                d.Gid.ToString(CultureInfo.InvariantCulture),
                d.Token ?? string.Empty,
                d.Title ?? string.Empty,
                d.TitleJpn ?? string.Empty,
                d.Thumb ?? string.Empty,
                d.Category.ToString(CultureInfo.InvariantCulture),
                d.Posted ?? string.Empty,
                d.Uploader ?? string.Empty,
                d.Rating.ToString(CultureInfo.InvariantCulture),
                d.Rated ? "true" : "false",
                d.SimpleLanguage ?? string.Empty,
                d.ThumbWidth.ToString(CultureInfo.InvariantCulture),
                d.ThumbHeight.ToString(CultureInfo.InvariantCulture),
                d.FavoriteSlot.ToString(CultureInfo.InvariantCulture),
                d.FavoriteName ?? string.Empty,
                d.Pages.ToString(CultureInfo.InvariantCulture),
                d.State.ToString(CultureInfo.InvariantCulture),
                d.Legacy.ToString(CultureInfo.InvariantCulture),
                d.Time.ToString(CultureInfo.InvariantCulture),
                d.Label ?? string.Empty
            ];
            sb.AppendLine(string.Join(",", cols.Select(EscapeCsvField)));
        }

        return sb.ToString();
    }

    /// <summary>Imports downloads from either Desktop RFC-4180 CSV or upstream <c>GalleryInfo.FromCsv</c> lines.</summary>
    public int ImportDownloadsCsv(string csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return 0;
        }

        var existingLabels = _database.GetAllDownloadLabels()
            .Select(static l => l.Label)
            .Where(static l => !string.IsNullOrWhiteSpace(l))
            .ToHashSet(StringComparer.Ordinal);

        int imported = 0;
        using var reader = new StringReader(csv);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith("gid,", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DownloadInfo? parsed = TryParseDownloadCsvRow(line)
                ?? (GalleryInfo.FromCsv(line) is { } gi ? new DownloadInfo(gi) : null);

            if (parsed is null || parsed.Gid <= 0)
            {
                continue;
            }

            if (parsed.Time <= 0)
            {
                parsed.Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            _database.PutDownloadInfo(parsed);
            if (!string.IsNullOrWhiteSpace(parsed.Label) && existingLabels.Add(parsed.Label))
            {
                _database.AddDownloadLabel(parsed.Label);
            }

            imported++;
        }

        return imported;
    }

    /// <summary>Finds empty or incomplete gallery folders inside <see cref="EffectiveDownloadRoot"/>.</summary>
    public IReadOnlyList<InvalidDownloadCandidate> FindInvalidDownloads()
    {
        string root = EffectiveDownloadRoot;
        if (!Directory.Exists(root))
        {
            return [];
        }

        var candidates = new List<InvalidDownloadCandidate>();
        foreach (string dir in Directory.EnumerateDirectories(root))
        {
            string folderName = Path.GetFileName(dir);
            if (folderName.StartsWith('_'))
            {
                continue;
            }

            long? gid = null;
            int dash = folderName.IndexOf('-');
            if (dash > 0 && long.TryParse(folderName[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedGid))
            {
                gid = parsedGid;
                if (_tasks.ContainsKey(parsedGid))
                {
                    continue;
                }
            }

            int actualImages = LocalLibraryScanner.CountImages(dir);
            int expectedPages = 0;
            string progressPath = Path.Combine(dir, ".ehviewer");
            if (File.Exists(progressPath))
            {
                try
                {
                    using FileStream stream = File.OpenRead(progressPath);
                    SpiderInfo? info = SpiderInfo.ReadHeader(stream);
                    if (info is not null)
                    {
                        expectedPages = Math.Max(0, info.Pages);
                    }
                }
                catch
                {
                }
            }

            if (actualImages == 0)
            {
                candidates.Add(new InvalidDownloadCandidate(dir, folderName, gid, "无有效图片文件", expectedPages, actualImages));
            }
            else if (expectedPages > 0 && actualImages < expectedPages)
            {
                candidates.Add(new InvalidDownloadCandidate(dir, folderName, gid, $"页面不完整 ({actualImages}/{expectedPages})", expectedPages, actualImages));
            }
        }

        return candidates;
    }

    /// <summary>Deletes invalid gallery directories and clears matching stale DB directory entries.</summary>
    public int CleanInvalidDownloads(IEnumerable<string> directoryPaths)
    {
        ArgumentNullException.ThrowIfNull(directoryPaths);
        string root = Path.GetFullPath(EffectiveDownloadRoot);
        int cleaned = 0;

        foreach (string path in directoryPaths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                continue;
            }

            string full = Path.GetFullPath(path);
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string folderName = Path.GetFileName(full);
            int dash = folderName.IndexOf('-');
            if (dash > 0 && long.TryParse(folderName[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out long gid))
            {
                _database.RemoveDownloadDirname(gid);
            }

            try
            {
                Directory.Delete(full, recursive: true);
                cleaned++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "清理无效下载目录失败：{Dir}", full);
            }
        }

        return cleaned;
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

    private static string EscapeCsvField(string value)
    {
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }

    private static List<string> SplitCsvLine(string line)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQuotes)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    sb.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    inQuotes = false;
                }
                else
                {
                    sb.Append(c);
                }
            }
            else if (c == '"')
            {
                inQuotes = true;
            }
            else if (c == ',')
            {
                result.Add(sb.ToString());
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }

        result.Add(sb.ToString());
        return result;
    }

    private static DownloadInfo? TryParseDownloadCsvRow(string line)
    {
        List<string> cols = SplitCsvLine(line);
        if (cols.Count < 20)
        {
            return null;
        }

        if (!long.TryParse(cols[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long gid) || gid <= 0)
        {
            return null;
        }

        try
        {
            return new DownloadInfo
            {
                Gid = gid,
                Token = cols[1],
                Title = cols[2],
                TitleJpn = cols[3],
                Thumb = cols[4],
                Category = int.TryParse(cols[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out int cat) ? cat : 0,
                Posted = cols[6],
                Uploader = cols[7],
                Rating = float.TryParse(cols[8], NumberStyles.Float, CultureInfo.InvariantCulture, out float rating) ? rating : 0f,
                Rated = bool.TryParse(cols[9], out bool rated) && rated,
                SimpleLanguage = string.IsNullOrEmpty(cols[10]) ? null : cols[10],
                ThumbWidth = int.TryParse(cols[11], NumberStyles.Integer, CultureInfo.InvariantCulture, out int tw) ? tw : 0,
                ThumbHeight = int.TryParse(cols[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out int th) ? th : 0,
                FavoriteSlot = int.TryParse(cols[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fs) ? fs : -2,
                FavoriteName = string.IsNullOrEmpty(cols[14]) ? null : cols[14],
                Pages = int.TryParse(cols[15], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pages) ? pages : 0,
                State = int.TryParse(cols[16], NumberStyles.Integer, CultureInfo.InvariantCulture, out int state) ? state : DownloadInfo.StateNone,
                Legacy = int.TryParse(cols[17], NumberStyles.Integer, CultureInfo.InvariantCulture, out int legacy) ? legacy : 0,
                Time = long.TryParse(cols[18], NumberStyles.Integer, CultureInfo.InvariantCulture, out long time) ? time : 0,
                Label = string.IsNullOrWhiteSpace(cols[19]) ? null : cols[19]
            };
        }
        catch
        {
            return null;
        }
    }

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

    internal static int CountExistingImages(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Count(static f =>
                {
                    if (Path.GetFileNameWithoutExtension(f).Length != 8)
                    {
                        return false;
                    }

                    string ext = Path.GetExtension(f);
                    return PageImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
                });
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>Image file names use the upstream zero-padded eight-digit index.</summary>
    internal static string BuildImagePath(string folder, int index)
        => Path.Combine(folder, index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture) + ".jpg");

    /// <summary>
    /// Finds an already-downloaded page regardless of its image extension. The downloader stores
    /// <c>{index:D8}.{ext}</c> where <c>ext</c> is derived from the payload, so a resumed download
    /// that only probed <c>.jpg</c> would re-download every <c>.png</c>/<c>.webp</c>/<c>.gif</c>
    /// page it already had.
    /// </summary>
    internal static bool TryFindExistingPageFile(string folder, int index, out string? path)
    {
        path = null;
        string stem = index.ToString("D8", System.Globalization.CultureInfo.InvariantCulture);

        foreach (string extension in PageImageExtensions)
        {
            string candidate = Path.Combine(folder, stem + extension);
            try
            {
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
                {
                    path = candidate;
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Treat an unreadable candidate as absent and keep probing the other extensions.
            }
        }

        return false;
    }

    /// <summary>Extensions a stored page image may legitimately carry.</summary>
    internal static readonly string[] PageImageExtensions = [".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp"];

    /// <summary>Gallery folder naming must match Android: <c>{gid}-{sanitized_title}</c>.</summary>
    internal static string BuildGalleryFolder(string downloadRoot, GalleryInfo info)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadRoot);
        ArgumentNullException.ThrowIfNull(info);

        string title = EhUtils.ExtractTitle(info.Title) ?? info.Title ?? "untitled";
        string folderName = $"{info.Gid}-{FileUtils.SanitizeFilename(title)}";
        return Path.Combine(downloadRoot, folderName);
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

    internal static int ReadStartPage(string progressFile)
        => ReadProgress(progressFile)?.StartPage ?? 0;

    /// <summary>
    /// Reads the full progress document, including the pToken map, so a resumed download can keep
    /// the image urls a previous run already resolved.
    /// </summary>
    internal static SpiderInfo? ReadProgress(string progressFile)
    {
        if (!File.Exists(progressFile))
        {
            return null;
        }

        try
        {
            using FileStream stream = File.OpenRead(progressFile);
            SpiderInfo? info = SpiderInfo.Read(stream);
            return info is null || info.StartPage < 0 ? null : info;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes the progress document atomically (temp file + replace) so an interrupted write can
    /// never leave a truncated <c>.ehviewer</c> that the next run cannot parse.
    /// </summary>
    internal static void WriteProgress(string progressFile, SpiderInfo info)
    {
        string tempPath = progressFile + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                info.Write(stream);
            }

            File.Move(tempPath, progressFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Progress persistence is best-effort; the downloaded files remain valid.
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception cleanupEx) when (cleanupEx is IOException or UnauthorizedAccessException)
            {
                // Nothing further to do.
            }
        }
    }

    private void SaveRow(GalleryInfo info, DownloadState state, int finished, int total, string? label = null)
    {
        try
        {
            string? effectiveLabel = label ?? (_labels.TryGetValue(info.Gid, out string? cached) ? cached : null);
            var row = new DownloadInfo(info)
            {
                State = (int)state,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Total = total,
                Finished = finished,
                Label = effectiveLabel
            };

            _database.PutDownloadInfo(row);
            _database.PutDownloadDirname(info.Gid, BuildGalleryFolder(EffectiveDownloadRoot, info));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "下载状态落库失败：{Gid}", info.Gid);
        }
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

    private int _disposed;

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
