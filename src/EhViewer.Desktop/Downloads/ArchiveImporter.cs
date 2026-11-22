using System.Globalization;
using EhViewer.Core.Util;
using Microsoft.Extensions.Logging;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace EhViewer.Desktop.Downloads;

/// <summary>
/// Translation of upstream <c>gallery/A7ZipArchive.java</c> + <c>gallery/ArchiveGalleryProvider.java</c>
/// using SharpCompress instead of the deprecated 7-Zip JNI bridge.
/// </summary>
/// <remarks>
/// Upstream rejects archives that are encrypted, solid, or split into volumes, and only keeps entries
/// that are regular files with a supported image extension. The extracted order is the natural
/// (human) order of the entry names, then renumbered as <c>{index:D8}.{ext}</c> so the result is
/// indistinguishable from a downloaded gallery.
/// <para>
/// Path handling is intentionally not derived from entry keys: the destination file name is always
/// generated, so a crafted <c>../</c> entry key cannot escape the target directory. Sizes are also
/// bounded to keep a maliciously large archive from exhausting the disk.
/// </para>
/// </remarks>
public static class ArchiveImporter
{
    /// <summary>Archive extensions upstream can open, mapped to the ones SharpCompress supports.</summary>
    public static readonly string[] SupportedExtensions =
        [".zip", ".cbz", ".rar", ".cbr", ".7z", ".cb7"];

    /// <summary>Entry extensions treated as gallery pages (upstream <c>SUPPORT_IMAGE_EXTENSIONS</c>).</summary>
    public static readonly string[] ImageExtensions =
        [".jpg", ".jpeg", ".png", ".gif", ".webp", ".bmp"];

    /// <summary>Hard ceiling on bytes written for one archive import.</summary>
    public const long MaxExtractedBytes = 4L * 1024 * 1024 * 1024;

    public static bool IsSupportedArchive(string? archivePath)
        => !string.IsNullOrWhiteSpace(archivePath)
           && SupportedExtensions.Contains(Path.GetExtension(archivePath), StringComparer.OrdinalIgnoreCase);

    public static bool IsImageEntry(string? entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
        {
            return false;
        }

        return ImageExtensions.Contains(Path.GetExtension(entryName), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Extracts the image entries of <paramref name="archivePath"/> into <paramref name="targetDirectory"/>
    /// as <c>{index:D8}.{ext}</c>. Returns the number of files written.
    /// </summary>
    /// <exception cref="InvalidOperationException">The archive is encrypted, solid, or has no pages.</exception>
    public static int Import(string archivePath, string targetDirectory, ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);

        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("归档不存在。", archivePath);
        }

        Directory.CreateDirectory(targetDirectory);

        using IArchive archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions { LeaveStreamOpen = false });

        if (archive.IsEncrypted)
        {
            throw new InvalidOperationException("加密归档不受支持。");
        }

        if (archive.IsSolid)
        {
            throw new InvalidOperationException("固实（solid）归档不受支持。");
        }

        List<IArchiveEntry> pages = archive.Entries
            .Where(static entry => !entry.IsDirectory && IsImageEntry(entry.Key))
            .OrderBy(static entry => entry.Key, NaturalComparator.Instance)
            .ToList();

        if (pages.Count == 0)
        {
            throw new InvalidOperationException("归档中没有可识别的图片页。");
        }

        int written = 0;
        long totalBytes = 0;

        foreach (IArchiveEntry entry in pages)
        {
            if (entry.Size > 0 && totalBytes + entry.Size > MaxExtractedBytes)
            {
                throw new InvalidOperationException("归档解压体积超出上限，已中止。");
            }

            string? entryKey = entry.Key;
            if (string.IsNullOrEmpty(entryKey))
            {
                continue;
            }

            string extension = Path.GetExtension(entryKey).ToLowerInvariant();
            if (!ImageExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // File name is generated, never taken from the entry key: no traversal is possible.
            string fileName = written.ToString("D8", CultureInfo.InvariantCulture) + extension;
            string destination = Path.Combine(targetDirectory, fileName);

            using (Stream source = entry.OpenEntryStream())
            using (var target = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(target);
            }

            totalBytes += new FileInfo(destination).Length;
            written++;

            logger?.LogDebug("归档导入：{Entry} -> {File}", entry.Key, fileName);
        }

        logger?.LogInformation("归档导入完成：{Archive} 共 {Count} 页", Path.GetFileName(archivePath), written);
        return written;
    }
}
