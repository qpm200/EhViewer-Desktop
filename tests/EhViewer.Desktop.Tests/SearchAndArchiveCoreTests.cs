using System.IO.Compression;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Storage;
using EhViewer.Desktop.Downloads;
using Xunit;

namespace EhViewer.Desktop.Tests;

/// <summary>
/// Guards the translation of <c>widget/AdvanceSearchTable.java</c>: individual search flags, the
/// min-rating check box gating a spinner index of <c>rating - 2</c>, and the pages check box gating
/// both page bounds.
/// </summary>
public sealed class AdvanceSearchOptionsTests
{
    [Fact]
    public void DefaultState_KeepsUpstreamDefaultFlags()
    {
        var options = new AdvanceSearchOptions();

        Assert.Equal(ListUrlBuilder.DefaultAdvance, options.AdvanceSearch);
        Assert.Equal(-1, options.EffectiveMinRating);
        Assert.Equal(-1, options.EffectivePageFrom);
        Assert.Equal(-1, options.EffectivePageTo);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 4)]
    [InlineData(3, 5)]
    public void MinRatingIndex_MapsToRatingTwoThroughFive(int index, int expectedRating)
    {
        var options = new AdvanceSearchOptions { MinRatingEnabled = true, MinRatingIndex = index };

        Assert.Equal(expectedRating, options.EffectiveMinRating);
    }

    [Fact]
    public void MinRating_UntickedSendsSentinel()
    {
        var options = new AdvanceSearchOptions { MinRatingEnabled = false, MinRatingIndex = 3 };

        Assert.Equal(-1, options.EffectiveMinRating);
    }

    [Fact]
    public void PageRange_OnlyAppliesWhenEnabled()
    {
        var disabled = new AdvanceSearchOptions { PagesEnabled = false, PageFrom = 10, PageTo = 20 };
        Assert.Equal(-1, disabled.EffectivePageFrom);
        Assert.Equal(-1, disabled.EffectivePageTo);

        var enabled = new AdvanceSearchOptions { PagesEnabled = true, PageFrom = 10, PageTo = 20 };
        Assert.Equal(10, enabled.EffectivePageFrom);
        Assert.Equal(20, enabled.EffectivePageTo);
    }

    [Fact]
    public void SetFlag_TogglesIndividualBits()
    {
        var options = new AdvanceSearchOptions { AdvanceSearch = 0 };

        options.SetFlag(AdvanceSearchOptions.SDesc, true);
        Assert.True(options.IsFlagSet(AdvanceSearchOptions.SDesc));

        options.SetFlag(AdvanceSearchOptions.SDesc, false);
        Assert.False(options.IsFlagSet(AdvanceSearchOptions.SDesc));
    }

    [Fact]
    public void ApplyTo_WritesGatedValuesOntoBuilder()
    {
        var builder = new ListUrlBuilder { Keyword = "test" };
        var options = new AdvanceSearchOptions
        {
            AdvanceSearch = AdvanceSearchOptions.SName | AdvanceSearchOptions.STags,
            MinRatingEnabled = true,
            MinRatingIndex = 1,
            PagesEnabled = true,
            PageFrom = 5,
            PageTo = 9
        };

        options.ApplyTo(builder);

        Assert.Equal(AdvanceSearchOptions.SName | AdvanceSearchOptions.STags, builder.AdvanceSearch);
        Assert.Equal(3, builder.MinRating);
        Assert.Equal(5, builder.PageFrom);
        Assert.Equal(9, builder.PageTo);
        Assert.Equal("test", builder.Keyword);
    }

    [Fact]
    public void From_RestoresStateFromBuilder()
    {
        var builder = new ListUrlBuilder
        {
            AdvanceSearch = AdvanceSearchOptions.SDesc,
            MinRating = 4,
            PageFrom = 2,
            PageTo = 7
        };

        AdvanceSearchOptions options = AdvanceSearchOptions.From(builder);

        Assert.True(options.IsFlagSet(AdvanceSearchOptions.SDesc));
        Assert.True(options.MinRatingEnabled);
        Assert.Equal(2, options.MinRatingIndex);
        Assert.True(options.PagesEnabled);
        Assert.Equal(2, options.PageFrom);
        Assert.Equal(7, options.PageTo);
    }

    [Fact]
    public void From_UntickedBuilderValuesStayDisabled()
    {
        var builder = new ListUrlBuilder { MinRating = -1, PageFrom = -1, PageTo = -1 };

        AdvanceSearchOptions options = AdvanceSearchOptions.From(builder);

        Assert.False(options.MinRatingEnabled);
        Assert.False(options.PagesEnabled);
    }
}

