using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace EhViewer.Desktop.Controls;

/// <summary>
/// Five-star Material rating indicator matching Android EhViewer's SimpleRatingView (0..5 with half-star steps).
/// </summary>
public sealed partial class SimpleRatingView : UserControl
{
    private static readonly SolidColorBrush GoldBrush = new(Color.FromArgb(255, 0xFF, 0xC1, 0x07));
    private static readonly SolidColorBrush EmptyBrush = new(Color.FromArgb(115, 0x9E, 0x9E, 0x9E));

    public static readonly DependencyProperty RatingProperty = DependencyProperty.Register(
        nameof(Rating),
        typeof(double),
        typeof(SimpleRatingView),
        new PropertyMetadata(0.0, OnVisualPropertyChanged));

    public static readonly DependencyProperty StarSizeProperty = DependencyProperty.Register(
        nameof(StarSize),
        typeof(double),
        typeof(SimpleRatingView),
        new PropertyMetadata(12.0, OnVisualPropertyChanged));

    public SimpleRatingView()
    {
        InitializeComponent();
        UpdateStars();
    }

    public double Rating
    {
        get => (double)GetValue(RatingProperty);
        set => SetValue(RatingProperty, value);
    }

    public double StarSize
    {
        get => (double)GetValue(StarSizeProperty);
        set => SetValue(StarSizeProperty, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((SimpleRatingView)d).UpdateStars();
    }

    private void UpdateStars()
    {
        // Round to nearest 0.5 like E-Hentai rating sprite (0.0..5.0)
        double rounded = Math.Round(Math.Clamp(Rating, 0.0, 5.0) * 2.0, MidpointRounding.AwayFromZero) / 2.0;
        double size = StarSize > 4 ? StarSize : 12.0;
        FontIcon[] stars = [Star1, Star2, Star3, Star4, Star5];

        for (int i = 0; i < stars.Length; i++)
        {
            double threshold = i + 1;
            FontIcon icon = stars[i];
            icon.FontSize = size;

            if (rounded >= threshold)
            {
                icon.Glyph = "\uE735";
                icon.Foreground = GoldBrush;
            }
            else if (rounded >= threshold - 0.5)
            {
                icon.Glyph = "\uE7C6";
                icon.Foreground = GoldBrush;
            }
            else
            {
                icon.Glyph = "\uE734";
                icon.Foreground = EmptyBrush;
            }
        }
    }
}
