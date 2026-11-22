using System.Data;
using EhViewer.Core.Client.Data;
using Microsoft.Data.Sqlite;

namespace EhViewer.Core.Storage;

/// <summary>
/// Thread-safe, parameterized SQLite storage repository matching upstream greenDAO <c>eh.db</c>
/// (<c>SCHEMA_VERSION = 8</c>) across downloads, labels, dirnames, history, quick searches,
/// local favorites, bookmarks, filters, blacklist, and cached gallery tags.
/// </summary>
public sealed class EhDatabase : IDisposable
{
    public const int SchemaVersion = 8;

    private const string GalleryInfoSelectColumns =
        "GID, TOKEN, TITLE, TITLE_JPN, THUMB, CATEGORY, POSTED, UPLOADER, RATING, SIMPLE_LANGUAGE";

    private const string GalleryInfoInsertColumns = GalleryInfoSelectColumns;
    private const string GalleryInfoInsertParams =
        "$gid, $token, $title, $titleJpn, $thumb, $category, $posted, $uploader, $rating, $simpleLanguage";

    private const string GalleryInfoUpdateSet =
        "TOKEN = $token, TITLE = $title, TITLE_JPN = $titleJpn, THUMB = $thumb, CATEGORY = $category, " +
        "POSTED = $posted, UPLOADER = $uploader, RATING = $rating, SIMPLE_LANGUAGE = $simpleLanguage";

    private readonly object _gate = new();
    private readonly SqliteConnection _connection;

    public EhDatabase(string databasePath, int maxHistoryCount = 100)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        MaxHistoryCount = maxHistoryCount;

