using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Services.Translation;

public sealed partial class ComicBubbleOcrPipeline
{
    internal static bool IsRangeSatisfiedAsComplete(
        long existingBytes,
        long? contentRangeTotalLength,
        long minValidBytes)
        => contentRangeTotalLength.HasValue &&
           contentRangeTotalLength.Value >= minValidBytes &&
           existingBytes == contentRangeTotalLength.Value;

    internal static bool IsValidModelFile(string filePath, bool isVocabFile)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var info = new FileInfo(filePath);
            long minBytes = isVocabFile ? MinValidVocabBytes : MinValidOnnxBytes;
            if (info.Length < minBytes)
            {
                return false;
            }

            if (isVocabFile)
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                int nonEmptyLines = 0;
                bool checkedFirstNonWhitespace = false;
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }

                    if (!checkedFirstNonWhitespace)
                    {
                        checkedFirstNonWhitespace = true;
                        if (trimmed[0] is '<' or '{')
                        {
                            return false;
                        }
                    }

                    nonEmptyLines++;
                    if (nonEmptyLines >= 10)
                    {
                        return true;
                    }
                }

                return false;
            }

            Span<byte> header = stackalloc byte[64];
            int bytesRead;
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                bytesRead = fs.Read(header);
            }

            if (bytesRead < 4)
            {
                return false;
            }

            ReadOnlySpan<byte> slice = header[..bytesRead];
            if (LooksLikeTextErrorPage(slice))
            {
                return false;
            }

            // ONNX ModelProto fields 1..8 tags:
            // 1:ir_version(0x08), 2:producer_name(0x12), 3:producer_version(0x1A), 4:domain(0x22),
            // 5:model_version(0x28), 6:doc_string(0x32), 7:graph(0x3A), 8:opset_import(0x42).
            byte firstByte = slice[0];
            bool validModelProtoTag = firstByte is 0x08 or 0x12 or 0x1A or 0x22 or 0x28 or 0x32 or 0x3A or 0x42;
            if (!validModelProtoTag)
            {
                return false;
            }

            for (int i = 1; i < slice.Length; i++)
            {
                if (slice[i] != 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool LooksLikeTextErrorPage(ReadOnlySpan<byte> bytes)
    {
        int start = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            start = 3;
        }

        while (start < bytes.Length && bytes[start] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
        {
            start++;
        }

        if (start >= bytes.Length)
        {
            return true;
        }

        byte first = bytes[start];
        if (first is (byte)'<' or (byte)'{')
        {
            return true;
        }

        ReadOnlySpan<byte> remaining = bytes[start..];
        return StartsWithAsciiIgnoreCase(remaining, "<html"u8) ||
               StartsWithAsciiIgnoreCase(remaining, "<!doc"u8) ||
               StartsWithAsciiIgnoreCase(remaining, "<?xml"u8);
    }

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> data, ReadOnlySpan<byte> asciiPrefix)
    {
        if (data.Length < asciiPrefix.Length)
        {
            return false;
        }

        for (int i = 0; i < asciiPrefix.Length; i++)
        {
            byte a = data[i];
            if (a is >= (byte)'A' and <= (byte)'Z')
            {
                a = (byte)(a + 32);
            }

            if (a != asciiPrefix[i])
            {
                return false;
            }
        }

        return true;
    }

    public async Task DownloadModelsAsync(
        bool useMirror,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        Directory.CreateDirectory(_paths.OcrModelsDirectory);

        (string Name, string Url, string Dest, long ApproxBytes)[] items =
        [
            (
                "气泡文本检测器 (RT-DETR)",
                useMirror ? BubbleDetectorMirrorUrl : BubbleDetectorUrl,
                _paths.BubbleDetectorModelPath,
                43_838_857
            ),
            (
                "Manga-OCR CPT-v2 视觉编码器",
                useMirror ? MangaOcrEncoderMirrorUrl : MangaOcrEncoderUrl,
                _paths.MangaOcrEncoderPath,
                86_912_680
            ),
            (
                "Manga-OCR CPT-v2 日语解码器",
                useMirror ? MangaOcrDecoderMirrorUrl : MangaOcrDecoderUrl,
                _paths.MangaOcrDecoderPath,
                30_632_263
            ),
            (
                "Manga-OCR CPT-v2 词表",
                useMirror ? MangaOcrVocabMirrorUrl : MangaOcrVocabUrl,
                _paths.MangaOcrVocabPath,
                27_001
            )
        ];

        long totalExpected = items.Sum(i => i.ApproxBytes);
        long completedOffset = 0;

        foreach ((string name, string url, string dest, long approxBytes) in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool isVocabFile = dest.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
            if (File.Exists(dest) && new FileInfo(dest).Length == approxBytes && IsValidModelFile(dest, isVocabFile))
            {
                completedOffset += approxBytes;
                progress?.Report(new ModelDownloadProgress(completedOffset, totalExpected, $"{name} 已存在，跳过"));
                continue;
            }

            string partPath = dest + ".part";
            if (File.Exists(partPath) && new FileInfo(partPath).Length > approxBytes)
            {
                File.Delete(partPath);
            }

            long itemOffset = completedOffset;
            var subProgress = new Progress<ModelDownloadProgress>(p =>
            {
                long overallDone = Math.Min(totalExpected, itemOffset + p.BytesReceived);
                progress?.Report(new ModelDownloadProgress(overallDone, totalExpected, p.StatusText));
            });

            await DownloadFileWithResumeAsync(url, dest, $"正在下载 {name}", subProgress, cancellationToken)
                .ConfigureAwait(false);
            completedOffset += approxBytes;
        }

        await ResetSessionsAsync().ConfigureAwait(false);
        CleanupLegacyV1Models();
        progress?.Report(new ModelDownloadProgress(totalExpected, totalExpected, "Manga-OCR CPT-v2 离线识别模型已全部就绪"));
    }

    private void CleanupLegacyV1Models()
    {
        foreach (string legacyFileName in LegacyV1ModelFileNames)
        {
            foreach (string path in new[]
            {
                Path.Combine(_paths.OcrModelsDirectory, legacyFileName),
                Path.Combine(_paths.OcrModelsDirectory, legacyFileName + ".part")
            })
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger?.LogDebug(ex, "Skipping cleanup of legacy OCR file {Path}", path);
                }
            }
        }
    }

    internal async Task DownloadFileWithResumeAsync(
        string url,
        string destinationPath,
        string label,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string partPath = destinationPath + ".part";
        bool isVocabFile = destinationPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
        long minValidBytes = isVocabFile ? MinValidVocabBytes : MinValidOnnxBytes;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            long existingBytes = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("EhViewer.Desktop/1.0");
            if (existingBytes > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existingBytes, null);
            }

            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                long? contentRangeTotal = response.Content.Headers.ContentRange?.Length;
                if (existingBytes > 0 &&
                    IsRangeSatisfiedAsComplete(existingBytes, contentRangeTotal, minValidBytes) &&
                    IsValidModelFile(partPath, isVocabFile))
                {
                    File.Move(partPath, destinationPath, overwrite: true);
                    progress?.Report(new ModelDownloadProgress(existingBytes, existingBytes, $"{label} 完成"));
                    return;
                }

                TryDeleteFile(partPath);
                if (attempt == 0)
                {
                    continue;
                }

                throw new InvalidDataException($"下载 {label} 收到 HTTP 416 且重试失败，已清理损坏的临时文件。");
            }

            response.EnsureSuccessStatusCode();

            bool append = response.StatusCode == HttpStatusCode.PartialContent && existingBytes > 0;
            long baseOffset = append ? existingBytes : 0;
            long? contentLength = response.Content.Headers.ContentLength;
            long? contentRangeTotalLength = response.Content.Headers.ContentRange?.Length;
            long? expectedTotalBytes = contentRangeTotalLength ??
                (contentLength.HasValue ? baseOffset + contentLength.Value : null);

            await using (Stream remote = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var local = new FileStream(
                partPath,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            {
                byte[] buffer = new byte[81920];
                long written = baseOffset;
                long lastTick = Environment.TickCount64;
                progress?.Report(new ModelDownloadProgress(written, expectedTotalBytes, $"{label}..."));

                int read;
                while ((read = await remote.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await local.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    written += read;
                    long now = Environment.TickCount64;
                    if (now - lastTick >= 200)
                    {
                        lastTick = now;
                        progress?.Report(new ModelDownloadProgress(written, expectedTotalBytes, $"{label}..."));
                    }
                }

                await local.FlushAsync(cancellationToken).ConfigureAwait(false);
                progress?.Report(new ModelDownloadProgress(written, expectedTotalBytes ?? written, $"{label} 完成"));
            }

            long finalLength = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            if (expectedTotalBytes.HasValue && finalLength != expectedTotalBytes.Value)
            {
                TryDeleteFile(partPath);
                throw new InvalidDataException(
                    $"下载 {label} 长度不匹配（期望 {expectedTotalBytes.Value} 字节，实际 {finalLength} 字节）。");
            }

            if (!IsValidModelFile(partPath, isVocabFile))
            {
                TryDeleteFile(partPath);
                throw new InvalidDataException($"下载 {label} 完整性校验失败，临时文件已删除。");
            }

            File.Move(partPath, destinationPath, overwrite: true);
            return;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
