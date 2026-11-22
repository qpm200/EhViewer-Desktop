using System.Text;

namespace EhViewer.Core.Network;

/// <summary>
/// URL query builder preserving upstream <c>UrlBuilder</c> semantics.
/// Uses ordered insertion rather than Java <c>HashMap</c> iteration order for deterministic URLs.
/// </summary>
public sealed class UrlBuilder
{
    private string _rootUrl;
    private readonly Dictionary<string, string?> _queryMap = new(StringComparer.Ordinal);

    public UrlBuilder(string rootUrl)
    {
        _rootUrl = rootUrl ?? throw new ArgumentNullException(nameof(rootUrl));
    }

    public void SetRootUrl(string rootUrl)
    {
        _rootUrl = rootUrl ?? throw new ArgumentNullException(nameof(rootUrl));
    }

    public UrlBuilder AddQuery(string key, object? value)
    {
        _queryMap[key] = value?.ToString();
        return this;
    }

    public void RemoveQuery(string key)
    {
        _queryMap.Remove(key);
    }

    public IReadOnlyDictionary<string, string?> Queries => _queryMap;

    public string Build()
    {
        if (_queryMap.Count == 0)
        {
            return _rootUrl;
        }

        var sb = new StringBuilder(_rootUrl);
        if (!_rootUrl.Contains('?', StringComparison.Ordinal))
        {
            sb.Append('?');
        }
        else if (!_rootUrl.EndsWith('?') && !_rootUrl.EndsWith('&'))
        {
            sb.Append('&');
        }

        bool first = true;
        foreach ((string key, string? value) in _queryMap)
        {
            if (!first)
            {
                sb.Append('&');
            }
            first = false;
            sb.Append(key);
            if (value != null)
            {
                sb.Append('=').Append(value);
            }
        }

        return sb.ToString();
    }
}