        _connection = new SqliteConnection(BuildConnectionString(databasePath));
        _connection.Open();
        ConfigureConnection(_connection, databasePath);
        EnsureSchemaAndUpgrade(_connection);
    }

    public int MaxHistoryCount { get; set; }

    // --- Downloads ---

    public List<DownloadInfo> GetAllDownloadInfo()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {GalleryInfoSelectColumns}, STATE, LEGACY, TIME, LABEL, ARCHIVE_URI
                FROM DOWNLOADS
                ORDER BY TIME DESC;
                """;
            return ReadDownloadInfoList(cmd, collapseTransientStates: true);
        }
    }

    /// <summary>
    /// Returns downloads whose persisted state is <see cref="DownloadInfo.StateWait"/> or
    /// <see cref="DownloadInfo.StateDownload"/>, ordered oldest-first for startup queue resumption.
    /// </summary>
    public List<DownloadInfo> GetPendingDownloadInfo()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {GalleryInfoSelectColumns}, STATE, LEGACY, TIME, LABEL, ARCHIVE_URI
                FROM DOWNLOADS
                WHERE STATE IN ($wait, $download)
                ORDER BY TIME ASC;
                """;
            cmd.Parameters.AddWithValue("$wait", DownloadInfo.StateWait);
            cmd.Parameters.AddWithValue("$download", DownloadInfo.StateDownload);
            return ReadDownloadInfoList(cmd, collapseTransientStates: false);
        }
    }

    /// <summary>
    /// Returns the persisted download row for <paramref name="gid"/>, or <see langword="null"/>.
    /// </summary>
    public DownloadInfo? GetDownloadInfo(long gid, bool collapseTransientStates = true)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {GalleryInfoSelectColumns}, STATE, LEGACY, TIME, LABEL, ARCHIVE_URI
                FROM DOWNLOADS
                WHERE GID = $gid
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$gid", gid);
            using SqliteDataReader reader = cmd.ExecuteReader();
            return reader.Read() ? ReadDownloadInfoRow(reader, collapseTransientStates) : null;
        }
    }

    private static List<DownloadInfo> ReadDownloadInfoList(SqliteCommand cmd, bool collapseTransientStates)
    {
        using SqliteDataReader reader = cmd.ExecuteReader();
        var list = new List<DownloadInfo>();
        while (reader.Read())
        {
            list.Add(ReadDownloadInfoRow(reader, collapseTransientStates));
        }

        return list;
    }

    private static DownloadInfo ReadDownloadInfoRow(SqliteDataReader reader, bool collapseTransientStates)
    {
        var info = new DownloadInfo();
        ReadGalleryInfo(reader, info);
        int state = reader.GetInt32(10);
        if (collapseTransientStates && state is DownloadInfo.StateWait or DownloadInfo.StateDownload)
        {
            state = DownloadInfo.StateNone;
        }

        info.State = state;
        info.Legacy = reader.GetInt32(11);
        info.Time = reader.GetInt64(12);
        info.Label = GetNullableString(reader, 13);
        info.ArchiveUri = GetNullableString(reader, 14);
        return info;
    }

    public void PutDownloadInfo(DownloadInfo downloadInfo)
    {
        ArgumentNullException.ThrowIfNull(downloadInfo);

        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO DOWNLOADS ({GalleryInfoInsertColumns}, STATE, LEGACY, TIME, LABEL, ARCHIVE_URI)
                VALUES ({GalleryInfoInsertParams}, $state, $legacy, $time, $label, $archiveUri)
                ON CONFLICT(GID) DO UPDATE SET
                    {GalleryInfoUpdateSet},
                    STATE = $state,
                    LEGACY = $legacy,
                    TIME = $time,
                    LABEL = $label,
                    ARCHIVE_URI = $archiveUri;
                """;
            BindGalleryInfo(cmd, downloadInfo);
            cmd.Parameters.AddWithValue("$state", downloadInfo.State);
            cmd.Parameters.AddWithValue("$legacy", downloadInfo.Legacy);
            cmd.Parameters.AddWithValue("$time", downloadInfo.Time);
            cmd.Parameters.AddWithValue("$label", (object?)downloadInfo.Label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$archiveUri", (object?)downloadInfo.ArchiveUri ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveDownloadInfo(long gid)
    {
        lock (_gate)
        {
            ExecuteDeleteByGid("DOWNLOADS", gid);
        }
    }

    public void MoveDownloadInfo(IList<DownloadInfo> infos, int fromPosition, int toPosition)
    {
        ArgumentNullException.ThrowIfNull(infos);
        if (fromPosition == toPosition || fromPosition < 0 || toPosition < 0 || fromPosition >= infos.Count || toPosition >= infos.Count)
        {
            return;
        }

        lock (_gate)
        {
            bool reverse = fromPosition > toPosition;
            int offset = reverse ? toPosition : fromPosition;
            int limit = Math.Abs(toPosition - fromPosition) + 1;

            var slice = new List<DownloadInfo>(limit);
            for (int i = 0; i < limit; i++)
            {
                slice.Add(infos[offset + i]);
            }

            ShiftTimestamps(slice, reverse, static x => x.Time, static (x, t) => x.Time = t);

            using SqliteTransaction tx = _connection.BeginTransaction();
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE DOWNLOADS SET TIME = $time WHERE GID = $gid;";
            SqliteParameter pTime = cmd.Parameters.Add("$time", SqliteType.Integer);
            SqliteParameter pGid = cmd.Parameters.Add("$gid", SqliteType.Integer);
            foreach (DownloadInfo item in slice)
            {
                pTime.Value = item.Time;
                pGid.Value = item.Gid;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    // --- Download Dirname ---

    public string? GetDownloadDirname(long gid)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT DIRNAME FROM DOWNLOAD_DIRNAME WHERE GID = $gid LIMIT 1;";
            cmd.Parameters.AddWithValue("$gid", gid);
            object? result = cmd.ExecuteScalar();
            return result is DBNull ? null : result as string;
        }
    }

    public void PutDownloadDirname(long gid, string? dirname)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO DOWNLOAD_DIRNAME (GID, DIRNAME)
                VALUES ($gid, $dirname)
                ON CONFLICT(GID) DO UPDATE SET DIRNAME = $dirname;
                """;
            cmd.Parameters.AddWithValue("$gid", gid);
            cmd.Parameters.AddWithValue("$dirname", (object?)dirname ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveDownloadDirname(long gid)
    {
        lock (_gate)
        {
            ExecuteDeleteByGid("DOWNLOAD_DIRNAME", gid);
        }
    }

    public void UpdateDownloadDirname(long removeGid, long newGid, string? dirname)
    {
        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();
            using (SqliteCommand del = _connection.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM DOWNLOAD_DIRNAME WHERE GID = $gid;";
                del.Parameters.AddWithValue("$gid", removeGid);
                del.ExecuteNonQuery();
            }

            using (SqliteCommand upsert = _connection.CreateCommand())
            {
                upsert.Transaction = tx;
                upsert.CommandText = """
                    INSERT INTO DOWNLOAD_DIRNAME (GID, DIRNAME)
                    VALUES ($gid, $dirname)
                    ON CONFLICT(GID) DO UPDATE SET DIRNAME = $dirname;
                    """;
                upsert.Parameters.AddWithValue("$gid", newGid);
                upsert.Parameters.AddWithValue("$dirname", (object?)dirname ?? DBNull.Value);
                upsert.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void ClearDownloadDirname()
    {
        lock (_gate)
        {
            ExecuteNonQuery("DELETE FROM DOWNLOAD_DIRNAME;");
        }
    }

    public List<DownloadDirname> GetAllDownloadDirname()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT GID, DIRNAME FROM DOWNLOAD_DIRNAME;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            var list = new List<DownloadDirname>();
            while (reader.Read())
            {
                list.Add(new DownloadDirname
                {
                    Gid = reader.GetInt64(0),
                    Dirname = GetNullableString(reader, 1)
                });
            }

            return list;
        }
    }

    public Dictionary<long, string> GetAllDownloadDirnamesMap()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT GID, DIRNAME FROM DOWNLOAD_DIRNAME WHERE DIRNAME IS NOT NULL AND DIRNAME <> '';";
            using SqliteDataReader reader = cmd.ExecuteReader();
            var map = new Dictionary<long, string>();
            while (reader.Read())
            {
                map[reader.GetInt64(0)] = reader.GetString(1);
            }

            return map;
        }
    }

    // --- Download Labels ---

    public bool HasDownloadLabel(string label)
    {
        if (string.IsNullOrEmpty(label))
        {
            return false;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM DOWNLOAD_LABELS WHERE LABEL = $label LIMIT 1;";
            cmd.Parameters.AddWithValue("$label", label);
            return cmd.ExecuteScalar() is not null;
        }
    }

    public List<DownloadLabel> GetAllDownloadLabels()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT _id, LABEL, TIME FROM DOWNLOAD_LABELS ORDER BY TIME ASC;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            var list = new List<DownloadLabel>();
            while (reader.Read())
            {
                list.Add(new DownloadLabel
                {
                    Id = reader.GetInt64(0),
                    Label = GetNullableString(reader, 1),
                    Time = reader.GetInt64(2)
                });
            }

            return list;
        }
    }

    public DownloadLabel AddDownloadLabel(string label)
    {
        var entity = new DownloadLabel
        {
            Label = label,
            Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        return AddDownloadLabel(entity);
    }

    public DownloadLabel AddDownloadLabel(DownloadLabel raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        lock (_gate)
        {
            if (raw.Time == 0)
            {
                raw.Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO DOWNLOAD_LABELS (LABEL, TIME) VALUES ($label, $time);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$label", (object?)raw.Label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$time", raw.Time);
            raw.Id = Convert.ToInt64(cmd.ExecuteScalar());
            return raw;
        }
    }

    public void UpdateDownloadLabel(DownloadLabel raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Id is null)
        {
            return;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE DOWNLOAD_LABELS SET LABEL = $label, TIME = $time WHERE _id = $id;";
            cmd.Parameters.AddWithValue("$label", (object?)raw.Label ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$time", raw.Time);
            cmd.Parameters.AddWithValue("$id", raw.Id.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void MoveDownloadLabel(int fromPosition, int toPosition)
    {
        if (fromPosition == toPosition)
        {
            return;
        }

        lock (_gate)
        {
            MoveOrderedRowsByTimestamp(
                tableName: "DOWNLOAD_LABELS",
                selectColumn: "TIME",
                fromPosition: fromPosition,
                toPosition: toPosition);
        }
    }

    public void RemoveDownloadLabel(DownloadLabel raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        if (raw.Id is null)
        {
            return;
        }

        lock (_gate)
        {
            ExecuteDeleteById("DOWNLOAD_LABELS", raw.Id.Value);
        }
    }

    // --- Local Favorites ---

    public List<GalleryInfo> GetAllLocalFavorites()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {GalleryInfoSelectColumns}, TIME
                FROM LOCAL_FAVORITES
                ORDER BY TIME DESC;
                """;
            using SqliteDataReader reader = cmd.ExecuteReader();
            var result = new List<GalleryInfo>();
            while (reader.Read())
            {
                var item = new LocalFavoriteInfo();
                ReadGalleryInfo(reader, item);
                item.Time = reader.GetInt64(10);
                result.Add(item);
            }

            return result;
        }
    }

    public List<GalleryInfo> SearchLocalFavorites(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {GalleryInfoSelectColumns}, TIME
                FROM LOCAL_FAVORITES
                WHERE TITLE LIKE $pattern ESCAPE '\'
                ORDER BY TIME DESC;
                """;
            cmd.Parameters.AddWithValue("$pattern", "%" + EscapeLikePattern(query) + "%");
            using SqliteDataReader reader = cmd.ExecuteReader();
            var result = new List<GalleryInfo>();
            while (reader.Read())
            {
                var item = new LocalFavoriteInfo();
                ReadGalleryInfo(reader, item);
                item.Time = reader.GetInt64(10);
                result.Add(item);
            }

            return result;
        }
    }

    public GalleryInfo? SearchLocalFavorites(long gid)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {GalleryInfoSelectColumns}, TIME
                FROM LOCAL_FAVORITES
                WHERE GID = $gid
                ORDER BY TIME DESC
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$gid", gid);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var item = new LocalFavoriteInfo();
            ReadGalleryInfo(reader, item);
            item.Time = reader.GetInt64(10);
            return item;
        }
    }

    public bool ContainLocalFavorites(long gid)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM LOCAL_FAVORITES WHERE GID = $gid LIMIT 1;";
            cmd.Parameters.AddWithValue("$gid", gid);
            return cmd.ExecuteScalar() is not null;
        }
    }

    public int GetLocalFavoritesCount()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM LOCAL_FAVORITES;";
            object? scalar = cmd.ExecuteScalar();
            return scalar is long count ? (int)count : Convert.ToInt32(scalar, System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public void PutLocalFavorite(GalleryInfo galleryInfo)
    {
        ArgumentNullException.ThrowIfNull(galleryInfo);

        lock (_gate)
        {
            if (RecordExistsByGid("LOCAL_FAVORITES", galleryInfo.Gid))
            {
                return;
            }

            long time = galleryInfo is LocalFavoriteInfo lfi && lfi.Time != 0
                ? lfi.Time
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO LOCAL_FAVORITES ({GalleryInfoInsertColumns}, TIME)
                VALUES ({GalleryInfoInsertParams}, $time);
                """;
            BindGalleryInfo(cmd, galleryInfo);
            cmd.Parameters.AddWithValue("$time", time);
            cmd.ExecuteNonQuery();
        }
    }

    public void PutLocalFavorites(IEnumerable<GalleryInfo> galleryInfoList)
    {
        ArgumentNullException.ThrowIfNull(galleryInfoList);

        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();
            foreach (GalleryInfo info in galleryInfoList)
            {
                if (RecordExistsByGid("LOCAL_FAVORITES", info.Gid, tx))
                {
                    continue;
                }

                long time = info is LocalFavoriteInfo lfi && lfi.Time != 0
                    ? lfi.Time
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                using SqliteCommand cmd = _connection.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = $"""
                    INSERT INTO LOCAL_FAVORITES ({GalleryInfoInsertColumns}, TIME)
                    VALUES ({GalleryInfoInsertParams}, $time);
                    """;
                BindGalleryInfo(cmd, info);
                cmd.Parameters.AddWithValue("$time", time);
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    public void RemoveLocalFavorites(long gid)
    {
        lock (_gate)
        {
            ExecuteDeleteByGid("LOCAL_FAVORITES", gid);
        }
    }

    public void RemoveLocalFavorites(IEnumerable<long> gids)
    {
        ArgumentNullException.ThrowIfNull(gids);

        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM LOCAL_FAVORITES WHERE GID = $gid;";
            SqliteParameter pGid = cmd.Parameters.Add("$gid", SqliteType.Integer);
            foreach (long gid in gids)
            {
                pGid.Value = gid;
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    // --- Bookmarks ---

    public List<BookmarkInfo> GetAllBookmarks()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {GalleryInfoSelectColumns}, PAGE, TIME
                FROM BOOKMARKS
                ORDER BY TIME DESC;
                """;
            using SqliteDataReader reader = cmd.ExecuteReader();
            var list = new List<BookmarkInfo>();
            while (reader.Read())
            {
                var item = new BookmarkInfo();
                ReadGalleryInfo(reader, item);
                item.Page = reader.GetInt32(10);
                item.Time = reader.GetInt64(11);
                list.Add(item);
            }

            return list;
        }
    }

    public BookmarkInfo? GetBookmark(long gid)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {GalleryInfoSelectColumns}, PAGE, TIME
                FROM BOOKMARKS
                WHERE GID = $gid
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$gid", gid);
            using SqliteDataReader reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var item = new BookmarkInfo();
            ReadGalleryInfo(reader, item);
            item.Page = reader.GetInt32(10);
            item.Time = reader.GetInt64(11);
            return item;
        }
    }

    public void PutBookmark(BookmarkInfo bookmark)
    {
        ArgumentNullException.ThrowIfNull(bookmark);

        lock (_gate)
        {
            if (bookmark.Time == 0)
            {
                bookmark.Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO BOOKMARKS ({GalleryInfoInsertColumns}, PAGE, TIME)
                VALUES ({GalleryInfoInsertParams}, $page, $time)
                ON CONFLICT(GID) DO UPDATE SET
                    {GalleryInfoUpdateSet},
                    PAGE = $page,
                    TIME = $time;
                """;
            BindGalleryInfo(cmd, bookmark);
            cmd.Parameters.AddWithValue("$page", bookmark.Page);
            cmd.Parameters.AddWithValue("$time", bookmark.Time);
            cmd.ExecuteNonQuery();
        }
    }

    public void RemoveBookmark(long gid)
    {
        lock (_gate)
        {
            ExecuteDeleteByGid("BOOKMARKS", gid);
        }
    }

    // --- BlackList (Parameterized!) ---

    public List<BlackListEntry> GetAllBlackList()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT _id, BADGAYNAME, REASON, ANGRYWITH, ADD_TIME, MODE FROM Black_List ORDER BY ADD_TIME ASC;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            var list = new List<BlackListEntry>();
            while (reader.Read())
            {
                list.Add(new BlackListEntry
                {
                    Id = reader.GetInt64(0),
                    BadGayName = GetNullableString(reader, 1),
                    Reason = GetNullableString(reader, 2),
                    AngryWith = GetNullableString(reader, 3),
                    AddTime = GetNullableString(reader, 4),
                    Mode = reader.IsDBNull(5) ? null : reader.GetInt32(5)
                });
            }

            return list;
        }
    }

    public bool InBlackList(string? badGayName)
    {
        if (badGayName is null)
        {
            return false;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM Black_List WHERE BADGAYNAME = $name LIMIT 1;";
            cmd.Parameters.AddWithValue("$name", badGayName);
            return cmd.ExecuteScalar() is not null;
        }
    }

    public void InsertBlackList(BlackListEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.BadGayName is null)
        {
            return;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Black_List (BADGAYNAME, REASON, ANGRYWITH, ADD_TIME, MODE)
                VALUES ($name, $reason, $angryWith, $addTime, $mode);
                SELECT last_insert_rowid();
                """;
            BindBlackListParameters(cmd, entry);
            entry.Id = Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public void UpdateBlackList(BlackListEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Id is null)
        {
            return;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE Black_List
                SET BADGAYNAME = $name, REASON = $reason, ANGRYWITH = $angryWith, ADD_TIME = $addTime, MODE = $mode
                WHERE _id = $id;
                """;
            BindBlackListParameters(cmd, entry);
            cmd.Parameters.AddWithValue("$id", entry.Id.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteBlackList(BlackListEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Id is null)
        {
            return;
        }

        lock (_gate)
        {
            ExecuteDeleteById("Black_List", entry.Id.Value);
        }
    }

    // --- Gallery Tags ---

    public List<GalleryTagsRecord> GetAllGalleryTags()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT GID, ROWS, ARTIST, COSPLAYER, CHARACTER, FEMALE, "GROUP", LANGUAGE,
                       MALE, MISC, MIXED, OTHER, PARODY, RECLASS, CREATE_TIME, UPDATE_TIME, LOCATION
                FROM Gallery_Tags
                ORDER BY GID ASC;
                """;
            using SqliteDataReader reader = cmd.ExecuteReader();
            var list = new List<GalleryTagsRecord>();
            while (reader.Read())
            {
                list.Add(ReadGalleryTagsRow(reader));
            }

            return list;
        }
    }

    public bool InGalleryTags(long gid)
    {
        lock (_gate)
        {
            return RecordExistsByGid("Gallery_Tags", gid);
        }
    }

    public GalleryTagsRecord? QueryGalleryTags(long gid)
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT GID, ROWS, ARTIST, COSPLAYER, CHARACTER, FEMALE, "GROUP", LANGUAGE,
                       MALE, MISC, MIXED, OTHER, PARODY, RECLASS, CREATE_TIME, UPDATE_TIME, LOCATION
                FROM Gallery_Tags
                WHERE GID = $gid
                LIMIT 1;
                """;
            cmd.Parameters.AddWithValue("$gid", gid);
            using SqliteDataReader reader = cmd.ExecuteReader();
            return reader.Read() ? ReadGalleryTagsRow(reader) : null;
        }
    }

    public void InsertGalleryTags(GalleryTagsRecord tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        lock (_gate)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            tags.CreateTime ??= now;
            tags.UpdateTime ??= tags.CreateTime;

            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO Gallery_Tags (
                    GID, ROWS, ARTIST, COSPLAYER, CHARACTER, FEMALE, "GROUP", LANGUAGE,
                    MALE, MISC, MIXED, OTHER, PARODY, RECLASS, CREATE_TIME, UPDATE_TIME, LOCATION
                ) VALUES (
                    $gid, $rows, $artist, $cosplayer, $character, $female, $group, $language,
                    $male, $misc, $mixed, $other, $parody, $reclass, $createTime, $updateTime, $location
                );
                """;
            BindGalleryTagsParameters(cmd, tags);
            cmd.ExecuteNonQuery();
        }
    }

    public void UpdateGalleryTags(GalleryTagsRecord tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        lock (_gate)
        {
            tags.UpdateTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE Gallery_Tags
                SET ROWS = $rows, ARTIST = $artist, COSPLAYER = $cosplayer, CHARACTER = $character,
                    FEMALE = $female, "GROUP" = $group, LANGUAGE = $language, MALE = $male,
                    MISC = $misc, MIXED = $mixed, OTHER = $other, PARODY = $parody,
                    RECLASS = $reclass, CREATE_TIME = $createTime, UPDATE_TIME = $updateTime, LOCATION = $location
                WHERE GID = $gid;
                """;
            BindGalleryTagsParameters(cmd, tags);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteGalleryTags(GalleryTagsRecord tags)
    {
        ArgumentNullException.ThrowIfNull(tags);
        lock (_gate)
        {
            ExecuteDeleteByGid("Gallery_Tags", tags.Gid);
        }
    }

    // --- Quick Search ---

    public List<QuickSearch> GetAllQuickSearch()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT _id, NAME, MODE, CATEGORY, KEYWORD, ADVANCE_SEARCH, MIN_RATING, PAGE_FROM, PAGE_TO, TIME
                FROM QUICK_SEARCH
                ORDER BY TIME ASC;
                """;
            using SqliteDataReader reader = cmd.ExecuteReader();
            var list = new List<QuickSearch>();
            while (reader.Read())
            {
                list.Add(new QuickSearch
                {
                    Id = reader.GetInt64(0),
                    Name = GetNullableString(reader, 1),
                    Mode = reader.GetInt32(2),
                    Category = reader.GetInt32(3),
                    Keyword = GetNullableString(reader, 4),
                    AdvanceSearch = reader.GetInt32(5),
                    MinRating = reader.GetInt32(6),
                    PageFrom = reader.GetInt32(7),
                    PageTo = reader.GetInt32(8),
                    Time = reader.GetInt64(9)
                });
            }

            return list;
        }
    }

    public void InsertQuickSearch(QuickSearch quickSearch)
    {
        ArgumentNullException.ThrowIfNull(quickSearch);

        lock (_gate)
        {
            if (quickSearch.Time == 0)
            {
                quickSearch.Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            quickSearch.Id = InsertQuickSearchCore(quickSearch);
        }
    }

    public void TakeOverQuickSearchList(IReadOnlyList<QuickSearch> quickSearchList)
    {
        ArgumentNullException.ThrowIfNull(quickSearchList);

        List<QuickSearch> existing = GetAllQuickSearch();
        var existingKeywords = new HashSet<string?>(existing.Select(static x => x.Keyword));

        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();
            foreach (QuickSearch item in quickSearchList)
            {
                if (existingKeywords.Contains(item.Keyword))
                {
                    continue;
                }

                item.Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                item.Id = InsertQuickSearchCore(item, tx);
                existingKeywords.Add(item.Keyword);
            }

            tx.Commit();
        }
    }

    public void UpdateQuickSearch(QuickSearch quickSearch)
    {
        ArgumentNullException.ThrowIfNull(quickSearch);
        if (quickSearch.Id is null)
        {
            return;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                UPDATE QUICK_SEARCH
                SET NAME = $name, MODE = $mode, CATEGORY = $category, KEYWORD = $keyword,
                    ADVANCE_SEARCH = $advSearch, MIN_RATING = $minRating, PAGE_FROM = $pageFrom,
                    PAGE_TO = $pageTo, TIME = $time
                WHERE _id = $id;
                """;
            BindQuickSearchParameters(cmd, quickSearch);
            cmd.Parameters.AddWithValue("$id", quickSearch.Id.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public void DeleteQuickSearch(QuickSearch quickSearch)
    {
        ArgumentNullException.ThrowIfNull(quickSearch);
        if (quickSearch.Id is null)
        {
            return;
        }

        lock (_gate)
        {
            ExecuteDeleteById("QUICK_SEARCH", quickSearch.Id.Value);
        }
    }

    public void MoveQuickSearch(int fromPosition, int toPosition)
    {
        if (fromPosition == toPosition)
        {
            return;
        }

        lock (_gate)
        {
            MoveOrderedRowsByTimestamp(
                tableName: "QUICK_SEARCH",
                selectColumn: "TIME",
                fromPosition: fromPosition,
                toPosition: toPosition);
        }
    }

    // --- History ---

    public List<HistoryInfo> GetAllHistoryInfo()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = $"""
                SELECT {GalleryInfoSelectColumns}, MODE, TIME
                FROM HISTORY
                ORDER BY TIME DESC;
                """;
            using SqliteDataReader reader = cmd.ExecuteReader();
            var list = new List<HistoryInfo>();
            while (reader.Read())
            {
                var info = new HistoryInfo();
                ReadGalleryInfo(reader, info);
                info.Mode = reader.GetInt32(10);
                info.Time = reader.GetInt64(11);
                list.Add(info);
            }

            return list;
        }
    }

    public void PutHistoryInfo(GalleryInfo galleryInfo)
    {
        ArgumentNullException.ThrowIfNull(galleryInfo);

        lock (_gate)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using SqliteTransaction tx = _connection.BeginTransaction();

            bool exists = RecordExistsByGid("HISTORY", galleryInfo.Gid, tx);
            if (exists)
            {
                using SqliteCommand upd = _connection.CreateCommand();
                upd.Transaction = tx;
                upd.CommandText = "UPDATE HISTORY SET TIME = $time WHERE GID = $gid;";
                upd.Parameters.AddWithValue("$time", now);
                upd.Parameters.AddWithValue("$gid", galleryInfo.Gid);
                upd.ExecuteNonQuery();
            }
            else
            {
                int mode = galleryInfo is HistoryInfo hi ? hi.Mode : 0;
                using SqliteCommand ins = _connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = $"""
                    INSERT INTO HISTORY ({GalleryInfoInsertColumns}, MODE, TIME)
                    VALUES ({GalleryInfoInsertParams}, $mode, $time);
                    """;
                BindGalleryInfo(ins, galleryInfo);
                ins.Parameters.AddWithValue("$mode", mode);
                ins.Parameters.AddWithValue("$time", now);
                ins.ExecuteNonQuery();

                TrimHistoryCore(tx);
            }

            tx.Commit();
        }
    }

    public void PutHistoryInfo(IEnumerable<HistoryInfo> historyInfoList)
    {
        ArgumentNullException.ThrowIfNull(historyInfoList);

        lock (_gate)
        {
            using SqliteTransaction tx = _connection.BeginTransaction();
            foreach (HistoryInfo info in historyInfoList)
            {
                if (RecordExistsByGid("HISTORY", info.Gid, tx))
                {
                    continue;
                }

                using SqliteCommand ins = _connection.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = $"""
                    INSERT INTO HISTORY ({GalleryInfoInsertColumns}, MODE, TIME)
                    VALUES ({GalleryInfoInsertParams}, $mode, $time);
                    """;
                BindGalleryInfo(ins, info);
                ins.Parameters.AddWithValue("$mode", info.Mode);
                ins.Parameters.AddWithValue("$time", info.Time);
                ins.ExecuteNonQuery();
            }

            TrimHistoryCore(tx);
            tx.Commit();
        }
    }

    public void DeleteHistoryInfo(long gid)
    {
        lock (_gate)
        {
            ExecuteDeleteByGid("HISTORY", gid);
        }
    }

    public void ClearHistoryInfo()
    {
        lock (_gate)
        {
            ExecuteNonQuery("DELETE FROM HISTORY;");
        }
    }

    // --- Filter ---

    public List<FilterRule> GetAllFilter()
    {
        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "SELECT _id, MODE, TEXT, ENABLE FROM FILTER;";
            using SqliteDataReader reader = cmd.ExecuteReader();
            var list = new List<FilterRule>();
            while (reader.Read())
            {
                list.Add(new FilterRule
                {
                    Id = reader.GetInt64(0),
                    Mode = reader.GetInt32(1),
                    Text = GetNullableString(reader, 2),
                    Enable = reader.IsDBNull(3) ? null : reader.GetInt64(3) != 0
                });
            }

            return list;
        }
    }

    public void AddFilter(FilterRule filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO FILTER (MODE, TEXT, ENABLE) VALUES ($mode, $text, $enable);
                SELECT last_insert_rowid();
                """;
            cmd.Parameters.AddWithValue("$mode", filter.Mode);
            cmd.Parameters.AddWithValue("$text", (object?)filter.Text ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$enable", filter.Enable is null ? DBNull.Value : (filter.Enable.Value ? 1 : 0));
            filter.Id = Convert.ToInt64(cmd.ExecuteScalar());
        }
    }

    public void DeleteFilter(FilterRule filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Id is null)
        {
            return;
        }

        lock (_gate)
        {
            ExecuteDeleteById("FILTER", filter.Id.Value);
        }
    }

    public void TriggerFilter(FilterRule filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        filter.Enable = !(filter.Enable ?? true);

        if (filter.Id is null)
        {
            return;
        }

        lock (_gate)
        {
            using SqliteCommand cmd = _connection.CreateCommand();
            cmd.CommandText = "UPDATE FILTER SET ENABLE = $enable WHERE _id = $id;";
            cmd.Parameters.AddWithValue("$enable", filter.Enable.Value ? 1 : 0);
            cmd.Parameters.AddWithValue("$id", filter.Id.Value);
            cmd.ExecuteNonQuery();
        }
    }

    // --- Export / Import ---

    public void ExportDatabase(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        lock (_gate)
        {
            using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = destinationPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            destination.Open();
            _connection.BackupDatabase(destination);
        }
    }

    public void ImportDatabase(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        string tempPath = Path.Combine(Path.GetTempPath(), $"eh_import_{Guid.NewGuid():N}.db");
        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);

            using (var checkConn = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = tempPath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString()))
            {
                checkConn.Open();
                int sourceVersion = GetUserVersion(checkConn);
                if (sourceVersion > SchemaVersion)
                {
                    throw new InvalidOperationException($"Unsupported database schema version {sourceVersion} (max supported {SchemaVersion}).");
                }
            }

            using var srcDb = new EhDatabase(tempPath, MaxHistoryCount);

            foreach (DownloadLabel label in srcDb.GetAllDownloadLabels())
            {
                AddDownloadLabel(new DownloadLabel { Label = label.Label, Time = label.Time });
            }

            foreach (DownloadInfo dl in srcDb.GetAllDownloadInfo())
            {
                PutDownloadInfo(dl);
            }

            foreach (DownloadDirname dir in srcDb.GetAllDownloadDirname())
            {
                PutDownloadDirname(dir.Gid, dir.Dirname);
            }

            PutHistoryInfo(srcDb.GetAllHistoryInfo());

            var existingQuickSearches = new HashSet<string?>(GetAllQuickSearch().Select(static x => x.Name));
            foreach (QuickSearch qs in srcDb.GetAllQuickSearch())
            {
                if (qs.Name is null || existingQuickSearches.Add(qs.Name))
                {
                    InsertQuickSearch(new QuickSearch
                    {
                        Name = qs.Name,
                        Mode = qs.Mode,
                        Category = qs.Category,
                        Keyword = qs.Keyword,
                        AdvanceSearch = qs.AdvanceSearch,
                        MinRating = qs.MinRating,
                        PageFrom = qs.PageFrom,
                        PageTo = qs.PageTo,
                        Time = qs.Time
                    });
                }
            }

            PutLocalFavorites(srcDb.GetAllLocalFavorites());

            foreach (BookmarkInfo bookmark in srcDb.GetAllBookmarks())
            {
                PutBookmark(bookmark);
            }

            var existingFilters = GetAllFilter().ToHashSet();
            foreach (FilterRule filter in srcDb.GetAllFilter())
            {
                if (!existingFilters.Contains(filter))
                {
                    AddFilter(new FilterRule
                    {
                        Mode = filter.Mode,
                        Text = filter.Text,
                        Enable = filter.Enable
                    });
                    existingFilters.Add(filter);
                }
            }

            var existingBlacklist = GetAllBlackList().ToHashSet();
            foreach (BlackListEntry entry in srcDb.GetAllBlackList())
            {
                if (!existingBlacklist.Contains(entry))
                {
                    InsertBlackList(new BlackListEntry
                    {
                        BadGayName = entry.BadGayName,
                        Reason = entry.Reason,
                        AngryWith = entry.AngryWith,
                        AddTime = entry.AddTime,
                        Mode = entry.Mode
                    });
                    existingBlacklist.Add(entry);
                }
            }

            foreach (GalleryTagsRecord tags in srcDb.GetAllGalleryTags())
            {
                if (!InGalleryTags(tags.Gid))
                {
                    InsertGalleryTags(tags);
                }
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Best-effort cleanup of temp import copy.
            }
        }
    }

    public int GetSchemaUserVersion()
    {
        lock (_gate)
        {
            return GetUserVersion(_connection);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _connection.Dispose();
        }
    }

    // --- Schema & Migration ---

    private static void EnsureSchemaAndUpgrade(SqliteConnection conn)
    {
        int currentVersion = GetUserVersion(conn);
        if (currentVersion == 0 && !TableExists(conn, "DOWNLOADS"))
        {
            CreateAllTables(conn);
            EnsurePerformanceIndexes(conn);
            SetUserVersion(conn, SchemaVersion);
            return;
        }

        if (currentVersion > 0 && currentVersion < SchemaVersion)
        {
            UpgradeSchema(conn, currentVersion);
            EnsurePerformanceIndexes(conn);
            SetUserVersion(conn, SchemaVersion);
        }
        else
        {
            // Ensure any missing tables on partial schemas are created safely.
            CreateAllTables(conn);
            EnsurePerformanceIndexes(conn);
            if (currentVersion == 0)
            {
                SetUserVersion(conn, SchemaVersion);
            }
        }
    }

    private static void ConfigureConnection(SqliteConnection conn, string databasePath)
    {
        if (string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            ExecuteSql(conn, """
                PRAGMA temp_store = MEMORY;
                PRAGMA cache_size = -8000;
                """);
            return;
        }

        ExecuteSql(conn, """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA cache_size = -8000;
            PRAGMA busy_timeout = 5000;
            """);
    }

    private static void EnsurePerformanceIndexes(SqliteConnection conn)
    {
        ExecuteSql(conn, """
            CREATE INDEX IF NOT EXISTS "IDX_HISTORY_TIME" ON "HISTORY" ("TIME" DESC);
            CREATE INDEX IF NOT EXISTS "IDX_DOWNLOADS_TIME" ON "DOWNLOADS" ("TIME" DESC);
            CREATE INDEX IF NOT EXISTS "IDX_DOWNLOADS_STATE_TIME" ON "DOWNLOADS" ("STATE", "TIME" ASC);
            CREATE INDEX IF NOT EXISTS "IDX_LOCAL_FAVORITES_TIME" ON "LOCAL_FAVORITES" ("TIME" DESC);
            CREATE INDEX IF NOT EXISTS "IDX_BOOKMARKS_TIME" ON "BOOKMARKS" ("TIME" DESC);
            """);
    }

    private static void CreateAllTables(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS "Gallery_Tags" (
                "GID" INTEGER PRIMARY KEY NOT NULL,
                "ROWS" TEXT,
                "ARTIST" TEXT,
                "COSPLAYER" TEXT,
                "CHARACTER" TEXT,
                "FEMALE" TEXT,
                "GROUP" TEXT,
                "LANGUAGE" TEXT,
                "MALE" TEXT,
                "MISC" TEXT,
                "MIXED" TEXT,
                "OTHER" TEXT,
                "PARODY" TEXT,
                "RECLASS" TEXT,
                "CREATE_TIME" INTEGER,
                "UPDATE_TIME" INTEGER,
                "LOCATION" TEXT
            );
            CREATE TABLE IF NOT EXISTS "DOWNLOADS" (
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
                "LABEL" TEXT,
                "ARCHIVE_URI" TEXT
            );
            CREATE TABLE IF NOT EXISTS "DOWNLOAD_LABELS" (
                "_id" INTEGER PRIMARY KEY,
                "LABEL" TEXT,
                "TIME" INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "DOWNLOAD_DIRNAME" (
                "GID" INTEGER PRIMARY KEY NOT NULL,
                "DIRNAME" TEXT
            );
            CREATE TABLE IF NOT EXISTS "HISTORY" (
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
            CREATE TABLE IF NOT EXISTS "QUICK_SEARCH" (
                "_id" INTEGER PRIMARY KEY,
                "NAME" TEXT,
                "MODE" INTEGER NOT NULL,
                "CATEGORY" INTEGER NOT NULL,
                "KEYWORD" TEXT,
                "ADVANCE_SEARCH" INTEGER NOT NULL,
                "MIN_RATING" INTEGER NOT NULL,
                "PAGE_FROM" INTEGER NOT NULL,
                "PAGE_TO" INTEGER NOT NULL,
                "TIME" INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "Black_List" (
                "_id" INTEGER PRIMARY KEY AUTOINCREMENT,
                "BADGAYNAME" TEXT,
                "REASON" TEXT,
                "ANGRYWITH" TEXT,
                "ADD_TIME" TEXT,
                "MODE" INTEGER
            );
            CREATE INDEX IF NOT EXISTS "IDX_Black_List_BADGAYNAME" ON "Black_List" ("BADGAYNAME" ASC);
            CREATE TABLE IF NOT EXISTS "LOCAL_FAVORITES" (
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
            CREATE TABLE IF NOT EXISTS "BOOKMARKS" (
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
            CREATE TABLE IF NOT EXISTS "FILTER" (
                "_id" INTEGER PRIMARY KEY,
                "MODE" INTEGER NOT NULL,
                "TEXT" TEXT,
                "ENABLE" INTEGER
            );
            """;
        cmd.ExecuteNonQuery();
    }

    private static void UpgradeSchema(SqliteConnection conn, int oldVersion)
    {
        if (oldVersion <= 2)
        {
            ExecuteSql(conn, """
                CREATE TABLE "FILTER2" (
                    "_id" INTEGER PRIMARY KEY,
                    "MODE" INTEGER NOT NULL,
                    "TEXT" TEXT,
                    "ENABLE" INTEGER
                );
                INSERT INTO "FILTER2" (_id, MODE, TEXT, ENABLE)
                SELECT _id, MODE, TEXT, 1 FROM "FILTER";
                DROP TABLE "FILTER";
                ALTER TABLE "FILTER2" RENAME TO "FILTER";
                """);
        }

        if (oldVersion <= 3)
        {
            ExecuteSql(conn, """
                CREATE TABLE "QUICK_SEARCH2" (
                    "_id" INTEGER PRIMARY KEY,
                    "NAME" TEXT,
                    "MODE" INTEGER NOT NULL,
                    "CATEGORY" INTEGER NOT NULL,
                    "KEYWORD" TEXT,
                    "ADVANCE_SEARCH" INTEGER NOT NULL,
                    "MIN_RATING" INTEGER NOT NULL,
                    "PAGE_FROM" INTEGER NOT NULL,
                    "PAGE_TO" INTEGER NOT NULL,
                    "TIME" INTEGER NOT NULL
                );
                INSERT INTO "QUICK_SEARCH2" (_id, NAME, MODE, CATEGORY, KEYWORD, ADVANCE_SEARCH, MIN_RATING, PAGE_FROM, PAGE_TO, TIME)
                SELECT _id, NAME, MODE, CATEGORY, KEYWORD, ADVANCE_SEARCH, MIN_RATING, -1, -1, TIME FROM "QUICK_SEARCH";
                DROP TABLE "QUICK_SEARCH";
                ALTER TABLE "QUICK_SEARCH2" RENAME TO "QUICK_SEARCH";
                """);
        }

        if (oldVersion <= 4)
        {
            ExecuteSql(conn, """
                DROP TABLE IF EXISTS "Black_List";
                CREATE TABLE "Black_List" (
                    "_id" INTEGER PRIMARY KEY AUTOINCREMENT,
                    "BADGAYNAME" TEXT,
                    "REASON" TEXT,
                    "ANGRYWITH" TEXT,
                    "ADD_TIME" TEXT,
                    "MODE" INTEGER
                );
                CREATE INDEX IF NOT EXISTS "IDX_Black_List_BADGAYNAME" ON "Black_List" ("BADGAYNAME" ASC);
                """);
        }

        if (oldVersion <= 5)
        {
            ExecuteSql(conn, """
                DROP TABLE IF EXISTS "Gallery_Tags";
                CREATE TABLE "Gallery_Tags" (
                    "GID" INTEGER PRIMARY KEY NOT NULL,
                    "ROWS" TEXT,
                    "ARTIST" TEXT,
                    "COSPLAYER" TEXT,
                    "CHARACTER" TEXT,
                    "FEMALE" TEXT,
                    "GROUP" TEXT,
                    "LANGUAGE" TEXT,
                    "MALE" TEXT,
                    "MISC" TEXT,
                    "MIXED" TEXT,
                    "OTHER" TEXT,
                    "PARODY" TEXT,
                    "RECLASS" TEXT,
                    "CREATE_TIME" INTEGER,
                    "UPDATE_TIME" INTEGER
                );
                """);
        }

        if (oldVersion <= 6 && !ColumnExists(conn, "DOWNLOADS", "ARCHIVE_URI"))
        {
            ExecuteSql(conn, """ALTER TABLE "DOWNLOADS" ADD COLUMN "ARCHIVE_URI" TEXT;""");
        }

        if (oldVersion <= 7 && !ColumnExists(conn, "Gallery_Tags", "LOCATION"))
        {
            ExecuteSql(conn, """ALTER TABLE "Gallery_Tags" ADD COLUMN "LOCATION" TEXT;""");
        }

        CreateAllTables(conn);
    }

    // --- Shared Helpers ---

    private static string BuildConnectionString(string databasePath)
    {
        if (string.Equals(databasePath, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            return "Data Source=:memory:";
        }

        string? dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    private static int GetUserVersion(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static void SetUserVersion(SqliteConnection conn, int version)
    {
        ExecuteSql(conn, $"PRAGMA user_version = {version};");
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        cmd.Parameters.AddWithValue("$name", tableName);
        return cmd.ExecuteScalar() is not null;
    }

    private static bool ColumnExists(SqliteConnection conn, string tableName, string columnName)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ExecuteSql(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void ExecuteNonQuery(string sql)
    {
        ExecuteSql(_connection, sql);
    }

    private static void BindGalleryInfo(SqliteCommand cmd, GalleryInfo info)
    {
        cmd.Parameters.AddWithValue("$gid", info.Gid);
        cmd.Parameters.AddWithValue("$token", (object?)info.Token ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)info.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$titleJpn", (object?)info.TitleJpn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$thumb", (object?)info.Thumb ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$category", info.Category);
        cmd.Parameters.AddWithValue("$posted", (object?)info.Posted ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$uploader", (object?)info.Uploader ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$rating", info.Rating);
        cmd.Parameters.AddWithValue("$simpleLanguage", (object?)info.SimpleLanguage ?? DBNull.Value);
    }

    private static void ReadGalleryInfo(SqliteDataReader reader, GalleryInfo target)
    {
        target.Gid = reader.GetInt64(0);
        target.Token = GetNullableString(reader, 1);
        target.Title = GetNullableString(reader, 2);
        target.TitleJpn = GetNullableString(reader, 3);
        target.Thumb = GetNullableString(reader, 4);
        target.Category = reader.GetInt32(5);
        target.Posted = GetNullableString(reader, 6);
        target.Uploader = GetNullableString(reader, 7);
        target.Rating = Convert.ToSingle(reader.GetDouble(8));
        target.SimpleLanguage = GetNullableString(reader, 9);
    }

    private static GalleryTagsRecord ReadGalleryTagsRow(SqliteDataReader reader)
    {
        return new GalleryTagsRecord
        {
            Gid = reader.GetInt64(0),
            Rows = GetNullableString(reader, 1),
            Artist = GetNullableString(reader, 2),
            Cosplayer = GetNullableString(reader, 3),
            Character = GetNullableString(reader, 4),
            Female = GetNullableString(reader, 5),
            Group = GetNullableString(reader, 6),
            Language = GetNullableString(reader, 7),
            Male = GetNullableString(reader, 8),
            Misc = GetNullableString(reader, 9),
            Mixed = GetNullableString(reader, 10),
            Other = GetNullableString(reader, 11),
            Parody = GetNullableString(reader, 12),
            Reclass = GetNullableString(reader, 13),
            CreateTime = reader.IsDBNull(14) ? null : reader.GetInt64(14),
            UpdateTime = reader.IsDBNull(15) ? null : reader.GetInt64(15),
            Location = GetNullableString(reader, 16)
        };
    }

    private static void BindGalleryTagsParameters(SqliteCommand cmd, GalleryTagsRecord tags)
    {
        cmd.Parameters.AddWithValue("$gid", tags.Gid);
        cmd.Parameters.AddWithValue("$rows", (object?)tags.Rows ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$artist", (object?)tags.Artist ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cosplayer", (object?)tags.Cosplayer ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$character", (object?)tags.Character ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$female", (object?)tags.Female ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$group", (object?)tags.Group ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$language", (object?)tags.Language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$male", (object?)tags.Male ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$misc", (object?)tags.Misc ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mixed", (object?)tags.Mixed ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$other", (object?)tags.Other ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$parody", (object?)tags.Parody ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reclass", (object?)tags.Reclass ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$createTime", (object?)tags.CreateTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$updateTime", (object?)tags.UpdateTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$location", (object?)tags.Location ?? DBNull.Value);
    }

    private static void BindBlackListParameters(SqliteCommand cmd, BlackListEntry entry)
    {
        cmd.Parameters.AddWithValue("$name", (object?)entry.BadGayName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$reason", (object?)entry.Reason ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$angryWith", (object?)entry.AngryWith ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$addTime", (object?)entry.AddTime ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mode", (object?)entry.Mode ?? DBNull.Value);
    }

    private long InsertQuickSearchCore(QuickSearch qs, SqliteTransaction? tx = null)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO QUICK_SEARCH (NAME, MODE, CATEGORY, KEYWORD, ADVANCE_SEARCH, MIN_RATING, PAGE_FROM, PAGE_TO, TIME)
            VALUES ($name, $mode, $category, $keyword, $advSearch, $minRating, $pageFrom, $pageTo, $time);
            SELECT last_insert_rowid();
            """;
        BindQuickSearchParameters(cmd, qs);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    private static void BindQuickSearchParameters(SqliteCommand cmd, QuickSearch qs)
    {
        cmd.Parameters.AddWithValue("$name", (object?)qs.Name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$mode", qs.Mode);
        cmd.Parameters.AddWithValue("$category", qs.Category);
        cmd.Parameters.AddWithValue("$keyword", (object?)qs.Keyword ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$advSearch", qs.AdvanceSearch);
        cmd.Parameters.AddWithValue("$minRating", qs.MinRating);
        cmd.Parameters.AddWithValue("$pageFrom", qs.PageFrom);
        cmd.Parameters.AddWithValue("$pageTo", qs.PageTo);
        cmd.Parameters.AddWithValue("$time", qs.Time);
    }

    private bool RecordExistsByGid(string tableName, long gid, SqliteTransaction? tx = null)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT 1 FROM \"{tableName}\" WHERE GID = $gid LIMIT 1;";
        cmd.Parameters.AddWithValue("$gid", gid);
        return cmd.ExecuteScalar() is not null;
    }

    private void ExecuteDeleteByGid(string tableName, long gid)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM \"{tableName}\" WHERE GID = $gid;";
        cmd.Parameters.AddWithValue("$gid", gid);
        cmd.ExecuteNonQuery();
    }

    private void ExecuteDeleteById(string tableName, long id)
    {
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.CommandText = $"DELETE FROM \"{tableName}\" WHERE _id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private void TrimHistoryCore(SqliteTransaction tx)
    {
        int keepCount = MaxHistoryCount < 1 ? 100 : MaxHistoryCount;
        using SqliteCommand cmd = _connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            DELETE FROM HISTORY
            WHERE GID IN (
                SELECT GID FROM HISTORY
                ORDER BY TIME DESC
                LIMIT -1 OFFSET $keepCount
            );
            """;
        cmd.Parameters.AddWithValue("$keepCount", keepCount);
        cmd.ExecuteNonQuery();
    }

    private void MoveOrderedRowsByTimestamp(string tableName, string selectColumn, int fromPosition, int toPosition)
    {
        bool reverse = fromPosition > toPosition;
        int offset = reverse ? toPosition : fromPosition;
        int limit = Math.Abs(toPosition - fromPosition) + 1;

        var rows = new List<(long Id, long Time)>(limit);
        using (SqliteCommand selectCmd = _connection.CreateCommand())
        {
            selectCmd.CommandText = $"""
                SELECT _id, {selectColumn}
                FROM "{tableName}"
                ORDER BY {selectColumn} ASC
                LIMIT $limit OFFSET $offset;
                """;
            selectCmd.Parameters.AddWithValue("$limit", limit);
            selectCmd.Parameters.AddWithValue("$offset", offset);
            using SqliteDataReader reader = selectCmd.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
            }
        }

        if (rows.Count < 2)
        {
            return;
        }

            ShiftTupleTimestamps(rows, reverse);

        using SqliteTransaction tx = _connection.BeginTransaction();
        using SqliteCommand updateCmd = _connection.CreateCommand();
        updateCmd.Transaction = tx;
        updateCmd.CommandText = $"UPDATE \"{tableName}\" SET {selectColumn} = $time WHERE _id = $id;";
        SqliteParameter pTime = updateCmd.Parameters.Add("$time", SqliteType.Integer);
        SqliteParameter pId = updateCmd.Parameters.Add("$id", SqliteType.Integer);
        foreach ((long id, long time) in rows)
        {
            pTime.Value = time;
            pId.Value = id;
            updateCmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private static void ShiftTimestamps<T>(
        IList<T> slice,
        bool reverse,
        Func<T, long> getTime,
        Action<T, long> setTime)
    {
        int limit = slice.Count;
        int step = reverse ? 1 : -1;
        int start = reverse ? limit - 1 : 0;
        int end = reverse ? 0 : limit - 1;
        long targetTime = getTime(slice[end]);

        for (int i = end; reverse ? i < start : i > start; i += step)
        {
            setTime(slice[i], getTime(slice[i + step]));
        }

        setTime(slice[start], targetTime);
    }

    private static void ShiftTupleTimestamps(List<(long Id, long Time)> slice, bool reverse)
    {
        int limit = slice.Count;
        int step = reverse ? 1 : -1;
        int start = reverse ? limit - 1 : 0;
        int end = reverse ? 0 : limit - 1;
        long targetTime = slice[end].Time;

        for (int i = end; reverse ? i < start : i > start; i += step)
        {
            slice[i] = (slice[i].Id, slice[i + step].Time);
        }

        slice[start] = (slice[start].Id, targetTime);
    }

    private static string? GetNullableString(IDataRecord record, int ordinal)
    {
        return record.IsDBNull(ordinal) ? null : record.GetString(ordinal);
    }

    private static string EscapeLikePattern(string input)
    {
        return input
            .Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
    }
}
