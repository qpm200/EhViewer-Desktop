namespace EhViewer.Core.Client.Data;

public sealed class TorrentInfo(string? url, string? name, string? posted)
{
    public string? Url { get; } = url;
    public string? Name { get; } = name;
    public string? Posted { get; } = posted;
}
