using EhViewer.Desktop.Services.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace EhViewer.Desktop.Controls;

/// <summary>
/// Expandable corner Floating Action Button menu matching Android EhViewer's FabLayout.
/// </summary>
public sealed partial class FabLayoutControl : UserControl
{
    private bool _isExpanded;

    public FabLayoutControl()
    {
        InitializeComponent();
    }

    public event EventHandler? FilterRequested;
    public event EventHandler? GoToRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? RandomRequested;

    public bool IsExpanded => _isExpanded;

    public void Collapse()
    {
        if (!_isExpanded)
        {
            return;
        }

        _isExpanded = false;
        ThemeMotionHelper.AnimateScrimOpacity(ScrimBorder, show: false, durationMs: 150);
        ThemeMotionHelper.AnimatePopoverHide(MiniFabsPanel, toOffsetY: 12f, toScale: 0.94f, durationMs: 140);
        ThemeMotionHelper.AnimateRotation(PrimaryFabIcon, 0f, 180);
    }

    public void Expand()
    {
        if (_isExpanded)
        {
            return;
        }

        _isExpanded = true;
        ThemeMotionHelper.AnimateScrimOpacity(ScrimBorder, show: true, durationMs: 180);
        ThemeMotionHelper.AnimatePopoverShow(MiniFabsPanel, fromOffsetY: 0f, fromScale: 1f, durationMs: 120);

        int count = MiniFabsPanel.Children.Count;
        if (count > 0)
        {
            var itemsBottomUp = new List<UIElement>(count);
            for (int i = count - 1; i >= 0; i--)
            {
                itemsBottomUp.Add(MiniFabsPanel.Children[i]);
            }

            ThemeMotionHelper.AnimateStaggeredShow(itemsBottomUp, fromOffsetY: 16f, staggerMs: 28, durationMs: 190);
        }

        ThemeMotionHelper.AnimateRotation(PrimaryFabIcon, 45f, 200);
    }

    private void OnPrimaryFabClick(object sender, RoutedEventArgs e)
    {
        if (_isExpanded)
        {
            Collapse();
        }
        else
        {
            Expand();
        }
    }

    private void OnScrimPressed(object sender, PointerRoutedEventArgs e) => Collapse();

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        Collapse();
        FilterRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnGoToClick(object sender, RoutedEventArgs e)
    {
        Collapse();
        GoToRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        Collapse();
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnRandomClick(object sender, RoutedEventArgs e)
    {
        Collapse();
        RandomRequested?.Invoke(this, EventArgs.Empty);
    }
}
