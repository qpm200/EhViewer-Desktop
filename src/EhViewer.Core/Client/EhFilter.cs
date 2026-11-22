using EhViewer.Core.Client.Data;
using EhViewer.Core.Storage;

namespace EhViewer.Core.Client;

/// <summary>
/// Manages local title, uploader, tag, and tag-namespace exclusion rules backed by <see cref="EhDatabase"/>.
/// Boolean return convention matches upstream <c>EhFilter</c>:
/// <see langword="true"/> means the gallery passes the filter (keep);
/// <see langword="false"/> means it is filtered out (hide).
/// </summary>
public sealed class EhFilter
{
    public const int ModeTitle = 0;
    public const int ModeUploader = 1;
    public const int ModeTag = 2;
    public const int ModeTagNamespace = 3;

    private readonly EhDatabase _database;
    private readonly object _gate = new();

    private readonly List<FilterRule> _titleFilters = [];
    private readonly List<FilterRule> _uploaderFilters = [];
    private readonly List<FilterRule> _tagFilters = [];
    private readonly List<FilterRule> _tagNamespaceFilters = [];

    public EhFilter(EhDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));

        foreach (FilterRule filter in _database.GetAllFilter())
        {
            AddToBucket(filter);
        }
    }

    public IReadOnlyList<FilterRule> TitleFilters => _titleFilters;
    public IReadOnlyList<FilterRule> UploaderFilters => _uploaderFilters;
    public IReadOnlyList<FilterRule> TagFilters => _tagFilters;
    public IReadOnlyList<FilterRule> TagNamespaceFilters => _tagNamespaceFilters;

    public bool NeedTags
    {
        get
        {
            lock (_gate)
            {
                return _tagFilters.Count > 0 || _tagNamespaceFilters.Count > 0;
            }
        }
    }

    public void AddFilter(FilterRule filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        lock (_gate)
        {
            filter.Enable = true;
            _database.AddFilter(filter);
            AddToBucket(filter);
        }
    }

    public void TriggerFilter(FilterRule filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        lock (_gate)
        {
            _database.TriggerFilter(filter);
        }
    }

    public void DeleteFilter(FilterRule filter)
    {
        ArgumentNullException.ThrowIfNull(filter);

        lock (_gate)
        {
            _database.DeleteFilter(filter);
            switch (filter.Mode)
            {
                case ModeTitle:
                    _titleFilters.Remove(filter);
                    break;
                case ModeUploader:
                    _uploaderFilters.Remove(filter);
                    break;
                case ModeTag:
                    _tagFilters.Remove(filter);
                    break;
                case ModeTagNamespace:
                    _tagNamespaceFilters.Remove(filter);
                    break;
            }
        }
    }

    public bool FilterTitle(GalleryInfo? info)
    {
        if (info is null)
        {
            return false;
        }

        lock (_gate)
        {
            string? title = info.Title;
            if (title is not null && _titleFilters.Count > 0)
            {
                string lowerTitle = title.ToLowerInvariant();
                foreach (FilterRule filter in _titleFilters)
                {
                    if (filter.IsEnabled && filter.Text is not null && lowerTitle.Contains(filter.Text, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    public bool FilterUploader(GalleryInfo? info)
    {
        if (info is null)
        {
            return false;
        }

        lock (_gate)
        {
            string? uploader = info.Uploader;
            if (uploader is not null && _uploaderFilters.Count > 0)
            {
                foreach (FilterRule filter in _uploaderFilters)
                {
                    if (filter.IsEnabled && string.Equals(uploader, filter.Text, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
    }

    public bool FilterTag(GalleryInfo? info)
    {
        if (info is null)
        {
            return false;
        }

        lock (_gate)
        {
            string[]? tags = info.SimpleTags;
            if (tags is not null && _tagFilters.Count > 0)
            {
                foreach (string tag in tags)
                {
                    foreach (FilterRule filter in _tagFilters)
                    {
                        if (filter.IsEnabled && MatchTag(tag, filter.Text))
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }
    }

    public bool FilterTagNamespace(GalleryInfo? info)
    {
        if (info is null)
        {
            return false;
        }

        lock (_gate)
        {
            string[]? tags = info.SimpleTags;
            if (tags is not null && _tagNamespaceFilters.Count > 0)
            {
                foreach (string tag in tags)
                {
                    foreach (FilterRule filter in _tagNamespaceFilters)
                    {
                        if (filter.IsEnabled && MatchTagNamespace(tag, filter.Text))
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }
    }

    private void AddToBucket(FilterRule filter)
    {
        switch (filter.Mode)
        {
            case ModeTitle:
                filter.Text = filter.Text?.ToLowerInvariant();
                _titleFilters.Add(filter);
                break;
            case ModeUploader:
                _uploaderFilters.Add(filter);
                break;
            case ModeTag:
                filter.Text = filter.Text?.ToLowerInvariant();
                _tagFilters.Add(filter);
                break;
            case ModeTagNamespace:
                filter.Text = filter.Text?.ToLowerInvariant();
                _tagNamespaceFilters.Add(filter);
                break;
        }
    }

    private static bool MatchTag(string? tag, string? filter)
    {
        if (tag is null || filter is null)
        {
            return false;
        }

        SplitTag(tag, out string? tagNs, out string tagName);
        SplitTag(filter, out string? filterNs, out string filterName);

        if (tagNs is not null && filterNs is not null && !string.Equals(tagNs, filterNs, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(tagName, filterName, StringComparison.Ordinal);
    }

    private static bool MatchTagNamespace(string? tag, string? filter)
    {
        if (tag is null || filter is null)
        {
            return false;
        }

        int colonIndex = tag.IndexOf(':');
        if (colonIndex < 0)
        {
            return false;
        }

        return string.Equals(tag[..colonIndex], filter, StringComparison.Ordinal);
    }

    private static void SplitTag(string raw, out string? ns, out string name)
    {
        int index = raw.IndexOf(':');
        if (index < 0)
        {
            ns = null;
            name = raw;
        }
        else
        {
            ns = raw[..index];
            name = raw[(index + 1)..];
        }
    }
}
