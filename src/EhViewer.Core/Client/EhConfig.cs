using EhViewer.Core.Network;

namespace EhViewer.Core.Client;

public enum EhSite
{
    E = 0,
    Ex = 1
}

/// <summary>
/// E-Hentai/ExHentai site configuration and <c>uconfig</c> cookie generator.
/// </summary>
public sealed class EhConfig
{
    public const string KeyUconfig = "uconfig";

    private const string KeyLoadFromHah = "uh";
    private const string KeyImageSize = "xr";
    private const string KeyScaleWidth = "rx";
    private const string KeyScaleHeight = "ry";
    private const string KeyGalleryTitle = "tl";
    private const string KeyArchiverDownload = "ar";
    private const string KeyLayoutMode = "dm";
    private const string KeyPopular = "prn";
    private const string KeyDefaultCategories = "cats";
    private const string KeyFavoritesSort = "fs";
    private const string KeyExcludedNamespaces = "xns";
    private const string KeyExcludedLanguages = "xl";
    private const string KeyResultCount = "rc";
    private const string KeyMouseOver = "lt";
    private const string KeyPreviewSize = "ts";
    private const string KeyPreviewRow = "tr";
    private const string KeyCommentsSort = "cs";
    private const string KeyCommentsVotes = "sc";
    private const string KeyTagsSort = "to";
    private const string KeyShowGalleryIndex = "pn";
    private const string KeyHahClientIpPort = "hp";
    private const string KeyHahClientPasskey = "hk";
    private const string KeyEnableTagFlagging = "tf";
    private const string KeyAlwaysOriginal = "oi";
    private const string KeyMultiPage = "qb";
    private const string KeyMultiPageStyle = "ms";
    private const string KeyMultiPageThumb = "mt";

    public const string KeyLofiResolution = "xres";
    public const string KeyContentWarning = "nw";

    public const string LoadFromHahYes = "y";
    public const string LoadFromHahNo = "n";

    public const string ImageSizeAuto = "a";
    public const string ImageSize780X = "780";
    public const string ImageSize980X = "980";
    public const string ImageSize1280X = "1280";
    public const string ImageSize1600X = "1600";
    public const string ImageSize2400X = "2400";

    public const string GalleryTitleDefault = "r";
    public const string GalleryTitleJapanese = "j";

    public const string ArchiverDownloadMams = "0";
    public const string ArchiverDownloadAams = "1";
    public const string ArchiverDownloadMaas = "2";
    public const string ArchiverDownloadAaas = "3";

    public const string LayoutModeList = "l";
    public const string LayoutModeThumb = "t";

    public const string PopularYes = "y";
    public const string PopularNo = "n";

    public const int Misc = 0x1;
    public const int Doujinshi = 0x2;
    public const int Manga = 0x4;
    public const int ArtistCg = 0x8;
    public const int GameCg = 0x10;
    public const int ImageSet = 0x20;
    public const int Cosplay = 0x40;
    public const int AsianPorn = 0x80;
    public const int NonH = 0x100;
    public const int Western = 0x200;
    public const int AllCategory = 0x3ff;

    public const int NamespacesReclass = 0x1;
    public const int NamespacesLanguage = 0x2;
    public const int NamespacesParody = 0x4;
    public const int NamespacesCharacter = 0x8;
    public const int NamespacesGroup = 0x10;
    public const int NamespacesArtist = 0x20;
    public const int NamespacesMale = 0x40;
    public const int NamespacesFemale = 0x80;

    public const string JapaneseOriginal = "0";
    public const string JapaneseTranslated = "1024";
    public const string JapaneseRewrite = "2048";
    public const string EnglishOriginal = "1";
    public const string EnglishTranslated = "1025";
    public const string EnglishRewrite = "2049";
    public const string ChineseOriginal = "10";
    public const string ChineseTranslated = "1034";
    public const string ChineseRewrite = "2058";
    public const string DutchOriginal = "20";
    public const string DutchTranslated = "1044";
    public const string DutchRewrite = "2068";
    public const string FrenchOriginal = "30";
    public const string FrenchTranslated = "1054";
    public const string FrenchRewrite = "2078";
    public const string GermanOriginal = "40";
    public const string GermanTranslated = "1064";
    public const string GermanRewrite = "2088";
    public const string HungarianOriginal = "50";
    public const string HungarianTranslated = "1074";
    public const string HungarianRewrite = "2098";
    public const string ItalianOriginal = "60";
    public const string ItalianTranslated = "1084";
    public const string ItalianRewrite = "2108";
    public const string KoreanOriginal = "70";
    public const string KoreanTranslated = "1094";
    public const string KoreanRewrite = "2118";
    public const string PolishOriginal = "80";
    public const string PolishTranslated = "1104";
    public const string PolishRewrite = "2128";
    public const string PortugueseOriginal = "90";
    public const string PortugueseTranslated = "1114";
    public const string PortugueseRewrite = "2138";
    public const string RussianOriginal = "100";
    public const string RussianTranslated = "1124";
    public const string RussianRewrite = "2148";
    public const string SpanishOriginal = "110";
    public const string SpanishTranslated = "1134";
    public const string SpanishRewrite = "2158";
    public const string ThaiOriginal = "120";
    public const string ThaiTranslated = "1144";
    public const string ThaiRewrite = "2168";
    public const string VietnameseOriginal = "130";
    public const string VietnameseTranslated = "1154";
    public const string VietnameseRewrite = "2178";
    public const string NaOriginal = "254";
    public const string NaTranslated = "1278";
    public const string NaRewrite = "2302";
    public const string OtherOriginal = "255";
    public const string OtherTranslated = "1279";
    public const string OtherRewrite = "2303";

