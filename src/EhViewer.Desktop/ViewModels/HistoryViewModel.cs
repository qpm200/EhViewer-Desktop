using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using EhViewer.Core.Client;
using EhViewer.Core.Client.Data;
using EhViewer.Core.Storage;

namespace EhViewer.Desktop.ViewModels;

/// <summary>One local history row.</summary>
public sealed record HistoryRow(
    long Gid,
    string Title,
    string Uploader,
    string ModeText,
    string TimeText,
    GalleryInfo Info);

/// <summary>
/// Local history view model backed by <see cref="EhDatabase"/>. History never touches the network:
/// it is read from and mutated on the local SQLite database.
/// </summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly EhDatabase _database;

    public HistoryViewModel(EhDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; } = true;

    [ObservableProperty]
    public partial string CountText { get; set; } = string.Empty;

    public ObservableCollection<HistoryRow> Rows { get; } = [];

    public void Reload()
    {
        ErrorMessage = string.Empty;

        try
        {
            Rows.Clear();
            foreach (HistoryInfo info in _database.GetAllHistoryInfo())
            {
                Rows.Add(new HistoryRow(
                    info.Gid,
                    EhUtils.GetSuitableTitle(info) ?? string.Empty,
                    info.Uploader ?? string.Empty,
                    info.Mode == 0 ? "阅读" : "浏览",
                    FormatTime(info.Time),
                    info));
            }

            IsEmpty = Rows.Count == 0;
            CountText = $"共 {Rows.Count} 条";
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            IsEmpty = true;
        }
    }

    public void Delete(long gid)
    {
        _database.DeleteHistoryInfo(gid);
        Reload();
    }

    public void Clear()
    {
        _database.ClearHistoryInfo();
        Reload();
    }

    internal static string FormatTime(long unixMillis)
    {
        if (unixMillis <= 0)
        {
            return string.Empty;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeMilliseconds(unixMillis)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return string.Empty;
        }
    }
}
