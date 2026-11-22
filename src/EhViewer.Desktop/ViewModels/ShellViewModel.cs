using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EhViewer.Desktop.ViewModels;

/// <summary>
/// Root shell view model: owns the navigation rail entries and the currently selected page.
/// </summary>
public sealed partial class ShellViewModel : ObservableObject
{
    [ObservableProperty]
    public partial NavigationEntry? SelectedEntry { get; set; }

    public ObservableCollection<NavigationEntry> Entries { get; } = [];

    public ShellViewModel()
    {
        Entries.Add(new NavigationEntry(NavigationKind.Home, "\uE80F", "首页"));
        Entries.Add(new NavigationEntry(NavigationKind.WhatsHot, "\uE735", "热门"));

        // Watched entries are the site's subscriptions; the list is built from ListUrlBuilder.ModeSubscription.
        Entries.Add(new NavigationEntry(NavigationKind.Watched, "\uE8A4", "订阅"));
        Entries.Add(new NavigationEntry(NavigationKind.Favorites, "\uE734", "收藏"));
        Entries.Add(new NavigationEntry(NavigationKind.TopList, "\uE9D9", "排行榜"));
        Entries.Add(new NavigationEntry(NavigationKind.History, "\uE81C", "历史"));
        Entries.Add(new NavigationEntry(NavigationKind.Downloads, "\uE896", "下载"));
        Entries.Add(new NavigationEntry(NavigationKind.Login, "\uE77B", "登录"));
        Entries.Add(new NavigationEntry(NavigationKind.Settings, "\uE713", "设置"));

        SelectedEntry = Entries[0];
    }
}

public enum NavigationKind
{
    Home,
    WhatsHot,
    Watched,
    Favorites,
    TopList,
    History,
    Downloads,
    Login,
    Settings
}

/// <summary>One entry in the navigation rail.</summary>
public sealed partial class NavigationEntry : ObservableObject
{
    public NavigationEntry(NavigationKind kind, string glyph, string label)
    {
        Kind = kind;
        Glyph = glyph;
        Label = label;
    }

    public NavigationKind Kind { get; }
    public string Glyph { get; }
    public string Label { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CompactToolTip))]
    public partial bool IsCompact { get; set; }

    public string? CompactToolTip => IsCompact ? Label : null;
}
