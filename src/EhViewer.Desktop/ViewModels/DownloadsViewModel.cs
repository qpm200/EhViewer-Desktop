using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Downloads;
using EhViewer.Desktop.Services;

namespace EhViewer.Desktop.ViewModels;

/// <summary>One download row shown in the downloads page.</summary>
public sealed record DownloadRow(
    long Gid,
    string Title,
    string StateText,
    string ProgressText,
    string? Error,
    string? Label = null,
    bool IsPaused = false,
    bool CanPause = false,
    bool CanResume = false,
    bool CanCancel = false,
    DownloadState RawState = DownloadState.None,
    int Done = 0,
    int Total = 0,
    string? DirectoryPath = null)
{
    public string LabelDisplay => string.IsNullOrWhiteSpace(Label) ? "未分类" : Label;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public double ProgressPercent =>
        Total > 0
            ? Math.Clamp((double)Done * 100.0 / Total, 0.0, 100.0)
            : (RawState == DownloadState.Finish ? 100.0 : 0.0);

    public bool IsIndeterminate => RawState is DownloadState.Wait or DownloadState.Update;
}

/// <summary>
/// Downloads page view model: mirrors <see cref="DownloadManager"/>, <see cref="ArchiverDownloadManager"/>,
/// download labels/SQLite persistence, and the local library scanner into observable collections.
/// </summary>
public sealed partial class DownloadsViewModel : ObservableObject, IDisposable
{
    public const string AllLabelsFilter = "全部标签";
    public const string UnlabeledFilter = "未分类";

    private static readonly IReadOnlyList<string> DefaultStateFilterOptions =
    [
        "全部状态",
        "等待/下载中",
        "已暂停",
        "已完成",
        "失败"
    ];

    private static readonly IReadOnlyList<string> DefaultLibrarySortOptions =
    [
        "按下载时间",
        "按画廊 GID",
        "按标题"
    ];

    public IReadOnlyList<string> StateFilterOptions => DefaultStateFilterOptions;

    public IReadOnlyList<string> LibrarySortOptions => DefaultLibrarySortOptions;

    private readonly DownloadManager _manager;
    private readonly LocalLibraryScanner _scanner;
    private readonly AppPaths _paths;
    private readonly EhDatabase? _database;
    private readonly ArchiverDownloadManager? _archiverManager;
    private readonly Func<DesktopSettings?>? _resolveDesktopSettings;

    private readonly Dictionary<long, DownloadRow> _allDownloadRows = [];
    private readonly Dictionary<long, long> _downloadTimes = [];
    private readonly HashSet<long> _selectedGids = [];
    private List<LocalGallery> _cachedLocalGalleries = [];
    private bool _localLibraryScanned;
    private CancellationTokenSource? _libraryScanCts;
    private FileSystemWatcher? _watcher;
    private Timer? _watcherDebounce;
    private Action<Action>? _watcherPost;
    private bool _disposed;

