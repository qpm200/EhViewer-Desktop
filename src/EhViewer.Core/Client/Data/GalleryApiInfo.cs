namespace EhViewer.Core.Client.Data;

public sealed class GalleryApiInfo
{
    public long Gid { get; set; }
    public string? Token { get; set; }
    public string? ArchiverKey { get; set; }
    public string? Title { get; set; }
    public string? TitleJpn { get; set; }
    public int Category { get; set; }
    public string? Thumb { get; set; }
    public string? Uploader { get; set; }
    public long Posted { get; set; }
    public int FileCount { get; set; }
    public long FileSize { get; set; }
    public bool Expunged { get; set; }
    public float Rating { get; set; }
    public int TorrentCount { get; set; }
    public string[]? Tags { get; set; }
}
