using System.IO;
using System.Numerics;
using System.Reflection;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Color = SixLabors.ImageSharp.Color;
using IoPath = System.IO.Path;

namespace Sesame.Services.GameOptimizer;

public static class CoverMask
{
    public const int PortraitWidth = 600;
    public const int PortraitHeight = 900;
    public const int LandscapeWidth = 920;
    public const int LandscapeHeight = 430;

    private static readonly object FontGate = new();
    private static readonly Dictionary<string, byte[]> LogoCache = new(StringComparer.OrdinalIgnoreCase);
    private static FontFamily? _family;

    public static byte[] Portrait(byte[]? source, SystemProfile system, bool romHack = false, bool translation = false) =>
        Apply(source, PortraitWidth, PortraitHeight, system, romHack, translation);

    public static byte[] Landscape(byte[]? source, SystemProfile system, bool romHack = false, bool translation = false) =>
        Apply(source, LandscapeWidth, LandscapeHeight, system, romHack, translation);

    public static byte[] Apply(byte[]? source, int width, int height, SystemProfile system,
        bool romHack = false, bool translation = false)
    {
        // Steam/previous Optimize may already have SESAME bars baked in — strip them
        // so we never stack 2–3 platform labels on top of each other.
        source = StripSesameBars(source);

        if (!OptimizerSettings.UseMaskFor(system.Id))
            return FitOnly(source, width, height, MaskTheme.For(system.Id).Backdrop);

        var theme = MaskTheme.For(system.Id);
        var barH = Math.Max(72, (int)Math.Round(height * (height > width ? 0.14 : 0.22)));
        var bottomH = romHack || translation ? barH : 0;
        using var canvas = new Image<Rgba32>(width, height, theme.Backdrop);
        if (source is { Length: > 0 })
            TryDrawCover(canvas, source, new Rectangle(0, barH, width, height - barH - bottomH));

        canvas.Mutate(ctx =>
        {
            ctx.Fill(theme.Bar, new RectangularPolygon(0, 0, width, barH));
            ctx.Fill(theme.Accent, new RectangularPolygon(0, barH - 5, width, 5));

            var pad = Math.Max(10, barH / 8);
            var logoMaxH = barH - pad * 2;
            var logoMaxW = width * 0.62f;
            var logoRight = DrawOfficialLogo(ctx, system.Id, pad, pad, logoMaxH, logoMaxW);
            var wordmark = logoRight - pad > logoMaxH * 1.45f;
            if (!wordmark && logoRight > pad)
            {
                var textX = logoRight + pad;
                var font = UiFont((int)Math.Clamp(barH * 0.34, 18, 36));
                if (font is not null)
                {
                    var options = new RichTextOptions(font)
                    {
                        Origin = new Vector2(textX, barH / 2f - 1),
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        WrappingLength = width - textX - pad
                    };
                    ctx.DrawText(options, system.Name, theme.Shadow);
                    options.Origin = new Vector2(textX, barH / 2f - 2);
                    ctx.DrawText(options, system.Name, theme.Text);
                }
            }
            else if (logoRight <= pad)
            {
                var font = UiFont((int)Math.Clamp(barH * 0.36, 18, 36));
                if (font is not null)
                {
                    var options = new RichTextOptions(font)
                    {
                        Origin = new Vector2(pad, barH / 2f - 2),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    ctx.DrawText(options, system.Category, theme.Text);
                }
            }
        });

        if (romHack || translation)
            DrawBottomBars(canvas, barH, romHack, translation);

        using var ms = new MemoryStream();
        canvas.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static byte[] FitOnly(byte[]? source, int width, int height, Color backdrop)
    {
        using var canvas = new Image<Rgba32>(width, height, backdrop);
        if (source is { Length: > 0 })
            TryDrawCover(canvas, source, contain: true);
        using var ms = new MemoryStream();
        canvas.SaveAsPng(ms);
        return ms.ToArray();
    }

    /// <summary>Letterbox a cover into Steam grid size without cropping (Hydra/apps preview + write).</summary>
    public static byte[] FitOnlyPublic(byte[]? source, int width, int height) =>
        FitOnly(source, width, height, Color.FromRgb(16, 16, 20));

    /// <summary>
    /// Remove stacked SESAME category bars (solid top strip + 5px accent line) and
    /// ROM-hack / NL Vertaling footers so re-Optimize never stacks labels.
    /// </summary>
    public static byte[]? StripSesameBars(byte[]? source)
    {
        if (source is not { Length: > 0 }) return source;
        try
        {
            using var img = Image.Load<Rgba32>(source);
            var changed = false;
            for (var pass = 0; pass < 6; pass++)
            {
                var bar = DetectTopBarHeight(img);
                var bottom = DetectBottomBarHeight(img);
                // Top and bottom must be stripable independently — otherwise an orphan
                // NL Vertaling footer survives after the category header is removed.
                if (bar <= 0 && bottom <= 0) break;
                var keepH = img.Height - bar - bottom;
                if (keepH < img.Height / 3) break;
                img.Mutate(c => c.Crop(new Rectangle(0, bar, img.Width, keepH)));
                changed = true;
            }
            if (!changed) return source;
            using var ms = new MemoryStream();
            img.SaveAsPng(ms);
            return ms.ToArray();
        }
        catch
        {
            return source;
        }
    }

    private static int DetectTopBarHeight(Image<Rgba32> img)
    {
        // SESAME bar is ~14% portrait / ~22% landscape, with a 5px accent under it.
        var guess = Math.Max(48, (int)Math.Round(img.Height * (img.Height > img.Width ? 0.14 : 0.22)));
        for (var barH = guess + 24; barH >= 40; barH--)
        {
            if (barH + 5 >= img.Height) continue;
            if (!RowMostlyUniform(img, 2, barH - 8, 0.82f)) continue;
            if (!RowLooksLikeAccent(img, barH - 5, barH)) continue;
            // Cover below the bar should look different from the bar fill.
            if (RowMostlyUniform(img, barH + 8, Math.Min(img.Height - 1, barH + 40), 0.82f) &&
                SameBandColor(img, 4, barH / 2, barH + 16))
                continue;
            return barH;
        }
        return 0;
    }

    private static int DetectBottomBarHeight(Image<Rgba32> img)
    {
        var guess = Math.Max(48, (int)Math.Round(img.Height * (img.Height > img.Width ? 0.14 : 0.22)));
        var maxH = Math.Min(guess + 16, img.Height / 3);
        // Prefer the bright ROM-hack / NL Vertaling footers (pink / orange).
        for (var h = maxH; h >= 40; h--)
        {
            var y0 = img.Height - h;
            if (y0 < img.Height / 2) continue;
            if (!RowLooksLikeKindBar(img, y0 + 6, img.Height - 4)) continue;
            // Row just above the footer should not be the same solid kind color.
            if (RowLooksLikeKindBar(img, Math.Max(0, y0 - 18), y0 - 4)) continue;
            return h;
        }

        var start = img.Height - guess - 8;
        if (start < img.Height / 2) return 0;
        if (!RowMostlyUniform(img, start + 8, img.Height - 4, 0.80f)) return 0;
        return Math.Min(guess + 8, img.Height / 4);
    }

    private static bool RowLooksLikeKindBar(Image<Rgba32> img, int y0, int y1)
    {
        y0 = Math.Clamp(y0, 0, img.Height - 1);
        y1 = Math.Clamp(y1, y0, img.Height - 1);
        var kind = 0;
        var n = 0;
        for (var y = y0; y <= y1; y += 2)
        for (var x = 0; x < img.Width; x += 4)
        {
            n++;
            var p = img[x, y];
            if (p.A < 200) continue;
            // NL Vertaling orange #FF6B00 / ROM-hack pink #C2185B
            var orange = p.R > 200 && p.G is > 60 and < 160 && p.B < 80;
            var pink = p.R > 160 && p.G < 90 && p.B is > 60 and < 140;
            if (orange || pink) kind++;
        }
        return n > 0 && kind / (float)n >= 0.55f;
    }

    private static bool RowMostlyUniform(Image<Rgba32> img, int y0, int y1, float minRatio)
    {
        y0 = Math.Clamp(y0, 0, img.Height - 1);
        y1 = Math.Clamp(y1, y0, img.Height - 1);
        var mid = img[img.Width / 2, (y0 + y1) / 2];
        var ok = 0;
        var n = 0;
        for (var y = y0; y <= y1; y += 2)
        for (var x = 0; x < img.Width; x += 4)
        {
            n++;
            var p = img[x, y];
            if (Near(p, mid, 28)) ok++;
        }
        return n > 0 && ok / (float)n >= minRatio;
    }

    private static bool RowLooksLikeAccent(Image<Rgba32> img, int y0, int y1)
    {
        y0 = Math.Clamp(y0, 0, img.Height - 1);
        y1 = Math.Clamp(y1, y0, img.Height - 1);
        var barSample = img[img.Width / 2, Math.Max(0, y0 - 8)];
        var accentOk = 0;
        var n = 0;
        for (var y = y0; y <= y1; y++)
        for (var x = 0; x < img.Width; x += 3)
        {
            n++;
            var p = img[x, y];
            // Accent stripe differs from the bar fill (red/teal/etc.).
            if (!Near(p, barSample, 36) && p.A > 200) accentOk++;
        }
        return n > 0 && accentOk / (float)n >= 0.45f;
    }

    private static bool SameBandColor(Image<Rgba32> img, int yA, int yB, int yC)
    {
        var a = img[img.Width / 2, Math.Clamp(yA, 0, img.Height - 1)];
        var b = img[img.Width / 2, Math.Clamp(yB, 0, img.Height - 1)];
        var c = img[img.Width / 2, Math.Clamp(yC, 0, img.Height - 1)];
        return Near(a, b, 30) && Near(b, c, 30);
    }

    private static bool Near(Rgba32 a, Rgba32 b, int tol) =>
        Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol && Math.Abs(a.B - b.B) <= tol;

    /// <param name="contain">
    /// True = letterbox (Hydra/apps). False = cover-fill the slot under category masks
    /// (mask bars change the aspect ratio; contain would leave black side bars).
    /// </param>
    private static void TryDrawCover(Image<Rgba32> canvas, byte[] source, Rectangle? slot = null,
        bool contain = false)
    {
        try
        {
            using var src = Image.Load<Rgba32>(source);
            var area = slot ?? new Rectangle(0, 0, canvas.Width, canvas.Height);
            var scale = contain
                ? Math.Min(area.Width / (float)src.Width, area.Height / (float)src.Height)
                : Math.Max(area.Width / (float)src.Width, area.Height / (float)src.Height);
            var w = Math.Max(1, (int)Math.Ceiling(src.Width * scale));
            var h = Math.Max(1, (int)Math.Ceiling(src.Height * scale));
            src.Mutate(c => c.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(w, h),
                Sampler = KnownResamplers.Lanczos3,
                Mode = ResizeMode.Stretch
            }));
            var x = area.X + (area.Width - w) / 2;
            var y = area.Y + (area.Height - h) / 2;
            canvas.Mutate(c => c.DrawImage(src, new Point(x, y), 1f));
        }
        catch
        {
            /* placeholder blijft staan */
        }
    }

