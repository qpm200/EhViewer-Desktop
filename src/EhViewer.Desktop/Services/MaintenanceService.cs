using System.Globalization;
using EhViewer.Core.Client;
using EhViewer.Core.Storage;
using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Services;

/// <summary>
/// Translations of the upstream "advanced" maintenance preferences:
/// <c>ExportDataPreference</c>, <c>ClearDownloadPathCachePreference</c> and
/// <c>CleanRedundancyPreference</c>.
/// </summary>
/// <remarks>
/// Upstream runs these inside a <c>TaskPreference</c> (background task + Toast). Here they are plain
/// async methods that return a result record, so the settings page decides how to report it.
/// </remarks>
public sealed class MaintenanceService
{
    private readonly EhDatabase _database;
    private readonly AppPaths _paths;
    private readonly ILogger<MaintenanceService> _logger;

    public MaintenanceService(EhDatabase database, AppPaths paths, ILogger<MaintenanceService> logger)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Exports the database next to the data root (upstream writes to the external data dir).
    /// Returns the created file, or null when the export failed.
    /// </summary>
    public string? ExportDatabase(string? targetDirectory = null)
    {
        string directory = string.IsNullOrWhiteSpace(targetDirectory) ? _paths.RootDirectory : targetDirectory;
        try
        {
            Directory.CreateDirectory(directory);
            string fileName = BuildExportFileName(DateTimeOffset.Now) + ".db";
            string destination = Path.Combine(directory, fileName);

            _database.ExportDatabase(destination);
            _logger.LogInformation("数据已导出：{Path}", destination);
            return destination;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "导出数据失败");
            return null;
        }
    }

    /// <summary>
    /// Imports a database export produced by <see cref="ExportDatabase"/> or by the Android client.
    /// </summary>
    public bool ImportDatabase(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        if (!File.Exists(sourcePath))
        {
            return false;
        }

        try
        {
            _database.ImportDatabase(sourcePath);
            _logger.LogInformation("数据已导入：{Path}", sourcePath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "导入数据失败：{Path}", sourcePath);
            return false;
        }
    }

    /// <summary>Upstream <c>EhDB.clearDownloadDirname()</c>: forget every cached download path.</summary>
    public void ClearDownloadPathCache()
    {
        _database.ClearDownloadDirname();
        _logger.LogInformation("已清除下载路径缓存");
    }

    /// <summary>
    /// Upstream <c>CleanRedundancyPreference</c>: drop download rows whose folder no longer exists so
    /// the downloads list stops showing dead entries.
    /// </summary>
    public int CleanRedundantDownloadRows()
    {
        int removed = 0;

        foreach (DownloadDirname dirname in _database.GetAllDownloadDirname())
        {
            string? path = dirname.Dirname;
            if (string.IsNullOrWhiteSpace(path))
            {
                _database.RemoveDownloadDirname(dirname.Gid);
                removed++;
                continue;
            }

            if (!Directory.Exists(path))
            {
                _database.RemoveDownloadDirname(dirname.Gid);
                removed++;
            }
        }

        _logger.LogInformation("已清理 {Count} 条冗余下载记录", removed);
        return removed;
    }

    /// <summary>
    /// Clears the content caches (thumbnails and decoded images) while keeping downloads and settings.
    /// Returns the number of bytes freed.
    /// </summary>
    public long ClearImageCaches()
    {
        long freed = 0;

        foreach (string directory in (string[])[_paths.ThumbnailCacheDirectory, _paths.ImageCacheDirectory])
        {
            try
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        freed += new FileInfo(file).Length;
                        File.Delete(file);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // A file in use stays cached; keep going.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "清理缓存目录失败：{Directory}", directory);
            }
        }

        _logger.LogInformation("已清理图片缓存 {Bytes} 字节", freed);
        return freed;
    }

    /// <summary>
    /// Upstream <c>ReadableTime.getFilenamableTime</c>: a timestamp that is safe in a file name.
    /// </summary>
    internal static string BuildExportFileName(DateTimeOffset timestamp)
        => timestamp.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);

    /// <summary>
    /// Database export names always carry <c>.db</c>; used by the settings page to filter the picker.
    /// </summary>
    public const string ExportExtension = ".db";

    /// <summary>Hint shown in the UI, mirroring the upstream success string.</summary>
    public static string DescribeExportResult(string? path)
        => path is null ? "导出数据失败。" : $"数据已导出至 {path}";

    /// <summary>Maps a site enum to the same display text the rest of the shell uses.</summary>
    public static string DescribeSite(EhSite site) => site == EhSite.Ex ? "里站（ExHentai）" : "表站（E-Hentai）";
}