/// <summary>Guards the inverted check-box semantics of upstream <c>widget/CategoryTable.java</c>.</summary>
public sealed class CategoryTableTests
{
    [Fact]
    public void GetCategory_AllUncheckedMeansEveryCategory()
    {
        var excluded = new bool[CategoryTable.Options.Count];

        Assert.Equal(CategoryTable.All, CategoryTable.GetCategory(excluded));
    }

    [Fact]
    public void GetCategory_TickedBoxIsExcluded()
    {
        var excluded = new bool[CategoryTable.Options.Count];
        excluded[0] = true; // 同人志 ticked => excluded

        int category = CategoryTable.GetCategory(excluded);

        Assert.Equal(0, category & EhConfig.Doujinshi);
        Assert.NotEqual(0, category & EhConfig.Manga);
    }

    [Fact]
    public void GetExcluded_IsInverseOfGetCategory()
    {
        int category = EhConfig.Manga | EhConfig.NonH;

        bool[] excluded = CategoryTable.GetExcluded(category);

        Assert.Equal(category, CategoryTable.GetCategory(excluded));
        Assert.True(excluded[0]);  // 同人志 not in mask => ticked/excluded
        Assert.False(excluded[1]); // 漫画 in mask => unticked/kept
    }

    [Fact]
    public void InvertExcept_MirrorsUpstreamLongPress()
    {
        var excluded = new bool[CategoryTable.Options.Count];
        excluded[3] = true;

        CategoryTable.InvertExcept(excluded, 3);

        Assert.True(excluded[3]);
        for (int i = 0; i < excluded.Length; i++)
        {
            if (i != 3)
            {
                Assert.False(excluded[i]);
            }
        }
    }

    [Fact]
    public void IsAllCategories_DetectsFullMask()
    {
        Assert.True(CategoryTable.IsAllCategories(CategoryTable.All));
        Assert.False(CategoryTable.IsAllCategories(EhConfig.Manga));
    }
}

/// <summary>
/// Guards the translation of <c>widget/SearchDatabase.java</c>, including the LIKE escaping the
/// Android original did not have to worry about because it escaped by hand.
/// </summary>
public sealed class SearchHistoryDatabaseTests : DesktopTestBase
{
    private string CreateTempDatabasePath()
        => Path.Combine(CreateTempDirectory("ehv-search"), "search.db");

    [Fact]
    public void AddQuery_ThenGetSuggestions_ReturnsNewestFirst()
    {
        string path = CreateTempDatabasePath();
        using var database = new SearchHistoryDatabase(path);
        database.AddQuery("alpha");
        database.AddQuery("alphabet");

        List<string> suggestions = database.GetSuggestions("alph", 10);

        Assert.Equal(["alphabet", "alpha"], suggestions);
    }

    [Fact]
    public void GetSuggestions_SkipsExactPrefixEcho()
    {
        string path = CreateTempDatabasePath();
        using var database = new SearchHistoryDatabase(path);
        database.AddQuery("alpha");

        Assert.Empty(database.GetSuggestions("alpha", 10));
    }

    [Fact]
    public void GetSuggestions_LimitIsHonoured()
    {
        string path = CreateTempDatabasePath();
        using var database = new SearchHistoryDatabase(path);
        for (int i = 0; i < 5; i++)
        {
            database.AddQuery($"tag{i}");
        }

        Assert.Equal(2, database.GetSuggestions("tag", 2).Count);
    }

    [Fact]
    public void AddQuery_DuplicateRefreshesInsteadOfGrowing()
    {
        string path = CreateTempDatabasePath();
        using var database = new SearchHistoryDatabase(path);
        database.AddQuery("dup");
        database.AddQuery("dup");

        Assert.Single(database.GetSuggestions("du", 10));
    }

    [Fact]
    public void TruncateHistory_KeepsNewestEntries()
    {
        string path = CreateTempDatabasePath();
        using var database = new SearchHistoryDatabase(path);
        for (int i = 0; i < 10; i++)
        {
            database.AddQuery($"q{i}");
            Thread.Sleep(2);
        }

        database.TruncateHistory(3);

        Assert.Equal(3, database.GetSuggestions(string.Empty, 100).Count);
    }

    [Fact]
    public void ClearQuery_EmptiesTheTable()
    {
        string path = CreateTempDatabasePath();
        using var database = new SearchHistoryDatabase(path);
        database.AddQuery("x");

        database.ClearQuery();

        Assert.Empty(database.GetSuggestions(string.Empty, 100));
    }

    [Fact]
    public void GetSuggestions_QuoteInPrefixDoesNotBreakTheStatement()
    {
        string path = CreateTempDatabasePath();
        using var database = new SearchHistoryDatabase(path);
        database.AddQuery("a'b");

        // A quote must be data, not SQL; the escaped LIKE keeps the literal prefix intact.
        Assert.Single(database.GetSuggestions("a'", 10));
        Assert.Empty(database.GetSuggestions("a''", 10));
    }

