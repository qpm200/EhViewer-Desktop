namespace EhViewer.Core.Client.Data;

public sealed class TorrentDownloadMessage
{
    public string? Path { get; set; }
    public string? Dir { get; set; }
    public string? Name { get; set; }
    public int Progress { get; set; }
    public bool Failed { get; set; }
}
