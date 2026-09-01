using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Sesame.Services.Mii;

namespace Sesame;

/// <summary>
/// Displays only the image produced by the bundled FFL renderer. There is no
/// vector/avatar fallback: while a new render is pending the last real frame is
/// kept, and before the first frame the card remains empty.
/// </summary>
public sealed class MiiAvatarControl : FrameworkElement
{
    public static readonly DependencyProperty RenderedImageProperty =
        DependencyProperty.Register(nameof(RenderedImage), typeof(ImageSource), typeof(MiiAvatarControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ImageSource? RenderedImage
    {
        get => (ImageSource?)GetValue(RenderedImageProperty);
        set => SetValue(RenderedImageProperty, value);
    }

    public void PlayRevealAnimation()
    {
        var transform = new ScaleTransform(0.94, 0.94);
        var tilt = new RotateTransform(-1.2);
        var lift = new TranslateTransform(0, 2);
        var group = new TransformGroup();
        group.Children.Add(transform);
        group.Children.Add(tilt);
        group.Children.Add(lift);
        RenderTransformOrigin = new Point(0.5, 0.5);
        RenderTransform = group;
        BeginAnimation(OpacityProperty, null);
        BeginAnimation(RenderTransformProperty, null);

        var opacity = new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var scaleX = new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new BackEase { Amplitude = 0.12, EasingMode = EasingMode.EaseOut }
        };
        var scaleY = new DoubleAnimation(0.94, 1, scaleX.Duration)
        {
            EasingFunction = new BackEase { Amplitude = 0.12, EasingMode = EasingMode.EaseOut }
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        tilt.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(-1.2, 0, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        lift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(2, 0, TimeSpan.FromMilliseconds(260))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        BeginAnimation(OpacityProperty, opacity);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(availableSize.Width, 250), Math.Min(availableSize.Height, 285));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth > 1 ? ActualWidth : 220;
        var height = ActualHeight > 1 ? ActualHeight : 275;
        var background = new SolidColorBrush(Color.FromRgb(232, 244, 246));
        background.Freeze();
        var border = new SolidColorBrush(Color.FromRgb(157, 196, 204));
        border.Freeze();
        dc.DrawRoundedRectangle(background, new Pen(border, 1),
            new Rect(1, 1, Math.Max(0, width - 2), Math.Max(0, height - 2)), 12, 12);

        if (RenderedImage is null) return;
        var padding = 8d;
        var scale = Math.Min((width - padding * 2) / RenderedImage.Width,
            (height - padding * 2) / RenderedImage.Height);
        var imageWidth = RenderedImage.Width * scale;
        var imageHeight = RenderedImage.Height * scale;
        var imageRect = new Rect((width - imageWidth) / 2, (height - imageHeight) / 2,
            imageWidth, imageHeight);
        dc.DrawImage(RenderedImage, imageRect);
    }
}
