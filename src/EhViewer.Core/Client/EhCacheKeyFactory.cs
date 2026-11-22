namespace EhViewer.Core.Client;

public static class EhCacheKeyFactory
{
    public static string GetThumbKey(long gid) => $"preview:large:{gid}:0";
    public static string GetNormalPreviewKey(long gid, int index) => $"preview:normal:{gid}:{index}";
    public static string GetLargePreviewKey(long gid, int index) => $"preview:large:{gid}:{index}";
    public static string GetLargePreviewSetKey(long gid, int index) => $"large_preview_set:{gid}:{index}";
    public static string GetImageKey(long gid, int index) => $"image:{gid}:{index}";
}
