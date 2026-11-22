using System.Text.RegularExpressions;
using EhViewer.Core.Client.Data;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses gallery torrent links and timestamps from <c>gallerytorrents.php</c>.
/// </summary>
public static partial class TorrentParser
{
    private static readonly Regex PatternTorrentBlock = CreateTorrentBlockPattern();
    private static readonly Regex PatternPosted = CreatePostedPattern();
    private static readonly Regex PatternTorrent = CreateTorrentPattern();

    public static TorrentInfo[] Parse(string body)
    {
        ArgumentNullException.ThrowIfNull(body);

        var torrentList = new List<TorrentInfo>();
        foreach (Match blockMatch in PatternTorrentBlock.Matches(body))
        {
            string block = blockMatch.Value;
            Match torrentMatcher = PatternTorrent.Match(block);
            if (!torrentMatcher.Success)
            {
                continue;
            }

            // Strip ?p= so torrent URL remains redistributable without personal passkey.
            string url = ParserUtils.Trim(torrentMatcher.Groups[1].Value);
            int queryPasskeyIndex = url.IndexOf("?p=", StringComparison.Ordinal);
            if (queryPasskeyIndex >= 0)
            {
                url = url[..queryPasskeyIndex];
            }

            string name = ParserUtils.Trim(torrentMatcher.Groups[2].Value);
            string posted = string.Empty;
            Match postedMatcher = PatternPosted.Match(block);
            if (postedMatcher.Success)
            {
                posted = ParserUtils.Trim(postedMatcher.Groups[1].Value);
            }

            torrentList.Add(new TorrentInfo(url, name, posted));
        }

        return [.. torrentList];
    }

    [GeneratedRegex(@"<form\b[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTorrentBlockPattern();

    [GeneratedRegex(@"<span[^>]*>\s*Posted:\s*</span>\s*<span>([^<]+)</span>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CreatePostedPattern();

    [GeneratedRegex(@"<td colspan=""5"">\s*&nbsp;\s*<a href=""([^""]+)""[^<]*>([^<]+)</a></td>", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTorrentPattern();
}
