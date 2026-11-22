using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EhViewer.Core.Client.Data;

/// <summary>
/// Core gallery metadata record shared across list views, history, downloads, and detail views.
/// </summary>
public class GalleryInfo
{
    public const string SLangJa = "JA";
    public const string SLangEn = "EN";
    public const string SLangZh = "ZH";
    public const string SLangNl = "NL";
    public const string SLangFr = "FR";
    public const string SLangDe = "DE";
    public const string SLangHu = "HU";
    public const string SLangIt = "IT";
    public const string SLangKo = "KO";
    public const string SLangPl = "PL";
    public const string SLangPt = "PT";
    public const string SLangRu = "RU";
    public const string SLangEs = "ES";
    public const string SLangTh = "TH";
    public const string SLangVi = "VI";

    public static readonly string[] SLangs =
    [
        SLangEn, SLangZh, SLangEs, SLangKo, SLangRu, SLangFr, SLangPt,
        SLangTh, SLangDe, SLangIt, SLangVi, SLangPl, SLangHu, SLangNl
    ];

    public static readonly string[] SLangTags =
    [
        "language:english",
        "language:chinese",
        "language:spanish",
        "language:korean",
        "language:russian",
        "language:french",
        "language:portuguese",
        "language:thai",
        "language:german",
        "language:italian",
        "language:vietnamese",
        "language:polish",
        "language:hungarian",
        "language:dutch"
    ];

