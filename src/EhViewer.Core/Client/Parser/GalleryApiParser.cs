using System.Text.Json;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Util;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses JSON <c>gmetadata</c> API responses and merges fields into matching <see cref="GalleryInfo"/> instances.
/// </summary>
public static class GalleryApiParser
{
    public static void Parse(string body, IList<GalleryInfo> galleryInfoList, int thumbResolution = 0)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(galleryInfoList);

        JsonElement root = ParserUtils.ParseJsonObject(body);
        JsonElement ja = ParserUtils.GetRequiredProperty(root, "gmetadata");
        if (ja.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Expected 'gmetadata' JSON array.");
        }

        foreach (JsonElement g in ja.EnumerateArray())
        {
            long gid = ParserUtils.GetRequiredInt64(g, "gid");
            GalleryInfo? gi = FindByGid(galleryInfoList, gid);
            if (gi is null)
            {
                continue;
            }

            // A removed or rejected gallery has "error" and no title. Skip it so one bad
            // item does not abort the rest of this batch.
            if (g.TryGetProperty("error", out _) ||
                !g.TryGetProperty("title", out JsonElement titleEl) ||
                titleEl.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            gi.Title = ParserUtils.Trim(ParserUtils.GetRequiredString(g, "title"));
            gi.TitleJpn = ParserUtils.Trim(ParserUtils.GetRequiredString(g, "title_jpn"));
            gi.Category = EhUtils.GetCategory(ParserUtils.GetRequiredString(g, "category"));
            gi.Thumb = EhUtils.HandleThumbUrlResolution(ParserUtils.GetRequiredString(g, "thumb"), thumbResolution);
            gi.Uploader = ParserUtils.GetRequiredString(g, "uploader");
            gi.Posted = ParserUtils.FormatDate(ParserUtils.ParseLong(ParserUtils.GetRequiredString(g, "posted"), 0) * 1000L);
            gi.Rating = NumberUtils.ParseFloatSafely(ParserUtils.GetRequiredString(g, "rating"), 0.0f);

            JsonElement tagJa = ParserUtils.GetRequiredProperty(g, "tags");
            if (tagJa.ValueKind != JsonValueKind.Array)
            {
                throw new JsonException("Expected 'tags' JSON array.");
            }

            int tagLength = tagJa.GetArrayLength();
            string[] tags = new string[tagLength];
            int j = 0;
            foreach (JsonElement tagEl in tagJa.EnumerateArray())
            {
                tags[j++] = tagEl.GetString() ?? throw new JsonException("Null tag element in 'tags'.");
            }

            gi.SimpleTags = tags;
            gi.Pages = NumberUtils.ParseIntSafely(ParserUtils.GetRequiredString(g, "filecount"), 0);
            gi.GenerateSLang();
        }
    }

    private static GalleryInfo? FindByGid(IList<GalleryInfo> galleryInfoList, long gid)
    {
        for (int i = 0; i < galleryInfoList.Count; i++)
        {
            if (galleryInfoList[i].Gid == gid)
            {
                return galleryInfoList[i];
            }
        }
        return null;
    }
}
