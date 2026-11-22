using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Text.Json.Serialization;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Client.Parser;
using EhViewer.Core.Util;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace EhViewer.Desktop.Services;

/// <summary>Where a pasted URL points: a gallery detail page, or one page of a gallery.</summary>
public abstract record ClipboardTarget
{
    public sealed record GalleryDetailTarget(long Gid, string Token) : ClipboardTarget;

    public sealed record GalleryPageTarget(long Gid, string PToken, int Page) : ClipboardTarget;
}

/// <summary>
/// Translation of upstream <c>util/ClipboardUtil.java</c>: copy a gallery (or its title/link) and the
/// inverse "read a gallery or gallery URL back from the clipboard".
/// </summary>
/// <remarks>
/// Upstream packs <c>LocalFavoriteInfo</c> through fastjson, gzips and Base64-encodes it. This port
/// keeps the same wire shape (gzip + Base64 over a compact JSON object) so a payload produced by the
/// Android client still decodes here, but uses System.Text.Json with an explicit field list instead
/// of reflecting over the whole entity, which keeps volatile fields out by construction.
/// </remarks>
public static class ClipboardService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Copies plain text, clearing the clipboard first like upstream <c>copyText</c>.</summary>
    public static void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    /// <summary>Copies the gallery as an announce payload (gzip + Base64 JSON).</summary>
    public static void CopyGallery(GalleryInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);

        string json = JsonSerializer.Serialize(ToPayload(info), JsonOptions);
        string? packed = GzipUtils.Compress(json);
        if (packed is not null)
        {
            CopyText(packed);
        }
    }

    /// <summary>Copies the image currently shown to the clipboard as a bitmap.</summary>
    public static async Task<bool> CopyImageAsync(WriteableBitmap? bitmap)
    {
        if (bitmap is null)
        {
            return false;
        }

        try
        {
            using InMemoryRandomAccessStream stream = new();
            BitmapEncoder encoder = await BitmapEncoder
                .CreateAsync(BitmapEncoder.PngEncoderId, stream)
                .AsTask()
                .ConfigureAwait(true);

            byte[] pixels = await GetPixelBytesAsync(bitmap).ConfigureAwait(true);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)bitmap.PixelWidth,
                (uint)bitmap.PixelHeight,
                96,
                96,
                pixels);

            await encoder.FlushAsync().AsTask().ConfigureAwait(true);
            stream.Seek(0);

            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
            Clipboard.SetContent(package);
            Clipboard.Flush();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Reads an announce payload back into a <see cref="GalleryInfo"/>, or null.</summary>
    public static GalleryInfo? TryReadGallery()
    {
        string? text = ReadText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string? json = GzipUtils.Uncompress(text.Trim());
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            AnnouncePayload? payload = JsonSerializer.Deserialize<AnnouncePayload>(json, JsonOptions);
            return payload?.ToGalleryInfo();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Raw clipboard text, or null when the clipboard holds no text.</summary>
    public static string? ReadText()
    {
        try
        {
            if (!Clipboard.GetContent().Contains(StandardDataFormats.Text))
            {
                return null;
            }

            return Clipboard.GetContent().GetTextAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Port of upstream <c>createAnnouncerFromClipboardUrl</c>: recognises a gallery detail URL or a
    /// gallery page URL and describes where to navigate to.
    /// </summary>
    public static ClipboardTarget? ParseTarget(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string candidate = url.Trim();

        GalleryDetailUrlParser.Result? detail = GalleryDetailUrlParser.Parse(candidate, strict: false);
        if (detail is not null)
        {
            return new ClipboardTarget.GalleryDetailTarget(detail.Gid, detail.Token);
        }

        GalleryPageUrlParser.Result? page = GalleryPageUrlParser.Parse(candidate, strict: false);
        if (page is not null)
        {
            return new ClipboardTarget.GalleryPageTarget(page.Gid, page.PToken, page.Page);
        }

        return null;
    }

    /// <summary>Convenience: parse whatever is currently on the clipboard.</summary>
    public static ClipboardTarget? TryReadTarget() => ParseTarget(ReadText());

    private static async Task<byte[]> GetPixelBytesAsync(WriteableBitmap bitmap)
    {
        using Stream stream = bitmap.PixelBuffer.AsStream();
        byte[] pixels = new byte[stream.Length];
        _ = await stream.ReadAsync(pixels).ConfigureAwait(true);
        return pixels;
    }

    private static AnnouncePayload ToPayload(GalleryInfo info) => new()
    {
        Gid = info.Gid,
        Token = info.Token,
        Title = info.Title,
        TitleJpn = info.TitleJpn,
        Thumb = info.Thumb,
        Category = info.Category,
        Posted = info.Posted,
        Uploader = info.Uploader,
        Rating = info.Rating,
        SimpleLanguage = info.SimpleLanguage,
        SimpleTags = info.SimpleTags
    };

    /// <summary>
    /// Compact wire shape. Field names are lower-camel to stay compatible with the Android payload
    /// produced by fastjson over the database entity.
    /// </summary>
    private sealed class AnnouncePayload
    {
        [JsonPropertyName("gid")]
        public long Gid { get; set; }

        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("titleJpn")]
        public string? TitleJpn { get; set; }

        [JsonPropertyName("thumb")]
        public string? Thumb { get; set; }

        [JsonPropertyName("category")]
        public int Category { get; set; }

        [JsonPropertyName("posted")]
        public string? Posted { get; set; }

        [JsonPropertyName("uploader")]
        public string? Uploader { get; set; }

        [JsonPropertyName("rating")]
        public float Rating { get; set; }

        [JsonPropertyName("simpleLanguage")]
        public string? SimpleLanguage { get; set; }

        [JsonPropertyName("simpleTags")]
        public string[]? SimpleTags { get; set; }

        public GalleryInfo ToGalleryInfo() => new()
        {
            Gid = Gid,
            Token = Token,
            Title = Title,
            TitleJpn = TitleJpn,
            Thumb = Thumb,
            Category = Category,
            Posted = Posted,
            Uploader = Uploader,
            Rating = Rating,
            SimpleLanguage = SimpleLanguage,
            SimpleTags = SimpleTags
        };
    }
}
