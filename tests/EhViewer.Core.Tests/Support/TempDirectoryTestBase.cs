namespace EhViewer.Core.Tests;

public abstract class TempDirectoryTestBase : IDisposable
{
    private readonly List<string> _tempDirs = new();

    protected string CreateTempDirectory(string prefix = "ehviewer-core-test")
    {
        string path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        _tempDirs.Add(path);
        return path;
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