    private static float DrawOfficialLogo(IImageProcessingContext ctx, string id, float x, float y,
        float maxH, float maxW)
    {
        var bytes = LoadLogo(id);
        if (bytes is null) return x;
        try
        {
            using var logo = Image.Load<Rgba32>(bytes);
            KnockoutBackground(logo);
            if (id == "n64")
                CropToN64Icon(logo);
            TrimTransparent(logo);
            if (logo.Width < 8 || logo.Height < 8) return x;

            var scale = Math.Min(maxH / logo.Height, maxW / logo.Width);
            if (scale > 8) scale = 8;
            var w = Math.Max(1, (int)Math.Round(logo.Width * scale));
            var h = Math.Max(1, (int)Math.Round(logo.Height * scale));
            logo.Mutate(c => c.Resize(new ResizeOptions
            {
                Size = new SixLabors.ImageSharp.Size(w, h),
                Sampler = KnownResamplers.Lanczos3,
                Mode = ResizeMode.Stretch
            }));
            var top = (int)Math.Round(y + (maxH - h) / 2f);
            ctx.DrawImage(logo, new Point((int)Math.Round(x), top), 1f);
            return x + w;
        }
        catch
        {
            return x;
        }
    }

    private static void KnockoutBackground(Image<Rgba32> img)
    {
        var corner = img[0, 0];
        if (corner.A < 20) return;
        bool Match(Rgba32 p) =>
            Math.Abs(p.R - corner.R) < 26 &&
            Math.Abs(p.G - corner.G) < 26 &&
            Math.Abs(p.B - corner.B) < 26 &&
            p.A > 20;

        var seen = new bool[img.Width * img.Height];
        var stack = new Stack<(int x, int y)>();
        void Push(int x, int y)
        {
            if ((uint)x >= (uint)img.Width || (uint)y >= (uint)img.Height) return;
            var i = y * img.Width + x;
            if (seen[i]) return;
            seen[i] = true;
            var p = img[x, y];
            if (!Match(p)) return;
            img[x, y] = new Rgba32(0, 0, 0, 0);
            stack.Push((x, y));
        }

        Push(0, 0);
        Push(img.Width - 1, 0);
        Push(0, img.Height - 1);
        Push(img.Width - 1, img.Height - 1);
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            Push(x + 1, y);
            Push(x - 1, y);
            Push(x, y + 1);
            Push(x, y - 1);
        }
    }

    private static void CropToN64Icon(Image<Rgba32> img)
    {
        var minX = img.Width;
        var minY = img.Height;
        var maxX = 0;
        var maxY = 0;
        var found = false;
        for (var y = 0; y < img.Height; y++)
        for (var x = 0; x < img.Width; x++)
        {
            var p = img[x, y];
            if (p.A < 40) continue;
            var yellow = p.R > 180 && p.G > 140 && p.B < 130;
            var green = p.G > 110 && p.G > p.R + 15 && p.G > p.B;
            if (!yellow && !green) continue;
            found = true;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }
        if (!found) return;
        const int pad = 10;
        var rect = Rectangle.FromLTRB(
            Math.Max(0, minX - pad),
            Math.Max(0, minY - pad),
            Math.Min(img.Width, maxX + pad + 1),
            Math.Min(img.Height, maxY + pad + 1));
        if (rect.Width < 16 || rect.Height < 16) return;
        img.Mutate(c => c.Crop(rect));
    }

    private static void TrimTransparent(Image<Rgba32> img)
    {
        var minX = img.Width;
        var minY = img.Height;
        var maxX = 0;
        var maxY = 0;
        for (var y = 0; y < img.Height; y++)
        for (var x = 0; x < img.Width; x++)
        {
            if (img[x, y].A < 16) continue;
            if (x < minX) minX = x;
            if (y < minY) minY = y;
            if (x > maxX) maxX = x;
            if (y > maxY) maxY = y;
        }
        if (maxX <= minX || maxY <= minY) return;
        var rect = Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
        if (rect.Width == img.Width && rect.Height == img.Height) return;
        img.Mutate(c => c.Crop(rect));
    }

    private static byte[]? LoadLogo(string id)
    {
        lock (LogoCache)
        {
            if (LogoCache.TryGetValue(id, out var cached))
                return cached.Length == 0 ? null : cached;

            foreach (var name in new[] { id, id + "icon", id + "word" })
            {
                var file = IoPath.Combine(AppContext.BaseDirectory, "Assets", "Logos", name + ".png");
                if (File.Exists(file))
                {
                    var data = File.ReadAllBytes(file);
                    LogoCache[id] = data;
                    return data;
                }

                var resource = "Sesame.Assets.Logos." + name + ".png";
                using var stream =
                    typeof(CoverMask).Assembly.GetManifestResourceStream(resource)
                    ?? Assembly.GetEntryAssembly()?.GetManifestResourceStream(resource);
                if (stream is null) continue;
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var embedded = ms.ToArray();
                LogoCache[id] = embedded;
                return embedded;
            }

            LogoCache[id] = [];
            return null;
        }
    }

    private static void DrawBottomBars(Image<Rgba32> canvas, int barH, bool romHack, bool translation)
    {
        var y = canvas.Height - barH;
        var w = canvas.Width;
        canvas.Mutate(ctx =>
        {
            if (romHack && translation)
            {
                var half = w / 2;
                FillKindBar(ctx, 0, y, half, barH, Color.ParseHex("C2185B"), Color.White, "ROM-hack");
                FillKindBar(ctx, half, y, w - half, barH, Color.ParseHex("FF6B00"), Color.ParseHex("1A237E"), "NL Vertaling");
                ctx.Fill(Color.FromRgba(0, 0, 0, 50), new RectangularPolygon(half - 1, y, 2, barH));
            }
            else if (romHack)
                FillKindBar(ctx, 0, y, w, barH, Color.ParseHex("C2185B"), Color.White, "ROM-hack");
            else
                FillKindBar(ctx, 0, y, w, barH, Color.ParseHex("FF6B00"), Color.ParseHex("1A237E"), "NL Vertaling");
        });
    }

    private static void FillKindBar(IImageProcessingContext ctx, int x, int y, int w, int h,
        Color fill, Color ink, string text)
    {
        ctx.Fill(fill, new RectangularPolygon(x, y, w, h));
        ctx.Fill(Color.FromRgba(255, 255, 255, 40), new RectangularPolygon(x, y, w, 5));
        var font = UiFont((int)Math.Clamp(h * 0.38, 16, 34));
        if (font is null) return;
        var options = new RichTextOptions(font)
        {
            Origin = new Vector2(x + w / 2f, y + h / 2f - 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        ctx.DrawText(options, text, Color.FromRgba(0, 0, 0, 90));
        options.Origin = new Vector2(x + w / 2f, y + h / 2f - 2);
        ctx.DrawText(options, text, ink);
    }

    private static Font? UiFont(int size)
    {
        try
        {
            var family = EnsureFont();
            return family?.CreateFont(size, FontStyle.Bold);
        }
        catch
        {
            return null;
        }
    }

    private static FontFamily? EnsureFont()
    {
        if (_family is not null) return _family;
        lock (FontGate)
        {
            if (_family is not null) return _family;
            var dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (var name in new[] { "segoeuib.ttf", "segoeui.ttf", "arialbd.ttf", "arial.ttf", "calibrib.ttf" })
            {
                var path = IoPath.Combine(dir, name);
                if (!File.Exists(path)) continue;
                var col = new FontCollection();
                _family = col.Add(path);
                return _family;
            }
            _family = SystemFonts.Families.FirstOrDefault();
            return _family;
        }
    }

    private readonly record struct MaskTheme(
        Color Bar, Color Accent, Color Text, Color Shadow, Color Icon, Color IconBack, Color Backdrop)
    {
        public static MaskTheme For(string id) => id switch
        {
            "nes" => T("C41E3A", "F4D35E", "FFFFFF"),
            "snes" => T("5B3A9C", "E0C3FC", "FFFFFF"),
            "n64" => T("1C3D3A", "E8D54A", "F4F1DE"),
            "gc" => T("5944C6", "9AE6FF", "FFFFFF"),
            "wii" => T("E8EEF4", "1B4F9C", "12325C", icon: "1B4F9C"),
            "wiiu" => T("1B9E77", "F7F7F7", "FFFFFF"),
            "switch" => T("2B2B2B", "E60012", "FFFFFF"),
            "gb" => T("306230", "9BBC0F", "F0F8C8"),
            "gbc" => T("2D5A27", "F8D030", "FFFFFF"),
            "gba" => T("4A478A", "B8B5FF", "FFFFFF"),
            "nds" => T("1F5FA8", "7EC8FF", "FFFFFF"),
            "3ds" => T("5C5C5C", "E60012", "FFFFFF"),
            "genesis" => T("123A7A", "C0C0C0", "FFFFFF"),
            "sms" => T("1A1A6C", "FFD200", "FFFFFF"),
            "saturn" => T("2A2458", "C0C0C0", "FFFFFF"),
            "dc" => T("FF6600", "FFFFFF", "FFFFFF"),
            "ps1" => T("6E6E6E", "FFFFFF", "FFFFFF"),
            "ps2" => T("003791", "7EB6FF", "FFFFFF"),
            "psp" => T("1A1A1A", "C0C0C0", "F2F2F2"),
            "vita" => T("003791", "E8E8E8", "FFFFFF"),
            "arcade" => T("F5C518", "C41E3A", "1A1A1A", icon: "C41E3A"),
            "xbox" => T("107C10", "9BF00B", "FFFFFF"),
            "hydra" => T("1B1B2A", "7C5CFF", "FFFFFF"),
            "app" => T("2A2A2A", "2AD4C5", "FFFFFF"),
            _ => T("1A4D56", "2AD4C5", "FFFFFF")
        };

        private static MaskTheme T(string bar, string accent, string text, string? icon = null)
        {
            var barC = Color.ParseHex(bar);
            var textC = Color.ParseHex(text);
            var p = barC.ToPixel<Rgba32>();
            return new MaskTheme(
                barC,
                Color.ParseHex(accent),
                textC,
                Color.FromRgba(0, 0, 0, 90),
                Color.ParseHex(icon ?? text),
                Color.FromRgba(0, 0, 0, 70),
                Color.FromRgb((byte)(p.R / 4), (byte)(p.G / 4), (byte)(p.B / 4)));
        }
    }
}