    public const string FavoritesSortGalleryUpdateTime = "p";
    public const string FavoritesSortFavoritedTime = "f";

    public const string ResultCount25 = "0";
    public const string ResultCount50 = "1";
    public const string ResultCount100 = "2";
    public const string ResultCount200 = "3";

    public const string MouseOverYes = "m";
    public const string MouseOverNo = "p";

    public const string PreviewSizeNormal = "m";
    public const string PreviewSizeLarge = "l";

    public const string PreviewRow4 = "2";
    public const string PreviewRow10 = "5";
    public const string PreviewRow20 = "10";
    public const string PreviewRow40 = "20";

    public const string CommentsSortOldestFirst = "a";
    public const string CommentsSortRecentFirst = "d";
    public const string CommentsSortHighestScoreFirst = "s";

    public const string CommentsVotesPop = "0";
    public const string CommentsVotesAlways = "1";

    public const string TagsSortAlphabetical = "a";
    public const string TagsSortPower = "p";

    public const string ShowGalleryIndexYes = "1";
    public const string ShowGalleryIndexNo = "0";

    public const string EnableTagFlaggingYes = "y";
    public const string EnableTagFlaggingNo = "n";

    public const string AlwaysOriginalYes = "y";
    public const string AlwaysOriginalNo = "n";

    public const string MultiPageYes = "y";
    public const string MultiPageNo = "n";

    public const string MultiPageStyleN = "n";
    public const string MultiPageStyleC = "c";
    public const string MultiPageStyleY = "y";

    public const string MultiPageThumbShow = "n";
    public const string MultiPageThumbHide = "y";

    public const string LofiResolution460X = "1";
    public const string LofiResolution780X = "2";
    public const string LofiResolution980X = "3";

    public const string ContentWarningShow = "0";
    public const string ContentWarningNotShow = "1";

    public const string OrderByFavTime = "f";
    public const string OrderByPubTime = "p";

    private string? _uconfig;
    private bool _dirty = true;

    public string LoadFromHah { get; set; } = LoadFromHahYes;
    public string ImageSize { get; set; } = ImageSizeAuto;
    public int ScaleWidth { get; set; }
    public int ScaleHeight { get; set; }
    public string GalleryTitle { get; set; } = GalleryTitleDefault;
    public string ArchiverDownload { get; set; } = ArchiverDownloadMams;
    public string LayoutMode { get; set; } = LayoutModeList;
    public string Popular { get; set; } = PopularYes;
    public int DefaultCategories { get; set; }
    public string FavoritesSort { get; set; } = FavoritesSortFavoritedTime;
    public int ExcludedNamespaces { get; set; }
    public string ExcludedLanguages { get; set; } = string.Empty;
    public string ResultCount { get; set; } = ResultCount25;
    public string MouseOver { get; set; } = MouseOverYes;
    public string PreviewSize { get; set; } = PreviewSizeLarge;
    public string PreviewRow { get; set; } = PreviewRow4;
    public string CommentSort { get; set; } = CommentsSortOldestFirst;
    public string CommentVotes { get; set; } = CommentsVotesPop;
    public string TagSort { get; set; } = TagsSortAlphabetical;
    public string ShowGalleryIndex { get; set; } = ShowGalleryIndexYes;
    public string HahClientIp { get; set; } = string.Empty;
    public int HahClientPort { get; set; } = -1;
    public string HahClientPasskey { get; set; } = string.Empty;
    public string EnableTagFlagging { get; set; } = EnableTagFlaggingNo;
    public string AlwaysOriginal { get; set; } = AlwaysOriginalNo;
    public string MultiPage { get; set; } = MultiPageNo;
    public string MultiPageStyle { get; set; } = MultiPageStyleN;
    public string MultiPageThumb { get; set; } = MultiPageThumbShow;
    public string LofiResolution { get; set; } = LofiResolution980X;
    public string ContentWarning { get; set; } = ContentWarningNotShow;

