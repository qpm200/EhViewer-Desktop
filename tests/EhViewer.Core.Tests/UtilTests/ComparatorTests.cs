using System.Text.Json;
using EhViewer.Core.Util;
using Xunit;

namespace EhViewer.Core.Tests.UtilTests;

public class ComparatorTests
{
    [Fact]
    public void NaturalComparator_MatchesGoldenVectors()
    {
        string expectedPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected", "unit_vectors.expected.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(expectedPath));
        string[] expected = doc.RootElement
            .GetProperty("comparators")
            .GetProperty("naturalSorted")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

        var input = new List<string>
        {
            "10.jpg", "2.jpg", "1.jpg", "01.jpg", "001.jpg",
            "page 20.png", "page 3.png", "page 03.png",
            "a1b", "a01b", "a2b", "chapter 10", "chapter 2", "B.jpg", "a.jpg"
        };

        input.Sort(NaturalComparator.Instance);
        Assert.Equal(expected, input);
    }

    [Fact]
    public void PathNaturalComparator_MatchesGoldenVectors()
    {
        string expectedPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "expected", "unit_vectors.expected.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(expectedPath));
        string[] expected = doc.RootElement
            .GetProperty("comparators")
            .GetProperty("pathNaturalSorted")
            .EnumerateArray()
            .Select(e => e.GetString()!)
            .ToArray();

        var input = new List<string>
        {
            "vol2/01.jpg", "vol10/01.jpg", "vol1/10.jpg", "vol1/2.jpg", "vol01/1.jpg"
        };

        input.Sort(PathNaturalComparator.Instance);
        Assert.Equal(expected, input);
    }

    [Fact]
    public void Comparators_HandleNullsSeparatorsAndCaseFolding()
    {
        var nc = NaturalComparator.Instance;
        Assert.Equal(0, nc.Compare(null, null));
        Assert.True(nc.Compare(null, "a") < 0);
        Assert.True(nc.Compare("a", null) > 0);
        Assert.True(nc.Compare("_a", "A") < 0); // '_' (95) < 'a' (97) matching JVM toLower(toUpper(c))

        var pc = PathNaturalComparator.Instance;
        Assert.Equal(0, pc.Compare("/a//b\\c", "a\\b/c"));
        Assert.Equal(0, pc.Compare("/", "\\\\"));
        Assert.True(pc.Compare(null, "") < 0);
        Assert.Equal(0, pc.Compare("", "/"));
    }
}
