using EhViewer.Desktop.Services;

namespace EhViewer.Desktop.Tests;

public abstract class DesktopTestBase : IDisposable
{
    private readonly List<string> _tempDirs = new();

    protected string CreateTempDirectory(string prefix = "ehviewer-test")
    {
        string path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
    }

    protected AppPaths CreateTempAppPaths(string prefix = "ehviewer-test", bool ensureCreated = true)
    {
        string root = CreateTempDirectory(prefix);
        AppPaths paths = AppPaths.CreateAt(root);
        if (ensureCreated)
        {
            paths.EnsureCreated();
        }

        return paths;
    }

    protected static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "EhViewer.Desktop.slnx")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Unable to locate EhViewer.Desktop.slnx from AppContext.BaseDirectory.");
    }

    protected static string ResolveRepoFile(params string[] relativeSegments)
    {
        string[] all = new string[relativeSegments.Length + 1];
        all[0] = FindRepoRoot();
        Array.Copy(relativeSegments, 0, all, 1, relativeSegments.Length);
        return Path.Combine(all);
    }

    protected static string ResolveDesktopSourceFile(params string[] relativeSegments)
    {
        string[] all = new string[relativeSegments.Length + 3];
        all[0] = FindRepoRoot();
        all[1] = "src";
        all[2] = "EhViewer.Desktop";
        Array.Copy(relativeSegments, 0, all, 3, relativeSegments.Length);
        return Path.Combine(all);
    }

    protected virtual void DisposeCore()
    {
    }

    public void Dispose()
    {
        try
        {
            DisposeCore();
        }
        finally
        {
            for (int i = _tempDirs.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (Directory.Exists(_tempDirs[i]))
                    {
                        Directory.Delete(_tempDirs[i], recursive: true);
                    }
                }
                catch
                {
                    // Best-effort cleanup so a locked handle on Windows does not mask test assertions.
                }
            }

            _tempDirs.Clear();
        }

        GC.SuppressFinalize(this);
    }
}
