namespace EhViewer.Core.Client.Data;

public sealed class HomeDetail
{
    public long Used { get; set; }
    public long Total { get; set; }
    public long ResetCostValue { get; set; }
    public long FromGalleryVisitsValue { get; set; }
    public long FromTorrentCompletionsValue { get; set; }
    public long FromArchiveDownloadsValue { get; set; }
    public long FromHentaiAtHomeValue { get; set; }
    public long CurrentModerationPowerValue { get; set; }

    public string ResetCost => WithGp(ResetCostValue);
    public string FromGalleryVisits => WithGp(FromGalleryVisitsValue);
    public string FromTorrentCompletions => WithGp(FromTorrentCompletionsValue);
    public string FromArchiveDownloads => WithGp(FromArchiveDownloadsValue);
    public string FromHentaiAtHome => WithGp(FromHentaiAtHomeValue);
    public string CurrentModerationPower => CurrentModerationPowerValue.ToString();

    public string GetImageLimits(string ipBaseLimitsLabel = "IP-based limits")
    {
        return Total == 0L ? ipBaseLimitsLabel : $"{Used}/{Total}";
    }

    private static string WithGp(long value) => $"{value} Gp";
}
