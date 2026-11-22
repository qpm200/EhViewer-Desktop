using System.Globalization;
using System.Net;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Util;

namespace EhViewer.Core.Client.Parser;

/// <summary>
/// Parses E-Hentai/ExHentai gallery list/search/uploader/tag URLs into a <see cref="ListUrlBuilder"/>.
/// </summary>
public static class GalleryListUrlParser
{
    private static readonly string[] ValidHosts = [EhUrl.DomainEx, EhUrl.DomainE, EhUrl.DomainLofi];

    private const string PathNormal = "/";
    private const string PathUploader = "/uploader/";
    private const string PathTag = "/tag/";

    public static ListUrlBuilder? Parse(string? urlStr)
    {
        if (!Uri.TryCreate(urlStr, UriKind.Absolute, out Uri? url) ||
            (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        if (!Utilities.Contains(ValidHosts, url.Host))
        {
            return null;
        }

        string path = url.AbsolutePath;
        if (string.IsNullOrEmpty(path) || path == PathNormal)
        {
            var builder = new ListUrlBuilder();
            builder.SetQuery(GetRawQuery(url));
            return builder;
        }

        if (path.StartsWith(PathUploader, StringComparison.Ordinal))
        {
            return ParsePathSegment(path, PathUploader, ListUrlBuilder.ModeUploader);
        }

        if (path.StartsWith(PathTag, StringComparison.Ordinal))
        {
            return ParsePathSegment(path, PathTag, ListUrlBuilder.ModeTag);
        }

        if (path.StartsWith("/", StringComparison.Ordinal))
        {
            if (!int.TryParse(path.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int category))
            {
                return null;
            }

            var builder = new ListUrlBuilder();
            builder.SetQuery(GetRawQuery(url));
            builder.Category = category;
            return builder;
        }

        return null;
    }

    private static string GetRawQuery(Uri url)
    {
        string query = url.Query;
        return query.StartsWith('?') ? query[1..] : query;
    }

    private static ListUrlBuilder? ParsePathSegment(string path, string prefix, int mode)
    {
        int prefixLength = prefix.Length;
        int slashIndex = path.IndexOf('/', prefixLength);
        string raw = slashIndex < 0 ? path[prefixLength..] : path[prefixLength..slashIndex];
        string decoded = WebUtility.UrlDecode(raw);
        if (string.IsNullOrEmpty(decoded))
        {
            return null;
        }

        return new ListUrlBuilder
        {
            Mode = mode,
            Keyword = decoded
        };
    }
}
