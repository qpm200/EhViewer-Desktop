using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace EhViewer.Core.Tests.StorageTests;

/// <summary>
/// Storage-layer contract tests.
/// <para>
/// Target contract: <see cref="EhDatabase"/> (SCHEMA_VERSION = 8), the entity records declared in
/// <c>src/EhViewer.Core/Storage/Entities.cs</c>, and <see cref="EhFilter"/>.
/// </para>
/// <para>
/// Every assertion below is written against the *actual* behaviour of the production source.
/// No production file is modified by this fixture.
/// </para>
/// </summary>
public sealed class StorageTests : TempDirectoryTestBase
{
    /// <summary>Index created by <c>CreateAllTables</c> / the v4 migration and used by <c>InBlackList</c>.</summary>
    private const string BlackListIndexName = "IDX_Black_List_BADGAYNAME";

    /// <summary>The ten user tables of SCHEMA_VERSION = 8, in SQLite <c>BINARY</c> (= ordinal) name order.</summary>
    private static readonly string[] TablesV8 =
    [
        "BOOKMARKS",
        "Black_List",
        "DOWNLOADS",
        "DOWNLOAD_DIRNAME",
        "DOWNLOAD_LABELS",
        "FILTER",
        "Gallery_Tags",
        "HISTORY",
        "LOCAL_FAVORITES",
        "QUICK_SEARCH"
    ];

    // ------------------------------------------------------------------------------------
    // 1. Fresh schema
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Schema_FreshDatabase_ReportsVersion8()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            Assert.Equal(8, EhDatabase.SchemaVersion);
            Assert.Equal(8, db.GetSchemaUserVersion());
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void Schema_FreshDatabase_CreatesAllTenTables()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using (var db = new EhDatabase(path))
            {
                Assert.Equal(8, db.GetSchemaUserVersion());
            }

