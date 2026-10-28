using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Shadowshot
{
    public abstract class Annotation : IDisposable
    {
        public Color Color;
        public int Size;                                        // meaning per tool (see the Tools table in SPEC.md)
        public abstract void Draw(Graphics g, Bitmap source);   // g in capture space; source = full screenshot
        public abstract Rectangle GetBounds();                  // everything Draw can paint, for invalidation
        public virtual bool IsEmpty => false;
        public virtual void Dispose() { }
    }

    public sealed class PenAnnotation : Annotation
    {
        public List<Point> Points = new List<Point>();

        public override bool IsEmpty => Points.Count == 0;

        public override void Draw(Graphics g, Bitmap source)
        {
            if (Points.Count == 0) return;
            if (Points.TrueForAll(p => p == Points[0]))
            {
                float halo = AnnotationGfx.HaloWidth(Size);
                using (var haloBrush = new SolidBrush(AnnotationStyle.Halo(Color)))
                using (var brush = new SolidBrush(Color))
                {
                    g.FillEllipse(haloBrush, Points[0].X - halo / 2, Points[0].Y - halo / 2, halo, halo);
                    g.FillEllipse(brush, Points[0].X - Size / 2f, Points[0].Y - Size / 2f, Size, Size);
                }
                return;
            }
            using (var path = new GraphicsPath())
            {
                path.AddLines(Points.ToArray());
                AnnotationGfx.StrokeWithHalo(g, path, Color, Size, LineJoin.Round);
            }
        }

        public override Rectangle GetBounds() => Points.Count == 0
            ? Rectangle.Empty
            : AnnotationGfx.Bounds(AnnotationGfx.Enclose(Points), AnnotationGfx.HaloWidth(Size) / 2);
    }

    public sealed class MarkerAnnotation : Annotation
    {
        public List<Point> Points = new List<Point>();

        public override bool IsEmpty => Points.TrueForAll(p => p == Points[0]);

        public override void Draw(Graphics g, Bitmap source)
        {
            if (IsEmpty) return;
            // One path drawn once, so the translucent stroke doesn't darken where it crosses itself.
            using (var path = new GraphicsPath())
            using (var pen = new Pen(Color.FromArgb(125, Color), Size) { LineJoin = LineJoin.Round })
            {
                path.AddLines(Points.ToArray());
                g.DrawPath(pen, path);
            }
        }

        public override Rectangle GetBounds() => Points.Count == 0
            ? Rectangle.Empty
            : AnnotationGfx.Bounds(AnnotationGfx.Enclose(Points), Size / 2f);
    }

    public sealed class LineAnnotation : Annotation
    {
        public Point Start, End;

        public override bool IsEmpty => Start == End;

        public override void Draw(Graphics g, Bitmap source)
        {
            using (var path = new GraphicsPath())
            {
                path.AddLine(Start, End);
                AnnotationGfx.StrokeWithHalo(g, path, Color, Size, LineJoin.Round);
            }
        }

        public override Rectangle GetBounds() =>
            AnnotationGfx.Bounds(AnnotationGfx.Enclose(new[] { Start, End }), AnnotationGfx.HaloWidth(Size) / 2);
    }

    public sealed class ArrowAnnotation : Annotation
    {
        public Point Start, End;   // head at End

        public override bool IsEmpty => Start == End;

        public override void Draw(Graphics g, Bitmap source)
        {
            using (var path = AnnotationGfx.Arrow(Start, End, Size))
                AnnotationGfx.FillWithHalo(g, path, Color, Size);
        }

        public override Rectangle GetBounds()
        {
            using (var path = AnnotationGfx.Arrow(Start, End, Size))
                return AnnotationGfx.Bounds(path.GetBounds(), AnnotationGfx.HaloExtra(Size));
        }
    }

    public sealed class RectangleAnnotation : Annotation
    {
        public Rectangle Rect;

        public override bool IsEmpty => Rect.Width < 2 || Rect.Height < 2;

        public override void Draw(Graphics g, Bitmap source)
        {
            using (var path = new GraphicsPath())
            {
                path.AddRectangle(Rect);
                AnnotationGfx.StrokeWithHalo(g, path, Color, Size, LineJoin.Miter);
            }
        }

        public override Rectangle GetBounds() => AnnotationGfx.Bounds(Rect, AnnotationGfx.HaloWidth(Size) / 2);
    }

    public sealed class EllipseAnnotation : Annotation
    {
        public Rectangle Rect;

        public override bool IsEmpty => Rect.Width < 2 || Rect.Height < 2;

        public override void Draw(Graphics g, Bitmap source)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(Rect);
                AnnotationGfx.StrokeWithHalo(g, path, Color, Size, LineJoin.Round);
            }
        }

        public override Rectangle GetBounds() => AnnotationGfx.Bounds(Rect, AnnotationGfx.HaloWidth(Size) / 2);
    }

    public sealed class PixelateAnnotation : Annotation
    {
        public Rectangle Rect;

        Bitmap cache;          // one pixel per block, opaque
        Rectangle cacheRect;
        int cacheBlock;

        public override bool IsEmpty => Rect.Width < 2 || Rect.Height < 2;

        public override void Draw(Graphics g, Bitmap source)
        {
            var r = Rectangle.Intersect(Rect, new Rectangle(Point.Empty, source.Size));
            if (r.Width <= 0 || r.Height <= 0) return;
            int block = Math.Max(1, Size);
            int minSide = Math.Min(r.Width, r.Height);
            if (minSide <= 120) block = Math.Max(block, (minSide + 2) / 3);   // ≤ 3 blocks across a thin rect (≤ 40 px): a redacted text line is never readable
            if (cache == null || cacheRect != r || cacheBlock != block)
            {
                cache?.Dispose();
                cache = Downsample(source, r, block);
                cacheRect = r;
                cacheBlock = block;
            }
            var state = g.Save();
            g.SetClip(r, CombineMode.Intersect);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(cache, new Rectangle(r.X, r.Y, cache.Width * block, cache.Height * block),
                0, 0, cache.Width, cache.Height, GraphicsUnit.Pixel);
            g.Restore(state);
        }

        public override Rectangle GetBounds() => AnnotationGfx.Bounds(Rect, 0);

        public override void Dispose()
        {
            cache?.Dispose();
            cache = null;
        }

        // Exact average of each block x block cell (true redaction: no original pixel survives).
        static Bitmap Downsample(Bitmap source, Rectangle r, int block)
        {
            int cols = (r.Width + block - 1) / block, rows = (r.Height + block - 1) / block;
            var cells = new int[cols * rows];
            var row = new int[r.Width];
            var sums = new long[cols * 3];
            var data = source.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < r.Height; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, r.Width);
                    for (int x = 0; x < r.Width; x++)
                    {
                        int p = row[x], c = x / block * 3;
                        sums[c] += (p >> 16) & 0xFF;
                        sums[c + 1] += (p >> 8) & 0xFF;
                        sums[c + 2] += p & 0xFF;
                    }
                    if ((y + 1) % block != 0 && y != r.Height - 1) continue;

                    int cellRow = y / block, cellHeight = y - cellRow * block + 1;
                    for (int cx = 0; cx < cols; cx++)
                    {
                        int c = cx * 3, n = cellHeight * (Math.Min((cx + 1) * block, r.Width) - cx * block);
                        cells[cellRow * cols + cx] = unchecked((int)0xFF000000)
                            | (int)(sums[c] / n) << 16 | (int)(sums[c + 1] / n) << 8 | (int)(sums[c + 2] / n);
                        sums[c] = sums[c + 1] = sums[c + 2] = 0;
                    }
                }
            }
            finally { source.UnlockBits(data); }

            var bmp = new Bitmap(cols, rows, PixelFormat.Format32bppArgb);
            var target = bmp.LockBits(new Rectangle(0, 0, cols, rows), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < rows; y++)
                Marshal.Copy(cells, y * cols, target.Scan0 + y * target.Stride, cols);
            bmp.UnlockBits(target);
            return bmp;
        }
    }

    public sealed class TextAnnotation : Annotation
    {
        public Point Location;      // top-left of the first line's box
        public string Text = "";
        public bool Editing;
        public bool CaretVisible;

        public override bool IsEmpty => string.IsNullOrWhiteSpace(Text);

        public override void Draw(Graphics g, Bitmap source)
        {
            using (var layout = new Layout(this))
            {
                Color halo = AnnotationStyle.Halo(Color);
                using (var haloPen = new Pen(halo, layout.HaloWidth) { LineJoin = LineJoin.Round })
                using (var brush = new SolidBrush(Color))
                {
                    g.DrawPath(haloPen, layout.Glyphs);
                    g.FillPath(brush, layout.Glyphs);
                    if (Editing) AnnotationGfx.DashedFrame(g, layout.Frame);
                    if (Editing && CaretVisible)
                    {
                        using (var haloBrush = new SolidBrush(halo))
                            g.FillRectangle(haloBrush, RectangleF.Inflate(layout.Caret, 1, 1));
                        g.FillRectangle(brush, layout.Caret);
                    }
                }
            }
        }

        public override Rectangle GetBounds()
        {
            using (var layout = new Layout(this))
                return AnnotationGfx.Bounds(layout.Frame, 1);
        }

        // Shared by Draw and GetBounds so the repaint rect always matches the paint. The frame and caret
        // are laid out even when not shown, so toggling Editing/CaretVisible never leaves stale pixels.
        sealed class Layout : IDisposable
        {
            public readonly GraphicsPath Glyphs = new GraphicsPath();
            public readonly float HaloWidth;
            public readonly RectangleF Caret;
            public readonly RectangleF Frame;   // encloses glyphs + halo + caret, at least Size wide

            public Layout(TextAnnotation t)
            {
                FontFamily family = AnnotationGfx.TextFamily;
                FontStyle style = AnnotationGfx.TextStyle;
                float px = t.Size, lineHeight = 1.2f * px;
                string[] lines = t.Text.Replace("\r\n", "\n").Split('\n', '\r');

                float width = 0, lastWidth = 0;
                for (int i = 0; i < lines.Length; i++)
                {
                    Glyphs.AddString(lines[i], family, (int)style, px,
                        new PointF(t.Location.X, t.Location.Y + i * lineHeight), AnnotationGfx.Typographic);
                    lastWidth = AnnotationGfx.MeasureWidth(lines[i], family, style, px);
                    width = Math.Max(width, lastWidth);
                }

                HaloWidth = Math.Max(1.5f, px / 8f);   // thinner than the shape halo so it doesn't fill small letters' holes
                // Ascenders reach ~0.74 em above the baseline and descenders ~0.24 em below it (Segoe UI).
                float ascent = AnnotationGfx.Ascent(family, style, px);
                float firstBaseline = t.Location.Y + ascent, lastBaseline = firstBaseline + (lines.Length - 1) * lineHeight;
                float caretWidth = Math.Max(1.5f, px / 16f);
                Caret = new RectangleF(t.Location.X + lastWidth + caretWidth, lastBaseline - 0.8f * px, caretWidth, 1.05f * px);

                var box = RectangleF.FromLTRB(t.Location.X, firstBaseline - 0.8f * px,
                    t.Location.X + Math.Max(width, px), lastBaseline + 0.25f * px);
                if (Glyphs.PointCount > 0) box = RectangleF.Union(box, Glyphs.GetBounds());
                box = RectangleF.Union(box, Caret);
                float pad = HaloWidth / 2 + Math.Max(3f, px / 8f);
                Frame = RectangleF.Inflate(box, pad, pad);
            }

            public void Dispose() => Glyphs.Dispose();
        }
    }

    public sealed class StepAnnotation : Annotation
    {
        public int Number;
        public Point Center;
        public Point? Tip;          // leader arrow target
        public string Label = "";
        public bool Editing;
        public bool CaretVisible;

        public override void Draw(Graphics g, Bitmap source)
        {
            using (var layout = new Layout(this))
            using (var ink = new SolidBrush(AnnotationGfx.Ink(Color)))
            {
                if (layout.Leader != null) AnnotationGfx.FillWithHalo(g, layout.Leader, Color, layout.LeaderWidth);
                AnnotationGfx.Sticker(g, layout.BadgeShape, Color, layout.Halo);
                g.FillPath(ink, layout.Digits);
                if (Label.Length == 0 && !Editing) return;
                AnnotationGfx.Sticker(g, layout.BoxShape, Color, layout.Halo);
                g.FillPath(ink, layout.LabelGlyphs);
                if (Editing && CaretVisible) g.FillRectangle(ink, layout.Caret);
            }
        }

        public override Rectangle GetBounds()
        {
            using (var layout = new Layout(this))
                return layout.Bounds;
        }

        // Shared by Draw and GetBounds. The label box is laid out even when hidden (empty label, not
        // editing), so committing or starting an edit never leaves stale pixels behind.
        sealed class Layout : IDisposable
        {
            public readonly GraphicsPath BadgeShape = new GraphicsPath();   // fill area; the ring is drawn outside it
            public readonly GraphicsPath BoxShape;
            public readonly GraphicsPath Digits = new GraphicsPath();
            public readonly GraphicsPath LabelGlyphs = new GraphicsPath();
            public readonly GraphicsPath Leader;                             // null without a Tip outside the badge
            public readonly float LeaderWidth, Halo;
            public readonly RectangleF Caret;
            readonly RectangleF badge, box;                                  // outer edges, ring included

            public Layout(StepAnnotation s)
            {
                const float ring = AnnotationGfx.StickerRing;
                float size = s.Size, radius = size / 2;
                Halo = Math.Max(1.5f, size / 15f);

                badge = new RectangleF(s.Center.X - radius, s.Center.Y - radius, size, size);
                BadgeShape.AddEllipse(RectangleF.Inflate(badge, -ring, -ring));

                string number = s.Number.ToString(CultureInfo.InvariantCulture);
                float digitsPx = size * (number.Length == 1 ? 0.6f : number.Length == 2 ? 0.5f : 0.4f);
                Digits.AddString(number, AnnotationGfx.BoldFamily, (int)FontStyle.Bold, digitsPx, PointF.Empty, AnnotationGfx.Typographic);
                Digits.Flatten();   // tight bounds for exact optical centering
                RectangleF ink = Digits.GetBounds();
                using (var move = new Matrix())
                {
                    move.Translate(s.Center.X - (ink.X + ink.Width / 2), s.Center.Y - (ink.Y + ink.Height / 2));
                    Digits.Transform(move);
                }

                float dx = 0, dy = 0;
                if (s.Tip.HasValue)
                {
                    dx = s.Tip.Value.X - s.Center.X;
                    dy = s.Tip.Value.Y - s.Center.Y;
                    float distance = (float)Math.Sqrt(dx * dx + dy * dy);
                    if (distance > radius)
                    {
                        LeaderWidth = Math.Max(2f, size / 10f);
                        var start = new PointF(s.Center.X + dx / distance * radius, s.Center.Y + dy / distance * radius);
                        Leader = AnnotationGfx.Arrow(start, s.Tip.Value, LeaderWidth);
                    }
                }

                float labelPx = 0.55f * size;
                float padX = 0.45f * labelPx + ring;
                float labelWidth = AnnotationGfx.MeasureWidth(s.Label, AnnotationGfx.BoldFamily, FontStyle.Bold, labelPx);
                // Right of the badge, unless the leader heads right through that band: then above or below the
                // badge, on the side the leader doesn't go, so the callout never hides its own leader.
                float boxX = s.Center.X + radius + size / 5, boxY = badge.Y;
                if (Leader != null && dx > 0 && Math.Abs(dy) < dx)
                {
                    boxX = badge.X;
                    boxY = dy > 0 ? badge.Y - size / 5 - size : badge.Bottom + size / 5;
                }
                box = new RectangleF(boxX, boxY, Math.Max(labelWidth + 2 * padX, size), size);
                var inner = RectangleF.Inflate(box, -ring, -ring);
                BoxShape = AnnotationGfx.RoundRect(inner, inner.Height * 0.3f);

                // Baseline sits half a cap height (Segoe UI caps ~0.7 em) below the box center.
                float middle = box.Y + radius;
                float ascent = AnnotationGfx.Ascent(AnnotationGfx.BoldFamily, FontStyle.Bold, labelPx);
                var origin = new PointF(box.X + padX, middle + 0.35f * labelPx - ascent);
                LabelGlyphs.AddString(s.Label, AnnotationGfx.BoldFamily, (int)FontStyle.Bold, labelPx, origin, AnnotationGfx.Typographic);
                Caret = new RectangleF(origin.X + labelWidth + 1, middle - labelPx / 2, Math.Max(1.5f, labelPx / 12f), labelPx);
            }

            public Rectangle Bounds
            {
                get
                {
                    var b = AnnotationGfx.Bounds(RectangleF.Union(badge, box), Halo);
                    return Leader == null
                        ? b
                        : Rectangle.Union(b, AnnotationGfx.Bounds(Leader.GetBounds(), AnnotationGfx.HaloExtra(LeaderWidth)));
                }
            }

            public void Dispose()
            {
                BadgeShape.Dispose();
                BoxShape.Dispose();
                Digits.Dispose();
                LabelGlyphs.Dispose();
                Leader?.Dispose();
            }
        }
    }

    public static class AnnotationStyle
    {
        // Perceived brightness (BT.601 luma); the palette splits cleanly: yellow/orange/green/cyan/white are light.
        public static bool IsLight(Color c) => c.R * 299 + c.G * 587 + c.B * 114 >= 135000;

        public static Color Halo(Color c) => IsLight(c) ? Color.FromArgb(140, 0, 0, 0) : Color.FromArgb(170, 255, 255, 255);
    }

    // Drawing helpers, fonts and text measurement shared by the annotations.
    internal static class AnnotationGfx
    {
        public const float StickerRing = 2f;
        const int Slack = 3;   // anti-aliasing fringe, pixel-offset mode and rounding in GetBounds

        public static readonly FontFamily BoldFamily = FindFamily("Segoe UI") ?? FontFamily.GenericSansSerif;
        public static readonly FontFamily TextFamily;
        public static readonly FontStyle TextStyle;
        public static readonly StringFormat Typographic = CreateTypographic();
        static readonly Bitmap MeasureBitmap = new Bitmap(1, 1);   // kept alive for Measurer's lifetime
        static readonly Graphics Measurer = CreateMeasurer();

        static AnnotationGfx()
        {
            FontFamily semibold = FindFamily("Segoe UI Semibold");
            TextFamily = semibold ?? BoldFamily;
            TextStyle = semibold != null ? FontStyle.Regular : FontStyle.Bold;
        }

        static FontFamily FindFamily(string name)
        {
            try { return new FontFamily(name); }
            catch (ArgumentException) { return null; }
        }

        static StringFormat CreateTypographic()
        {
            var format = (StringFormat)StringFormat.GenericTypographic.Clone();
            format.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
            return format;
        }

        static Graphics CreateMeasurer()
        {
            var g = Graphics.FromImage(MeasureBitmap);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;   // unhinted, like path outlines
            return g;
        }

        public static Color Ink(Color background) => AnnotationStyle.IsLight(background) ? Color.Black : Color.White;

        public static float HaloExtra(float size) => Math.Max(1f, size / 3f);

        public static float HaloWidth(float size) => size + 2 * HaloExtra(size);

        public static float MeasureWidth(string text, FontFamily family, FontStyle style, float px)
        {
            if (text.Length == 0) return 0;
            using (var font = new Font(family, px, style, GraphicsUnit.Pixel))
                return Measurer.MeasureString(text, font, PointF.Empty, Typographic).Width;
        }

        // Distance from the AddString origin to the baseline at px em size.
        public static float Ascent(FontFamily family, FontStyle style, float px) =>
            px * family.GetCellAscent(style) / family.GetEmHeight(style);

        public static RectangleF Enclose(IEnumerable<Point> points)
        {
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
            foreach (var p in points)
            {
                left = Math.Min(left, p.X); top = Math.Min(top, p.Y);
                right = Math.Max(right, p.X); bottom = Math.Max(bottom, p.Y);
            }
            return RectangleF.FromLTRB(left, top, right, bottom);
        }

        public static Rectangle Bounds(RectangleF r, float outset)
        {
            float d = outset + Slack;
            return Rectangle.FromLTRB((int)Math.Floor(r.Left - d), (int)Math.Floor(r.Top - d),
                (int)Math.Ceiling(r.Right + d), (int)Math.Ceiling(r.Bottom + d));
        }

        public static void StrokeWithHalo(Graphics g, GraphicsPath path, Color color, int size, LineJoin join)
        {
            using (var halo = new Pen(AnnotationStyle.Halo(color), HaloWidth(size)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = join })
            using (var pen = new Pen(color, size) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = join })
            {
                g.DrawPath(halo, path);
                g.DrawPath(pen, path);
            }
        }

        // Solid shape (arrow) outlined by a halo of HaloExtra(strokeWidth) on every side.
        public static void FillWithHalo(Graphics g, GraphicsPath shape, Color color, float strokeWidth)
        {
            using (var halo = new Pen(AnnotationStyle.Halo(color), 2 * HaloExtra(strokeWidth)) { LineJoin = LineJoin.Round })
            using (var brush = new SolidBrush(color))
            {
                g.DrawPath(halo, shape);
                g.FillPath(brush, shape);
            }
        }

        // Filled shape with a solid contrasting ring outside it and a soft halo contrasting with the ring.
        public static void Sticker(Graphics g, GraphicsPath shape, Color fill, float haloWidth)
        {
            Color ring = Ink(fill);
            using (var halo = new Pen(AnnotationStyle.Halo(ring), 2 * (StickerRing + haloWidth)))
            using (var ringPen = new Pen(ring, 2 * StickerRing))
            using (var brush = new SolidBrush(fill))
            {
                g.DrawPath(halo, shape);
                g.DrawPath(ringPen, shape);
                g.FillPath(brush, shape);
            }
        }

        public static void DashedFrame(Graphics g, RectangleF frame)
        {
            var smoothing = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.None;   // crisp 1 px dashes
            using (var light = new Pen(Color.FromArgb(220, 255, 255, 255)))
            using (var dark = new Pen(Color.FromArgb(220, 0, 0, 0)) { DashPattern = new[] { 3f, 3f } })
            {
                var r = Rectangle.Round(frame);
                g.DrawRectangle(light, r);
                g.DrawRectangle(dark, r);
            }
            g.SmoothingMode = smoothing;
        }

        // One closed figure — round tail, shaft of `width`, solid head ending exactly at `end` — so a single
        // halo outline wraps shaft and head without double-blending where they meet.
        public static GraphicsPath Arrow(PointF start, PointF end, float width)
        {
            var path = new GraphicsPath();
            float r = width / 2, dx = end.X - start.X, dy = end.Y - start.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < 1)
            {
                path.AddEllipse(start.X - r, start.Y - r, width, width);
                return path;
            }
            float ux = dx / length, uy = dy / length, nx = -uy, ny = ux;
            float head = Math.Min(Math.Max(10f, 3.5f * width), length);
            float half = Math.Max(0.45f * head, r);
            float bx = end.X - ux * head, by = end.Y - uy * head;
            float angle = (float)(Math.Atan2(uy, ux) * 180 / Math.PI);
            path.AddArc(start.X - r, start.Y - r, width, width, angle + 90, 180);
            path.AddLines(new[]
            {
                new PointF(bx - nx * r, by - ny * r),
                new PointF(bx - nx * half, by - ny * half),
                end,
                new PointF(bx + nx * half, by + ny * half),
                new PointF(bx + nx * r, by + ny * r),
            });
            path.CloseFigure();
            return path;
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(2 * radius, Math.Min(r.Width, r.Height));
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
