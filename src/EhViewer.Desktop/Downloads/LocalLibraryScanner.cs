using EhViewer.Core.Spider;
using EhViewer.Core.Util;
using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Downloads;

/// <summary>Sort ordering for local scanned galleries.</summary>
public enum LocalLibrarySortMode
{
    GidDescending = 0,
    TimeDescending = 1,
    TitleAscending = 2
}

/// <summary>One gallery discovered on disk.</summary>
public sealed record LocalGallery(
    long Gid,
    string Title,
    string DirectoryPath,
    int StartPage,
    int Pages,
    int ImageCount,
    bool HasProgressFile,
    long TimestampMs = 0,
    string? Label = null);

/// <summary>
/// Scans a download root for galleries produced by EhViewer (Android or this client) and imports
/// stand-alone archives. Folder and file naming follows the upstream conventions so an Android
/// download tree can be read in place without conversion.
/// </summary>
public sealed class LocalLibraryScanner
{
    private readonly ILogger<LocalLibraryScanner> _logger;

    public LocalLibraryScanner(ILogger<LocalLibraryScanner> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Enumerates <c>{gid}-{title}</c> folders under <paramref name="downloadRoot"/> that contain at
    /// least one image, reading <c>.ehviewer</c> progress when present.
    /// </summary>
    public IReadOnlyList<LocalGallery> Scan(string downloadRoot) =>
        Scan(downloadRoot, LocalLibrarySortMode.GidDescending);

    /// <summary>
    /// Enumerates <c>{gid}-{title}</c> folders under <paramref name="downloadRoot"/> with configurable
    /// ordering and optional DB label/timestamp enrichment.
    /// </summary>
    public IReadOnlyList<LocalGallery> Scan(
        string downloadRoot,
        LocalLibrarySortMode sortMode,
        IReadOnlyDictionary<long, string?>? labelLookup = null,
        IReadOnlyDictionary<long, long>? taskTimeLookup = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadRoot);

        var results = new List<LocalGallery>();
        if (!Directory.Exists(downloadRoot))
        {
            return results;
        }

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(downloadRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "无法枚举下载目录：{Root}", downloadRoot);
            return results;
        }

        foreach (string directory in directories)
        {
            try
            {
                LocalGallery? gallery = ReadGallery(directory);
                if (gallery is not null)
                {
                    long ts = gallery.TimestampMs;
                    if (ts <= 0 && taskTimeLookup is not null && taskTimeLookup.TryGetValue(gallery.Gid, out long fallbackTs))
                    {
                        ts = fallbackTs;
                    }

                    string? label = null;
                    if (labelLookup is not null && labelLookup.TryGetValue(gallery.Gid, out string? foundLabel))
                    {
                        label = foundLabel;
                    }

                    results.Add(gallery with { TimestampMs = ts, Label = label });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "跳过无法读取的目录：{Directory}", directory);
            }
        }

        switch (sortMode)
        {
            case LocalLibrarySortMode.TimeDescending:
                results.Sort(static (a, b) =>
                {
                    int cmp = b.TimestampMs.CompareTo(a.TimestampMs);
                    return cmp != 0 ? cmp : b.Gid.CompareTo(a.Gid);
                });
                break;
            case LocalLibrarySortMode.TitleAscending:
                results.Sort(static (a, b) =>
                {
                    int cmp = string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
                    return cmp != 0 ? cmp : b.Gid.CompareTo(a.Gid);
                });
                break;
            default:
                results.Sort(static (a, b) => b.Gid.CompareTo(a.Gid));
                break;
        }

