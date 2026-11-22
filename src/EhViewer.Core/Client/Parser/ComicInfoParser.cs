using System.Text;
using System.Text.RegularExpressions;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses gallery gid and token from the <c>&lt;Web&gt;</c> element of <c>ComicInfo.xml</c>.
/// </summary>
public static partial class ComicInfoParser
{
    public const string Filename = "ComicInfo.xml";

    private const int MaxBytes = 256 * 1024;

    private static readonly Regex WebPattern = CreateWebPattern();

    public static GalleryDetailUrlParser.Result? Parse(string? xml)
    {
        if (string.IsNullOrEmpty(xml))
        {
            return null;
        }

        Match matcher = WebPattern.Match(xml);
        if (!matcher.Success)
        {
            return null;
        }

        return GalleryDetailUrlParser.Parse(matcher.Groups[1].Value.Trim(), strict: true);
    }

    public static GalleryDetailUrlParser.Result? Parse(Stream? input)
    {
        if (input is null)
        {
            return null;
        }

        try
        {
            using var output = new MemoryStream();
            byte[] buffer = new byte[4096];
            int total = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total += read;
                if (total > MaxBytes)
                {
                    return null;
                }
                output.Write(buffer, 0, read);
            }

            string xml = Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
            return Parse(xml);
        }
        catch (IOException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<Web>\s*([^<]+?)\s*</Web>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateWebPattern();
}
