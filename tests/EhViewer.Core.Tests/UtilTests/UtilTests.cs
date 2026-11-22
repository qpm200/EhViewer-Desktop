using System.Text;
using EhViewer.Core.Network;
using EhViewer.Core.Util;
using Xunit;

namespace EhViewer.Core.Tests.UtilTests;

public class UtilTests
{
    [Fact]
    public void Architecture_CoreAssemblyHasNoUiReferences()
    {
        var coreAssembly = typeof(StringUtils).Assembly;
        var forbiddenPrefixes = new[]
        {
            "Microsoft.UI",
            "Microsoft.Windows.",
            "Microsoft.Graphics.",
            "System.Windows",
            "System.Drawing",
            "WinRT",
            "PresentationFramework",
            "PresentationCore",
            "WindowsBase"
        };

        var referenced = coreAssembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();
        foreach (string prefix in forbiddenPrefixes)
        {
            Assert.DoesNotContain(referenced, name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void StringUtils_UnescapeXml_SinglePassNonRepeating()
    {
        Assert.Null(StringUtils.UnescapeXml(null));
        Assert.Equal("&<>\"'× ", StringUtils.UnescapeXml("&amp;&lt;&gt;&quot;&#039;&times;&nbsp;"));
        Assert.Equal("&lt;", StringUtils.UnescapeXml("&amp;lt;"));
    }

    [Fact]
    public void StringUtils_SplitAndTrim_MatchUpstream()
    {
        Assert.Null(StringUtils.Split(null, ','));
        Assert.Empty(StringUtils.Split(string.Empty, ',')!);
        Assert.Equal(["a", "b", "c"], StringUtils.Split(",,a,,b,c,", ',')!);
        Assert.Equal("abc", StringUtils.Trim("\u3000\u00A0 \t abc \u3000"));
        Assert.True(StringUtils.Equals(null, string.Empty));
        Assert.Equal(5, StringUtils.OrdinalIndexOf("a-b-c-d", "-", 3));
    }

    [Fact]
    public void FileUtils_SanitizeFilename_TruncatesUtf8Safely()
    {
        Assert.Equal("hello world", FileUtils.SanitizeFilename("  hello:world?  ".Replace(":world?", " world")));
        Assert.Equal("abc", FileUtils.SanitizeFilename("a\\b/c:*?\"<>|"));

        // 85 * 3 = 255 bytes in UTF-8 ('中' is 3 bytes)
        string eightyFiveChinese = new string('中', 85);
        Assert.Equal(85, FileUtils.SanitizeFilename(eightyFiveChinese + "a").Length);

        // Surrogate pair (4 bytes UTF-8) near 255-byte boundary shouldn't split high/low surrogate
        string prefix253 = new string('a', 253);
        string withEmoji = prefix253 + "\U0001F600"; // 253 + 4 = 257 > 255
        string sanitized = FileUtils.SanitizeFilename(withEmoji);
        Assert.Equal(253, sanitized.Length);
    }

    [Fact]
    public void FileUtils_SanitizeWindowsName_HandlesReservedNamesAndDots()
    {
        Assert.Equal("_CON", FileUtils.SanitizeWindowsName("CON"));
        Assert.Equal("_con.txt", FileUtils.SanitizeWindowsName("con.txt"));
        Assert.Equal("folder", FileUtils.SanitizeWindowsName("folder...   "));
        Assert.Equal("_", FileUtils.SanitizeWindowsName("..."));
    }

    [Fact]
    public void IoUtils_ReadAsciiLine_ReadsCrLfAndThrowsOnEofOrLimit()
    {
        byte[] data = Encoding.ASCII.GetBytes("VERSION2\r\n00000000\n");
        using var ms = new MemoryStream(data);
        Assert.Equal("VERSION2", IoUtils.ReadAsciiLine(ms));
        Assert.Equal("00000000", IoUtils.ReadAsciiLine(ms));
        Assert.Throws<EndOfStreamException>(() => IoUtils.ReadAsciiLine(ms));

        using var tooLong = new MemoryStream(Encoding.ASCII.GetBytes("abcdef\n"));
        Assert.Throws<IOException>(() => IoUtils.ReadAsciiLine(tooLong, maxLength: 3));
    }

    [Fact]
    public void UrlBuilder_AppendsQueriesProperly()
    {
        var ub1 = new UrlBuilder("https://e-hentai.org/");
        Assert.Equal("https://e-hentai.org/", ub1.Build());
        ub1.AddQuery("page", 2).AddQuery("f_search", "test");
        Assert.Equal("https://e-hentai.org/?page=2&f_search=test", ub1.Build());

        var ub2 = new UrlBuilder("https://e-hentai.org/?f_cats=1");
        ub2.AddQuery("page", 0);
        Assert.Equal("https://e-hentai.org/?f_cats=1&page=0", ub2.Build());
    }

    [Fact]
    public void InetValidator_ValidatesIpv4HostsAndPorts()
    {
        Assert.True(InetValidator.IsValidIpv4("127.0.0.1"));
        Assert.False(InetValidator.IsValidIpv4("999.0.0.1"));
        Assert.True(InetValidator.IsValidInetAddress("e-hentai.org"));
        Assert.True(InetValidator.IsValidInetPort(8080));
        Assert.False(InetValidator.IsValidInetPort(70000));
    }
}