            using SqliteConnection conn = OpenRaw(path, SqliteOpenMode.ReadOnly);
            string[] tables = ReadTableNames(conn)
                .Where(static n => !n.StartsWith("sqlite_", StringComparison.Ordinal))
                .OrderBy(static n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(TablesV8, tables);
            Assert.Equal(10, tables.Length);
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void Schema_FreshDatabase_BlackListHasBadGayNameIndex()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using (var db = new EhDatabase(path))
            {
                Assert.Equal(8, db.GetSchemaUserVersion());
            }

            using SqliteConnection conn = OpenRaw(path, SqliteOpenMode.ReadOnly);
            string[] indexes = ReadIndexNames(conn, "Black_List").ToArray();

            Assert.Equal(new[] { BlackListIndexName }, indexes);
            Assert.Equal(new[] { "BADGAYNAME" }, ReadIndexColumns(conn, BlackListIndexName).ToArray());

            // The exact statement issued by InBlackList() must be able to use that index.
            List<string> plan = ExplainQueryPlan(conn, "SELECT 1 FROM Black_List WHERE BADGAYNAME = $name LIMIT 1;");
            Assert.Contains(plan, static detail => detail.Contains(BlackListIndexName, StringComparison.Ordinal));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void Schema_FreshDatabase_EnablesWalAndPerformanceTimeIndexes()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using (var db = new EhDatabase(path))
            {
                Assert.Equal(8, db.GetSchemaUserVersion());
            }

            using SqliteConnection conn = OpenRaw(path, SqliteOpenMode.ReadOnly);
            using (SqliteCommand cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode;";
                Assert.Equal("wal", cmd.ExecuteScalar()?.ToString(), ignoreCase: true);
            }

            Assert.Contains("IDX_HISTORY_TIME", ReadIndexNames(conn, "HISTORY"));
            Assert.Contains("IDX_DOWNLOADS_TIME", ReadIndexNames(conn, "DOWNLOADS"));
            Assert.Contains("IDX_DOWNLOADS_STATE_TIME", ReadIndexNames(conn, "DOWNLOADS"));
            Assert.Contains("IDX_LOCAL_FAVORITES_TIME", ReadIndexNames(conn, "LOCAL_FAVORITES"));
            Assert.Contains("IDX_BOOKMARKS_TIME", ReadIndexNames(conn, "BOOKMARKS"));

            List<string> historyPlan = ExplainQueryPlan(conn, "SELECT GID FROM HISTORY ORDER BY TIME DESC;");
            Assert.Contains(historyPlan, static d => d.Contains("IDX_HISTORY_TIME", StringComparison.Ordinal));

            List<string> pendingPlan = ExplainQueryPlan(conn, "SELECT GID FROM DOWNLOADS WHERE STATE IN (1, 2) ORDER BY TIME ASC;");
            Assert.Contains(pendingPlan, static d => d.Contains("IDX_DOWNLOADS_STATE_TIME", StringComparison.Ordinal));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void EhDatabase_SingleRowDownloadHelpers_AndSearchHistoryWalIndex_WorkCorrectly()
    {
        string path = NewDatabasePath(out string dir);
        string searchPath = Path.Combine(dir, "search_database.db");
        try
        {
            using (var db = new EhDatabase(path))
            {
                db.PutDownloadInfo(new DownloadInfo
                {
                    Gid = 1234,
                    Token = "tok1234",
                    Title = "Point Lookup",
                    Category = 2,
                    State = DownloadInfo.StateDownload,
                    Time = 1_700_000_000_000L,
                    Label = "精品"
                });
                db.PutDownloadDirname(1234, "1234-Point Lookup");
                db.AddDownloadLabel("精品");

                DownloadInfo? collapsed = db.GetDownloadInfo(1234, collapseTransientStates: true);
                Assert.NotNull(collapsed);
                Assert.Equal(DownloadInfo.StateNone, collapsed!.State);
                Assert.Equal("精品", collapsed.Label);

                DownloadInfo? rawState = db.GetDownloadInfo(1234, collapseTransientStates: false);
                Assert.NotNull(rawState);
                Assert.Equal(DownloadInfo.StateDownload, rawState!.State);

                Assert.Null(db.GetDownloadInfo(9999));

                Dictionary<long, string> dirMap = db.GetAllDownloadDirnamesMap();
                Assert.Equal("1234-Point Lookup", dirMap[1234]);

                Assert.True(db.HasDownloadLabel("精品"));
                Assert.False(db.HasDownloadLabel("不存在"));

                Assert.Null(db.GetBookmark(8888));
                db.PutBookmark(new BookmarkInfo
                {
                    Gid = 8888,
                    Token = "bm8888",
                    Title = "Bookmarked Gallery",
                    Category = 4,
                    Page = 17,
                    Time = 1_700_000_005_000L
                });
                BookmarkInfo? bm = db.GetBookmark(8888);
                Assert.NotNull(bm);
                Assert.Equal(17, bm!.Page);
                Assert.Equal("Bookmarked Gallery", bm.Title);
                db.PutBookmark(new BookmarkInfo
                {
                    Gid = 8888,
                    Token = "bm8888",
                    Title = "Bookmarked Gallery",
                    Category = 4,
                    Page = 23,
                    Time = 1_700_000_006_000L
                });
                Assert.Equal(23, db.GetBookmark(8888)?.Page);
            }

            using (var searchDb = new SearchHistoryDatabase(searchPath))
            {
                searchDb.AddQuery("touhou");
                Assert.Contains("touhou", searchDb.GetSuggestions(null, 10));
            }

            using SqliteConnection searchConn = OpenRaw(searchPath, SqliteOpenMode.ReadOnly);
            using (SqliteCommand cmd = searchConn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA journal_mode;";
                Assert.Equal("wal", cmd.ExecuteScalar()?.ToString(), ignoreCase: true);
            }

            Assert.Contains("IDX_SUGGESTIONS_DATE", ReadIndexNames(searchConn, "SUGGESTIONS"));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void Schema_FreshDatabase_ColumnLayoutsMatchVersion8()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using (var db = new EhDatabase(path))
            {
                Assert.Equal(8, db.GetSchemaUserVersion());
            }

            using SqliteConnection conn = OpenRaw(path, SqliteOpenMode.ReadOnly);

            // Column order is part of the contract: every SELECT in EhDatabase reads by ordinal.
            Assert.Equal(
                new[]
                {
                    "GID", "TOKEN", "TITLE", "TITLE_JPN", "THUMB", "CATEGORY", "POSTED", "UPLOADER",
                    "RATING", "SIMPLE_LANGUAGE", "STATE", "LEGACY", "TIME", "LABEL", "ARCHIVE_URI"
                },
                ReadColumns(conn, "DOWNLOADS").ToArray());

            Assert.Equal(
                new[]
                {
                    "GID", "TOKEN", "TITLE", "TITLE_JPN", "THUMB", "CATEGORY", "POSTED", "UPLOADER",
                    "RATING", "SIMPLE_LANGUAGE", "MODE", "TIME"
                },
                ReadColumns(conn, "HISTORY").ToArray());

            Assert.Equal(
                new[]
                {
                    "GID", "TOKEN", "TITLE", "TITLE_JPN", "THUMB", "CATEGORY", "POSTED", "UPLOADER",
                    "RATING", "SIMPLE_LANGUAGE", "TIME"
                },
                ReadColumns(conn, "LOCAL_FAVORITES").ToArray());

            Assert.Equal(
                new[]
                {
                    "GID", "TOKEN", "TITLE", "TITLE_JPN", "THUMB", "CATEGORY", "POSTED", "UPLOADER",
                    "RATING", "SIMPLE_LANGUAGE", "PAGE", "TIME"
                },
                ReadColumns(conn, "BOOKMARKS").ToArray());

            Assert.Equal(
                new[] { "_id", "NAME", "MODE", "CATEGORY", "KEYWORD", "ADVANCE_SEARCH", "MIN_RATING", "PAGE_FROM", "PAGE_TO", "TIME" },
                ReadColumns(conn, "QUICK_SEARCH").ToArray());

            Assert.Equal(new[] { "_id", "MODE", "TEXT", "ENABLE" }, ReadColumns(conn, "FILTER").ToArray());
            Assert.Equal(new[] { "_id", "LABEL", "TIME" }, ReadColumns(conn, "DOWNLOAD_LABELS").ToArray());
            Assert.Equal(new[] { "GID", "DIRNAME" }, ReadColumns(conn, "DOWNLOAD_DIRNAME").ToArray());
            Assert.Equal(
                new[] { "_id", "BADGAYNAME", "REASON", "ANGRYWITH", "ADD_TIME", "MODE" },
                ReadColumns(conn, "Black_List").ToArray());

            string[] tags = ReadColumns(conn, "Gallery_Tags").ToArray();
            Assert.Equal(17, tags.Length);
            Assert.Equal("GID", tags[0]);
            Assert.Equal("LOCATION", tags[^1]);
            Assert.Contains("GROUP", tags);
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    // ------------------------------------------------------------------------------------
    // 2. v2 -> v8 migration
    // ------------------------------------------------------------------------------------

    [Fact]
    public void Migration_V2ToV8_UpgradesVersionAndCompletesSchema()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            CreateVersion2Database(path);

            using (SqliteConnection rawBefore = OpenRaw(path, SqliteOpenMode.ReadOnly))
            {
                Assert.Equal(2, ReadUserVersion(rawBefore));
                Assert.DoesNotContain("ENABLE", ReadColumns(rawBefore, "FILTER"));
                Assert.DoesNotContain("ARCHIVE_URI", ReadColumns(rawBefore, "DOWNLOADS"));
                Assert.DoesNotContain("PAGE_FROM", ReadColumns(rawBefore, "QUICK_SEARCH"));
                Assert.DoesNotContain("MODE", ReadColumns(rawBefore, "Black_List"));
                Assert.Contains("legacy-black", ReadAllBlackListNames(rawBefore));
            }

            using (var db = new EhDatabase(path))
            {
                Assert.Equal(8, db.GetSchemaUserVersion());
            }

            using SqliteConnection conn = OpenRaw(path, SqliteOpenMode.ReadOnly);
            Assert.Equal(8, ReadUserVersion(conn));

            string[] tables = ReadTableNames(conn)
                .Where(static n => !n.StartsWith("sqlite_", StringComparison.Ordinal))
                .OrderBy(static n => n, StringComparer.Ordinal)
                .ToArray();
            Assert.Equal(TablesV8, tables);

            // Columns added by the <=2, <=3, <=4, <=6 and <=7 steps.
            Assert.Contains("ENABLE", ReadColumns(conn, "FILTER"));
            Assert.Contains("ARCHIVE_URI", ReadColumns(conn, "DOWNLOADS"));
            Assert.Contains("PAGE_FROM", ReadColumns(conn, "QUICK_SEARCH"));
            Assert.Contains("PAGE_TO", ReadColumns(conn, "QUICK_SEARCH"));
            Assert.Contains("MODE", ReadColumns(conn, "Black_List"));
            Assert.Contains("LOCATION", ReadColumns(conn, "Gallery_Tags"));
            Assert.Equal(new[] { BlackListIndexName }, ReadIndexNames(conn, "Black_List").ToArray());
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void Migration_V2ToV8_PreservesRowsAndBackfillsNewColumns()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            CreateVersion2Database(path);

            using var db = new EhDatabase(path);
            Assert.Equal(8, db.GetSchemaUserVersion());

            // DOWNLOADS row survives and gains a NULL ARCHIVE_URI.
            DownloadInfo download = Assert.Single(db.GetAllDownloadInfo());
            Assert.Equal(1001, download.Gid);
            Assert.Equal("Legacy Title", download.Title);
            Assert.Equal("Legacy Uploader", download.Uploader);
            Assert.Equal(3, download.State);
            Assert.Equal(1_700_000_000_000, download.Time);
            Assert.Equal("legacy-label-ref", download.Label);
            Assert.Null(download.ArchiveUri);

            // FILTER rebuilt by the <=2 step: ENABLE is backfilled with 1.
            FilterRule filter = Assert.Single(db.GetAllFilter());
            Assert.Equal(EhFilter.ModeTitle, filter.Mode);
            Assert.Equal("legacy-filter", filter.Text);
            Assert.True(filter.Enable);
            Assert.True(filter.IsEnabled);

            // QUICK_SEARCH rebuilt by the <=3 step: PAGE_FROM/PAGE_TO are backfilled with -1.
            QuickSearch quickSearch = Assert.Single(db.GetAllQuickSearch());
            Assert.Equal("legacy-qs", quickSearch.Name);
            Assert.Equal("legacy-keyword", quickSearch.Keyword);
            Assert.Equal(-1, quickSearch.PageFrom);
            Assert.Equal(-1, quickSearch.PageTo);

            HistoryInfo history = Assert.Single(db.GetAllHistoryInfo());
            Assert.Equal(1001, history.Gid);
            Assert.Equal(1_700_000_000_200, history.Time);
            Assert.Equal(0, history.Mode);

            BookmarkInfo bookmark = Assert.Single(db.GetAllBookmarks());
            Assert.Equal(1002, bookmark.Gid);
            Assert.Equal(5, bookmark.Page);
            Assert.Equal(1_700_000_000_300, bookmark.Time);

            LocalFavoriteInfo favorite = Assert.IsType<LocalFavoriteInfo>(Assert.Single(db.GetAllLocalFavorites()));
            Assert.Equal(1003, favorite.Gid);
            Assert.Equal(1_700_000_000_400, favorite.Time);

            Assert.Equal("legacy-label", Assert.Single(db.GetAllDownloadLabels()).Label);
            Assert.Equal("legacy-dir", db.GetDownloadDirname(1004));
            Assert.True(db.ContainLocalFavorites(1003));
            Assert.False(db.InGalleryTags(1005));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void Migration_V2ToV8_DropsBlackListAndGalleryTagsRows()
    {
        // Documents real migration behaviour: the <=4 and <=5 steps DROP TABLE and recreate it,
        // so pre-existing Black_List / Gallery_Tags rows are discarded by design.
        string path = NewDatabasePath(out string dir);
        try
        {
            CreateVersion2Database(path);

            using var db = new EhDatabase(path);
            Assert.Equal(8, db.GetSchemaUserVersion());

            Assert.Empty(db.GetAllBlackList());
            Assert.False(db.InBlackList("legacy-black"));
            Assert.Empty(db.GetAllGalleryTags());
            Assert.False(db.InGalleryTags(1005));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    // ------------------------------------------------------------------------------------
    // 3. Entity CRUD
    // ------------------------------------------------------------------------------------

    [Fact]
    public void DownloadInfo_PutGetRemove_RoundTripsAllGalleryColumns()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            DownloadInfo expected = NewDownload(gid: 1234, time: 5_000);
            db.PutDownloadInfo(expected);

            DownloadInfo actual = Assert.Single(db.GetAllDownloadInfo());
            AssertGalleryColumnsRoundTrip(expected, actual);
            Assert.Equal(DownloadInfo.StateFinish, actual.State);
            Assert.Equal(expected.Legacy, actual.Legacy);
            Assert.Equal(expected.Time, actual.Time);
            Assert.Equal(expected.Label, actual.Label);
            Assert.Equal(expected.ArchiveUri, actual.ArchiveUri);

            db.RemoveDownloadInfo(1234);

            Assert.Empty(db.GetAllDownloadInfo());
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void DownloadInfo_PutSameGidTwice_UpsertsInsteadOfDuplicating()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            db.PutDownloadInfo(NewDownload(gid: 42, time: 1_000));

            var updated = new DownloadInfo(Fill(new GalleryInfo(), 42))
            {
                State = DownloadInfo.StateFailed,
                Legacy = 1,
                Time = 2_000,
                Label = "updated-label",
                ArchiveUri = "archive://updated"
            };
            db.PutDownloadInfo(updated);

            DownloadInfo stored = Assert.Single(db.GetAllDownloadInfo());
            Assert.Equal(42, stored.Gid);
            Assert.Equal("Title 42", stored.Title);
            Assert.Equal(DownloadInfo.StateFailed, stored.State);
            Assert.Equal(1, stored.Legacy);
            Assert.Equal(2_000, stored.Time);
            Assert.Equal("updated-label", stored.Label);
            Assert.Equal("archive://updated", stored.ArchiveUri);
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void DownloadInfo_GetAll_OrdersByTimeDescendingAndNormalizesTransientStates()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            db.PutDownloadInfo(NewDownload(gid: 1, time: 3_000));
            db.PutDownloadInfo(NewDownload(gid: 2, time: 1_000));
            db.PutDownloadInfo(NewDownload(gid: 3, time: 2_000));

            Assert.Equal(new long[] { 1, 3, 2 }, db.GetAllDownloadInfo().Select(static x => x.Gid));

            // GetAllDownloadInfo collapses the transient wait/download states to StateNone.
            DownloadInfo waiting = NewDownload(gid: 9, time: 9_000);
            waiting.State = DownloadInfo.StateWait;
            db.PutDownloadInfo(waiting);
            DownloadInfo downloading = NewDownload(gid: 10, time: 9_500);
            downloading.State = DownloadInfo.StateDownload;
            db.PutDownloadInfo(downloading);

            List<DownloadInfo> all = db.GetAllDownloadInfo();
            Assert.Equal(DownloadInfo.StateNone, all.Single(static x => x.Gid == 9).State);
            Assert.Equal(DownloadInfo.StateNone, all.Single(static x => x.Gid == 10).State);

            // GetPendingDownloadInfo preserves wait/download states oldest-first for startup resume.
            List<DownloadInfo> pending = db.GetPendingDownloadInfo();
            Assert.Equal(new long[] { 9, 10 }, pending.Select(static x => x.Gid));
            Assert.Equal(DownloadInfo.StateWait, pending[0].State);
            Assert.Equal(DownloadInfo.StateDownload, pending[1].State);
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void DownloadLabel_AddUpdateRemove_RoundTrips()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            DownloadLabel first = db.AddDownloadLabel(new DownloadLabel { Label = "alpha", Time = 1_000 });
            DownloadLabel second = db.AddDownloadLabel(new DownloadLabel { Label = "beta", Time = 2_000 });
            DownloadLabel third = db.AddDownloadLabel("gamma");

            Assert.NotNull(first.Id);
            Assert.NotNull(second.Id);
            Assert.NotNull(third.Id);
            Assert.NotEqual(first.Id, second.Id);
            Assert.NotEqual(second.Id, third.Id);
            // The string overload stamps Time with "now", which is always newer than the seeded values.
            Assert.True(third.Time > 2_000);

            List<DownloadLabel> all = db.GetAllDownloadLabels();
            Assert.Equal(3, all.Count);
            // GetAllDownloadLabels orders by TIME ASC.
            Assert.Equal(new[] { "alpha", "beta", "gamma" }, all.Select(static x => x.Label));
            Assert.Equal(1_000, all[0].Time);
            Assert.Equal(2_000, all[1].Time);

            first.Label = "alpha-renamed";
            first.Time = 3_000;
            db.UpdateDownloadLabel(first);

            List<DownloadLabel> updated = db.GetAllDownloadLabels();
            Assert.Equal(new[] { "beta", "alpha-renamed", "gamma" }, updated.Select(static x => x.Label));

            db.RemoveDownloadLabel(second);
            Assert.Equal(new[] { "alpha-renamed", "gamma" }, db.GetAllDownloadLabels().Select(static x => x.Label));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void DownloadDirname_PutGetUpdateClear_RoundTrips()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            Assert.Null(db.GetDownloadDirname(7));

            db.PutDownloadDirname(7, "dir-seven");
            Assert.Equal("dir-seven", db.GetDownloadDirname(7));

            db.PutDownloadDirname(7, "dir-seven-v2");
            Assert.Equal("dir-seven-v2", db.GetDownloadDirname(7));
            Assert.Single(db.GetAllDownloadDirname());

            db.PutDownloadDirname(8, "dir-eight");
            db.UpdateDownloadDirname(removeGid: 7, newGid: 800, dirname: "dir-moved");
            Assert.Equal("dir-moved", db.GetDownloadDirname(800));
            Assert.Null(db.GetDownloadDirname(7));
            Assert.Equal(2, db.GetAllDownloadDirname().Count);

            db.RemoveDownloadDirname(800);
            Assert.Null(db.GetDownloadDirname(800));

            db.ClearDownloadDirname();
            Assert.Empty(db.GetAllDownloadDirname());
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void LocalFavorite_PutGetSearchRemove_RoundTrips()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            LocalFavoriteInfo expected = NewLocalFavorite(gid: 555, time: 4_000);
            db.PutLocalFavorite(expected);

            GalleryInfo? loaded = db.SearchLocalFavorites(555);
            Assert.NotNull(loaded);
            AssertGalleryColumnsRoundTrip(expected, loaded!);
            Assert.Equal(4_000, Assert.IsType<LocalFavoriteInfo>(loaded).Time);
            Assert.True(db.ContainLocalFavorites(555));
            Assert.False(db.ContainLocalFavorites(556));

            Assert.Null(db.SearchLocalFavorites(556));
            Assert.Single(db.SearchLocalFavorites("Title 555"));
            Assert.Empty(db.SearchLocalFavorites("Title 556"));

            // 100% and _ are escaped, so they are matched literally rather than as LIKE wildcards.
            Assert.Empty(db.SearchLocalFavorites("%"));

            db.PutLocalFavorite(NewLocalFavorite(gid: 556, time: 5_000));
            Assert.Equal(new long[] { 556, 555 }, db.GetAllLocalFavorites().Select(static x => x.Gid));

            db.RemoveLocalFavorites(555);
            Assert.False(db.ContainLocalFavorites(555));

            db.RemoveLocalFavorites(new long[] { 556 });
            Assert.Empty(db.GetAllLocalFavorites());
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void LocalFavorite_PutExistingGid_IsIgnored()
    {
        // PutLocalFavorite returns early when the GID already exists: re-putting does not update.
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            db.PutLocalFavorite(NewLocalFavorite(gid: 900, time: 1_000));

            var replacement = new LocalFavoriteInfo(Fill(new GalleryInfo(), 900))
            {
                Title = "replacement title",
                Time = 9_000
            };
            db.PutLocalFavorite(replacement);

            LocalFavoriteInfo stored = Assert.IsType<LocalFavoriteInfo>(Assert.Single(db.GetAllLocalFavorites()));
            Assert.Equal("Title 900", stored.Title);
            Assert.Equal(1_000, stored.Time);
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void GetLocalFavoritesCount_TracksInsertDeduplicationAndRemoval()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);
            Assert.Equal(0, db.GetLocalFavoritesCount());

            db.PutLocalFavorite(NewLocalFavorite(gid: 910, time: 1_000));
            db.PutLocalFavorite(NewLocalFavorite(gid: 911, time: 2_000));
            Assert.Equal(2, db.GetLocalFavoritesCount());

            // Re-inserting an existing GID is deduplicated.
            db.PutLocalFavorite(NewLocalFavorite(gid: 910, time: 9_000));
            Assert.Equal(2, db.GetLocalFavoritesCount());

            db.RemoveLocalFavorites(910);
            Assert.Equal(1, db.GetLocalFavoritesCount());

            db.RemoveLocalFavorites(new long[] { 911 });
            Assert.Equal(0, db.GetLocalFavoritesCount());
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void Bookmark_PutGetRemove_RoundTrips()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            BookmarkInfo expected = NewBookmark(gid: 71, page: 12, time: 2_500);
            db.PutBookmark(expected);

            BookmarkInfo stored = Assert.Single(db.GetAllBookmarks());
            AssertGalleryColumnsRoundTrip(expected, stored);
            Assert.Equal(12, stored.Page);
            Assert.Equal(2_500, stored.Time);

            db.PutBookmark(NewBookmark(gid: 72, page: 1, time: 3_500));
            Assert.Equal(new long[] { 72, 71 }, db.GetAllBookmarks().Select(static x => x.Gid));

            // Bookmarks are upserted on GID.
            db.PutBookmark(NewBookmark(gid: 71, page: 99, time: 4_500));
            List<BookmarkInfo> all = db.GetAllBookmarks();
            Assert.Equal(2, all.Count);
            Assert.Equal(99, all.Single(static x => x.Gid == 71).Page);
            Assert.Equal(4_500, all.Single(static x => x.Gid == 71).Time);

            db.RemoveBookmark(71);
            Assert.Equal(new long[] { 72 }, db.GetAllBookmarks().Select(static x => x.Gid));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void BlackList_InsertUpdateDelete_RoundTrips()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            var entry = new BlackListEntry
            {
                BadGayName = "bad-uploader",
                Reason = "spam",
                AngryWith = "artist-x",
                AddTime = "2024-02-03",
                Mode = 2
            };
            db.InsertBlackList(entry);
            Assert.NotNull(entry.Id);
            Assert.True(db.InBlackList("bad-uploader"));
            Assert.False(db.InBlackList("bad-uploader "));
            Assert.False(db.InBlackList("BAD-UPLOADER"));
            Assert.False(db.InBlackList(null));

            BlackListEntry stored = Assert.Single(db.GetAllBlackList());
            Assert.True(entry.Equals(stored));
            Assert.Equal(entry.Id, stored.Id);

            entry.Reason = "spam-v2";
            entry.Mode = 3;
            db.UpdateBlackList(entry);
            BlackListEntry updated = Assert.Single(db.GetAllBlackList());
            Assert.Equal("spam-v2", updated.Reason);
            Assert.Equal(3, updated.Mode);

            db.DeleteBlackList(entry);
            Assert.Empty(db.GetAllBlackList());
            Assert.False(db.InBlackList("bad-uploader"));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void GalleryTags_InsertQueryUpdateDelete_RoundTrips()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            var tags = new GalleryTagsRecord
            {
                Gid = 8080,
                Rows = "rows",
                Artist = "artist",
                Cosplayer = "cosplayer",
                Character = "character",
                Female = "female",
                Group = "group",
                Language = "language",
                Male = "male",
                Misc = "misc",
                Mixed = "mixed",
                Other = "other",
                Parody = "parody",
                Reclass = "reclass",
                Location = "location"
            };
            db.InsertGalleryTags(tags);

            Assert.True(db.InGalleryTags(8080));
            Assert.False(db.InGalleryTags(8081));
            Assert.NotNull(tags.CreateTime);
            Assert.Equal(tags.CreateTime, tags.UpdateTime);

            GalleryTagsRecord stored = db.QueryGalleryTags(8080)!;
            Assert.Equal(tags.Gid, stored.Gid);
            Assert.Equal(tags.Rows, stored.Rows);
            Assert.Equal(tags.Artist, stored.Artist);
            Assert.Equal(tags.Cosplayer, stored.Cosplayer);
            Assert.Equal(tags.Character, stored.Character);
            Assert.Equal(tags.Female, stored.Female);
            Assert.Equal(tags.Group, stored.Group);
            Assert.Equal(tags.Language, stored.Language);
            Assert.Equal(tags.Male, stored.Male);
            Assert.Equal(tags.Misc, stored.Misc);
            Assert.Equal(tags.Mixed, stored.Mixed);
            Assert.Equal(tags.Other, stored.Other);
            Assert.Equal(tags.Parody, stored.Parody);
            Assert.Equal(tags.Reclass, stored.Reclass);
            Assert.Equal(tags.Location, stored.Location);
            Assert.Null(db.QueryGalleryTags(8081));

            tags.Artist = "artist-v2";
            tags.Location = "location-v2";
            db.UpdateGalleryTags(tags);
            GalleryTagsRecord updated = db.QueryGalleryTags(8080)!;
            Assert.Equal("artist-v2", updated.Artist);
            Assert.Equal("location-v2", updated.Location);

            db.InsertGalleryTags(new GalleryTagsRecord { Gid = 8081, Artist = "second" });
            Assert.Equal(new long[] { 8080, 8081 }, db.GetAllGalleryTags().Select(static x => x.Gid));

            db.DeleteGalleryTags(tags);
            Assert.False(db.InGalleryTags(8080));
            Assert.Single(db.GetAllGalleryTags());
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void QuickSearch_InsertUpdateDelete_RoundTrips()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            var search = new QuickSearch
            {
                Name = "my-search",
                Mode = 1,
                Category = 2,
                Keyword = "keyword",
                AdvanceSearch = 3,
                MinRating = 4,
                PageFrom = 5,
                PageTo = 6,
                Time = 1_500
            };
            db.InsertQuickSearch(search);
            Assert.NotNull(search.Id);

            QuickSearch stored = Assert.Single(db.GetAllQuickSearch());
            Assert.Equal(search.Id, stored.Id);
            Assert.Equal("my-search", stored.Name);
            Assert.Equal(1, stored.Mode);
            Assert.Equal(2, stored.Category);
            Assert.Equal("keyword", stored.Keyword);
            Assert.Equal(3, stored.AdvanceSearch);
            Assert.Equal(4, stored.MinRating);
            Assert.Equal(5, stored.PageFrom);
            Assert.Equal(6, stored.PageTo);
            Assert.Equal(1_500, stored.Time);

            search.Name = "my-search-v2";
            search.Keyword = "keyword-v2";
            search.PageTo = 60;
            search.Time = 2_500;
            db.UpdateQuickSearch(search);

            QuickSearch updated = Assert.Single(db.GetAllQuickSearch());
            Assert.Equal("my-search-v2", updated.Name);
            Assert.Equal("keyword-v2", updated.Keyword);
            Assert.Equal(60, updated.PageTo);
            Assert.Equal(2_500, updated.Time);

            db.DeleteQuickSearch(search);
            Assert.Empty(db.GetAllQuickSearch());
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void History_PutGetDeleteClear_RoundTrips()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            var info = new HistoryInfo(Fill(new GalleryInfo(), 31337))
            {
                Mode = 3,
                Time = 777
            };
            db.PutHistoryInfo(info);

            HistoryInfo stored = Assert.Single(db.GetAllHistoryInfo());
            AssertGalleryColumnsRoundTrip(info, stored);
            Assert.Equal(3, stored.Mode);
            // PutHistoryInfo(GalleryInfo) always stamps the row with "now", ignoring HistoryInfo.Time.
            Assert.NotEqual(777, stored.Time);
            Assert.True(stored.Time > 0);

            db.PutHistoryInfo(info);
            Assert.Single(db.GetAllHistoryInfo());

            db.PutHistoryInfo(NewGallery(31338));
            Assert.Equal(2, db.GetAllHistoryInfo().Count);

            db.DeleteHistoryInfo(31337);
            Assert.Equal(new long[] { 31338 }, db.GetAllHistoryInfo().Select(static x => x.Gid));

            db.ClearHistoryInfo();
            Assert.Empty(db.GetAllHistoryInfo());
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    // ------------------------------------------------------------------------------------
    // 4. Move / reorder (timestamp swap)
    // ------------------------------------------------------------------------------------

    [Fact]
    public void MoveDownloadInfo_SwapsTimestampsToReorderByTimeDescending()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            db.PutDownloadInfo(NewDownload(gid: 1, time: 3_000));
            db.PutDownloadInfo(NewDownload(gid: 2, time: 2_000));
            db.PutDownloadInfo(NewDownload(gid: 3, time: 1_000));

            Assert.Equal(new long[] { 1, 2, 3 }, db.GetAllDownloadInfo().Select(static x => x.Gid));

            // Move the first visible row to the last visible position.
            List<DownloadInfo> list = db.GetAllDownloadInfo();
            db.MoveDownloadInfo(list, fromPosition: 0, toPosition: 2);
            Assert.Equal(new long[] { 2, 3, 1 }, db.GetAllDownloadInfo().Select(static x => x.Gid));

            // Move it back to the front.
            list = db.GetAllDownloadInfo();
            db.MoveDownloadInfo(list, fromPosition: 2, toPosition: 0);
            Assert.Equal(new long[] { 1, 2, 3 }, db.GetAllDownloadInfo().Select(static x => x.Gid));

            // A single-step swap is the same operation with distance 1.
            list = db.GetAllDownloadInfo();
            db.MoveDownloadInfo(list, fromPosition: 1, toPosition: 2);
            Assert.Equal(new long[] { 1, 3, 2 }, db.GetAllDownloadInfo().Select(static x => x.Gid));

            // No-op moves must leave the order untouched.
            list = db.GetAllDownloadInfo();
            db.MoveDownloadInfo(list, fromPosition: 1, toPosition: 1);
            db.MoveDownloadInfo(list, fromPosition: -1, toPosition: 0);
            db.MoveDownloadInfo(list, fromPosition: 0, toPosition: 99);
            Assert.Equal(new long[] { 1, 3, 2 }, db.GetAllDownloadInfo().Select(static x => x.Gid));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void MoveDownloadLabel_ReordersByTimeAscending()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            db.AddDownloadLabel(new DownloadLabel { Label = "L0", Time = 100 });
            db.AddDownloadLabel(new DownloadLabel { Label = "L1", Time = 200 });
            db.AddDownloadLabel(new DownloadLabel { Label = "L2", Time = 300 });

            // GetAllDownloadLabels orders by TIME ASC, and MoveDownloadLabel positions follow that order.
            Assert.Equal(new[] { "L0", "L1", "L2" }, db.GetAllDownloadLabels().Select(static x => x.Label));

            db.MoveDownloadLabel(fromPosition: 0, toPosition: 2);
            Assert.Equal(new[] { "L1", "L2", "L0" }, db.GetAllDownloadLabels().Select(static x => x.Label));

            db.MoveDownloadLabel(fromPosition: 2, toPosition: 0);
            Assert.Equal(new[] { "L0", "L1", "L2" }, db.GetAllDownloadLabels().Select(static x => x.Label));

            db.MoveDownloadLabel(fromPosition: 1, toPosition: 0);
            Assert.Equal(new[] { "L1", "L0", "L2" }, db.GetAllDownloadLabels().Select(static x => x.Label));

            db.MoveDownloadLabel(fromPosition: 1, toPosition: 1);
            Assert.Equal(new[] { "L1", "L0", "L2" }, db.GetAllDownloadLabels().Select(static x => x.Label));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void MoveQuickSearch_ReordersByTimeAscending()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            db.InsertQuickSearch(new QuickSearch { Name = "Q0", Keyword = "k0", Time = 100 });
            db.InsertQuickSearch(new QuickSearch { Name = "Q1", Keyword = "k1", Time = 200 });
            db.InsertQuickSearch(new QuickSearch { Name = "Q2", Keyword = "k2", Time = 300 });

            Assert.Equal(new[] { "Q0", "Q1", "Q2" }, db.GetAllQuickSearch().Select(static x => x.Name));

            db.MoveQuickSearch(fromPosition: 2, toPosition: 0);
            Assert.Equal(new[] { "Q2", "Q0", "Q1" }, db.GetAllQuickSearch().Select(static x => x.Name));

            db.MoveQuickSearch(fromPosition: 0, toPosition: 2);
            Assert.Equal(new[] { "Q0", "Q1", "Q2" }, db.GetAllQuickSearch().Select(static x => x.Name));

            db.MoveQuickSearch(fromPosition: 0, toPosition: 1);
            Assert.Equal(new[] { "Q1", "Q0", "Q2" }, db.GetAllQuickSearch().Select(static x => x.Name));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    // ------------------------------------------------------------------------------------
    // 5. MaxHistoryCount trimming
    // ------------------------------------------------------------------------------------

    [Fact]
    public void MaxHistoryCount_BulkInsert_KeepsOnlyNewestEntries()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path, maxHistoryCount: 3);

            db.PutHistoryInfo(new List<HistoryInfo>
            {
                NewHistory(gid: 201, time: 1_000),
                NewHistory(gid: 202, time: 2_000),
                NewHistory(gid: 203, time: 3_000),
                NewHistory(gid: 204, time: 4_000),
                NewHistory(gid: 205, time: 5_000)
            });

            List<HistoryInfo> all = db.GetAllHistoryInfo();
            Assert.Equal(3, all.Count);
            Assert.Equal(new long[] { 205, 204, 203 }, all.Select(static x => x.Gid));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void MaxHistoryCount_SingleInsert_TrimsOnEachWrite()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path, maxHistoryCount: 3);

            for (long gid = 301; gid <= 305; gid++)
            {
                db.PutHistoryInfo(NewGallery(gid));
                Thread.Sleep(15);
            }

            List<HistoryInfo> all = db.GetAllHistoryInfo();
            Assert.Equal(3, all.Count);
            Assert.Equal(new long[] { 305, 304, 303 }, all.Select(static x => x.Gid));

            // Re-visiting an existing entry only refreshes TIME; it neither duplicates nor grows the table.
            Thread.Sleep(15);
            db.PutHistoryInfo(NewGallery(303));

            List<HistoryInfo> refreshed = db.GetAllHistoryInfo();
            Assert.Equal(3, refreshed.Count);
            Assert.Equal(303, refreshed[0].Gid);
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    // ------------------------------------------------------------------------------------
    // 6. Parameterized blacklist
    // ------------------------------------------------------------------------------------

    [Fact]
    public void InBlackList_SqlInjectionPayload_MatchesExactlyAndLeavesTablesIntact()
    {
        const string payload = "Robert'); DROP TABLE DOWNLOADS;--";

        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            db.InsertBlackList(new BlackListEntry
            {
                BadGayName = payload,
                Reason = "injection",
                AddTime = "2024-01-01",
                Mode = 0
            });

            // Exact parameterized match, no prefix/partial match, no SQL execution.
            Assert.True(db.InBlackList(payload));
            Assert.False(db.InBlackList("Robert"));
            Assert.False(db.InBlackList("Robert'); DROP TABLE DOWNLOADS;"));
            Assert.False(db.InBlackList("' OR '1'='1"));

            Assert.Equal(payload, Assert.Single(db.GetAllBlackList()).BadGayName);

            // DOWNLOADS is still there and still writable/readable.
            Assert.Empty(db.GetAllDownloadInfo());
            db.PutDownloadInfo(NewDownload(gid: 1, time: 1_000));
            Assert.Single(db.GetAllDownloadInfo());

            using (SqliteConnection conn = OpenRaw(path, SqliteOpenMode.ReadOnly))
            {
                Assert.Contains("DOWNLOADS", ReadTableNames(conn));
                Assert.Equal(8, ReadUserVersion(conn));
            }
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    // ------------------------------------------------------------------------------------
    // 7. Export / Import
    // ------------------------------------------------------------------------------------

    [Fact]
    public void ExportDatabase_WritesAnIndependentConsistentCopy()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            string exportPath = Path.Combine(dir, "export", "snapshot.db");

            using (var db = new EhDatabase(path))
            {
                db.PutDownloadInfo(NewDownload(gid: 1, time: 1_000));
                db.PutHistoryInfo(NewHistory(gid: 10, time: 2_000));
                db.ExportDatabase(exportPath);
            }

            Assert.True(File.Exists(exportPath));

            using var exported = new EhDatabase(exportPath);
            Assert.Equal(8, exported.GetSchemaUserVersion());
            Assert.Equal(new long[] { 1 }, exported.GetAllDownloadInfo().Select(static x => x.Gid));
            Assert.Equal(new long[] { 10 }, exported.GetAllHistoryInfo().Select(static x => x.Gid));

            // The export is a snapshot: mutating it does not touch the source database.
            exported.RemoveDownloadInfo(1);

            using var source = new EhDatabase(path);
            Assert.Single(source.GetAllDownloadInfo());
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void ImportDatabase_MergesSourceRowsIntoDestination()
    {
        string pathA = NewDatabasePath(out string dirA);
        string pathB = NewDatabasePath(out string dirB);
        string exportPath = Path.Combine(dirA, "export.db");
        try
        {
            using var dbA = new EhDatabase(pathA);
            SeedImportDatabase(dbA, suffix: "A", gidBase: 1);
            dbA.ExportDatabase(exportPath);

            using var dbB = new EhDatabase(pathB);
            SeedImportDatabase(dbB, suffix: "B", gidBase: 1000);
            dbB.ImportDatabase(exportPath);

            Assert.Equal(8, dbB.GetSchemaUserVersion());

            // Merge, not replace: B's own rows survive next to A's rows.
            Assert.Equal(
                new long[] { 1, 1000 },
                dbB.GetAllDownloadInfo().Select(static x => x.Gid).OrderBy(static x => x).ToArray());

            Assert.Equal(
                new long[] { 10, 1009 },
                dbB.GetAllHistoryInfo().Select(static x => x.Gid).OrderBy(static x => x).ToArray());

            Assert.Equal(
                new long[] { 20, 1019 },
                dbB.GetAllLocalFavorites().Select(static x => x.Gid).OrderBy(static x => x).ToArray());

            Assert.Equal(
                new long[] { 30, 1029 },
                dbB.GetAllBookmarks().Select(static x => x.Gid).OrderBy(static x => x).ToArray());

            Assert.Equal(
                new long[] { 40, 1039 },
                dbB.GetAllGalleryTags().Select(static x => x.Gid).OrderBy(static x => x).ToArray());

            Assert.Equal(
                new[] { "label-A", "label-B" },
                dbB.GetAllDownloadLabels().Select(static x => x.Label).OrderBy(static x => x, StringComparer.Ordinal).ToArray());

            Assert.Equal(
                new[] { "dir-A", "dir-B" },
                new[] { dbB.GetDownloadDirname(50), dbB.GetDownloadDirname(1049) });

            Assert.Equal(
                new[] { "qs-A", "qs-B" },
                dbB.GetAllQuickSearch().Select(static x => x.Name).OrderBy(static x => x, StringComparer.Ordinal).ToArray());

            Assert.Equal(
                new[] { "filter-a", "filter-b" },
                dbB.GetAllFilter().Select(static x => x.Text).OrderBy(static x => x, StringComparer.Ordinal).ToArray());

            Assert.Equal(
                new[] { "bad-A", "bad-B" },
                dbB.GetAllBlackList().Select(static x => x.BadGayName).OrderBy(static x => x, StringComparer.Ordinal).ToArray());

            Assert.True(dbB.InBlackList("bad-A"));
            Assert.True(dbB.InBlackList("bad-B"));
        }
        finally
        {
            CleanupDirectory(dirA);
            CleanupDirectory(dirB);
        }
    }

    // ------------------------------------------------------------------------------------
    // 8. EhFilter
    // ------------------------------------------------------------------------------------

    [Fact]
    public void EhFilter_NullGallery_IsAlwaysFilteredOut()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);
            var filter = new EhFilter(db);

            // false == filtered out; a null gallery never passes any mode.
            Assert.False(filter.FilterTitle(null));
            Assert.False(filter.FilterUploader(null));
            Assert.False(filter.FilterTag(null));
            Assert.False(filter.FilterTagNamespace(null));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void EhFilter_ModeTitle_FiltersByLowercaseSubstring()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);
            var filter = new EhFilter(db);

            var rule = new FilterRule { Mode = EhFilter.ModeTitle, Text = "Blocked" };
            filter.AddFilter(rule);

            // AddToBucket normalizes title rules to lowercase and the comparison is ordinal contains.
            Assert.Equal("blocked", rule.Text);
            Assert.True(filter.TitleFilters.Count == 1);
            Assert.False(filter.FilterTitle(new GalleryInfo { Gid = 1, Title = "This is BLOCKED content" }));
            Assert.False(filter.FilterTitle(new GalleryInfo { Gid = 2, Title = "blocked" }));
            Assert.True(filter.FilterTitle(new GalleryInfo { Gid = 3, Title = "Totally fine" }));
            Assert.True(filter.FilterTitle(new GalleryInfo { Gid = 4, Title = null }));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void EhFilter_ModeUploader_RequiresExactOrdinalMatch()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);
            var filter = new EhFilter(db);

            var rule = new FilterRule { Mode = EhFilter.ModeUploader, Text = "Bad Uploader" };
            filter.AddFilter(rule);

            // Uploader rules are not lowercased, and equality is ordinal.
            Assert.Equal("Bad Uploader", rule.Text);
            Assert.False(filter.FilterUploader(new GalleryInfo { Gid = 1, Uploader = "Bad Uploader" }));
            Assert.True(filter.FilterUploader(new GalleryInfo { Gid = 2, Uploader = "bad uploader" }));
            Assert.True(filter.FilterUploader(new GalleryInfo { Gid = 3, Uploader = "Bad Uploader 2" }));
            Assert.True(filter.FilterUploader(new GalleryInfo { Gid = 4, Uploader = null }));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void EhFilter_ModeTag_MatchesTagNameAndRespectsNamespace()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);
            var filter = new EhFilter(db);