    public DownloadsViewModel(
        DownloadManager manager,
        LocalLibraryScanner scanner,
        AppPaths paths,
        EhDatabase? database = null,
        ArchiverDownloadManager? archiverManager = null,
        Func<DesktopSettings?>? resolveDesktopSettings = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _database = database;
        _archiverManager = archiverManager;
        _resolveDesktopSettings = resolveDesktopSettings;
    }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SearchKeyword { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SelectedLabelFilter { get; set; } = AllLabelsFilter;

    [ObservableProperty]
    public partial string SelectedStateFilter { get; set; } = "全部状态";

    [ObservableProperty]
    public partial int SelectedLibrarySortIndex { get; set; } = 0;

    [ObservableProperty]
    public partial bool IsMultiSelectMode { get; set; }

    [ObservableProperty]
    public partial int SelectedCount { get; set; }

    public ObservableCollection<DownloadRow> ActiveRows { get; } = [];

    public ObservableCollection<LocalGallery> LocalGalleries { get; } = [];

    public ObservableCollection<string> LabelNames { get; } = [];

    public ObservableCollection<string> LabelFilterOptions { get; } = [AllLabelsFilter, UnlabeledFilter];

    public ObservableCollection<ArchiverTaskSnapshot> ArchiverRows { get; } = [];

    public string DownloadRoot => _paths.GetEffectiveDownloadDirectory(_resolveDesktopSettings?.Invoke());

    public IReadOnlyCollection<long> SelectedGids => _selectedGids;

    partial void OnSearchKeywordChanged(string value)
    {
        ApplyFilters();
        RefreshLocalLibraryAfterFilterChange();
    }

    partial void OnSelectedLabelFilterChanged(string value)
    {
        ApplyFilters();
        RefreshLocalLibraryAfterFilterChange();
    }

    partial void OnSelectedStateFilterChanged(string value) => ApplyFilters();

    partial void OnSelectedLibrarySortIndexChanged(int value) => RefreshLocalLibraryAfterFilterChange();

    private void RefreshLocalLibraryAfterFilterChange()
    {
        if (_localLibraryScanned)
        {
            ApplyLocalLibraryView();
        }
        else
        {
            ReloadLibrary();
        }
    }

    partial void OnIsMultiSelectModeChanged(bool value)
    {
        if (!value)
        {
            ClearSelection();
        }
    }

    /// <summary>Starts or refreshes a debounced watcher on <see cref="DownloadRoot"/> for live library updates.</summary>
    public void EnableDirectoryWatcher(Action<Action>? post = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _watcherPost = post;
        RebuildWatcher();
    }

    /// <summary>Re-reads labels, persisted/live downloads, archiver queue, and rescans the local library.</summary>
    public void Reload()
    {
        ReloadLabels();
        ReloadDownloads();
        ReloadArchivers();
        ReloadLibrary();
        RebuildWatcherIfActive();
    }

    public void ReloadLabels()
    {
        string previousFilter = SelectedLabelFilter;

        LabelNames.Clear();
        LabelFilterOptions.Clear();
        LabelFilterOptions.Add(AllLabelsFilter);
        LabelFilterOptions.Add(UnlabeledFilter);

        if (_database is not null)
        {
            try
            {
                foreach (DownloadLabel label in _database.GetAllDownloadLabels())
                {
                    if (!string.IsNullOrWhiteSpace(label.Label) && !LabelNames.Contains(label.Label))
                    {
                        LabelNames.Add(label.Label);
                        LabelFilterOptions.Add(label.Label);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        if (!LabelFilterOptions.Contains(previousFilter))
        {
            SelectedLabelFilter = AllLabelsFilter;
        }
    }

    public void ReloadDownloads()
    {
        _allDownloadRows.Clear();
        _downloadTimes.Clear();

        if (_database is not null)
        {
            try
            {
                Dictionary<long, string> dirnames = _database.GetAllDownloadDirnamesMap();
                foreach (DownloadInfo row in _database.GetAllDownloadInfo())
                {
                    DownloadState state = Enum.IsDefined(typeof(DownloadState), row.State)
                        ? (DownloadState)row.State
                        : DownloadState.None;

                    dirnames.TryGetValue(row.Gid, out string? dir);
                    _downloadTimes[row.Gid] = row.Time;

                    bool isPaused = state == DownloadState.None;
                    string stateText = isPaused ? "已暂停" : DescribeState(state);
                    string progressText = row.Total > 0
                        ? $"{row.Finished}/{row.Total}"
                        : row.Finished > 0 ? row.Finished.ToString(CultureInfo.InvariantCulture) : string.Empty;

                    _allDownloadRows[row.Gid] = new DownloadRow(
                        row.Gid,
                        row.Title ?? string.Empty,
                        stateText,
                        progressText,
                        null,
                        row.Label,
                        IsPaused: isPaused,
                        CanPause: false,
                        CanResume: state != DownloadState.Finish,
                        CanCancel: false,
                        RawState: state,
                        Done: row.Finished,
                        Total: row.Total,
                        DirectoryPath: dir);
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = ex.Message;
            }
        }

        foreach (DownloadProgress progress in _manager.Snapshot())
        {
            _allDownloadRows[progress.Gid] = ToRow(progress);
        }

        ApplyFilters();
    }

    public void ReloadArchivers()
    {
        ArchiverRows.Clear();
        if (_archiverManager is null)
        {
            return;
        }

        foreach (ArchiverTaskSnapshot snapshot in _archiverManager.GetSnapshots())
        {
            ArchiverRows.Add(snapshot);
        }
    }

    public void ReloadLibrary()
    {
        ErrorMessage = string.Empty;

        try
        {
            (string root, Dictionary<long, string?>? labelLookup, Dictionary<long, long>? timeLookup) =
                CaptureLibraryScanInputs();
            _cachedLocalGalleries = [.. _scanner.Scan(root, LocalLibrarySortMode.GidDescending, labelLookup, timeLookup)];
            _localLibraryScanned = true;
            ApplyLocalLibraryView();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    public async Task ReloadLibraryAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        CancellationTokenSource? prevCts = _libraryScanCts;
        if (prevCts is not null)
        {
            try
            {
                prevCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            prevCts.Dispose();
        }

        var nextCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _libraryScanCts = nextCts;
        CancellationToken ct = nextCts.Token;

        ErrorMessage = string.Empty;
        StatusMessage = $"队列 {_allDownloadRows.Count} · 正在扫描本地库…";

        try
        {
            (string root, Dictionary<long, string?>? labelLookup, Dictionary<long, long>? timeLookup) =
                CaptureLibraryScanInputs();
            IReadOnlyList<LocalGallery> scanned = await Task
                .Run(() => _scanner.Scan(root, LocalLibrarySortMode.GidDescending, labelLookup, timeLookup), ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested || _disposed)
            {
                return;
            }

            _cachedLocalGalleries = [.. scanned];
            _localLibraryScanned = true;
            ApplyLocalLibraryView();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    private (string Root, Dictionary<long, string?>? LabelLookup, Dictionary<long, long>? TimeLookup) CaptureLibraryScanInputs()
    {
        Dictionary<long, string?>? labelLookup = null;
        Dictionary<long, long>? timeLookup = null;

        if (_allDownloadRows.Count > 0)
        {
            labelLookup = new Dictionary<long, string?>(_allDownloadRows.Count);
            foreach (KeyValuePair<long, DownloadRow> kv in _allDownloadRows)
            {
                labelLookup[kv.Key] = kv.Value.Label;
            }

            if (_downloadTimes.Count > 0)
            {
                timeLookup = new Dictionary<long, long>(_downloadTimes);
            }
        }
        else if (_database is not null)
        {
            labelLookup = [];
            timeLookup = [];
            foreach (DownloadInfo row in _database.GetAllDownloadInfo())
            {
                labelLookup[row.Gid] = row.Label;
                timeLookup[row.Gid] = row.Time;
            }
        }

        return (DownloadRoot, labelLookup, timeLookup);
    }

    private void ApplyLocalLibraryView()
    {
        ErrorMessage = string.Empty;

        LocalLibrarySortMode sortMode = SelectedLibrarySortIndex switch
        {
            1 => LocalLibrarySortMode.GidDescending,
            2 => LocalLibrarySortMode.TitleAscending,
            _ => LocalLibrarySortMode.TimeDescending
        };

        string keyword = SearchKeyword.Trim();
        string labelFilter = SelectedLabelFilter;

        var filtered = new List<LocalGallery>(_cachedLocalGalleries.Count);
        foreach (LocalGallery gallery in _cachedLocalGalleries)
        {
            if (!MatchesLabelFilter(gallery.Label, labelFilter))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(keyword) &&
                !gallery.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                !gallery.Gid.ToString(CultureInfo.InvariantCulture).Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filtered.Add(gallery);
        }

        switch (sortMode)
        {
            case LocalLibrarySortMode.TimeDescending:
                filtered.Sort(static (a, b) =>
                {
                    int cmp = b.TimestampMs.CompareTo(a.TimestampMs);
                    return cmp != 0 ? cmp : b.Gid.CompareTo(a.Gid);
                });
                break;
            case LocalLibrarySortMode.TitleAscending:
                filtered.Sort(static (a, b) =>
                {
                    int cmp = string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
                    return cmp != 0 ? cmp : b.Gid.CompareTo(a.Gid);
                });
                break;
            default:
                filtered.Sort(static (a, b) => b.Gid.CompareTo(a.Gid));
                break;
        }

        LocalGalleries.Clear();
        foreach (LocalGallery gallery in filtered)
        {
            LocalGalleries.Add(gallery);
        }

        StatusMessage = $"队列 {_allDownloadRows.Count} · 本地库 {LocalGalleries.Count} 个画廊";
    }

    public bool Pause(long gid) => _manager.Pause(gid);

    public bool Resume(long gid) => _manager.Resume(gid);

    public bool Cancel(long gid) => _manager.Cancel(gid);

    public int PauseAll() => _manager.PauseAll();

    public int StartAll() => _manager.StartAll();

    public void AssignLabel(long gid, string? label)
    {
        string? normalized = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (normalized is not null && _database is not null && !_database.HasDownloadLabel(normalized))
        {
            _database.AddDownloadLabel(normalized);
            ReloadLabels();
        }

        _manager.AssignLabel(gid, normalized);
        if (_allDownloadRows.TryGetValue(gid, out DownloadRow? existing))
        {
            _allDownloadRows[gid] = existing with { Label = normalized };
            ApplyFilters();
        }

        if (_localLibraryScanned)
        {
            for (int i = 0; i < _cachedLocalGalleries.Count; i++)
            {
                LocalGallery gallery = _cachedLocalGalleries[i];
                if (gallery.Gid == gid)
                {
                    _cachedLocalGalleries[i] = gallery with { Label = normalized };
                }
            }

            ApplyLocalLibraryView();
        }
        else
        {
            ReloadLibrary();
        }
    }

    public int AssignLabelToSelected(string? label)
    {
        if (_selectedGids.Count == 0)
        {
            return 0;
        }

        string? normalized = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (normalized is not null && _database is not null && !_database.HasDownloadLabel(normalized))
        {
            _database.AddDownloadLabel(normalized);
            ReloadLabels();
        }

        int count = 0;
        foreach (long gid in _selectedGids.ToArray())
        {
            _manager.AssignLabel(gid, normalized);
            if (_allDownloadRows.TryGetValue(gid, out DownloadRow? existing))
            {
                _allDownloadRows[gid] = existing with { Label = normalized };
            }

            count++;
        }

        ApplyFilters();
        if (_localLibraryScanned)
        {
            for (int i = 0; i < _cachedLocalGalleries.Count; i++)
            {
                LocalGallery gallery = _cachedLocalGalleries[i];
                if (_selectedGids.Contains(gallery.Gid))
                {
                    _cachedLocalGalleries[i] = gallery with { Label = normalized };
                }
            }

            ApplyLocalLibraryView();
        }
        else
        {
            ReloadLibrary();
        }

        return count;
    }

    public int DeleteDownloads(IEnumerable<long> gids, bool deleteFiles)
    {
        long[] targets = gids.Distinct().ToArray();
        if (targets.Length == 0)
        {
            return 0;
        }

        int deleted = _manager.DeleteDownloads(targets, deleteFiles);
        foreach (long gid in targets)
        {
            _allDownloadRows.Remove(gid);
            _selectedGids.Remove(gid);
        }

        SelectedCount = _selectedGids.Count;
        ApplyFilters();
        ReloadLibrary();
        return deleted;
    }

    public int DeleteSelectedDownloads(bool deleteFiles) => DeleteDownloads(_selectedGids, deleteFiles);

    public bool DeleteLocalGallery(LocalGallery gallery)
    {
        ArgumentNullException.ThrowIfNull(gallery);

        if (gallery.Gid > 0 && _allDownloadRows.ContainsKey(gallery.Gid))
        {
            return DeleteDownloads([gallery.Gid], deleteFiles: true) > 0;
        }

        string root = Path.GetFullPath(DownloadRoot);
        string target = Path.GetFullPath(gallery.DirectoryPath);
        if (target.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(target.TrimEnd(Path.DirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
            ReloadLibrary();
            return true;
        }

        return false;
    }

    public IReadOnlyList<DownloadLabel> GetLabels() =>
        _database?.GetAllDownloadLabels() ?? [];

    public bool AddLabel(string labelName)
    {
        if (_database is null || string.IsNullOrWhiteSpace(labelName))
        {
            return false;
        }

        string trimmed = labelName.Trim();
        if (_database.HasDownloadLabel(trimmed))
        {
            return false;
        }

        _database.AddDownloadLabel(trimmed);
        ReloadLabels();
        return true;
    }

    public bool RenameLabel(DownloadLabel label, string newName)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (_database is null || string.IsNullOrWhiteSpace(newName))
        {
            return false;
        }

        string? oldName = label.Label;
        string trimmed = newName.Trim();
        if (string.Equals(oldName, trimmed, StringComparison.Ordinal))
        {
            return false;
        }

        if (_database.HasDownloadLabel(trimmed))
        {
            return false;
        }

        label.Label = trimmed;
        _database.UpdateDownloadLabel(label);

        if (!string.IsNullOrEmpty(oldName))
        {
            foreach (DownloadInfo row in _database.GetAllDownloadInfo())
            {
                if (string.Equals(row.Label, oldName, StringComparison.Ordinal))
                {
                    _manager.AssignLabel(row.Gid, trimmed);
                }
            }
        }

        if (string.Equals(SelectedLabelFilter, oldName, StringComparison.Ordinal))
        {
            SelectedLabelFilter = trimmed;
        }

        Reload();
        return true;
    }

    public void MoveLabel(int fromIndex, int toIndex)
    {
        _database?.MoveDownloadLabel(fromIndex, toIndex);
        ReloadLabels();
    }

    public void DeleteLabel(DownloadLabel label)
    {
        ArgumentNullException.ThrowIfNull(label);
        if (_database is null)
        {
            return;
        }

        string? oldName = label.Label;
        _database.RemoveDownloadLabel(label);

        if (!string.IsNullOrEmpty(oldName))
        {
            foreach (DownloadInfo row in _database.GetAllDownloadInfo())
            {
                if (string.Equals(row.Label, oldName, StringComparison.Ordinal))
                {
                    _manager.AssignLabel(row.Gid, null);
                }
            }
        }

        if (string.Equals(SelectedLabelFilter, oldName, StringComparison.Ordinal))
        {
            SelectedLabelFilter = AllLabelsFilter;
        }

        Reload();
    }

    public string ExportCsv() => _manager.ExportDownloadsCsv();

    public int ImportCsv(string csv)
    {
        int count = _manager.ImportDownloadsCsv(csv);
        Reload();
        return count;
    }

    public IReadOnlyList<InvalidDownloadCandidate> FindInvalidDownloads() =>
        _manager.FindInvalidDownloads();

    public int CleanInvalidDownloads(IEnumerable<string> directoryPaths)
    {
        int count = _manager.CleanInvalidDownloads(directoryPaths);
        Reload();
        return count;
    }

    public void PauseArchiver(string taskId) => _archiverManager?.Pause(taskId);

    public void ResumeArchiver(string taskId) => _archiverManager?.Resume(taskId);

    public void CancelArchiver(string taskId) => _archiverManager?.Cancel(taskId);

    public void RemoveArchiver(string taskId)
    {
        _archiverManager?.Remove(taskId);
        ReloadArchivers();
    }

    public void SetSelectedGids(IEnumerable<long> gids)
    {
        _selectedGids.Clear();
        foreach (long gid in gids)
        {
            _selectedGids.Add(gid);
        }

        SelectedCount = _selectedGids.Count;
    }

    public bool ToggleSelection(long gid)
    {
        bool added = _selectedGids.Add(gid);
        if (!added)
        {
            _selectedGids.Remove(gid);
        }

        SelectedCount = _selectedGids.Count;
        return added;
    }

    public void SelectAllFiltered()
    {
        _selectedGids.Clear();
        foreach (DownloadRow row in ActiveRows)
        {
            _selectedGids.Add(row.Gid);
        }

        SelectedCount = _selectedGids.Count;
    }

    public void ClearSelection()
    {
        _selectedGids.Clear();
        SelectedCount = 0;
    }

    /// <summary>
    /// Applies a progress event. The manager raises events on worker threads, so the page supplies a
    /// dispatcher via <paramref name="post"/> to marshal the mutation onto the UI thread.
    /// </summary>
    public void ApplyProgress(DownloadProgress progress, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(progress);

        DownloadRow row = ToRow(progress);

        void Apply()
        {
            _allDownloadRows[row.Gid] = row;
            ApplySingleRowUpdate(row);
            if (progress.State == DownloadState.Finish)
            {
                if (_watcherPost is not null)
                {
                    _ = ReloadLibraryAsync();
                }
                else
                {
                    ReloadLibrary();
                }
            }
        }

        if (post is null)
        {
            Apply();
        }
        else
        {
            post(Apply);
        }
    }

    public void ApplyArchiverSnapshot(ArchiverTaskSnapshot snapshot, Action<Action>? post = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        void Apply()
        {
            int index = -1;
            for (int i = 0; i < ArchiverRows.Count; i++)
            {
                if (string.Equals(ArchiverRows[i].TaskId, snapshot.TaskId, StringComparison.Ordinal))
                {
                    index = i;
                    break;
                }
            }

            if (index >= 0)
            {
                ArchiverRows[index] = snapshot;
            }
            else
            {
                ArchiverRows.Add(snapshot);
            }

            if (snapshot.State == ArchiverTaskState.Completed && !string.IsNullOrEmpty(snapshot.ImportedDirectory))
            {
                if (_watcherPost is not null)
                {
                    _ = ReloadLibraryAsync();
                }
                else
                {
                    ReloadLibrary();
                }
            }
        }

        if (post is null)
        {
            Apply();
        }
        else
        {
            post(Apply);
        }
    }

    internal static DownloadRow ToRow(DownloadProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        string stateText = progress.IsPaused
            ? "已暂停"
            : DescribeState(progress.State);

        string progressText = progress.Total > 0
            ? $"{progress.Done}/{progress.Total}"
            : progress.Done > 0 ? progress.Done.ToString(CultureInfo.InvariantCulture) : string.Empty;

        bool canPause = progress.State is DownloadState.Wait or DownloadState.Download or DownloadState.Update;
        bool canResume = progress.IsPaused || progress.State is DownloadState.Failed or DownloadState.None;
        bool canCancel = canPause;

        return new DownloadRow(
            progress.Gid,
            progress.Title,
            stateText,
            progressText,
            progress.Error,
            progress.Label,
            progress.IsPaused,
            canPause,
            canResume,
            canCancel,
            progress.State,
            progress.Done,
            progress.Total,
            progress.DirectoryPath);
    }

    private static string DescribeState(DownloadState state) => state switch
    {
        DownloadState.Wait => "等待中",
        DownloadState.Download => "下载中",
        DownloadState.Finish => "已完成",
        DownloadState.Failed => "失败",
        DownloadState.Update => "更新中",
        _ => "未启动"
    };

    private void ApplySingleRowUpdate(DownloadRow row)
    {
        bool visible = MatchesRowFilters(row);
        int index = -1;
        for (int i = 0; i < ActiveRows.Count; i++)
        {
            if (ActiveRows[i].Gid == row.Gid)
            {
                index = i;
                break;
            }
        }

        if (visible)
        {
            if (index >= 0)
            {
                ActiveRows[index] = row;
            }
            else
            {
                ActiveRows.Add(row);
            }
        }
        else if (index >= 0)
        {
            ActiveRows.RemoveAt(index);
        }
    }

    private void ApplyFilters()
    {
        ActiveRows.Clear();
        foreach (DownloadRow row in _allDownloadRows.Values.OrderByDescending(static r => r.RawState is DownloadState.Download or DownloadState.Wait).ThenByDescending(static r => r.Gid))
        {
            if (MatchesRowFilters(row))
            {
                ActiveRows.Add(row);
            }
        }
    }

    private bool MatchesRowFilters(DownloadRow row)
    {
        if (!MatchesLabelFilter(row.Label, SelectedLabelFilter))
        {
            return false;
        }

        bool stateMatches = SelectedStateFilter switch
        {
            "等待/下载中" => row.RawState is DownloadState.Wait or DownloadState.Download or DownloadState.Update,
            "已暂停" => row.IsPaused || row.RawState == DownloadState.None,
            "已完成" => row.RawState == DownloadState.Finish,
            "失败" => row.RawState == DownloadState.Failed,
            _ => true
        };

        if (!stateMatches)
        {
            return false;
        }

        string keyword = SearchKeyword.Trim();
        if (!string.IsNullOrEmpty(keyword) &&
            !row.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
            !row.Gid.ToString(CultureInfo.InvariantCulture).Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool MatchesLabelFilter(string? rowLabel, string filter)
    {
        if (string.IsNullOrEmpty(filter) || string.Equals(filter, AllLabelsFilter, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(filter, UnlabeledFilter, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(rowLabel);
        }

        return string.Equals(rowLabel, filter, StringComparison.Ordinal);
    }

    private void RebuildWatcherIfActive()
    {
        if (_watcher is not null)
        {
            RebuildWatcher();
        }
    }

    private void RebuildWatcher()
    {
        DisposeWatcher();
        string root = DownloadRoot;
        if (!Directory.Exists(root))
        {
            return;
        }

        try
        {
            _watcherDebounce = new Timer(_ =>
            {
                if (_disposed)
                {
                    return;
                }

                if (_watcherPost is not null)
                {
                    _watcherPost(() =>
                    {
                        if (!_disposed)
                        {
                            _ = ReloadLibraryAsync();
                        }
                    });
                }
                else
                {
                    ReloadLibrary();
                }
            }, null, Timeout.Infinite, Timeout.Infinite);

            _watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true
            };

            void OnFsEvent(object _, FileSystemEventArgs __)
            {
                try
                {
                    _watcherDebounce?.Change(350, Timeout.Infinite);
                }
                catch (ObjectDisposedException)
                {
                }
            }

            _watcher.Created += OnFsEvent;
            _watcher.Deleted += OnFsEvent;
            _watcher.Renamed += OnFsEvent;
        }
        catch
        {
            DisposeWatcher();
        }
    }

    private void DisposeWatcher()
    {
        try
        {
            _watcher?.Dispose();
        }
        catch
        {
        }

        _watcher = null;

        try
        {
            _watcherDebounce?.Dispose();
        }
        catch
        {
        }

        _watcherDebounce = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DisposeWatcher();

        CancellationTokenSource? scanCts = Interlocked.Exchange(ref _libraryScanCts, null);
        if (scanCts is not null)
        {
            try
            {
                scanCts.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            scanCts.Dispose();
        }
    }
}