    [Fact]
    public void EscapeLike_EscapesWildcards()
    {
        Assert.Equal("a\\%b", SearchHistoryDatabase.EscapeLike("a%b"));
        Assert.Equal("a\\_b", SearchHistoryDatabase.EscapeLike("a_b"));
        Assert.Equal("a\\\\b", SearchHistoryDatabase.EscapeLike("a\\b"));
    }
}

/// <summary>
/// Guards the translation of <c>gallery/A7ZipArchive.java</c> onto SharpCompress, including the
/// path-traversal resistance the upstream JNI version needed before extracting.
/// </summary>
public sealed class ArchiveImporterTests : DesktopTestBase
{
    [Fact]
    public void Import_Zip_RenumbersImagesInNaturalOrder()
    {
        string root = CreateTempDirectory("ehv-archive");
        string archivePath = Path.Combine(root, "gallery.zip");
        CreateZip(archivePath, ("page10.png", [1]), ("page2.png", [2]), ("page1.png", [3]));

        string target = Path.Combine(root, "out");
        int count = ArchiveImporter.Import(archivePath, target);

        Assert.Equal(3, count);
        Assert.Equal(["00000000.png", "00000001.png", "00000002.png"],
            Directory.GetFiles(target).Select(Path.GetFileName).OrderBy(static n => n, StringComparer.Ordinal));

        // Natural order: page1, page2, page10.
        Assert.Equal([3], File.ReadAllBytes(Path.Combine(target, "00000000.png")));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(target, "00000001.png")));
        Assert.Equal([1], File.ReadAllBytes(Path.Combine(target, "00000002.png")));
    }

    [Fact]
    public void Import_EntryWithTraversalKey_StaysInsideTargetDirectory()
    {
        string root = CreateTempDirectory("ehv-archive");
        string archivePath = Path.Combine(root, "evil.zip");
        CreateZip(archivePath, ("../../escape.png", [9]), ("ok.png", [1]));

        string target = Path.Combine(root, "out", "gallery");
        int count = ArchiveImporter.Import(archivePath, target);

        Assert.Equal(2, count);

        // Destinations are generated names, so nothing can be written outside the target.
        Assert.All(Directory.GetFiles(target), file => Assert.Equal(target, Path.GetDirectoryName(file)));
        Assert.False(File.Exists(Path.Combine(root, "escape.png")));
        Assert.False(File.Exists(Path.Combine(root, "out", "escape.png")));
    }

    [Fact]
    public void Import_SkipsNonImageEntries()
    {
        string root = CreateTempDirectory("ehv-archive");
        string archivePath = Path.Combine(root, "mixed.zip");
        CreateZip(archivePath, ("readme.txt", [1]), ("001.jpg", [2]), ("info.xml", [3]));

        string target = Path.Combine(root, "out");
        int count = ArchiveImporter.Import(archivePath, target);

        Assert.Equal(1, count);
        Assert.Single(Directory.GetFiles(target));
        Assert.EndsWith("00000000.jpg", Directory.GetFiles(target)[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Import_ArchiveWithoutImages_Throws()
    {
        string root = CreateTempDirectory("ehv-archive");
        string archivePath = Path.Combine(root, "empty.zip");
        CreateZip(archivePath, ("readme.txt", [1]));

        Assert.Throws<InvalidOperationException>(
            () => ArchiveImporter.Import(archivePath, Path.Combine(root, "out")));
    }

    [Fact]
    public void Import_MissingArchive_ThrowsFileNotFound()
    {
        string root = CreateTempDirectory("ehv-archive");
        Assert.Throws<FileNotFoundException>(
            () => ArchiveImporter.Import(Path.Combine(root, "nope.zip"), Path.Combine(root, "out")));
    }

    [Theory]
    [InlineData("a.zip", true)]
    [InlineData("a.CBZ", true)]
    [InlineData("a.rar", true)]
    [InlineData("a.cbr", true)]
    [InlineData("a.7z", true)]
    [InlineData("a.cb7", true)]
    [InlineData("a.txt", false)]
    [InlineData("a.png", false)]
    public void IsSupportedArchive_MatchesUpstreamExtensionList(string name, bool expected)
    {
        Assert.Equal(expected, ArchiveImporter.IsSupportedArchive(name));
    }

    [Theory]
    [InlineData("001.jpg", true)]
    [InlineData("p.webp", true)]
    [InlineData("p.PNG", true)]
    [InlineData("notes.txt", false)]
    [InlineData("", false)]
    public void IsImageEntry_MatchesUpstreamExtensions(string name, bool expected)
    {
        Assert.Equal(expected, ArchiveImporter.IsImageEntry(name));
    }

    private static void CreateZip(string path, params (string Name, byte[] Content)[] entries)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        foreach ((string name, byte[] content) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name);
            using Stream entryStream = entry.Open();
            entryStream.Write(content, 0, content.Length);
        }
    }
}
