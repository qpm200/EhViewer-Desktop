using EhViewer.Core.Client;
using EhViewer.Core.Network;
using Microsoft.Extensions.Logging;

namespace EhViewer.Desktop.Services;

/// <summary>One tag suggestion pairing a localized dropdown label with an E-Hentai search token.</summary>
public sealed record TagSuggestionItem(string Display, string Query, string Chinese = "")
{
    public override string ToString() => Display;
}

/// <summary>
/// Owns the optional tag-translation database. Loading is lazy and failure-tolerant: a missing or
/// unreachable translation file leaves <see cref="Current"/> null and every caller degrades to
/// showing the original English tag.
/// </summary>
public sealed class TagTranslationSource
{
    private readonly AppPaths _paths;
    private readonly Func<HttpClient> _getHttpClient;
    private readonly ILogger<TagTranslationSource> _logger;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);

    private EhTagDatabase? _current;
    private bool _initialized;

    public TagTranslationSource(AppPaths paths, EhHttpClientFactory clientFactory, ILogger<TagTranslationSource> logger)
        : this(
            paths,
            () => (clientFactory ?? throw new ArgumentNullException(nameof(clientFactory))).StandardClient,
            logger)
    {
    }

    internal TagTranslationSource(AppPaths paths, Func<HttpClient> getHttpClient, ILogger<TagTranslationSource> logger)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _getHttpClient = getHttpClient ?? throw new ArgumentNullException(nameof(getHttpClient));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>The loaded database, or <see langword="null"/> when no translation file is available.</summary>
    public EhTagDatabase? Current => Volatile.Read(ref _current);

    /// <summary>True once a load attempt has completed (successfully or not).</summary>
    public bool IsInitialized => Volatile.Read(ref _initialized);

    /// <summary>
    /// Loads the local translation file and refreshes it when the upstream SHA-1 differs. Safe to
    /// call repeatedly; only the first caller performs the work and every caller may cancel its own
    /// wait without abandoning the shared load.
    /// </summary>
    public async Task<EhTagDatabase?> EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (IsInitialized)
        {
            return Current;
        }

        Task loadTask = LoadOnceAsync();
        try
        {
            await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The shared load continues; this caller just stops waiting.
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "标签翻译库加载失败");
        }

        return Current;
    }

    private async Task LoadOnceAsync()
    {
        await _initializeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            try
            {
                EhTagDatabase? loaded = await EhTagDatabase
                    .LoadAndUpdateAsync(_paths.TagTranslationDirectory, _getHttpClient())
                    .ConfigureAwait(false);

                if (loaded is not null)
                {
                    Volatile.Write(ref _current, loaded);
                }
            }
            catch (Exception ex)
            {
                // No network access at all, or a truncated file: keep whatever loaded earlier.
                _logger.LogDebug(ex, "标签翻译库更新失败");
            }

            Volatile.Write(ref _initialized, true);
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    /// <summary>Tag suggestions for the search box; empty when no database is loaded.</summary>
    public IReadOnlyList<string> Suggest(string? keyword, int maxResults = 20)
        => SuggestItems(keyword, maxResults).Select(static item => item.Display).ToList();

    /// <summary>
    /// Returns structured tag suggestions with human-readable display strings and E-Hentai search
    /// syntax tokens (<c>namespace:"tag name"$</c>).
    /// </summary>
    public IReadOnlyList<TagSuggestionItem> SuggestItems(string? keyword, int maxResults = 20)
    {
        EhTagDatabase? database = Current;
        if (database is null || string.IsNullOrWhiteSpace(keyword) || maxResults <= 0)
        {
            return [];
        }

        List<TagTranslationSuggestion> matches;
        try
        {
            matches = database.Suggest(keyword, maxResults);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "标签建议失败");
            return [];
        }

        var results = new List<TagSuggestionItem>(matches.Count);
        foreach (TagTranslationSuggestion suggestion in matches)
        {
            string query = FormatTagQuery(suggestion.English);
            if (string.IsNullOrWhiteSpace(query))
            {
                continue;
            }

            string display = string.IsNullOrEmpty(suggestion.Chinese)
                ? suggestion.English
                : $"{suggestion.Chinese}$ {suggestion.English}";

            results.Add(new TagSuggestionItem(display.Trim(), query, suggestion.Chinese ?? string.Empty));
        }

        return results;
    }

    /// <summary>Formats a raw <c>namespace:tag</c> string as an exact E-Hentai tag query.</summary>
    internal static string FormatTagQuery(string? rawTag)
    {
        if (string.IsNullOrWhiteSpace(rawTag))
        {
            return string.Empty;
        }

        string trimmed = rawTag.Trim();
        int colon = trimmed.IndexOf(':');
        if (colon < 0)
        {
            return QuoteExactTag(trimmed);
        }

        string ns = trimmed[..colon].Trim();
        string tag = trimmed[(colon + 1)..].Trim();
        if (string.IsNullOrEmpty(tag))
        {
            return QuoteExactTag(trimmed);
        }

        if (string.IsNullOrEmpty(ns) || string.Equals(ns, "misc", StringComparison.OrdinalIgnoreCase))
        {
            return QuoteExactTag(tag);
        }

        string quoted = tag.Contains(' ', StringComparison.Ordinal) ? $"\"{tag}\"" : tag;
        return $"{ns}:{quoted}$";
    }

    private static string QuoteExactTag(string tag)
        => tag.Contains(' ', StringComparison.Ordinal) ? $"\"{tag}\"$" : $"{tag}$";
}
