using EhViewer.Core.Network;
using EhViewer.Core.Storage;
using EhViewer.Core.Util;
using EhViewer.Desktop.Services;
using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Downloads;

/// <summary>Summary of one Android EhViewer backup / folder migration.</summary>
public sealed record AndroidMigrationReport(
    bool DatabaseImported,
    int CookiesImported,
    int GalleriesCopied,
    int FilesCopied,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Imports data produced by Android <c>Ehviewer_CN_SXJ</c>: the SQLite database (<c>eh.db</c>),
/// OkHttp cookie database (<c>okhttp3-cookie.db</c>, re-encrypted under Windows DPAPI on arrival),
/// and on-disk download directories (<c>{gid}-{title}</c> + <c>.ehviewer</c> progress).
/// </summary>
public sealed class AndroidMigrationService
{
    private readonly EhDatabase _database;
    private readonly SqliteCookieStore _cookieStore;
    private readonly AppPaths _paths;
    private readonly ILogger<AndroidMigrationService> _logger;
    private readonly Func<string>? _resolveDownloadRoot;

    public AndroidMigrationService(
        EhDatabase database,
        SqliteCookieStore cookieStore,
        AppPaths paths,
        ILogger<AndroidMigrationService> logger,
        Func<string>? resolveDownloadRoot = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _cookieStore = cookieStore ?? throw new ArgumentNullException(nameof(cookieStore));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _resolveDownloadRoot = resolveDownloadRoot;
    }

    private string EffectiveDownloadRoot => _resolveDownloadRoot?.Invoke() ?? _paths.DownloadDirectory;

    /// <summary>
    /// Migrates any combination of <paramref name="ehDatabasePath"/>,
    /// <paramref name="cookieDatabasePath"/>, and <paramref name="androidDownloadDirectory"/>.
    /// Null or missing inputs are skipped cleanly and noted in <see cref="AndroidMigrationReport.Warnings"/>.
    /// </summary>
    public AndroidMigrationReport Migrate(
        string? ehDatabasePath = null,
        string? cookieDatabasePath = null,
        string? androidDownloadDirectory = null)
    {
        bool dbImported = false;
        int cookiesImported = 0;
        int galleriesCopied = 0;
        int filesCopied = 0;
        var warnings = new List<string>();

        if (!string.IsNullOrWhiteSpace(ehDatabasePath))
        {
            if (File.Exists(ehDatabasePath))
            {
                try
                {
                    _database.ImportDatabase(ehDatabasePath);
                    dbImported = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "迁移 eh.db 失败：{Path}", ehDatabasePath);
                    warnings.Add($"eh.db 导入失败：{ex.Message}");
                }
            }
            else
            {
                warnings.Add($"未找到 eh.db：{ehDatabasePath}");
            }
        }

        if (!string.IsNullOrWhiteSpace(cookieDatabasePath))
        {
            if (File.Exists(cookieDatabasePath))
            {
                try
                {
                    cookiesImported = _cookieStore.ImportFromDatabase(cookieDatabasePath, allowUnprotectedSource: true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "迁移 Cookie 库失败：{Path}", cookieDatabasePath);
                    warnings.Add($"Cookie 导入失败：{ex.Message}");
                }
            }
            else
            {
                warnings.Add($"未找到 Cookie 数据库：{cookieDatabasePath}");
            }
        }

        if (!string.IsNullOrWhiteSpace(androidDownloadDirectory))
        {
            if (Directory.Exists(androidDownloadDirectory))
            {
                (galleriesCopied, filesCopied) = CopyDownloadDirectories(androidDownloadDirectory, warnings);
            }
            else
            {
                warnings.Add($"未找到 Android 下载目录：{androidDownloadDirectory}");
            }
        }

        return new AndroidMigrationReport(dbImported, cookiesImported, galleriesCopied, filesCopied, warnings);
    }

    private (int GalleriesCopied, int FilesCopied) CopyDownloadDirectories(
        string sourceRoot,
        List<string> warnings)
    {
        string destRootFull = Path.GetFullPath(EffectiveDownloadRoot);
        Directory.CreateDirectory(destRootFull);
        if (!destRootFull.EndsWith(Path.DirectorySeparatorChar))
        {
            destRootFull += Path.DirectorySeparatorChar;
        }

        int galleries = 0;
        int files = 0;

        IEnumerable<string> directories;
        try
        {
            directories = Directory.EnumerateDirectories(sourceRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            warnings.Add($"无法读取源下载目录：{ex.Message}");
            return (0, 0);
        }

        foreach (string sourceDirectory in directories)
        {
            string rawFolderName = Path.GetFileName(sourceDirectory);
            if (string.IsNullOrWhiteSpace(rawFolderName))
            {
                continue;
            }

            List<string> candidateFiles = CollectImportableFiles(sourceDirectory);
            if (candidateFiles.Count == 0)
            {
                continue;
            }

            string sanitizedName = FileUtils.SanitizeFilename(rawFolderName);
            if (string.IsNullOrWhiteSpace(sanitizedName))
            {
                continue;
            }

            string targetDirectory = Path.GetFullPath(Path.Combine(EffectiveDownloadRoot, sanitizedName));
            if (!targetDirectory.StartsWith(destRootFull, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"跳过不安全目录名：{rawFolderName}");
                continue;
            }

            Directory.CreateDirectory(targetDirectory);

            int copiedInFolder = 0;
            foreach (string sourceFile in candidateFiles)
            {
                string fileName = Path.GetFileName(sourceFile);
                string targetFile = Path.GetFullPath(Path.Combine(targetDirectory, fileName));
                if (!targetFile.StartsWith(targetDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    if (File.Exists(targetFile) && new FileInfo(targetFile).Length > 0)
                    {
                        continue;
                    }

                    File.Copy(sourceFile, targetFile, overwrite: true);
                    copiedInFolder++;
                    files++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    warnings.Add($"复制 {fileName} 失败：{ex.Message}");
                }
            }

            if (copiedInFolder > 0)
            {
                galleries++;
            }

            if (TryParseGid(rawFolderName, out long gid))
            {
                try
                {
                    _database.PutDownloadDirname(gid, targetDirectory);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "记录迁移目录映射失败：{Gid}", gid);
                }
            }
        }

        return (galleries, files);
    }

    private static List<string> CollectImportableFiles(string directory)
    {
        var list = new List<string>();
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            if (string.Equals(name, ".ehviewer", StringComparison.OrdinalIgnoreCase)
                || ArchiveImporter.IsImageEntry(name))
            {
                list.Add(file);
            }
        }

        return list;
    }

    private static bool TryParseGid(string folderName, out long gid)
    {
        int dash = folderName.IndexOf('-');
        string gidSpan = dash > 0 ? folderName[..dash] : folderName;
        return long.TryParse(gidSpan, out gid) && gid > 0;
    }
}
