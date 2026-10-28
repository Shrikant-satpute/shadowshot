// Generates a multi-size .ico: 16/20/24/32/40/48/64 as 32bpp BMP (DIB) entries, 256 as a PNG entry.
// BMP entries for small sizes = max compatibility (System.Drawing.Icon, NotifyIcon, shell);
// PNG for 256 keeps the file small (Vista+ supports PNG-compressed icon frames).
// Artwork: dark rounded square, violet selection-corner brackets, Claude-orange 4-point sparkle.
// Usage: IconGen.exe <out.ico> [previewDir]
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class IconGen
{
    static readonly Color Violet = Color.FromArgb(0x7C, 0x5C, 0xFF);
    static readonly Color Orange = Color.FromArgb(0xD9, 0x77, 0x57);

    static int Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "app.ico";
        if (args.Length > 1) Directory.CreateDirectory(args[1]);
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var blobs = new List<byte[]>();
        foreach (int s in sizes)
            using (var b = Render(s))
            {
                if (args.Length > 1) b.Save(Path.Combine(args[1], $"icon{s}.png"), ImageFormat.Png);
                if (s >= 256) { using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); blobs.Add(ms.ToArray()); } }
                else blobs.Add(Dib(b));
            }
        using (var fs = File.Create(outPath))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);   // ICONDIR
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)                                  // ICONDIRENTRY x N
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s)); w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                w.Write(blobs[i].Length); w.Write(offset); offset += blobs[i].Length;
            }
            foreach (var bl in blobs) w.Write(bl);
        }
        Console.WriteLine($"wrote {outPath} ({new FileInfo(outPath).Length} bytes)");
        return 0;
    }

    // Bracket geometry is rounded to whole pixels and drawn with PixelOffsetMode.Half (pixel i covers
    // [i, i+1]), so the strokes stay crisp at 16-24 px instead of smearing across two pixels.
    static Bitmap Render(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.Clear(Color.Transparent);

            float pad = s < 32 ? 0 : (float)Math.Round(s / 32f);
            var rc = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
            using (var path = RoundRect(rc, rc.Width * 0.22f))
            {
                using (var br = new LinearGradientBrush(rc, Color.FromArgb(46, 49, 66), Color.FromArgb(20, 21, 30), 90f))
                    g.FillPath(br, path);
                // Faint rim keeps the tile's edge visible on dark taskbars.
                using (var rim = new Pen(Color.FromArgb(48, 255, 255, 255), Math.Max(1f, s / 64f)) { Alignment = PenAlignment.Inset })
                    g.DrawPath(rim, path);
            }

            int w = Math.Max(2, (int)Math.Round(s * 0.085));   // bracket thickness
            int m = (int)Math.Round(s * 0.15);                  // bracket outer edge from the icon edge
            int len = (int)Math.Round(s * 0.24);                // bracket arm length; leaves a gap mid-side
            int far = s - m;
            using (var violet = new SolidBrush(Violet))
            {
                foreach (int x in new[] { m, far - len })
                    foreach (int y in new[] { m, far - w })
                        g.FillRectangle(violet, x, y, len, w);           // horizontal arms
                foreach (int x in new[] { m, far - w })
                    foreach (int y in new[] { m, far - len })
                        g.FillRectangle(violet, x, y, w, len);           // vertical arms
            }

            float r = Math.Min(s * 0.21f, s / 2f - m - w);   // stays inside the brackets
            using (var orange = new SolidBrush(Orange))
            {
                if (s <= 24)
                {
                    // At tray sizes the sparkle's thin tips would anti-alias into a blob; a 2 px plus is
                    // its crisp pixel rendition.
                    int c = s / 2, arm = Math.Min((int)r, s / 2 - m - w - 1);   // 1 px clear of the brackets
                    g.FillRectangle(orange, c - 1, c - arm, 2, 2 * arm);
                    g.FillRectangle(orange, c - arm, c - 1, 2 * arm, 2);
                }
                else
                {
                    using (var star = Sparkle(s / 2f, s / 2f, r))
                        g.FillPath(orange, star);
                }
            }
        }
        return bmp;
    }

    // Four sharp tips joined by concave curves that pinch toward the centre.
    static GraphicsPath Sparkle(float cx, float cy, float r)
    {
        float k = r * 0.16f;
        PointF[] tips = { new PointF(cx, cy - r), new PointF(cx + r, cy), new PointF(cx, cy + r), new PointF(cx - r, cy) };
        PointF[] waists = { new PointF(cx + k, cy - k), new PointF(cx + k, cy + k), new PointF(cx - k, cy + k), new PointF(cx - k, cy - k) };
        var p = new GraphicsPath();
        for (int i = 0; i < 4; i++) p.AddBezier(tips[i], waists[i], waists[i], tips[(i + 1) % 4]);
        p.CloseFigure();
        return p;
    }

    static GraphicsPath RoundRect(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // 32bpp BGRA DIB, bottom-up, height doubled (XOR + AND mask), AND mask from alpha==0.
    static byte[] Dib(Bitmap bmp)
    {
        int s = bmp.Width, maskStride = ((s + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = s - 1; y >= 0; y--)
                for (int x = 0; x < s; x++) { var c = bmp.GetPixel(x, y); w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A); }
            for (int y = s - 1; y >= 0; y--)
            {
                var row = new byte[maskStride];
                for (int x = 0; x < s; x++) if (bmp.GetPixel(x, y).A == 0) row[x >> 3] |= (byte)(0x80 >> (x & 7));
                w.Write(row);
            }
            return ms.ToArray();
        }
    }
}
