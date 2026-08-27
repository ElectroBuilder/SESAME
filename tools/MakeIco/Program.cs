using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: MakeIco <pngOut> <icoOut>");
            return;
        }

        var pngPath = args[0];
        var icoPath = args[1];
        int[] sizes = [16, 32, 48, 64, 256];
        var images = sizes.Select(s => EncodeBmp(Draw(s), s)).ToArray();
        var header = new byte[6 + 16 * images.Length];
        header[2] = 1;
        header[4] = (byte)images.Length;
        var offset = header.Length;
        for (var i = 0; i < images.Length; i++)
        {
            var o = 6 + 16 * i;
            header[o] = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            header[o + 1] = header[o];
            header[o + 4] = 1;
            header[o + 6] = 32;
            BitConverter.GetBytes(images[i].Length).CopyTo(header, o + 8);
            BitConverter.GetBytes(offset).CopyTo(header, o + 12);
            offset += images[i].Length;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(icoPath))!);
        using (var fs = File.Create(icoPath))
        {
            fs.Write(header);
            foreach (var img in images)
                fs.Write(img);
        }

        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(Draw(256)));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(pngPath))!);
        using (var fs = File.Create(pngPath))
            png.Save(fs);

        Console.WriteLine($"Wrote {icoPath} ({new FileInfo(icoPath).Length} bytes)");
        Console.WriteLine($"Wrote {pngPath} ({new FileInfo(pngPath).Length} bytes)");
    }

    private static RenderTargetBitmap Draw(int size)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var navy = Color.FromRgb(0x0B, 0x2A, 0x32);
            var teal = Color.FromRgb(0x2E, 0xD3, 0xC5);
            var gold = Color.FromRgb(0xE8, 0xB8, 0x4A);
            var pad = size * 0.04;
            var body = new Rect(pad, pad, size - 2 * pad, size - 2 * pad);
            var radius = size * 0.18;
            dc.DrawRoundedRectangle(new SolidColorBrush(navy), null, body, radius, radius);

            if (size >= 48)
            {
                var pen = new Pen(new SolidColorBrush(teal), Math.Max(1.6, size * 0.038))
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                var screen = new Rect(size * 0.22, size * 0.20, size * 0.56, size * 0.46);
                dc.DrawRoundedRectangle(null, pen, screen, size * 0.07, size * 0.07);
                dc.DrawRoundedRectangle(null, pen,
                    new Rect(size * 0.12, size * 0.28, size * 0.12, size * 0.30), size * 0.05, size * 0.05);
                dc.DrawRoundedRectangle(null, pen,
                    new Rect(size * 0.76, size * 0.28, size * 0.12, size * 0.30), size * 0.05, size * 0.05);
                dc.DrawRoundedRectangle(new SolidColorBrush(gold), null,
                    new Rect(size * 0.42, size * 0.70, size * 0.16, size * 0.045), 2, 2);
            }

            var fontSize = size >= 48 ? size * 0.20 : size * 0.46;
            var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold,
                FontStretches.Normal);
            var text = new FormattedText(">_", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, fontSize, new SolidColorBrush(teal), 96);
            var y = size >= 48 ? size * 0.30 : (size - text.Height) / 2;
            dc.DrawText(text, new Point((size - text.Width) / 2, y));
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static byte[] EncodeBmp(BitmapSource src, int size)
    {
        var stride = size * 4;
        var topDown = new byte[stride * size];
        src.CopyPixels(topDown, stride, 0);
        var xor = new byte[stride * size];
        for (var y = 0; y < size; y++)
            Buffer.BlockCopy(topDown, (size - 1 - y) * stride, xor, y * stride, stride);
        var andRow = ((size + 31) / 32) * 4;
        var and = new byte[andRow * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                var a = topDown[(y * stride) + (x * 4) + 3];
                if (a >= 16) continue;
                var destY = size - 1 - y;
                and[destY * andRow + (x >> 3)] |= (byte)(0x80 >> (x & 7));
            }
        }

        var data = new byte[40 + xor.Length + and.Length];
        BitConverter.GetBytes(40).CopyTo(data, 0);
        BitConverter.GetBytes(size).CopyTo(data, 4);
        BitConverter.GetBytes(size * 2).CopyTo(data, 8);
        BitConverter.GetBytes((short)1).CopyTo(data, 12);
        BitConverter.GetBytes((short)32).CopyTo(data, 14);
        xor.CopyTo(data, 40);
        and.CopyTo(data, 40 + xor.Length);
        return data;
    }
}