            filter.AddFilter(new FilterRule { Mode = EhFilter.ModeTag, Text = "Chinese" });

            // A namespace-less rule matches the bare tag name in any namespace.
            Assert.False(filter.FilterTag(new GalleryInfo { Gid = 1, SimpleTags = ["language:chinese"] }));
            Assert.False(filter.FilterTag(new GalleryInfo { Gid = 2, SimpleTags = ["chinese"] }));
            Assert.True(filter.FilterTag(new GalleryInfo { Gid = 3, SimpleTags = ["language:english"] }));
            Assert.True(filter.FilterTag(new GalleryInfo { Gid = 4, SimpleTags = null }));
            Assert.True(filter.FilterTag(new GalleryInfo { Gid = 5, SimpleTags = [] }));

            // A namespaced rule only matches inside its own namespace.
            using var db2 = new EhDatabase(Path.Combine(dir, "second.db"));
            var namespaced = new EhFilter(db2);
            namespaced.AddFilter(new FilterRule { Mode = EhFilter.ModeTag, Text = "Language:Chinese" });

            Assert.False(namespaced.FilterTag(new GalleryInfo { Gid = 6, SimpleTags = ["language:chinese"] }));
            Assert.True(namespaced.FilterTag(new GalleryInfo { Gid = 7, SimpleTags = ["male:chinese"] }));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void EhFilter_ModeTagNamespace_FiltersWholeNamespaceOnly()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);
            var filter = new EhFilter(db);

            filter.AddFilter(new FilterRule { Mode = EhFilter.ModeTagNamespace, Text = "Language" });

            Assert.False(filter.FilterTagNamespace(new GalleryInfo { Gid = 1, SimpleTags = ["language:chinese"] }));
            Assert.False(filter.FilterTagNamespace(new GalleryInfo { Gid = 2, SimpleTags = ["language:english"] }));
            Assert.True(filter.FilterTagNamespace(new GalleryInfo { Gid = 3, SimpleTags = ["male:sole male"] }));
            Assert.True(filter.FilterTagNamespace(new GalleryInfo { Gid = 4, SimpleTags = ["chinese"] }));
            Assert.True(filter.FilterTagNamespace(new GalleryInfo { Gid = 5, SimpleTags = null }));
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void EhFilter_AddFilter_PersistsAcrossDatabaseReopen()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using (var db = new EhDatabase(path))
            {
                var filter = new EhFilter(db);
                filter.AddFilter(new FilterRule { Mode = EhFilter.ModeTitle, Text = "Persist" });
                filter.AddFilter(new FilterRule { Mode = EhFilter.ModeUploader, Text = "Uploader" });
            }

            using (var db = new EhDatabase(path))
            {
                var filter = new EhFilter(db);

                Assert.Single(filter.TitleFilters);
                Assert.Single(filter.UploaderFilters);
                Assert.Equal("persist", filter.TitleFilters[0].Text);
                Assert.Equal("Uploader", filter.UploaderFilters[0].Text);
                Assert.True(filter.TitleFilters[0].IsEnabled);

                Assert.False(filter.FilterTitle(new GalleryInfo { Title = "persist here" }));
                Assert.False(filter.FilterUploader(new GalleryInfo { Uploader = "Uploader" }));
            }
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void EhFilter_TriggerFilter_TogglesIsEnabledAndPersists()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);
            var filter = new EhFilter(db);

            var rule = new FilterRule { Mode = EhFilter.ModeTitle, Text = "Toggle" };
            filter.AddFilter(rule);

            Assert.True(rule.IsEnabled);
            Assert.False(filter.FilterTitle(new GalleryInfo { Title = "toggle this" }));

            filter.TriggerFilter(rule);
            Assert.False(rule.IsEnabled);
            Assert.True(filter.FilterTitle(new GalleryInfo { Title = "toggle this" }));

            // The disable is persisted: a fresh EhFilter on the same database sees ENABLE = 0.
            var reloaded = new EhFilter(db);
            FilterRule stored = Assert.Single(reloaded.TitleFilters);
            Assert.False(stored.IsEnabled);
            Assert.True(reloaded.FilterTitle(new GalleryInfo { Title = "toggle this" }));

            filter.TriggerFilter(rule);
            Assert.True(rule.IsEnabled);
            Assert.False(filter.FilterTitle(new GalleryInfo { Title = "toggle this" }));
            Assert.True(Assert.Single(new EhFilter(db).TitleFilters).IsEnabled);
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void EhFilter_DeleteFilter_RemovesRuleAndStopsFiltering()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);
            var filter = new EhFilter(db);

            var rule = new FilterRule { Mode = EhFilter.ModeTitle, Text = "DeleteMe" };
            filter.AddFilter(rule);
            Assert.Single(filter.TitleFilters);
            Assert.False(filter.FilterTitle(new GalleryInfo { Title = "deleteme now" }));

            filter.DeleteFilter(rule);

            Assert.Empty(filter.TitleFilters);
            Assert.True(filter.FilterTitle(new GalleryInfo { Title = "deleteme now" }));
            Assert.Empty(new EhFilter(db).TitleFilters);
            Assert.Empty(db.GetAllFilter());
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void EhFilter_NeedTags_TrueOnlyForTagAndTagNamespaceRules()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);
            var filter = new EhFilter(db);

            Assert.False(filter.NeedTags);

            filter.AddFilter(new FilterRule { Mode = EhFilter.ModeTitle, Text = "title-only" });
            Assert.False(filter.NeedTags);

            filter.AddFilter(new FilterRule { Mode = EhFilter.ModeUploader, Text = "uploader-only" });
            Assert.False(filter.NeedTags);

            filter.AddFilter(new FilterRule { Mode = EhFilter.ModeTag, Text = "tag-only" });
            Assert.True(filter.NeedTags);
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void EhFilter_NeedTags_TrueForTagNamespaceRuleAlone()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);
            var filter = new EhFilter(db);

            filter.AddFilter(new FilterRule { Mode = EhFilter.ModeTagNamespace, Text = "language" });

            Assert.True(filter.NeedTags);
            Assert.Single(filter.TagNamespaceFilters);
            Assert.Empty(filter.TagFilters);
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    [Fact]
    public void EhFilter_DisabledRule_DoesNotFilterButStaysLoaded()
    {
        string path = NewDatabasePath(out string dir);
        try
        {
            using var db = new EhDatabase(path);

            // Seeded through EhDatabase directly with ENABLE = 0.
            db.AddFilter(new FilterRule { Mode = EhFilter.ModeTag, Text = "chinese", Enable = false });

            var filter = new EhFilter(db);
            FilterRule rule = Assert.Single(filter.TagFilters);
            Assert.False(rule.IsEnabled);
            Assert.False(rule.Enable);

            Assert.True(filter.FilterTag(new GalleryInfo { SimpleTags = ["language:chinese"] }));
            Assert.True(filter.NeedTags);
        }
        finally
        {
            CleanupDirectory(dir);
        }
    }

    // ------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------

    protected override void DisposeCore()
    {
        SqliteConnection.ClearAllPools();
    }

    private string NewDatabasePath(out string directory)
    {
        directory = CreateTempDirectory("ehviewer-storage");
        return Path.Combine(directory, "eh.db");
    }

    private static void CleanupDirectory(string directory)
    {
        _ = directory;
        SqliteConnection.ClearAllPools();
    }

    private static SqliteConnection OpenRaw(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static void Exec(SqliteConnection conn, string sql, Action<SqliteCommand>? bind = null)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd);
        cmd.ExecuteNonQuery();
    }

    private static int ReadUserVersion(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<string> ReadTableNames(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;";
        using SqliteDataReader reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static List<string> ReadColumns(SqliteConnection conn, string table)
    {
        // pragma_table_info() is a table-valued function, so the table name can be bound as a parameter.
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_table_info($table) ORDER BY cid;";
        cmd.Parameters.AddWithValue("$table", table);
        using SqliteDataReader reader = cmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static List<string> ReadIndexNames(SqliteConnection conn, string table)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_index_list($table) ORDER BY name;";
        cmd.Parameters.AddWithValue("$table", table);
        using SqliteDataReader reader = cmd.ExecuteReader();
        var indexes = new List<string>();
        while (reader.Read())
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes;
    }

    private static List<string> ReadIndexColumns(SqliteConnection conn, string index)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_index_info($index) ORDER BY seqno;";
        cmd.Parameters.AddWithValue("$index", index);
        using SqliteDataReader reader = cmd.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static List<string> ReadAllBlackListNames(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT BADGAYNAME FROM \"Black_List\";";
        using SqliteDataReader reader = cmd.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// Returns the <c>detail</c> column of <c>EXPLAIN QUERY PLAN</c>, which names the index chosen.
    /// The statement text is a fixed literal; only the parameter name is bound.
    /// </summary>
    private static List<string> ExplainQueryPlan(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        cmd.Parameters.AddWithValue("$name", "probe");
        using SqliteDataReader reader = cmd.ExecuteReader();
        var details = new List<string>();
        while (reader.Read())
        {
            details.Add(reader.GetString(3));
        }

        return details;
    }

    private static T Fill<T>(T target, long gid)
        where T : GalleryInfo
    {
        target.Gid = gid;
        target.Token = $"token{gid:D6}";
        target.Title = $"Title {gid}";
        target.TitleJpn = $"タイトル {gid}";
        target.Thumb = $"https://example.invalid/thumb/{gid}.jpg";
        target.Category = (int)(gid % 10);
        target.Posted = "2024-01-02 03:04:05";
        target.Uploader = $"uploader-{gid}";
        target.Rating = 4.5f;
        target.SimpleLanguage = GalleryInfo.SLangJa;
        target.SimpleTags = ["language:japanese", "male:sole male"];
        return target;
    }

    private static GalleryInfo NewGallery(long gid) => Fill(new GalleryInfo(), gid);

    private static DownloadInfo NewDownload(long gid, long time)
    {
        DownloadInfo info = Fill(new DownloadInfo(), gid);
        info.State = DownloadInfo.StateFinish;
        info.Legacy = 0;
        info.Time = time;
        info.Label = $"label-{gid}";
        info.ArchiveUri = $"archive://gallery/{gid}";
        return info;
    }

    private static HistoryInfo NewHistory(long gid, long time, int mode = 1)
    {
        HistoryInfo info = Fill(new HistoryInfo(), gid);
        info.Time = time;
        info.Mode = mode;
        return info;
    }

    private static LocalFavoriteInfo NewLocalFavorite(long gid, long time)
    {
        LocalFavoriteInfo info = Fill(new LocalFavoriteInfo(), gid);
        info.Time = time;
        return info;
    }

    private static BookmarkInfo NewBookmark(long gid, int page, long time)
    {
        BookmarkInfo info = Fill(new BookmarkInfo(), gid);
        info.Page = page;
        info.Time = time;
        return info;
    }

    private static void AssertGalleryColumnsRoundTrip(GalleryInfo expected, GalleryInfo actual)
    {
        Assert.Equal(expected.Gid, actual.Gid);
        Assert.Equal(expected.Token, actual.Token);
        Assert.Equal(expected.Title, actual.Title);
        Assert.Equal(expected.TitleJpn, actual.TitleJpn);
        Assert.Equal(expected.Thumb, actual.Thumb);
        Assert.Equal(expected.Category, actual.Category);
        Assert.Equal(expected.Posted, actual.Posted);
        Assert.Equal(expected.Uploader, actual.Uploader);
        Assert.Equal(expected.Rating, actual.Rating);
        Assert.Equal(expected.SimpleLanguage, actual.SimpleLanguage);

        // SCHEMA_VERSION 8 has no SimpleTags column, so the array is never round-tripped.
        Assert.NotNull(expected.SimpleTags);
        Assert.Null(actual.SimpleTags);
    }

    private static void SeedImportDatabase(EhDatabase db, string suffix, long gidBase)
    {
        db.PutDownloadInfo(NewDownload(gid: gidBase, time: 1_000 + gidBase));
        db.PutHistoryInfo(NewHistory(gid: gidBase + 9, time: 2_000 + gidBase));
        db.PutLocalFavorite(NewLocalFavorite(gid: gidBase + 19, time: 3_000 + gidBase));
        db.PutBookmark(NewBookmark(gid: gidBase + 29, page: 7, time: 4_000 + gidBase));
        db.AddDownloadLabel(new DownloadLabel { Label = "label-" + suffix, Time = 5_000 + gidBase });
        db.PutDownloadDirname(gidBase + 49, "dir-" + suffix);
        db.InsertQuickSearch(new QuickSearch { Name = "qs-" + suffix, Keyword = "kw-" + suffix, Time = 6_000 + gidBase });
        db.AddFilter(new FilterRule { Mode = EhFilter.ModeTitle, Text = "filter-" + suffix.ToLowerInvariant() });
        db.InsertBlackList(new BlackListEntry { BadGayName = "bad-" + suffix, Reason = "r-" + suffix, AddTime = "2024-01-01" });
        db.InsertGalleryTags(new GalleryTagsRecord { Gid = gidBase + 39, Artist = "artist-" + suffix });
    }

    /// <summary>
    /// Builds a synthetic user_version = 2 database with the pre-migration column layout:
    /// FILTER without ENABLE, QUICK_SEARCH without PAGE_FROM/PAGE_TO, Black_List without MODE,
    /// DOWNLOADS without ARCHIVE_URI and a reduced Gallery_Tags table.
    /// </summary>
    private static void CreateVersion2Database(string path)
    {
        using SqliteConnection conn = OpenRaw(path, SqliteOpenMode.ReadWriteCreate);

        Exec(conn, """
            CREATE TABLE "DOWNLOADS" (
                "GID" INTEGER PRIMARY KEY NOT NULL,
                "TOKEN" TEXT,
                "TITLE" TEXT,
                "TITLE_JPN" TEXT,
                "THUMB" TEXT,
                "CATEGORY" INTEGER NOT NULL,
                "POSTED" TEXT,
                "UPLOADER" TEXT,
                "RATING" REAL NOT NULL,
                "SIMPLE_LANGUAGE" TEXT,
                "STATE" INTEGER NOT NULL,
                "LEGACY" INTEGER NOT NULL,
                "TIME" INTEGER NOT NULL,
                "LABEL" TEXT
            );
            """);

        Exec(conn, """
            CREATE TABLE "DOWNLOAD_LABELS" (
                "_id" INTEGER PRIMARY KEY,
                "LABEL" TEXT,
                "TIME" INTEGER NOT NULL
            );
            """);

        Exec(conn, """
            CREATE TABLE "DOWNLOAD_DIRNAME" (
                "GID" INTEGER PRIMARY KEY NOT NULL,
                "DIRNAME" TEXT
            );
            """);

        Exec(conn, """
            CREATE TABLE "HISTORY" (
                "GID" INTEGER PRIMARY KEY NOT NULL,
                "TOKEN" TEXT,
                "TITLE" TEXT,
                "TITLE_JPN" TEXT,
                "THUMB" TEXT,
                "CATEGORY" INTEGER NOT NULL,
                "POSTED" TEXT,
                "UPLOADER" TEXT,
                "RATING" REAL NOT NULL,
                "SIMPLE_LANGUAGE" TEXT,
                "MODE" INTEGER NOT NULL,
                "TIME" INTEGER NOT NULL
            );
            """);

        Exec(conn, """
            CREATE TABLE "LOCAL_FAVORITES" (
                "GID" INTEGER PRIMARY KEY NOT NULL,
                "TOKEN" TEXT,
                "TITLE" TEXT,
                "TITLE_JPN" TEXT,
                "THUMB" TEXT,
                "CATEGORY" INTEGER NOT NULL,
                "POSTED" TEXT,
                "UPLOADER" TEXT,
                "RATING" REAL NOT NULL,
                "SIMPLE_LANGUAGE" TEXT,
                "TIME" INTEGER NOT NULL
            );
            """);

        Exec(conn, """
            CREATE TABLE "BOOKMARKS" (
                "GID" INTEGER PRIMARY KEY NOT NULL,
                "TOKEN" TEXT,
                "TITLE" TEXT,
                "TITLE_JPN" TEXT,
                "THUMB" TEXT,
                "CATEGORY" INTEGER NOT NULL,
                "POSTED" TEXT,
                "UPLOADER" TEXT,
                "RATING" REAL NOT NULL,
                "SIMPLE_LANGUAGE" TEXT,
                "PAGE" INTEGER NOT NULL,
                "TIME" INTEGER NOT NULL
            );
            """);

        // v2 FILTER has no ENABLE column; the <=2 migration rebuilds it with ENABLE = 1.
        Exec(conn, """
            CREATE TABLE "FILTER" (
                "_id" INTEGER PRIMARY KEY,
                "MODE" INTEGER NOT NULL,
                "TEXT" TEXT
            );
            """);

        // v2 QUICK_SEARCH has no PAGE_FROM/PAGE_TO; the <=3 migration backfills them with -1.
        Exec(conn, """
            CREATE TABLE "QUICK_SEARCH" (
                "_id" INTEGER PRIMARY KEY,
                "NAME" TEXT,
                "MODE" INTEGER NOT NULL,
                "CATEGORY" INTEGER NOT NULL,
                "KEYWORD" TEXT,
                "ADVANCE_SEARCH" INTEGER NOT NULL,
                "MIN_RATING" INTEGER NOT NULL,
                "TIME" INTEGER NOT NULL
            );
            """);

        // v2 Black_List has no MODE/ANGRYWITH semantics/index; the <=4 migration drops and recreates it.
        Exec(conn, """
            CREATE TABLE "Black_List" (
                "_id" INTEGER PRIMARY KEY,
                "BADGAYNAME" TEXT,
                "REASON" TEXT,
                "ANGRYWITH" TEXT,
                "ADD_TIME" TEXT
            );
            """);

        // v2 Gallery_Tags is dropped and recreated by the <=5 migration.
        Exec(conn, """
            CREATE TABLE "Gallery_Tags" (
                "GID" INTEGER PRIMARY KEY NOT NULL,
                "ROWS" TEXT,
                "ARTIST" TEXT
            );
            """);

        Exec(conn, """
            INSERT INTO "DOWNLOADS" (
                "GID", "TOKEN", "TITLE", "TITLE_JPN", "THUMB", "CATEGORY", "POSTED", "UPLOADER",
                "RATING", "SIMPLE_LANGUAGE", "STATE", "LEGACY", "TIME", "LABEL"
            ) VALUES (
                $gid, $token, $title, $titleJpn, $thumb, $category, $posted, $uploader,
                $rating, $simpleLanguage, $state, $legacy, $time, $label
            );
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$gid", 1001L);
            cmd.Parameters.AddWithValue("$token", "legacy-token");
            cmd.Parameters.AddWithValue("$title", "Legacy Title");
            cmd.Parameters.AddWithValue("$titleJpn", "レガシー");
            cmd.Parameters.AddWithValue("$thumb", "https://example.invalid/legacy.jpg");
            cmd.Parameters.AddWithValue("$category", 3);
            cmd.Parameters.AddWithValue("$posted", "2019-05-06 07:08:09");
            cmd.Parameters.AddWithValue("$uploader", "Legacy Uploader");
            cmd.Parameters.AddWithValue("$rating", 3.5);
            cmd.Parameters.AddWithValue("$simpleLanguage", "EN");
            cmd.Parameters.AddWithValue("$state", 3);
            cmd.Parameters.AddWithValue("$legacy", 1);
            cmd.Parameters.AddWithValue("$time", 1_700_000_000_000L);
            cmd.Parameters.AddWithValue("$label", "legacy-label-ref");
        });

        Exec(conn, """
            INSERT INTO "DOWNLOAD_LABELS" ("_id", "LABEL", "TIME") VALUES ($id, $label, $time);
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$id", 1L);
            cmd.Parameters.AddWithValue("$label", "legacy-label");
            cmd.Parameters.AddWithValue("$time", 1_700_000_000_500L);
        });

        Exec(conn, """
            INSERT INTO "DOWNLOAD_DIRNAME" ("GID", "DIRNAME") VALUES ($gid, $dirname);
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$gid", 1004L);
            cmd.Parameters.AddWithValue("$dirname", "legacy-dir");
        });

        Exec(conn, """
            INSERT INTO "HISTORY" (
                "GID", "TOKEN", "TITLE", "TITLE_JPN", "THUMB", "CATEGORY", "POSTED", "UPLOADER",
                "RATING", "SIMPLE_LANGUAGE", "MODE", "TIME"
            ) VALUES (
                $gid, $token, $title, $titleJpn, $thumb, $category, $posted, $uploader,
                $rating, $simpleLanguage, $mode, $time
            );
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$gid", 1001L);
            cmd.Parameters.AddWithValue("$token", "legacy-token");
            cmd.Parameters.AddWithValue("$title", "Legacy Title");
            cmd.Parameters.AddWithValue("$titleJpn", "レガシー");
            cmd.Parameters.AddWithValue("$thumb", "https://example.invalid/legacy.jpg");
            cmd.Parameters.AddWithValue("$category", 3);
            cmd.Parameters.AddWithValue("$posted", "2019-05-06 07:08:09");
            cmd.Parameters.AddWithValue("$uploader", "Legacy Uploader");
            cmd.Parameters.AddWithValue("$rating", 3.5);
            cmd.Parameters.AddWithValue("$simpleLanguage", "EN");
            cmd.Parameters.AddWithValue("$mode", 0);
            cmd.Parameters.AddWithValue("$time", 1_700_000_000_200L);
        });

        Exec(conn, """
            INSERT INTO "LOCAL_FAVORITES" (
                "GID", "TOKEN", "TITLE", "TITLE_JPN", "THUMB", "CATEGORY", "POSTED", "UPLOADER",
                "RATING", "SIMPLE_LANGUAGE", "TIME"
            ) VALUES (
                $gid, $token, $title, $titleJpn, $thumb, $category, $posted, $uploader,
                $rating, $simpleLanguage, $time
            );
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$gid", 1003L);
            cmd.Parameters.AddWithValue("$token", "legacy-token");
            cmd.Parameters.AddWithValue("$title", "Legacy Title");
            cmd.Parameters.AddWithValue("$titleJpn", "レガシー");
            cmd.Parameters.AddWithValue("$thumb", "https://example.invalid/legacy.jpg");
            cmd.Parameters.AddWithValue("$category", 3);
            cmd.Parameters.AddWithValue("$posted", "2019-05-06 07:08:09");
            cmd.Parameters.AddWithValue("$uploader", "Legacy Uploader");
            cmd.Parameters.AddWithValue("$rating", 3.5);
            cmd.Parameters.AddWithValue("$simpleLanguage", "EN");
            cmd.Parameters.AddWithValue("$time", 1_700_000_000_400L);
        });

        Exec(conn, """
            INSERT INTO "BOOKMARKS" (
                "GID", "TOKEN", "TITLE", "TITLE_JPN", "THUMB", "CATEGORY", "POSTED", "UPLOADER",
                "RATING", "SIMPLE_LANGUAGE", "PAGE", "TIME"
            ) VALUES (
                $gid, $token, $title, $titleJpn, $thumb, $category, $posted, $uploader,
                $rating, $simpleLanguage, $page, $time
            );
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$gid", 1002L);
            cmd.Parameters.AddWithValue("$token", "legacy-token");
            cmd.Parameters.AddWithValue("$title", "Legacy Title");
            cmd.Parameters.AddWithValue("$titleJpn", "レガシー");
            cmd.Parameters.AddWithValue("$thumb", "https://example.invalid/legacy.jpg");
            cmd.Parameters.AddWithValue("$category", 3);
            cmd.Parameters.AddWithValue("$posted", "2019-05-06 07:08:09");
            cmd.Parameters.AddWithValue("$uploader", "Legacy Uploader");
            cmd.Parameters.AddWithValue("$rating", 3.5);
            cmd.Parameters.AddWithValue("$simpleLanguage", "EN");
            cmd.Parameters.AddWithValue("$page", 5);
            cmd.Parameters.AddWithValue("$time", 1_700_000_000_300L);
        });

        Exec(conn, """
            INSERT INTO "FILTER" ("_id", "MODE", "TEXT") VALUES ($id, $mode, $text);
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$id", 1L);
            cmd.Parameters.AddWithValue("$mode", 0);
            cmd.Parameters.AddWithValue("$text", "legacy-filter");
        });

        Exec(conn, """
            INSERT INTO "QUICK_SEARCH" ("_id", "NAME", "MODE", "CATEGORY", "KEYWORD", "ADVANCE_SEARCH", "MIN_RATING", "TIME")
            VALUES ($id, $name, $mode, $category, $keyword, $advanceSearch, $minRating, $time);
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$id", 1L);
            cmd.Parameters.AddWithValue("$name", "legacy-qs");
            cmd.Parameters.AddWithValue("$mode", 0);
            cmd.Parameters.AddWithValue("$category", 0);
            cmd.Parameters.AddWithValue("$keyword", "legacy-keyword");
            cmd.Parameters.AddWithValue("$advanceSearch", 0);
            cmd.Parameters.AddWithValue("$minRating", 0);
            cmd.Parameters.AddWithValue("$time", 1_700_000_000_100L);
        });

        Exec(conn, """
            INSERT INTO "Black_List" ("_id", "BADGAYNAME", "REASON", "ANGRYWITH", "ADD_TIME")
            VALUES ($id, $name, $reason, $angryWith, $addTime);
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$id", 1L);
            cmd.Parameters.AddWithValue("$name", "legacy-black");
            cmd.Parameters.AddWithValue("$reason", "legacy");
            cmd.Parameters.AddWithValue("$angryWith", "legacy");
            cmd.Parameters.AddWithValue("$addTime", "2019-01-01");
        });

        Exec(conn, """
            INSERT INTO "Gallery_Tags" ("GID", "ROWS", "ARTIST") VALUES ($gid, $rows, $artist);
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("$gid", 1005L);
            cmd.Parameters.AddWithValue("$rows", "legacy-rows");
            cmd.Parameters.AddWithValue("$artist", "legacy-artist");
        });

        Exec(conn, "PRAGMA user_version = 2;");
    }
}