    public void SetDirty() => _dirty = true;

    private void UpdateUConfig()
    {
        string hahClientIpPort = InetValidator.IsValidIpv4(HahClientIp) && InetValidator.IsValidInetPort(HahClientPort)
            ? $"{HahClientIp}:{HahClientPort}"
            : string.Empty;
        string hahClientPasskey = HahClientPasskey ?? string.Empty;

        _uconfig = string.Join('-',
            $"{KeyLoadFromHah}_{LoadFromHah}",
            $"{KeyImageSize}_{ImageSize}",
            $"{KeyScaleWidth}_{ScaleWidth}",
            $"{KeyScaleHeight}_{ScaleHeight}",
            $"{KeyGalleryTitle}_{GalleryTitle}",
            $"{KeyArchiverDownload}_{ArchiverDownload}",
            $"{KeyLayoutMode}_{LayoutMode}",
            $"{KeyPopular}_{Popular}",
            $"{KeyDefaultCategories}_{DefaultCategories}",
            $"{KeyFavoritesSort}_{FavoritesSort}",
            $"{KeyExcludedNamespaces}_{ExcludedNamespaces}",
            $"{KeyExcludedLanguages}_{ExcludedLanguages}",
            $"{KeyResultCount}_{ResultCount}",
            $"{KeyMouseOver}_{MouseOver}",
            $"{KeyPreviewSize}_{PreviewSize}",
            $"{KeyPreviewRow}_{PreviewRow}",
            $"{KeyCommentsSort}_{CommentSort}",
            $"{KeyCommentsVotes}_{CommentVotes}",
            $"{KeyTagsSort}_{TagSort}",
            $"{KeyShowGalleryIndex}_{ShowGalleryIndex}",
            $"{KeyHahClientIpPort}_{hahClientIpPort}",
            $"{KeyHahClientPasskey}_{hahClientPasskey}",
            $"{KeyEnableTagFlagging}_{EnableTagFlagging}",
            $"{KeyAlwaysOriginal}_{AlwaysOriginal}",
            $"{KeyMultiPage}_{MultiPage}",
            $"{KeyMultiPageStyle}_{MultiPageStyle}",
            $"{KeyMultiPageThumb}_{MultiPageThumb}");
    }

    public string UConfig()
    {
        // Always recompute if _dirty or null; C# setters keep public API direct, so recompute reliably or track dirtiness.
        if (_dirty || _uconfig == null)
        {
            UpdateUConfig();
            _dirty = false;
        }
        return _uconfig!;
    }

    public string BuildUConfig()
    {
        UpdateUConfig();
        _dirty = false;
        return _uconfig!;
    }

    public static IReadOnlyList<ExcludedLanguageGroup> ExcludedLanguageCatalog { get; } =
    [
        new("japanese", "日语 (Japanese)", JapaneseOriginal, JapaneseTranslated, JapaneseRewrite),
        new("english", "英语 (English)", EnglishOriginal, EnglishTranslated, EnglishRewrite),
        new("chinese", "中文 (Chinese)", ChineseOriginal, ChineseTranslated, ChineseRewrite),
        new("dutch", "荷兰语 (Dutch)", DutchOriginal, DutchTranslated, DutchRewrite),
        new("french", "法语 (French)", FrenchOriginal, FrenchTranslated, FrenchRewrite),
        new("german", "德语 (German)", GermanOriginal, GermanTranslated, GermanRewrite),
        new("hungarian", "匈牙利语 (Hungarian)", HungarianOriginal, HungarianTranslated, HungarianRewrite),
        new("italian", "意大利语 (Italian)", ItalianOriginal, ItalianTranslated, ItalianRewrite),
        new("korean", "韩语 (Korean)", KoreanOriginal, KoreanTranslated, KoreanRewrite),
        new("polish", "波兰语 (Polish)", PolishOriginal, PolishTranslated, PolishRewrite),
        new("portuguese", "葡萄牙语 (Portuguese)", PortugueseOriginal, PortugueseTranslated, PortugueseRewrite),
        new("russian", "俄语 (Russian)", RussianOriginal, RussianTranslated, RussianRewrite),
        new("spanish", "西班牙语 (Spanish)", SpanishOriginal, SpanishTranslated, SpanishRewrite),
        new("thai", "泰语 (Thai)", ThaiOriginal, ThaiTranslated, ThaiRewrite),
        new("vietnamese", "越南语 (Vietnamese)", VietnameseOriginal, VietnameseTranslated, VietnameseRewrite),
        new("na", "无语言 (N/A)", NaOriginal, NaTranslated, NaRewrite),
        new("other", "其他 (Other)", OtherOriginal, OtherTranslated, OtherRewrite)
    ];

