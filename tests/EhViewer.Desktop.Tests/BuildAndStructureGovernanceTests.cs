using Xunit;

namespace EhViewer.Desktop.Tests;

public sealed class BuildAndStructureGovernanceTests : DesktopTestBase
{
    private const int MaxSplitSourceFileLines = 1000;

    [Fact]
    public void RootDirectoryBuildProps_EnablesTreatWarningsAsErrors()
    {
        string rootPropsPath = ResolveRepoFile("Directory.Build.props");

        Assert.True(File.Exists(rootPropsPath), $"Missing {rootPropsPath}");
        string content = File.ReadAllText(rootPropsPath);

        Assert.Contains("<TreatWarningsAsErrors>true</TreatWarningsAsErrors>", content, StringComparison.Ordinal);
        Assert.DoesNotContain("<TreatWarningsAsErrors>false</TreatWarningsAsErrors>", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TestsDirectoryBuildProps_ImportsParentAndDeclaresSharedTestProperties()
    {
        string testsPropsPath = ResolveRepoFile("tests", "Directory.Build.props");

        Assert.True(File.Exists(testsPropsPath), $"Missing {testsPropsPath}");
        string content = File.ReadAllText(testsPropsPath);

        Assert.Contains("GetPathOfFileAbove('Directory.Build.props'", content, StringComparison.Ordinal);
        Assert.Contains("<IsPackable>false</IsPackable>", content, StringComparison.Ordinal);
        Assert.Contains("<IsTestProject>true</IsTestProject>", content, StringComparison.Ordinal);

        string coreTestsCsproj = File.ReadAllText(
            ResolveRepoFile("tests", "EhViewer.Core.Tests", "EhViewer.Core.Tests.csproj"));
        string desktopTestsCsproj = File.ReadAllText(
            ResolveRepoFile("tests", "EhViewer.Desktop.Tests", "EhViewer.Desktop.Tests.csproj"));

        Assert.DoesNotContain("<IsPackable>", coreTestsCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("<IsTestProject>", coreTestsCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("<IsPackable>", desktopTestsCsproj, StringComparison.Ordinal);
        Assert.DoesNotContain("<IsTestProject>", desktopTestsCsproj, StringComparison.Ordinal);
    }

    [Fact]
    public void DownloadManager_XmlCommentMatchesUnboundedChannelWithBoundedWorkerPool()
    {
        string downloadManagerPath = ResolveDesktopSourceFile("Downloads", "DownloadManager.cs");

        Assert.True(File.Exists(downloadManagerPath), $"Missing {downloadManagerPath}");
        string content = File.ReadAllText(downloadManagerPath);

        Assert.DoesNotContain("bounded Channel", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("through a bounded", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unbounded", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bounded concurrent worker pool", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Channel.CreateUnbounded<DownloadTask>", content, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitPartialFiles_AllExistAndStayUnderLineLimit()
    {
        string translationDir = ResolveDesktopSourceFile("Services", "Translation");
        string downloadsDir = ResolveDesktopSourceFile("Downloads");

        string[] mangaTranslationFiles = Directory.GetFiles(translationDir, "MangaTranslationPipeline*.cs");
        string[] ocrPipelineFiles = Directory.GetFiles(translationDir, "ComicBubbleOcrPipeline*.cs");
        string[] downloadManagerFiles = Directory.GetFiles(downloadsDir, "DownloadManager*.cs");

        Assert.True(
            mangaTranslationFiles.Length >= 2,
            $"Expected MangaTranslationPipeline to be split into partial files, found {mangaTranslationFiles.Length}.");
        Assert.True(
            ocrPipelineFiles.Length >= 2,
            $"Expected ComicBubbleOcrPipeline to be split into partial files, found {ocrPipelineFiles.Length}.");
        Assert.True(
            downloadManagerFiles.Length >= 3,
            $"Expected DownloadManager to be split into 3 partial files, found {downloadManagerFiles.Length}.");

        foreach (string file in mangaTranslationFiles.Concat(ocrPipelineFiles).Concat(downloadManagerFiles))
        {
            int lineCount = File.ReadLines(file).Count();
            Assert.True(
                lineCount < MaxSplitSourceFileLines,
                $"Expected {Path.GetFileName(file)} to be < {MaxSplitSourceFileLines} lines, but found {lineCount} lines.");
        }
    }

    [Fact]
    public void TestFiles_DoNotRetainLegacyWaveOrBatchNaming()
    {
        string[] testDirectories =
        [
            ResolveRepoFile("tests", "EhViewer.Core.Tests"),
            ResolveRepoFile("tests", "EhViewer.Desktop.Tests")
        ];

        string[] forbiddenPrefixes = ["Batch", "M2ToM5", "TranslationWave", "Wave"];
        var violations = new List<string>();

        foreach (string dir in testDirectories)
        {
            Assert.True(Directory.Exists(dir), $"Missing test directory {dir}");
            foreach (string file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(file);
                bool hasForbiddenPrefix = forbiddenPrefixes.Any(p =>
                    fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                bool containsBatch1 = fileName.Contains("Batch1", StringComparison.OrdinalIgnoreCase);

                if (hasForbiddenPrefix || containsBatch1)
                {
                    violations.Add(fileName);
                }
            }
        }

        Assert.Empty(violations);
    }
}
