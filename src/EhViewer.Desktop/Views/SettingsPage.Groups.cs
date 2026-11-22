using EhViewer.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EhViewer.Desktop.Views;

/// <summary>
/// Grouped Android PreferenceHeader navigation partial for SettingsPage.
/// Preserves all uconfig, Hosts, Blacklist, PIN, Update, and Maintenance dialogs in SettingsPage.xaml.cs.
/// </summary>
public sealed partial class SettingsPage : ISceneAware
{
    private static readonly string[] GroupPanelNames =
    [
        "GroupEhPanel",
        "GroupReadPanel",
        "GroupDownloadPanel",
        "GroupPrivacyPanel",
        "GroupAdvancedPanel",
        "GroupAboutPanel"
    ];

    private readonly StackPanel?[] _groupPanels = new StackPanel?[GroupPanelNames.Length];
    private Button[]? _navButtons;
    private IShellCoordinator? _shell;
    private int _activeGroupIndex;

    public void AttachCoordinator(IShellCoordinator coordinator)
    {
        _shell = coordinator;
        SelectSettingsGroup(0);
    }

    private void OnSelectGroupEh(object sender, RoutedEventArgs e) => SelectSettingsGroup(0);
    private void OnSelectGroupRead(object sender, RoutedEventArgs e) => SelectSettingsGroup(1);
    private void OnSelectGroupDownload(object sender, RoutedEventArgs e) => SelectSettingsGroup(2);
    private void OnSelectGroupPrivacy(object sender, RoutedEventArgs e) => SelectSettingsGroup(3);
    private void OnSelectGroupAdvanced(object sender, RoutedEventArgs e) => SelectSettingsGroup(4);
    private void OnSelectGroupAbout(object sender, RoutedEventArgs e) => SelectSettingsGroup(5);

    private void SelectSettingsGroup(int index)
    {
        if ((uint)index >= (uint)GroupPanelNames.Length)
        {
            return;
        }

        _activeGroupIndex = index;
        _groupPanels[0] ??= GroupEhPanel;
        _navButtons ??=
        [
            NavGroupEhBtn,
            NavGroupReadBtn,
            NavGroupDownloadBtn,
            NavGroupPrivacyBtn,
            NavGroupAdvancedBtn,
            NavGroupAboutBtn
        ];

        bool newlyRealized = false;
        if (_groupPanels[index] is null)
        {
            if (FindName(GroupPanelNames[index]) is StackPanel realized)
            {
                _groupPanels[index] = realized;
                newlyRealized = true;
            }
        }

        if (newlyRealized)
        {
            bool prevAutoSave = ViewModel.AutoSaveOnChange;
            ViewModel.AutoSaveOnChange = false;
            try
            {
                Bindings.Update();
            }
            finally
            {
                ViewModel.AutoSaveOnChange = prevAutoSave;
            }
        }

        for (int i = 0; i < GroupPanelNames.Length; i++)
        {
            bool active = i == index;
            if (_groupPanels[i] is { } panel)
            {
                panel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            }

            _navButtons[i].Background = UiHelpers.NavRowBackground(active);
        }

        if (index == 1)
        {
            EnsureDeferredOcrStatusRefreshed();
        }
    }

    private async void OnManageFiltersClick(object sender, RoutedEventArgs e)
    {
        if (_shell is not null)
        {
            await _shell.ShowFilterManagerDialogAsync().ConfigureAwait(true);
        }
    }
}
