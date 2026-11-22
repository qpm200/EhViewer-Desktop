using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EhViewer.Desktop.Controls;

/// <summary>
/// Android AppBar-style Material toolbar used consistently across secondary scenes.
/// </summary>
public sealed partial class MaterialToolbar : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(MaterialToolbar),
        new PropertyMetadata(string.Empty, (d, e) => ((MaterialToolbar)d).TitleText.Text = e.NewValue as string ?? string.Empty));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle),
        typeof(string),
        typeof(MaterialToolbar),
        new PropertyMetadata(string.Empty, OnSubtitleChanged));

    public static readonly DependencyProperty ShowBackButtonProperty = DependencyProperty.Register(
        nameof(ShowBackButton),
        typeof(bool),
        typeof(MaterialToolbar),
        new PropertyMetadata(false, (d, e) => ((MaterialToolbar)d).BackBtn.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public static readonly DependencyProperty ShowRightDrawerButtonProperty = DependencyProperty.Register(
        nameof(ShowRightDrawerButton),
        typeof(bool),
        typeof(MaterialToolbar),
        new PropertyMetadata(false, (d, e) => ((MaterialToolbar)d).RightDrawerBtn.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));

    public static readonly DependencyProperty RightActionsProperty = DependencyProperty.Register(
        nameof(RightActions),
        typeof(object),
        typeof(MaterialToolbar),
        new PropertyMetadata(null, (d, e) => ((MaterialToolbar)d).ExtraActionsPresenter.Content = e.NewValue));

    public MaterialToolbar()
    {
        InitializeComponent();
    }

    public event RoutedEventHandler? BackRequested;
    public event RoutedEventHandler? RightDrawerRequested;

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    public bool ShowBackButton
    {
        get => (bool)GetValue(ShowBackButtonProperty);
        set => SetValue(ShowBackButtonProperty, value);
    }

    public bool ShowRightDrawerButton
    {
        get => (bool)GetValue(ShowRightDrawerButtonProperty);
        set => SetValue(ShowRightDrawerButtonProperty, value);
    }

    public object? RightActions
    {
        get => GetValue(RightActionsProperty);
        set => SetValue(RightActionsProperty, value);
    }

    private static void OnSubtitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var toolbar = (MaterialToolbar)d;
        string sub = e.NewValue as string ?? string.Empty;
        toolbar.SubtitleText.Text = sub;
        toolbar.SubtitleText.Visibility = string.IsNullOrWhiteSpace(sub) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, e);
    private void OnRightDrawerClick(object sender, RoutedEventArgs e) => RightDrawerRequested?.Invoke(this, e);
}
