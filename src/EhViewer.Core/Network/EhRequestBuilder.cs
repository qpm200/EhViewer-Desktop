namespace EhViewer.Core.Network;

/// <summary>
/// Constructs Chrome-compatible HTTP requests with E-Hentai headers, matching upstream
/// <c>ChromeRequestBuilder</c> and <c>EhRequestBuilder</c>.
/// </summary>
public static class EhRequestBuilder
{
    public const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/118.0.0.0 Safari/537.36";

    public const string ChromeAccept =
        "image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8";

    public const string ChromeAcceptLanguage =
        "zh-CN,zh;q=0.9,en-US;q=0.8,en;q=0.7";

    public static HttpRequestMessage Create(
        HttpMethod method,
        string url,
        string? referer = null,
        string? origin = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null,
        HttpContent? content = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);

        var uri = new Uri(url, UriKind.Absolute);
        var request = new HttpRequestMessage(method, uri)
        {
            Content = content
        };

        request.Headers.TryAddWithoutValidation("Host", uri.Authority);
        request.Headers.TryAddWithoutValidation("User-Agent", ChromeUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", ChromeAccept);
        request.Headers.TryAddWithoutValidation("Accept-Language", ChromeAcceptLanguage);

        if (!string.IsNullOrEmpty(referer))
        {
            request.Headers.TryAddWithoutValidation("Referer", referer);
        }

        if (!string.IsNullOrEmpty(origin))
        {
            request.Headers.TryAddWithoutValidation("Origin", origin);
        }

        if (extraHeaders is not null)
        {
            foreach ((string key, string value) in extraHeaders)
            {
                request.Headers.Remove(key);
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        return request;
    }

    public static HttpRequestMessage Get(
        string url,
        string? referer = null,
        string? origin = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        return Create(HttpMethod.Get, url, referer, origin, extraHeaders);
    }

    public static HttpRequestMessage Post(
        string url,
        HttpContent content,
        string? referer = null,
        string? origin = null,
        IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        return Create(HttpMethod.Post, url, referer, origin, extraHeaders, content);
    }
}
