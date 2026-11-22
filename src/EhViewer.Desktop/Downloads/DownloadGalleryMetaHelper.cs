using EhViewer.Core.Util;

namespace EhViewer.Desktop.Downloads;

/// <summary>
/// Translation of upstream <c>download/DownloadGalleryMetaHelper.java</c>: resolves the timestamp to
/// show for a downloaded gallery, preferring the folder creation time, then its last write time, and
/// finally the download task's own time.
/// </summary>
/// <remarks>
/// Upstream converts an Android <c>UniFile</c> to a <c>java.nio.file.Path</c> to read the creation
/// time because <c>File.lastModified()</c> cannot report it. On Windows the same information is
/// available directly, and NTFS preserves creation time, so the ordering is preserved faithfully.
/// </remarks>
public static class DownloadGalleryMetaHelper
{
    /// <summary>
    /// Directory timestamp in Unix milliseconds: creation time, else last write time, else
    /// <paramref name="taskTimeMillis"/> (upstream <c>info.time</c>), clamped at zero.
    /// </summary>
    public static long GetGalleryDirectoryTimestamp(
        string downloadRoot,
        Core.Client.Data.GalleryInfo info,
        long taskTimeMillis)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(downloadRoot);
        ArgumentNullException.ThrowIfNull(info);

        try
        {
            string directory = DownloadManager.BuildGalleryFolder(downloadRoot, info);

            long creation = GetDirectoryCreationTime(directory);
            if (creation > 0)
            {
                return creation;
            }

            long modified = GetDirectoryLastModifiedTime(directory);
            if (modified > 0)
            {
                return modified;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Fall through to the task time, exactly like upstream's catch-and-ignore.
        }

        return Math.Max(taskTimeMillis, 0L);
    }

    private static long GetDirectoryCreationTime(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return -1L;
        }

        try
        {
            return new DateTimeOffset(Directory.GetCreationTimeUtc(directory), TimeSpan.Zero).ToUnixTimeMilliseconds();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1L;
        }
    }

    private static long GetDirectoryLastModifiedTime(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return -1L;
        }

        try
        {
            return new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero).ToUnixTimeMilliseconds();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1L;
        }
    }
}