    public static IReadOnlyList<ExcludedNamespaceOption> ExcludedNamespaceOptions { get; } =
    [
        new(NamespacesReclass, "reclass", "重新分类 (Reclass)"),
        new(NamespacesLanguage, "language", "语言 (Language)"),
        new(NamespacesParody, "parody", "原作 (Parody)"),
        new(NamespacesCharacter, "character", "角色 (Character)"),
        new(NamespacesGroup, "group", "社团 (Group)"),
        new(NamespacesArtist, "artist", "画师 (Artist)"),
        new(NamespacesMale, "male", "男性 (Male)"),
        new(NamespacesFemale, "female", "女性 (Female)")
    ];

    public HashSet<string> GetExcludedLanguageSet()
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(ExcludedLanguages))
        {
            return set;
        }

        foreach (string part in ExcludedLanguages.Split(['x', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            set.Add(part);
        }

        return set;
    }

    public void SetExcludedLanguageSet(IEnumerable<string> codes)
    {
        ArgumentNullException.ThrowIfNull(codes);

        var distinct = codes
            .Where(static c => !string.IsNullOrWhiteSpace(c))
            .Select(static c => c.Trim())
            .Distinct(StringComparer.Ordinal);
        ExcludedLanguages = string.Join('x', distinct);
        SetDirty();
    }

    /// <summary>
    /// Parses a dash-delimited <c>uconfig</c> cookie string into this instance.
    /// </summary>
    public void ApplyUConfig(string? uconfig)
    {
        if (string.IsNullOrWhiteSpace(uconfig))
        {
            return;
        }

        foreach (string entry in uconfig.Split('-', StringSplitOptions.RemoveEmptyEntries))
        {
            int sep = entry.IndexOf('_');
            if (sep <= 0)
            {
                continue;
            }

            string key = entry[..sep];
            string value = entry[(sep + 1)..];
            switch (key)
            {
                case KeyLoadFromHah: LoadFromHah = value; break;
                case KeyImageSize: ImageSize = value; break;
                case KeyScaleWidth when int.TryParse(value, out int w): ScaleWidth = w; break;
                case KeyScaleHeight when int.TryParse(value, out int h): ScaleHeight = h; break;
                case KeyGalleryTitle: GalleryTitle = value; break;
                case KeyArchiverDownload: ArchiverDownload = value; break;
                case KeyLayoutMode: LayoutMode = value; break;
                case KeyPopular: Popular = value; break;
                case KeyDefaultCategories when int.TryParse(value, out int cats): DefaultCategories = cats; break;
                case KeyFavoritesSort: FavoritesSort = value; break;
                case KeyExcludedNamespaces when int.TryParse(value, out int xns): ExcludedNamespaces = xns; break;
                case KeyExcludedLanguages: ExcludedLanguages = value; break;
                case KeyResultCount when int.TryParse(value, out int _): ResultCount = value; break;
                case KeyMouseOver: MouseOver = value; break;
                case KeyPreviewSize: PreviewSize = value; break;
                case KeyPreviewRow: PreviewRow = value; break;
                case KeyCommentsSort: CommentSort = value; break;
                case KeyCommentsVotes: CommentVotes = value; break;
                case KeyTagsSort: TagSort = value; break;
                case KeyShowGalleryIndex: ShowGalleryIndex = value; break;
                case KeyHahClientIpPort:
                    string decoded = value.Replace("%3A", ":", StringComparison.OrdinalIgnoreCase);
                    int colon = decoded.LastIndexOf(':');
                    if (colon > 0 && int.TryParse(decoded[(colon + 1)..], out int port))
                    {
                        HahClientIp = decoded[..colon];
                        HahClientPort = port;
                    }
                    else
                    {
                        HahClientIp = string.Empty;
                        HahClientPort = -1;
                    }
                    break;
                case KeyHahClientPasskey: HahClientPasskey = value; break;
                case KeyEnableTagFlagging: EnableTagFlagging = value; break;
                case KeyAlwaysOriginal: AlwaysOriginal = value; break;
                case KeyMultiPage: MultiPage = value; break;
                case KeyMultiPageStyle: MultiPageStyle = value; break;
                case KeyMultiPageThumb: MultiPageThumb = value; break;
            }
        }

        SetDirty();
    }
}
