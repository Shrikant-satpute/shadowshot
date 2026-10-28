using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;

namespace Shadowshot.Tests
{
    static class AnnotationTests
    {
        static readonly Color[] Colors =
        {
            Color.FromArgb(0xFF, 0x00, 0x00), Color.FromArgb(0xFF, 0xCC, 0x00), Color.FromArgb(0xFF, 0xFF, 0xFF),
            Color.FromArgb(0x00, 0x00, 0x00), Color.FromArgb(0x00, 0x7A, 0xFF), Color.FromArgb(0x34, 0xC7, 0x59),
        };

        public static void Run()
        {
            T.Run("Annotation.StyleIsLightAndHalo", Style);
            T.Run("Annotation.IsEmptyRules", EmptyRules);
            T.Run("Annotation.PaintStaysInsideGetBounds", BoundsContainPaint);
            T.Run("Annotation.PixelateIsBlockUniformAverage", PixelateUniform);
            T.Run("Annotation.PixelateClippedAndOffDesktop", PixelateClipped);
            T.Run("Annotation.GallerySnapshots", Gallery);
            T.Run("Annotation.StepLeaderNotHiddenByLabel", LeaderVsLabel);
            T.Run("Annotation.StepLabelPlacementFollowsLeader", StepLabelPlacement);
            T.Run("Annotation.PixelateThinRectFloor", PixelateFloor);
            T.Run("Annotation.VisualSnapshots (steps, light text, secrets)", VisualSnapshots);
            T.Run("Annotation.MaxLengthTextCost (R1-05)", MaxLengthTextCost);
        }

