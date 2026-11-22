using EhViewer.Core.Client.Data;
using EhViewer.Core.Util;
using EhViewer.Desktop.Downloads;
using Xunit;

namespace EhViewer.Desktop.Tests;

/// <summary>Guards the translation of <c>util/GZIPUtils.java</c> (gzip + Base64 text round-trip).</summary>
public sealed class GzipUtilsTests
{
    [Fact]
    public void CompressThenUncompress_RoundTripsUtf8()
    {
        const string original = "标签 · テスト · 中文 · https://e-hentai.org/g/1/abc/";

        string? packed = GzipUtils.Compress(original);

        Assert.NotNull(packed);
        Assert.NotEqual(original, packed);
        Assert.Equal(original, GzipUtils.Uncompress(packed));
    }

    [Fact]
    public void CompressThenUncompress_RoundTripsJson()
    {
        const string json = """{"gid":123,"token":"abcdef","title":"A \"quoted\" title"}""";

        Assert.Equal(json, GzipUtils.Uncompress(GzipUtils.Compress(json)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Compress_PassesThroughEmptyInput(string? input)
    {
        Assert.Equal(input, GzipUtils.Compress(input));
    }

    [Fact]
    public void Uncompress_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, GzipUtils.Uncompress(string.Empty));
    }

    [Fact]
    public void Uncompress_Null_ReturnsNull()
    {
        Assert.Null(GzipUtils.Uncompress(null));
    }

    [Theory]
    [InlineData("not base64 !!!")]
    [InlineData("YWJjZGVm")]  // valid Base64, but not a gzip stream
    public void Uncompress_InvalidPayload_ReturnsNull(string input)
    {
        Assert.Null(GzipUtils.Uncompress(input));
    }
}

/// <summary>Guards the translation of <c>util/ReadingRefreshRate.java</c>.</summary>
public sealed class ReadingRefreshRateTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, -1)]
    [InlineData(60, 60)]
    [InlineData(90, 90)]
    [InlineData(120, 120)]
    public void Sanitize_KeepsAllowedValues(int input, int expected)
    {
        Assert.Equal(expected, ReadingRefreshRate.Sanitize(input));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(144)]
    [InlineData(-5)]
    public void Sanitize_FallsBackToSystemDefault(int input)
    {
        Assert.Equal(ReadingRefreshRate.SystemDefault, ReadingRefreshRate.Sanitize(input));
    }

    [Fact]
    public void SystemDefault_ResolvesToZero()
    {
        Assert.Equal(0f, ReadingRefreshRate.ResolveHz(StandardModes, Current, ReadingRefreshRate.SystemDefault));
    }

    [Fact]
    public void ExplicitHz_PicksHighestModeUnderTarget()
    {
        DisplayModeInfo? mode = ReadingRefreshRate.FindBestMode(StandardModes, Current, 90);

        Assert.NotNull(mode);
        Assert.Equal(60f, mode!.Value.RefreshRate);
    }

    [Fact]
    public void DisplayMax_PicksHighestMode()
    {
        DisplayModeInfo? mode = ReadingRefreshRate.FindBestMode(StandardModes, Current, ReadingRefreshRate.DisplayMax);

        Assert.NotNull(mode);
        Assert.Equal(120f, mode!.Value.RefreshRate);
    }

    [Fact]
    public void SameResolution_IsPreferredOverOtherResolutions()
    {
        // A 4K@144 mode exists, but the current display is 1080p@120: the same-resolution mode wins.
        DisplayModeInfo[] modes =
        [
            new(3840, 2160, 144f),
            new(1920, 1080, 60f),
            new(1920, 1080, 120f)
        ];

        DisplayModeInfo? mode = ReadingRefreshRate.FindBestMode(modes, Current, 120);

        Assert.NotNull(mode);
        Assert.Equal(1920, mode!.Value.Width);
        Assert.Equal(120f, mode.Value.RefreshRate);
    }

    [Fact]
    public void NoModes_ReturnsNull()
    {
        Assert.Null(ReadingRefreshRate.FindBestMode([], Current, 60));
        Assert.Null(ReadingRefreshRate.FindBestMode(null, Current, 60));
    }

    private static DisplayModeInfo Current => new(1920, 1080, 60f);

    private static readonly DisplayModeInfo[] StandardModes =
    [
        new(1920, 1080, 60f),
        new(1920, 1080, 120f),
        new(2560, 1440, 144f)
    ];
}

/// <summary>Guards the translation of <c>download/DownloadGalleryMetaHelper.java</c>.</summary>
public sealed class DownloadGalleryMetaHelperTests
{
    [Fact]
    public void ExistingFolder_ReportsItsOwnTimestamp()
    {
        string root = CreateTempRoot();
        try
        {
            var info = new GalleryInfo { Gid = 42, Title = "Test Gallery" };
            string folder = DownloadManager.BuildGalleryFolder(root, info);
            Directory.CreateDirectory(folder);

            long timestamp = DownloadGalleryMetaHelper.GetGalleryDirectoryTimestamp(root, info, taskTimeMillis: 1);

            Assert.True(timestamp > 1);
            long folderCreation = new DateTimeOffset(
                Directory.GetCreationTimeUtc(folder), TimeSpan.Zero).ToUnixTimeMilliseconds();
            Assert.Equal(folderCreation, timestamp);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void MissingFolder_FallsBackToTaskTime()
    {
        string root = CreateTempRoot();
        try
        {
            var info = new GalleryInfo { Gid = 7, Title = "Missing" };

            long timestamp = DownloadGalleryMetaHelper.GetGalleryDirectoryTimestamp(root, info, taskTimeMillis: 123456);

            Assert.Equal(123456, timestamp);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void NegativeTaskTime_IsClampedToZero()
    {
        string root = CreateTempRoot();
        try
        {
            var info = new GalleryInfo { Gid = 8, Title = "Clamp" };

            Assert.Equal(0, DownloadGalleryMetaHelper.GetGalleryDirectoryTimestamp(root, info, taskTimeMillis: -50));
        }
        finally
        {
            Cleanup(root);
        }
    }

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "EhViewerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Cleanup(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