    public static readonly Regex[] SLangPatterns =
    [
        new Regex(@"[(\[]eng(?:lish)?[)\]]|英訳", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"[(\uFF08\[]ch(?:inese)?[)\uFF09\]]|[汉漢]化|中[国國][语語]|中文|中国翻訳", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"[(\[]spanish[)\]]|[(\[]Español[)\]]|スペイン翻訳", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"[(\[]korean?[)\]]|韓国翻訳", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"[(\[]rus(?:sian)?[)\]]|ロシア翻訳", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"[(\[]fr(?:ench)?[)\]]|フランス翻訳", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"[(\[]portuguese|ポルトガル翻訳", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"[(\[]thai(?: ภาษาไทย)?[)\]]|แปลไทย|タイ翻訳", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"[(\[]german[)\]]|ドイツ翻訳", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"[(\[]italiano?[)\]]|イタリア翻訳", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"[(\[]vietnamese(?: Tiếng Việt)?[)\]]|ベトナム翻訳", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"[(\[]polish[)\]]|ポーランド翻訳", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"[(\[]hun(?:garian)?[)\]]|ハンガリー翻訳", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"[(\[]dutch[)\]]|オランダ翻訳", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    ];

    public long Gid { get; set; }
    public string? Token { get; set; }
    public string? Title { get; set; }
    public string? TitleJpn { get; set; }
    public string? Thumb { get; set; }
    public int Category { get; set; }
    public string? Posted { get; set; }
    public string? Uploader { get; set; }
    public float Rating { get; set; }
    public bool Rated { get; set; }
    public string? SimpleLanguage { get; set; }
    public string[]? SimpleTags { get; set; }
    public int ThumbWidth { get; set; }
    public int ThumbHeight { get; set; }
    public int SpanSize { get; set; }
    public int SpanIndex { get; set; }
    public int SpanGroupIndex { get; set; }

    /// <summary>
    /// Favorite slot index (<c>-2</c> = not favorited, <c>-1</c> = local favorite, <c>0..9</c> = cloud favorite slot).
    /// </summary>
    public int FavoriteSlot { get; set; } = -2;

    public string? FavoriteName { get; set; }
    public int Pages { get; set; }

    public List<string>? TgList { get; set; }

    public void GenerateSLang()
    {
        if (SimpleTags != null)
        {
            GenerateSLangFromTags();
        }
        if (SimpleLanguage == null && Title != null)
        {
            GenerateSLangFromTitle();
        }
    }

    private void GenerateSLangFromTags()
    {
        if (SimpleTags == null)
        {
            return;
        }

        foreach (string tag in SimpleTags)
        {
            for (int i = 0; i < SLangs.Length; i++)
            {
                if (string.Equals(SLangTags[i], tag, StringComparison.Ordinal))
                {
                    SimpleLanguage = SLangs[i];
                    return;
                }
            }
        }
    }

    private void GenerateSLangFromTitle()
    {
        if (Title == null)
        {
            SimpleLanguage = null;
            return;
        }

        for (int i = 0; i < SLangs.Length; i++)
        {
            if (SLangPatterns[i].IsMatch(Title))
            {
                SimpleLanguage = SLangs[i];
                return;
            }
        }
        SimpleLanguage = null;
    }

    public string ToCsvLine()
    {
        string tagsText = SimpleTags == null ? "null" : $"[{string.Join(", ", SimpleTags)}]";
        return string.Create(CultureInfo.InvariantCulture,
            $"{Gid},{Token},{Title},{TitleJpn},{Thumb},{Category},{Posted},{Uploader},{Rating},{Rated},{SimpleLanguage},{tagsText},{ThumbWidth},{ThumbHeight},{SpanSize},{SpanIndex},{SpanGroupIndex},{FavoriteSlot},{FavoriteName},{Pages}\n");
    }

    public static GalleryInfo? FromCsv(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        string[] values = csv.TrimEnd('\r', '\n').Split(',');
        if (values.Length < 20)
        {
            return null;
        }

        try
        {
            var gi = new GalleryInfo
            {
                Gid = long.Parse(values[0], CultureInfo.InvariantCulture),
                Token = values[1],
                Title = values[2],
                TitleJpn = values[3],
                Thumb = values[4],
                Category = int.Parse(values[5], CultureInfo.InvariantCulture),
                Posted = values[6],
                Uploader = values[7],
                Rating = float.Parse(values[8], CultureInfo.InvariantCulture),
                Rated = bool.Parse(values[9]),
                SimpleLanguage = values[10]
            };

            string rawTags = values[11];
            if (rawTags.Length >= 2 && rawTags[0] == '[' && rawTags[^1] == ']')
            {
                string inner = rawTags[1..^1];
                gi.SimpleTags = inner.Length == 0 ? [] : inner.Split(", ");
            }

            gi.ThumbWidth = int.Parse(values[12], CultureInfo.InvariantCulture);
            gi.ThumbHeight = int.Parse(values[13], CultureInfo.InvariantCulture);
            gi.SpanSize = int.Parse(values[14], CultureInfo.InvariantCulture);
            gi.SpanIndex = int.Parse(values[15], CultureInfo.InvariantCulture);
            gi.SpanGroupIndex = int.Parse(values[16], CultureInfo.InvariantCulture);
            gi.FavoriteSlot = int.Parse(values[17], CultureInfo.InvariantCulture);
            gi.FavoriteName = values[18];
            gi.Pages = int.Parse(values[19], CultureInfo.InvariantCulture);
            return gi;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public JsonObject ToJson()
    {
        var obj = new JsonObject
        {
            ["gid"] = Gid,
            ["token"] = Token,
            ["title"] = Title,
            ["titleJpn"] = TitleJpn,
            ["thumb"] = Thumb,
            ["category"] = Category,
            ["posted"] = Posted,
            ["uploader"] = Uploader,
            ["rating"] = Rating,
            ["rated"] = Rated,
            ["simpleLanguage"] = SimpleLanguage,
            ["thumbWidth"] = ThumbWidth,
            ["thumbHeight"] = ThumbHeight,
            ["spanSize"] = SpanSize,
            ["spanIndex"] = SpanIndex,
            ["spanGroupIndex"] = SpanGroupIndex,
            ["favoriteSlot"] = FavoriteSlot,
            ["favoriteName"] = FavoriteName,
            ["pages"] = Pages
        };

        if (SimpleTags != null)
        {
            var arr = new JsonArray();
            foreach (string t in SimpleTags)
            {
                arr.Add(t);
            }
            obj["simpleTags"] = arr;
        }

        if (TgList != null)
        {
            var arr = new JsonArray();
            foreach (string t in TgList)
            {
                arr.Add(t);
            }
            obj["tgList"] = arr;
        }

        return obj;
    }

    public static GalleryInfo FromJson(JsonObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        var gi = new GalleryInfo
        {
            Posted = obj["posted"]?.GetValue<string?>(),
            Category = obj["category"]?.GetValue<int>() ?? 0,
            FavoriteName = obj["favoriteName"]?.GetValue<string?>(),
            FavoriteSlot = obj["favoriteSlot"]?.GetValue<int>() ?? -2,
            Gid = obj["gid"]?.GetValue<long>() ?? 0,
            Pages = obj["pages"]?.GetValue<int>() ?? 0,
            Rated = obj["rated"]?.GetValue<bool>() ?? false,
            Rating = obj["rating"]?.GetValue<float>() ?? 0f,
            // Fixed upstream bug: read "simpleLanguage" instead of "" (with fallback for legacy JSON)
            SimpleLanguage = obj["simpleLanguage"]?.GetValue<string?>() ?? obj[string.Empty]?.GetValue<string?>(),
            SpanGroupIndex = obj["spanGroupIndex"]?.GetValue<int>() ?? 0,
            SpanIndex = obj["spanIndex"]?.GetValue<int>() ?? 0,
            SpanSize = obj["spanSize"]?.GetValue<int>() ?? 0,
            Thumb = obj["thumb"]?.GetValue<string?>(),
            ThumbHeight = obj["thumbHeight"]?.GetValue<int>() ?? 0,
            ThumbWidth = obj["thumbWidth"]?.GetValue<int>() ?? 0,
            Title = obj["title"]?.GetValue<string?>(),
            TitleJpn = obj["titleJpn"]?.GetValue<string?>(),
            Token = obj["token"]?.GetValue<string?>(),
            Uploader = obj["uploader"]?.GetValue<string?>()
        };

        if (obj["simpleTags"] is JsonArray tagsArr)
        {
            gi.SimpleTags = tagsArr.Select(x => x?.GetValue<string?>()).Where(x => x != null).Cast<string>().ToArray();
        }

        if (obj["tgList"] is JsonArray tgArr)
        {
            gi.TgList = tgArr.Select(x => x?.GetValue<string?>()).Where(x => x != null).Cast<string>().ToList();
        }

        return gi;
    }
}