        return results;
    }

    /// <summary>Reads one gallery folder, or <see langword="null"/> when it is not an EhViewer gallery.</summary>
    public LocalGallery? ReadGallery(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        string folderName = Path.GetFileName(directory);
        if (string.IsNullOrEmpty(folderName))
        {
            return null;
        }

        int dash = folderName.IndexOf('-');
        string gidText = dash > 0 ? folderName[..dash] : folderName;
        if (!long.TryParse(gidText, out long gid))
        {
            return null;
        }

        string title = dash > 0 ? folderName[(dash + 1)..] : folderName;

        int imageCount = CountImages(directory);
        if (imageCount == 0)
        {
            return null;
        }

        string progressFile = Path.Combine(directory, ".ehviewer");
        bool hasProgress = File.Exists(progressFile);
        int startPage = 0;
        int pages = imageCount;

        if (hasProgress)
        {
            try
            {
                using FileStream stream = File.OpenRead(progressFile);
                SpiderInfo? info = SpiderInfo.ReadHeader(stream);
                if (info is not null)
                {
                    startPage = info.StartPage < 0 ? 0 : info.StartPage;
                    if (info.Pages > 0)
                    {
                        pages = info.Pages;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "读取进度文件失败：{Path}", progressFile);
            }
        }

        long timestampMs = 0;
        try
        {
            DateTime created = Directory.GetCreationTimeUtc(directory);
            if (created.Year > 1970)
            {
                timestampMs = new DateTimeOffset(created, TimeSpan.Zero).ToUnixTimeMilliseconds();
            }

            if (timestampMs <= 0)
            {
                DateTime modified = Directory.GetLastWriteTimeUtc(directory);
                if (modified.Year > 1970)
                {
                    timestampMs = new DateTimeOffset(modified, TimeSpan.Zero).ToUnixTimeMilliseconds();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            timestampMs = 0;
        }

        return new LocalGallery(gid, title, directory, startPage, pages, imageCount, hasProgress, timestampMs);
    }

    /// <summary>Counts images named with the upstream eight-digit index.</summary>
    internal static int CountImages(string directory)
    {
        try
        {
            return Directory
                .EnumerateFiles(directory, "*.*", SearchOption.TopDirectoryOnly)
                .Count(static f =>
                {
                    string name = Path.GetFileNameWithoutExtension(f);
                    if (name.Length != 8)
                    {
                        return false;
                    }

                    foreach (char c in name)
                    {
                        if (c is < '0' or > '9')
                        {
                            return false;
                        }
                    }

                    string ext = Path.GetExtension(f);
                    return !string.IsNullOrEmpty(ext)
                        && !string.Equals(ext, ".part", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(ext, ".tmp", StringComparison.OrdinalIgnoreCase);
                });
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
    }

    /// <summary>
    /// Extracts a supported comic archive (<c>.zip/.cbz/.rar/.cbr/.7z/.cb7</c>) into a gallery
    /// folder named <c>{gid}-{sanitized_title}</c>, using natural page ordering and writing a clean
    /// <c>.ehviewer</c> progress file.
    /// </summary>
    public string? ImportArchive(string archivePath, string downloadRoot, long gid, string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadRoot);

        if (!ArchiveImporter.IsSupportedArchive(archivePath))
        {
            return null;
        }

        string safeTitle = FileUtils.SanitizeFilename(string.IsNullOrWhiteSpace(title) ? "archive" : title);
        string folderName = $"{gid}-{safeTitle}";
        string targetDirectory = Path.Combine(downloadRoot, folderName);

        try
        {
            int count = ArchiveImporter.Import(archivePath, targetDirectory, _logger);
            if (count <= 0)
            {
                return null;
            }

            // Imported archives start at page 0 so the reader begins cleanly at page 1.
            var info = new SpiderInfo
            {
                StartPage = 0,
                Gid = gid,
                Token = null,
                Pages = count,
                PreviewPages = -1,
                PreviewPerPage = -1
            };

            try
            {
                using var stream = new FileStream(
                    Path.Combine(targetDirectory, ".ehviewer"),
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);
                info.Write(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "写入导入进度失败：{Directory}", targetDirectory);
            }

            return targetDirectory;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "导入压缩包失败：{Archive}", archivePath);
            return null;
        }
    }
}
