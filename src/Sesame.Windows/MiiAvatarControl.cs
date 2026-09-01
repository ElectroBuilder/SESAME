using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Sesame.Services.Mii;

namespace Sesame;

/// <summary>
/// Lightweight vector preview for the fields that SESAME can safely encode in
/// both supported Mii formats. It is deliberately a preview, not a replacement
/// for the emulator's renderer: the exact face assets remain emulator-owned.
/// </summary>
public sealed class MiiAvatarControl : FrameworkElement
{
    public static readonly DependencyProperty AppearanceProperty =
        DependencyProperty.Register(nameof(Appearance), typeof(MiiAppearance), typeof(MiiAvatarControl),
            new FrameworkPropertyMetadata(new MiiAppearance("", false, 0, 0, 0, 0),
                FrameworkPropertyMetadataOptions.AffectsRender));

    public MiiAppearance Appearance
    {
        get => (MiiAppearance)GetValue(AppearanceProperty);
        set => SetValue(AppearanceProperty, value);
    }

    public static readonly DependencyProperty RenderedImageProperty =
        DependencyProperty.Register(nameof(RenderedImage), typeof(ImageSource), typeof(MiiAvatarControl),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public ImageSource? RenderedImage
    {
        get => (ImageSource?)GetValue(RenderedImageProperty);
        set => SetValue(RenderedImageProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Math.Min(availableSize.Width, 250), Math.Min(availableSize.Height, 285));

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth > 1 ? ActualWidth : 220;
        var height = ActualHeight > 1 ? ActualHeight : 260;
        var center = width / 2;
        var a = Appearance;

        if (RenderedImage is not null)
        {
            var imageHeight = Math.Max(1, height - 34);
            var scale = Math.Min((width - 2) / RenderedImage.Width, imageHeight / RenderedImage.Height);
            var imageWidth = RenderedImage.Width * scale;
            var fittedHeight = RenderedImage.Height * scale;
            var imageRect = new Rect((width - imageWidth) / 2, 1 + (imageHeight - fittedHeight) / 2,
                imageWidth, fittedHeight);
            dc.DrawImage(RenderedImage, imageRect);
            DrawName(dc, width, height, a.Name);
            return;
        }

        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(232, 244, 246)),
            new Pen(new SolidColorBrush(Color.FromRgb(157, 196, 204)), 1),
            new Rect(1, 1, Math.Max(0, width - 2), Math.Max(0, height - 2)), 12, 12);

