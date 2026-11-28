using System.Globalization;
using System.Text;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Spider;
using EhViewer.Core.Storage;
using EhViewer.Core.Util;
using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Downloads;

public sealed partial class DownloadManager
{
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
        => Path.Combine(folder, index.ToString("D8", CultureInfo.InvariantCulture) + ".jpg");

    /// <summary>
    /// Finds an already-downloaded page regardless of its image extension. The downloader stores
    /// <c>{index:D8}.{ext}</c> where <c>ext</c> is derived from the payload, so a resumed download
    /// that only probed <c>.jpg</c> would re-download every <c>.png</c>/<c>.webp</c>/<c>.gif</c>
    /// page it already had.
    /// </summary>
    internal static bool TryFindExistingPageFile(string folder, int index, out string? path)
    {
        path = null;
        string stem = index.ToString("D8", CultureInfo.InvariantCulture);

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
}