        // R1-05 caps text at 4000 chars. One caret blink costs GetBounds + Draw of the text; measure that at the cap.
        static void MaxLengthTextCost()
        {
            var sb = new System.Text.StringBuilder();
            while (sb.Length < 4000) sb.Append(new string('m', 78)).Append('\n');
            var cases = new[]
            {
                ("one line", new string('k', 4000)),
                ("51 lines", sb.ToString().Substring(0, 4000)),
            };
            using (var canvas = new Bitmap(3840, 1080, PixelFormat.Format32bppPArgb))
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                foreach (var (name, text) in cases)
                {
                    var t = new TextAnnotation { Color = Color.Red, Size = 24, Location = new Point(10, 10), Text = text, Editing = true, CaretVisible = true };
                    t.Draw(g, canvas); t.GetBounds();   // warm-up
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 3; i++) { t.GetBounds(); t.Draw(g, canvas); }
                    long ms = sw.ElapsedMilliseconds / 3;
                    T.Note("4000-char text (" + name + ", size 24): GetBounds + Draw = " + ms + " ms per caret blink");
                    T.True(ms < 1000, name + ": a caret blink at the length cap takes " + ms + " ms");
                }
            }
        }

        static void Style()
        {
            int[] light = { 0xFF9500, 0xFFCC00, 0x34C759, 0x32ADE6, 0xFFFFFF };
            int[] dark = { 0xFF0000, 0x007AFF, 0xAF52DE, 0xFF2D55, 0x000000 };
            foreach (int rgb in light)
            {
                Color c = Color.FromArgb(255, Color.FromArgb(rgb));
                T.True(AnnotationStyle.IsLight(c), rgb.ToString("X6") + " should be light");
                T.Eq(Color.FromArgb(140, 0, 0, 0).ToArgb(), AnnotationStyle.Halo(c).ToArgb(), "halo of light " + rgb.ToString("X6"));
            }
            foreach (int rgb in dark)
            {
                Color c = Color.FromArgb(255, Color.FromArgb(rgb));
                T.True(!AnnotationStyle.IsLight(c), rgb.ToString("X6") + " should be dark");
                T.Eq(Color.FromArgb(170, 255, 255, 255).ToArgb(), AnnotationStyle.Halo(c).ToArgb(), "halo of dark " + rgb.ToString("X6"));
            }
        }

        static void EmptyRules()
        {
            T.True(new LineAnnotation { Start = new Point(5, 5), End = new Point(5, 5) }.IsEmpty, "zero-length line");
            T.True(!new LineAnnotation { Start = new Point(5, 5), End = new Point(6, 5) }.IsEmpty, "1px line");
            T.True(new ArrowAnnotation { Start = new Point(5, 5), End = new Point(5, 5) }.IsEmpty, "zero-length arrow");
            T.True(new RectangleAnnotation { Rect = new Rectangle(0, 0, 1, 50) }.IsEmpty, "1px wide rect");
            T.True(!new RectangleAnnotation { Rect = new Rectangle(0, 0, 2, 2) }.IsEmpty, "2x2 rect");
            T.True(new EllipseAnnotation { Rect = new Rectangle(0, 0, 50, 1) }.IsEmpty, "flat ellipse");
            T.True(new PixelateAnnotation { Rect = new Rectangle(0, 0, 1, 1) }.IsEmpty, "1px pixelate");
            T.True(new TextAnnotation { Text = "  \n " }.IsEmpty, "whitespace text");
            T.True(!new TextAnnotation { Text = "a" }.IsEmpty, "text");
            T.True(new PenAnnotation().IsEmpty, "pen without points");
            T.True(!new PenAnnotation { Points = { new Point(1, 1) } }.IsEmpty, "pen dot");
            T.True(new MarkerAnnotation { Points = { new Point(1, 1) } }.IsEmpty, "marker single point");
            T.True(!new StepAnnotation { Number = 1 }.IsEmpty, "step never empty");
        }

        static IEnumerable<(string Name, Annotation A)> Cases()
        {
            Point c = new Point(400, 300);
            foreach (Color color in Colors)
            {
                string cn = color.ToArgb().ToString("X8").Substring(2);
                foreach (int size in new[] { 1, 2, 3, 7, 12, 30 })
                {
                    yield return ("pen zigzag " + cn + " s" + size, new PenAnnotation { Color = color, Size = size, Points = { new Point(300, 300), new Point(360, 200), new Point(362, 400), new Point(420, 210), new Point(421, 211) } });
                    yield return ("pen dot " + cn + " s" + size, new PenAnnotation { Color = color, Size = size, Points = { c } });
                    yield return ("line diag " + cn + " s" + size, new LineAnnotation { Color = color, Size = size, Start = new Point(300, 350), End = new Point(523, 211) });
                    yield return ("line short " + cn + " s" + size, new LineAnnotation { Color = color, Size = size, Start = c, End = new Point(c.X + 1, c.Y) });
                    yield return ("rect " + cn + " s" + size, new RectangleAnnotation { Color = color, Size = size, Rect = new Rectangle(250, 220, 151, 93) });
                    yield return ("rect tiny " + cn + " s" + size, new RectangleAnnotation { Color = color, Size = size, Rect = new Rectangle(250, 220, 2, 2) });
                    yield return ("ellipse " + cn + " s" + size, new EllipseAnnotation { Color = color, Size = size, Rect = new Rectangle(250, 220, 151, 93) });
                    if (size >= 2)
                        foreach (int length in new[] { 2, 6, 15, 60, 320 })
                            foreach (int angle in new[] { 0, 37, 90, 135, 200, 315 })
                            {
                                double rad = angle * Math.PI / 180;
                                var end = new Point(c.X + (int)Math.Round(length * Math.Cos(rad)), c.Y + (int)Math.Round(length * Math.Sin(rad)));
                                yield return ("arrow " + cn + " s" + size + " len" + length + " a" + angle, new ArrowAnnotation { Color = color, Size = size, Start = c, End = end });
                            }
                }
                foreach (int size in new[] { 6, 18, 60 })
                {
                    yield return ("marker zigzag " + cn + " s" + size, new MarkerAnnotation { Color = color, Size = size, Points = { new Point(300, 300), new Point(360, 200), new Point(362, 400), new Point(420, 210) } });
                    yield return ("marker line " + cn + " s" + size, new MarkerAnnotation { Color = color, Size = size, Points = { new Point(300, 300), new Point(500, 300) } });
                }
                foreach (int size in new[] { 10, 22, 50, 96 })
                    foreach (string text in new[] { "Hello", "gjpqy ÀÉÎ|", "Line one\nsecond, longer line\n", "W", "  trailing  ", "x\n\n\ny" })
                        for (int mode = 0; mode < 3; mode++)
                            yield return ("text " + cn + " s" + size + " '" + text.Replace("\n", "\\n") + "' m" + mode,
                                new TextAnnotation { Color = color, Size = size, Location = new Point(200, 200), Text = text, Editing = mode > 0, CaretVisible = mode == 2 });
                foreach (int size in new[] { 18, 30, 80 })
                    foreach (int number in new[] { 1, 9, 10, 99, 100, 1000 })
                        foreach (string label in new[] { "", "Click Save", "A much longer label that goes on and on" })
                        {
                            yield return ("step " + cn + " s" + size + " n" + number + " '" + label + "'",
                                new StepAnnotation { Color = color, Size = size, Number = number, Center = c, Label = label });
                            yield return ("step editing " + cn + " s" + size + " n" + number + " '" + label + "'",
                                new StepAnnotation { Color = color, Size = size, Number = number, Center = c, Label = label, Editing = true, CaretVisible = true });
                        }
                foreach (int size in new[] { 18, 30, 80 })
                    foreach (var tip in new[] { new Point(3, 2), new Point(size / 2 + 1, 0), new Point(160, 0), new Point(-120, -90), new Point(0, 200), new Point(-200, 10), new Point(90, -150), new Point(-11, 12) })
                        yield return ("step tip " + cn + " s" + size + " " + tip,
                            new StepAnnotation { Color = color, Size = size, Number = 3, Center = c, Tip = new Point(c.X + tip.X, c.Y + tip.Y), Label = tip.X > 100 ? "with label" : "" });
            }
            foreach (int size in new[] { 4, 10, 40 })
                yield return ("pixelate s" + size, new PixelateAnnotation { Size = size, Rect = new Rectangle(150, 120, 233, 97) });
        }

        static void BoundsContainPaint()
        {
            var problems = new List<string>();
            int count = 0, maxSlack = 0;
            string maxSlackName = null;
            using (Bitmap source = Fx.Busy(new Size(1000, 800), new List<Rectangle> { new Rectangle(100, 100, 500, 400) }))
                foreach (var (name, a) in Cases())
                {
                    count++;
                    int slack = Check(name, a, source, problems);
                    if (slack > maxSlack) { maxSlack = slack; maxSlackName = name; }
                    a.Dispose();
                }
            T.Note(count + " annotation cases; loosest GetBounds: " + maxSlack + " px beyond paint (" + maxSlackName + ")");
            T.Problems(problems, "paint outside GetBounds()", 10);
        }

        // Draws a on a transparent canvas exactly like Editor/Compose (AntiAlias, PixelOffsetMode.Half) and
        // checks that no painted pixel lies outside GetBounds(). Returns the largest side slack.
        static int Check(string name, Annotation a, Bitmap source, List<string> problems)
        {
            Rectangle bounds = a.GetBounds();
            Rectangle area = Rectangle.Inflate(bounds, 48, 48);
            using (var canvas = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.TranslateTransform(-area.X, -area.Y);
                    a.Draw(g, source);
                }
                int[] px = Img.Read(canvas);
                int outside = 0, painted = 0;
                int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, b = int.MinValue;
                Point first = Point.Empty;
                for (int y = 0; y < area.Height; y++)
                    for (int x = 0; x < area.Width; x++)
                    {
                        if (((px[y * area.Width + x] >> 24) & 0xFF) == 0) continue;
                        painted++;
                        int cx = x + area.X, cy = y + area.Y;
                        l = Math.Min(l, cx); t = Math.Min(t, cy); r = Math.Max(r, cx); b = Math.Max(b, cy);
                        if (!bounds.Contains(cx, cy) && outside++ == 0) first = new Point(cx, cy);
                    }
                if (painted == 0 && !a.IsEmpty) problems.Add(name + ": paints nothing");
                if (outside > 0)
                    problems.Add(name + ": " + outside + " px outside " + bounds + " (paint " + Rectangle.FromLTRB(l, t, r + 1, b + 1) + ", first " + first + ")");
                if (painted == 0) return 0;
                return Math.Max(Math.Max(l - bounds.Left, t - bounds.Top), Math.Max(bounds.Right - r - 1, bounds.Bottom - b - 1));
            }
        }

        // Effective block per the spec's Tools table: Size, floored at ⌈min side/3⌉ for rects whose shorter side is ≤ 120 px.
        static int EffectiveBlock(Rectangle r, int size)
        {
            int minSide = Math.Min(r.Width, r.Height);
            return minSide <= 120 ? Math.Max(size, (minSide + 2) / 3) : Math.Max(1, size);
        }

        static void PixelateUniform()
        {
            using (Bitmap source = Fx.Busy(new Size(800, 600), new List<Rectangle> { new Rectangle(50, 40, 500, 300) }))
                foreach (var (size, rect) in new[]
                {
                    // shorter side > 120: the configured block size is used as is
                    (4, new Rectangle(103, 57, 237, 161)), (10, new Rectangle(103, 57, 237, 161)), (40, new Rectangle(103, 57, 237, 161)),
                    // thin rect (91 px): floored at ⌈91/3⌉ = 31
                    (4, new Rectangle(103, 57, 237, 91)), (40, new Rectangle(103, 57, 237, 91)),
                })
                {
                    int block = EffectiveBlock(rect, size);
                    using (Bitmap canvas = Img.Clone(source, PixelFormat.Format32bppArgb))
                    using (var pix = new PixelateAnnotation { Rect = rect, Size = size })
                    {
                        using (var g = Graphics.FromImage(canvas))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            pix.Draw(g, source);
                        }
                        int[] got = Img.Read(canvas, rect), src = Img.Read(source, rect);
                        var distinct = new HashSet<int>();
                        for (int cy = 0; cy < rect.Height; cy += block)
                            for (int cx = 0; cx < rect.Width; cx += block)
                            {
                                long sr = 0, sg = 0, sb = 0;
                                int n = 0, value = got[cy * rect.Width + cx];
                                distinct.Add(value);
                                for (int y = cy; y < Math.Min(cy + block, rect.Height); y++)
                                    for (int x = cx; x < Math.Min(cx + block, rect.Width); x++)
                                    {
                                        int p = got[y * rect.Width + x];
                                        T.True(p == value, "block " + block + ": cell (" + cx + "," + cy + ") not uniform at (" + x + "," + y + ")");
                                        int s = src[y * rect.Width + x];
                                        sr += (s >> 16) & 0xFF; sg += (s >> 8) & 0xFF; sb += s & 0xFF; n++;
                                    }
                                Color avg = Color.FromArgb((int)(sr / n), (int)(sg / n), (int)(sb / n));
                                T.True(Img.Near(avg, Color.FromArgb(value), 1), "block " + block + ": cell (" + cx + "," + cy + ") = " + Color.FromArgb(value) + " expected average " + avg);
                            }
                        int cells = ((rect.Width + block - 1) / block) * ((rect.Height + block - 1) / block);
                        T.True(distinct.Count <= cells, "no more colors than cells");
                        // ring around the rect untouched
                        Img.Diff ring = Img.Compare(canvas, Rectangle.Inflate(rect, 2, 2), source, Rectangle.Inflate(rect, 2, 2), 0,
                            PixelFormat.Format32bppArgb, (x, y) => rect.Contains(x, y));
                        T.True(ring.Count == 0, "block " + block + ": pixels outside the rect changed: " + ring);
                        Img.SaveCrop(canvas, Rectangle.Inflate(rect, 30, 30), "a10_pixelate_size" + size + "_h" + rect.Height + "_block" + block, 2);
                    }
                }
        }

        static void PixelateClipped()
        {
            using (Bitmap source = Fx.Busy(new Size(400, 300), new List<Rectangle>()))
            using (Bitmap canvas = Img.Clone(source, PixelFormat.Format32bppArgb))
            using (var pix = new PixelateAnnotation { Rect = new Rectangle(-30, -20, 100, 80), Size = 10 })
            using (var g = Graphics.FromImage(canvas))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                pix.Draw(g, source);
                using (var off = new PixelateAnnotation { Rect = new Rectangle(500, 500, 50, 50), Size = 10 })
                    off.Draw(g, source); // fully outside the source: must not throw
                pix.Rect = new Rectangle(380, 280, 60, 60);
                pix.Draw(g, source);     // rebuild after Rect change, partly outside
                pix.Size = 4;
                pix.Draw(g, source);     // rebuild after Size change
            }
        }

        // The label callout is drawn after the leader: count the leader pixels that stay visible, for leaders in every direction.
        static void LeaderVsLabel()
        {
            using (Bitmap source = Fx.Busy(new Size(900, 700), new List<Rectangle>()))
            using (Bitmap canvas = new Bitmap(900, 700, PixelFormat.Format32bppArgb))
            {
                var list = new List<Annotation>();
                var center = new Point(450, 350);
                int i = 0;
                foreach (int angle in new[] { 0, 20, 45, 90, 135, 180, 225, 270, 315, -10 })
                {
                    double rad = angle * Math.PI / 180;
                    var c = new Point(150 + (i % 5) * 160, 120 + (i / 5) * 300);
                    list.Add(new StepAnnotation { Color = Color.Red, Size = 30, Number = ++i, Center = c, Label = "Label " + angle,
                        Tip = new Point(c.X + (int)(120 * Math.Cos(rad)), c.Y + (int)(120 * Math.Sin(rad))) });
                }
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.White);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    foreach (Annotation a in list) a.Draw(g, source);
                }
                Img.Save(canvas, "a04_step_leaders_with_labels");
                var hidden = new List<string>();
                foreach (StepAnnotation s in list)
                {
                    // visible leader pixels with the label vs without it
                    int withLabel = LeaderPixels(s, source), without;
                    string label = s.Label;
                    s.Label = "";
                    without = LeaderPixels(s, source);
                    s.Label = label;
                    if (withLabel < without / 2) hidden.Add(label + ": " + (100 * withLabel / Math.Max(1, without)) + "% of the leader visible");
                }
                foreach (Annotation a in list) a.Dispose();
                T.Problems(hidden, "leader arrow hidden under the label callout");
            }
        }

        static int LeaderPixels(StepAnnotation s, Bitmap source)
        {
            Point? tip = s.Tip;
            using (Bitmap a = Draw(s, source))
            {
                s.Tip = null;
                using (Bitmap b = Draw(s, source))
                {
                    s.Tip = tip;
                    int[] pa = Img.Read(a), pb = Img.Read(b);
                    int n = 0;
                    for (int i = 0; i < pa.Length; i++) if (pa[i] != pb[i]) n++;
                    return n;
                }
            }
        }

        static Bitmap Draw(Annotation x, Bitmap source)
        {
            var bmp = new Bitmap(900, 700, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                x.Draw(g, source);
            }
            return bmp;
        }

        // Reads StepAnnotation's private layout (badge, callout box, label glyph bounds).
        static (RectangleF Badge, RectangleF Box, RectangleF Glyphs) StepGeometry(StepAnnotation s)
        {
            const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type layoutType = typeof(StepAnnotation).GetNestedType("Layout", BindingFlags.NonPublic);
            var layout = (IDisposable)Activator.CreateInstance(layoutType, Any, null, new object[] { s }, null);
            try
            {
                var badge = (RectangleF)layoutType.GetField("badge", Any).GetValue(layout);
                var box = (RectangleF)layoutType.GetField("box", Any).GetValue(layout);
                var glyphs = (GraphicsPath)layoutType.GetField("LabelGlyphs", Any).GetValue(layout);
                return (badge, box, glyphs.PointCount > 0 ? glyphs.GetBounds() : RectangleF.Empty);
            }
            finally { layout.Dispose(); }
        }

        // R4-02 / T-R1-03: the callout sits right of the badge, except when the leader heads right (within 45°):
        // then it is left-aligned with the badge, above it when the leader goes down, else below it.
        static void StepLabelPlacement()
        {
            var problems = new List<string>();
            var c = new Point(500, 400);
            foreach (int size in new[] { 18, 30, 80 })
                foreach (string label in new[] { "Click Save", "Should be disabled until valid" })
                    foreach (int angle in new[] { int.MinValue, 0, 20, -20, 40, -40, 44, -44, 46, -46, 90, -90, 135, 180, -135 })
                    {
                        Point? tip = null;
                        if (angle != int.MinValue)
                        {
                            double rad = angle * Math.PI / 180;
                            tip = new Point(c.X + (int)Math.Round(4 * size * Math.Cos(rad)), c.Y + (int)Math.Round(4 * size * Math.Sin(rad)));
                        }
                        using (var s = new StepAnnotation { Color = Color.Red, Size = size, Number = 2, Center = c, Tip = tip, Label = label })
                        {
                            var (badge, box, glyphs) = StepGeometry(s);
                            string name = "s" + size + (tip.HasValue ? " " + angle + "°" : " no tip") + " '" + label + "'";
                            int dx = tip.HasValue ? tip.Value.X - c.X : 0, dy = tip.HasValue ? tip.Value.Y - c.Y : 0;
                            if (tip.HasValue && dx > 0 && Math.Abs(dy) < dx)
                            {
                                if (Math.Abs(box.X - badge.X) > 0.5f)
                                    problems.Add(name + ": callout should be left-aligned with the badge: box " + box + " badge " + badge);
                                bool above = box.Bottom <= badge.Top + 0.5f, below = box.Top >= badge.Bottom - 0.5f;
                                if (dy > 0 ? !above : !below)
                                    problems.Add(name + ": callout should be " + (dy > 0 ? "above" : "below") + " the badge: box " + box + " badge " + badge);
                            }
                            else if (box.Left < badge.Right || Math.Abs(box.Y - badge.Y) > 0.5f)
                                problems.Add(name + ": callout should be right of the badge, on its row: box " + box + " badge " + badge);
                            float textMid = glyphs.Y + glyphs.Height / 2, boxMid = box.Y + box.Height / 2;
                            if (Math.Abs(textMid - boxMid) > 0.15f * size)
                                problems.Add(name + ": label text not centred on its box (text mid " + textMid + ", box mid " + boxMid + ")");
                            if (glyphs.Left < box.Left || glyphs.Right > box.Right + 1)
                                problems.Add(name + ": label text " + glyphs + " outside its box " + box);
                            if (!s.GetBounds().Contains(Rectangle.Round(box)))
                                problems.Add(name + ": GetBounds " + s.GetBounds() + " misses the callout " + box);
                        }
                    }
            T.Problems(problems, "step label callout placement", 8);
        }

        // Number of runs of identical rows (vertical) or columns in a w x h pixel block.
        static int Bands(int[] px, int w, int h, bool rows)
        {
            int runs = 1;
            int outer = rows ? h : w, inner = rows ? w : h;
            for (int i = 1; i < outer; i++)
            {
                bool same = true;
                for (int j = 0; j < inner && same; j++)
                    same = rows ? px[i * w + j] == px[(i - 1) * w + j] : px[j * w + i] == px[j * w + i - 1];
                if (!same) runs++;
            }
            return runs;
        }

        // R4-08: a rect whose shorter side is <= 120 px gets at most 3 blocks across it whatever the size setting,
        // so a redacted line of text can't be read; larger areas keep the configured block size.
        static void PixelateFloor()
        {
            var problems = new List<string>();
            using (Bitmap source = Fx.Busy(new Size(900, 700), new List<Rectangle> { new Rectangle(30, 30, 600, 400) }))
            {
                Func<Rectangle, int, int[]> draw = (rect, size) =>
                {
                    using (Bitmap canvas = Img.Clone(source, PixelFormat.Format32bppArgb))
                    using (var pix = new PixelateAnnotation { Rect = rect, Size = size })
                    {
                        using (var g = Graphics.FromImage(canvas))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            pix.Draw(g, source);
                        }
                        return Img.Read(canvas, rect);
                    }
                };
                foreach (int side in new[] { 4, 9, 12, 13, 20, 33, 60, 91, 119, 120 })
                    foreach (int size in new[] { 4, 10, 40 })
                    {
                        var wide = new Rectangle(37, 41, 420, side);
                        int bands = Bands(draw(wide, size), wide.Width, wide.Height, true);
                        if (bands > 3) problems.Add("wide " + wide.Size + " size " + size + ": " + bands + " block rows across the short side");
                        var tall = new Rectangle(41, 37, side, 300);
                        bands = Bands(draw(tall, size), tall.Width, tall.Height, false);
                        if (bands > 3) problems.Add("tall " + tall.Size + " size " + size + ": " + bands + " block columns across the short side");
                    }
            }
            // Larger areas keep the configured block size. Counted on per-pixel noise so that no two neighbouring
            // cells average to the same colour (on the busy fixture, flat rows merged and undercounted the blocks).
            using (Bitmap noise = Noise(new Size(700, 500), 4808))
                foreach (var (side, size) in new[] { (121, 4), (200, 4), (200, 10), (400, 10), (121, 40) })
                {
                    var rect = new Rectangle(37, 41, 500, side);
                    int[] px;
                    int block;
                    using (Bitmap canvas = Img.Clone(noise, PixelFormat.Format32bppArgb))
                    using (var pix = new PixelateAnnotation { Rect = rect, Size = size })
                    {
                        using (var g = Graphics.FromImage(canvas))
                        {
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            pix.Draw(g, noise);
                        }
                        px = Img.Read(canvas, rect);
                        block = Priv.Get<int>(pix, "cacheBlock");
                    }
                    int rows = Bands(px, rect.Width, rect.Height, true), cols = Bands(px, rect.Width, rect.Height, false);
                    int expRows = (side + size - 1) / size, expCols = (rect.Width + size - 1) / size;
                    if (block != size || rows != expRows || cols != expCols)
                        problems.Add("large " + rect.Size + " size " + size + ": block " + block + ", " + rows + "x" + cols + " cells (expected " + expRows + "x" + expCols + ")");
                }
            T.Problems(problems, "pixelate floor", 8);
        }

        static Bitmap Noise(Size size, int seed)
        {
            var rnd = new Random(seed);
            var px = new int[size.Width * size.Height];
            for (int i = 0; i < px.Length; i++) px[i] = unchecked((int)0xFF000000) | rnd.Next(0x1000000);
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
            var d = bmp.LockBits(new Rectangle(Point.Empty, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            for (int y = 0; y < size.Height; y++)
                System.Runtime.InteropServices.Marshal.Copy(px, y * size.Width, d.Scan0 + y * d.Stride, size.Width);
            bmp.UnlockBits(d);
            return bmp;
        }

        static void VisualSnapshots()
        {
            // a05: step callouts with leaders pointing right / left / up / down (+ diagonals, no leader), on a busy and a white background
            foreach (var (name, white) in new[] { ("a05_step_leaders_4dirs_busy", false), ("a05b_step_leaders_4dirs_white", true) })
                using (Bitmap source = Fx.Busy(new Size(1300, 760), new List<Rectangle> { new Rectangle(700, 380, 560, 340) }))
                using (Bitmap canvas = Img.Clone(source, PixelFormat.Format32bppArgb))
                {
                    var list = new List<Annotation>
                    {
                        new StepAnnotation { Color = Color.Red, Size = 30, Number = 1, Center = new Point(80, 120), Tip = new Point(330, 120), Label = "Leader right: Submit button" },
                        new StepAnnotation { Color = Color.Red, Size = 30, Number = 2, Center = new Point(560, 120), Tip = new Point(400, 120), Label = "Leader left" },
                        new StepAnnotation { Color = Color.Red, Size = 30, Number = 3, Center = new Point(150, 380), Tip = new Point(150, 230), Label = "Leader up" },
                        new StepAnnotation { Color = Color.Red, Size = 30, Number = 4, Center = new Point(420, 260), Tip = new Point(420, 420), Label = "Leader down" },
                        new StepAnnotation { Color = Color.FromArgb(0x00, 0x7A, 0xFF), Size = 30, Number = 5, Center = new Point(80, 520), Tip = new Point(300, 600), Label = "Right and down" },
                        new StepAnnotation { Color = Color.FromArgb(0x00, 0x7A, 0xFF), Size = 30, Number = 6, Center = new Point(80, 700), Tip = new Point(300, 630), Label = "Right and up" },
                        new StepAnnotation { Color = Color.FromArgb(0xFF, 0xCC, 0x00), Size = 30, Number = 7, Center = new Point(450, 520), Label = "No leader" },
                        new StepAnnotation { Color = Color.FromArgb(0x34, 0xC7, 0x59), Size = 60, Number = 8, Center = new Point(760, 140), Tip = new Point(1180, 160), Label = "Large, leader right" },
                        new StepAnnotation { Color = Color.FromArgb(0xAF, 0x52, 0xDE), Size = 18, Number = 9, Center = new Point(760, 300), Tip = new Point(900, 300), Label = "Small, leader right", Editing = true, CaretVisible = true },
                    };
                    using (var g = Graphics.FromImage(canvas))
                    {
                        if (white) g.Clear(Color.White);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        using (var target = new SolidBrush(Color.FromArgb(0, 103, 192)))
                            g.FillRectangle(target, 330, 105, 90, 30); // the "Submit button" the first leader points at
                        foreach (Annotation a in list) a.Draw(g, source);
                    }
                    Img.Save(canvas, name);
                    foreach (Annotation a in list) a.Dispose();
                }

            // a06: light colors at small text sizes (halo must not fill the letter holes)
            using (Bitmap source = Fx.Busy(new Size(1500, 560), new List<Rectangle>()))
            using (Bitmap canvas = Img.Clone(source, PixelFormat.Format32bppArgb))
            {
                var list = new List<Annotation>();
                Color[] light = { Color.FromArgb(0xFF, 0xCC, 0x00), Color.FromArgb(0xFF, 0x95, 0x00), Color.FromArgb(0x34, 0xC7, 0x59), Color.FromArgb(0x32, 0xAD, 0xE6), Color.White, Color.Red };
                int[] sizes = { 10, 12, 14, 16, 22 };
                using (var g = Graphics.FromImage(canvas))
                {
                    g.FillRectangle(Brushes.White, 0, 0, 500, 560);
                    g.FillRectangle(Brushes.Black, 1000, 0, 500, 560);
                    for (int col = 0; col < 3; col++)
                    {
                        int y = 10;
                        foreach (int size in sizes)
                        {
                            for (int i = 0; i < light.Length; i++)
                                list.Add(new TextAnnotation { Color = light[i], Size = size, Location = new Point(col * 500 + 10 + (i % 2) * 245, y + (i / 2) * (int)(size * 1.35)), Text = "Light gjQ aeo 08" });
                            y += (int)(size * 1.35) * 3 + 8;
                        }
                    }
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    foreach (Annotation a in list) a.Draw(g, source);
                }
                Img.Save(canvas, "a06_text_light_small");
                Img.SaveCrop(canvas, new Rectangle(0, 0, 500, 200), "a06z_text_light_small_white_zoom", 3);
                Img.SaveCrop(canvas, new Rectangle(1000, 0, 500, 200), "a06z2_text_light_small_black_zoom", 3);
                foreach (Annotation a in list) a.Dispose();
            }

            // a11: a one-line secret, original vs Pixelate at the minimum size (4) and the default (10), at several text sizes
            const string Secret = "OPENAI_API_KEY=sk-proj-7Hq2Lx9mPz4R";
            using (var canvas = new Bitmap(1300, 640, PixelFormat.Format32bppArgb))
            {
                var rects = new List<(Rectangle Rect, int Size)>();
                using (var g = Graphics.FromImage(canvas))
                {
                    g.Clear(Color.White);
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
                    int y = 8;
                    foreach (var (family, px) in new[] { ("Consolas", 14f), ("Segoe UI", 18f), ("Segoe UI", 32f), ("Consolas", 48f) })
                        using (var font = new Font(family, px, GraphicsUnit.Pixel))
                        {
                            Size text = Size.Ceiling(g.MeasureString(Secret, font));
                            foreach (int size in new[] { 0, 4, 10 })
                            {
                                g.DrawString(Secret, font, Brushes.Black, 10, y);
                                if (size > 0) rects.Add((new Rectangle(8, y, text.Width + 4, text.Height), size));
                                y += text.Height + 6;
                            }
                            y += 8;
                        }
                }
                using (Bitmap source = Img.Clone(canvas, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(canvas))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    foreach (var (rect, size) in rects)
                        using (var pix = new PixelateAnnotation { Rect = rect, Size = size })
                            pix.Draw(g, source);
                }
                Img.Save(canvas, "a11_pixelate_one_line_secret");
            }
        }

        static void Gallery()
        {
            foreach (var (name, background) in new[] { ("a01_gallery_busy", (Color?)null), ("a02_gallery_white", Color.White), ("a03_gallery_black", Color.Black) })
            {
                using (Bitmap source = Fx.Busy(new Size(1500, 900), new List<Rectangle> { new Rectangle(980, 40, 480, 300) }))
                using (Bitmap canvas = Img.Clone(source, PixelFormat.Format32bppArgb))
                {
                    var list = new List<Annotation>();
                    int x = 40;
                    foreach (Color c in new[] { Color.FromArgb(0xFF, 0, 0), Color.FromArgb(0xFF, 0xCC, 0), Color.FromArgb(0x34, 0xC7, 0x59), Color.FromArgb(0x00, 0x7A, 0xFF), Color.White, Color.Black })
                    {
                        list.Add(new PenAnnotation { Color = c, Size = 3, Points = { new Point(x, 60), new Point(x + 40, 30), new Point(x + 80, 70), new Point(x + 120, 35) } });
                        list.Add(new PenAnnotation { Color = c, Size = 12, Points = { new Point(x + 140, 50) } });
                        list.Add(new LineAnnotation { Color = c, Size = 3, Start = new Point(x, 100), End = new Point(x + 140, 130) });
                        list.Add(new ArrowAnnotation { Color = c, Size = 4, Start = new Point(x, 170), End = new Point(x + 140, 150) });
                        list.Add(new ArrowAnnotation { Color = c, Size = 12, Start = new Point(x, 230), End = new Point(x + 140, 200) });
                        list.Add(new ArrowAnnotation { Color = c, Size = 4, Start = new Point(x + 140, 250), End = new Point(x + 146, 254) });
                        list.Add(new RectangleAnnotation { Color = c, Size = 3, Rect = new Rectangle(x, 270, 140, 60) });
                        list.Add(new EllipseAnnotation { Color = c, Size = 3, Rect = new Rectangle(x, 345, 140, 60) });
                        list.Add(new MarkerAnnotation { Color = c, Size = 18, Points = { new Point(x, 430), new Point(x + 140, 430), new Point(x + 70, 445), new Point(x + 10, 455) } });
                        list.Add(new TextAnnotation { Color = c, Size = 22, Location = new Point(x, 470), Text = "Text gjQ\nline 2" });
                        list.Add(new StepAnnotation { Color = c, Size = 30, Number = 1, Center = new Point(x + 15, 560), Label = "Open" });
                        list.Add(new StepAnnotation { Color = c, Size = 30, Number = 12, Center = new Point(x + 15, 610), Tip = new Point(x + 120, 660) });
                        list.Add(new StepAnnotation { Color = c, Size = 18, Number = 3, Center = new Point(x + 15, 700), Label = "small", Editing = true, CaretVisible = true });
                        list.Add(new TextAnnotation { Color = c, Size = 16, Location = new Point(x, 730), Text = "editing", Editing = true, CaretVisible = true });
                        x += 155;
                    }
                    list.Add(new PixelateAnnotation { Size = 10, Rect = new Rectangle(990, 120, 300, 80) });
                    list.Add(new StepAnnotation { Color = Color.Red, Size = 80, Number = 100, Center = new Point(1050, 480), Label = "Large badge 100", Tip = new Point(1300, 780) });
                    list.Add(new TextAnnotation { Color = Color.Red, Size = 96, Location = new Point(960, 600), Text = "Big" });
                    using (var g = Graphics.FromImage(canvas))
                    {
                        if (background.HasValue) g.Clear(background.Value);
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.PixelOffsetMode = PixelOffsetMode.Half;
                        foreach (Annotation a in list) a.Draw(g, source);
                    }
                    Img.Save(canvas, name);
                    foreach (Annotation a in list) a.Dispose();
                }
            }
        }
    }
}