        var shirt = Palette(a.FavoriteColor, new[]
        {
            Color.FromRgb(77, 145, 205), Color.FromRgb(220, 76, 76), Color.FromRgb(92, 175, 105),
            Color.FromRgb(235, 180, 59), Color.FromRgb(157, 102, 194), Color.FromRgb(238, 130, 63),
            Color.FromRgb(47, 166, 164), Color.FromRgb(230, 102, 153), Color.FromRgb(92, 107, 192),
            Color.FromRgb(118, 92, 67), Color.FromRgb(101, 172, 87), Color.FromRgb(90, 90, 98)
        });
        var hair = Palette(a.HairColor, new[]
        {
            Color.FromRgb(40, 29, 24), Color.FromRgb(92, 55, 35), Color.FromRgb(173, 111, 57),
            Color.FromRgb(224, 178, 87), Color.FromRgb(220, 220, 220), Color.FromRgb(137, 137, 145),
            Color.FromRgb(194, 69, 62), Color.FromRgb(56, 77, 125)
        });
        var eyes = Palette(a.EyeColor, new[]
        {
            Color.FromRgb(36, 28, 24), Color.FromRgb(73, 48, 32), Color.FromRgb(63, 107, 151),
            Color.FromRgb(74, 133, 82), Color.FromRgb(111, 71, 126), Color.FromRgb(45, 45, 52)
        });
        var skin = new SolidColorBrush(Color.FromRgb(246, 202, 163));
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(100, 70, 60)), 1.2);

        // Body and neck.
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(246, 202, 163)), null,
            new Rect(center - 14, 176, 28, 32), 8, 8);
        dc.DrawRoundedRectangle(shirt, outline,
            new Rect(center - 62, 193, 124, 68), 34, 22);

        // Ears and face.
        dc.DrawEllipse(skin, outline, new Point(center - 69, 117), 15, 22);
        dc.DrawEllipse(skin, outline, new Point(center + 69, 117), 15, 22);
        dc.DrawEllipse(skin, outline, new Point(center, 121.5), 66, 76.5);

        DrawHair(dc, center, hair, a.HairStyle);
        DrawEyes(dc, center, eyes, a.EyeColor);

        // A simple, friendly expression. The preview communicates the selected
        // values while the emulator remains the source of truth for exact assets.
        dc.DrawLine(outline, new Point(center, 119), new Point(center - 4, 142));
        dc.DrawLine(outline, new Point(center - 4, 142), new Point(center + 4, 142));
        var smile = new StreamGeometry();
        using (var context = smile.Open())
        {
            context.BeginFigure(new Point(center - 20, 154), false, false);
            context.BezierTo(new Point(center - 10, 166), new Point(center + 10, 166), new Point(center + 20, 154), true, false);
        }
        dc.DrawGeometry(null, outline, smile);

        if (!string.IsNullOrWhiteSpace(a.Name))
            DrawName(dc, width, height, a.Name);
    }

    private void DrawName(DrawingContext dc, double width, double height, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = new FormattedText(name, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"), 16, new SolidColorBrush(Color.FromRgb(35, 55, 62)), dip);
        dc.DrawText(text, new Point((width - text.Width) / 2, height - 28));
    }

    private static void DrawEyes(DrawingContext dc, double center, Brush eye, int eyeId)
    {
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(100, 70, 60)), 1);
        var y = 111 + (eyeId % 3);
        foreach (var x in new[] { center - 26, center + 26 })
        {
            dc.DrawEllipse(Brushes.White, outline, new Point(x, y), 13, 9);
            dc.DrawEllipse(eye, null, new Point(x, y + 1), 5, 6);
        }
    }

    private static void DrawHair(DrawingContext dc, double center, Brush hair, int style)
    {
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(70, 50, 45)), 1);
        var top = new Rect(center - 70, 33, 140, 76);
        switch (Math.Abs(style) % 6)
        {
            case 0:
                dc.DrawEllipse(hair, outline, new Point(center, 71), 70, 38);
                break;
            case 1:
                dc.DrawRoundedRectangle(hair, outline, new Rect(center - 72, 36, 144, 47), 34, 24);
                dc.DrawEllipse(hair, null, new Point(center - 61, 85), 18, 30);
                dc.DrawEllipse(hair, null, new Point(center + 61, 85), 18, 30);
                break;
            case 2:
                dc.DrawEllipse(hair, outline, new Point(center, 64), 65, 34);
                dc.DrawLine(outline, new Point(center, 31), new Point(center, 89));
                break;
            case 3:
                dc.DrawRoundedRectangle(hair, outline, new Rect(center - 72, 36, 144, 53), 12, 12);
                dc.DrawEllipse(hair, null, new Point(center - 54, 83), 15, 28);
                dc.DrawEllipse(hair, null, new Point(center + 54, 83), 15, 28);
                break;
            case 4:
                for (var i = -2; i <= 2; i++)
                    dc.DrawEllipse(hair, outline, new Point(center + i * 25, 47), 25, 25);
                break;
            default:
                dc.DrawEllipse(hair, outline, new Point(center, 68), 66, 30);
                dc.DrawEllipse(hair, null, new Point(center, 76), 23, 26);
                break;
        }
    }

    private static SolidColorBrush Palette(int id, IReadOnlyList<Color> palette) =>
        new(palette[Math.Abs(id) % palette.Count]);
}
