using EhViewer.Desktop.Services;
using EhViewer.Desktop.ViewModels;
using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class AppCompositionTests : DesktopTestBase
{
    [Fact]
    public void AppServices_Create_ResolvesCoreDesktopGraph()
    {
        AppPaths paths = CreateTempAppPaths("eh-app-composition", ensureCreated: false);
        using (AppServices services = AppServices.Create(paths))
        {
            Assert.NotNull(services.GetRequiredService<ShellViewModel>());
            Assert.NotNull(services.GetRequiredService<GalleryBrowseService>());
            Assert.NotNull(services.GetRequiredService<ThumbnailService>());
            Assert.NotNull(services.GetRequiredService<EhViewer.Desktop.Reader.ReaderEngine>());
            Assert.NotNull(services.GetRequiredService<EhViewer.Desktop.Downloads.DownloadManager>());
            Assert.NotNull(services.GetRequiredService<EhViewer.Desktop.Downloads.LocalLibraryScanner>());
            Assert.True(Directory.Exists(paths.DownloadDirectory));
            Assert.True(Directory.Exists(paths.ThumbnailCacheDirectory));
        }

        var locked = new List<string>();
        foreach (string db in new[] { paths.EhDatabasePath, paths.HostsDatabasePath, paths.CookieDatabasePath })
        {
            try
            {
                File.Delete(db);
            }
            catch (Exception ex)
            {
                locked.Add($"{Path.GetFileName(db)}: {ex.Message}");
            }
        }

        Assert.Empty(locked);
    }

    [Fact]
    public void SqliteCookieStore_StandaloneDispose_ReleasesFileHandle()
    {
        string tempRoot = CreateTempDirectory("eh-cookie-dispose");
        string dbPath = Path.Combine(tempRoot, "standalone_cookie.db");
        using (var store = new EhViewer.Core.Network.SqliteCookieStore(dbPath))
        {
        }

        File.Delete(dbPath);
    }
}
