using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Shadowshot.Tests
{
    sealed class TestFailure : Exception
    {
        public TestFailure(string message) : base(message) { }
    }

    sealed class SkipTest : Exception
    {
        public SkipTest(string message) : base(message) { }
    }

    // Minimal test runner + assertions.
    static class T
    {
        public static string OutDir;
        public static string Filter;

        static int passed, failed, skipped;
        static readonly List<string> failures = new List<string>();
        static readonly List<string> notes = new List<string>();
        static string current;

        public static void Run(string name, Action body)
        {
            if (Filter != null && name.IndexOf(Filter, StringComparison.OrdinalIgnoreCase) < 0) return;
            current = name;
            var sw = Stopwatch.StartNew();
            try
            {
                body();
                passed++;
                Console.WriteLine("PASS  " + name + "  (" + sw.ElapsedMilliseconds + " ms)");
            }
            catch (SkipTest s)
            {
                skipped++;
                Console.WriteLine("SKIP  " + name + ": " + s.Message);
            }
            catch (Exception ex)
            {
                if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
                failed++;
                string message = ex is TestFailure ? ex.Message : ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace;
                Console.WriteLine("FAIL  " + name + ": " + message);
                failures.Add(name + ": " + message.Split('\n')[0].TrimEnd());
            }
        }

        public static void Note(string text)
        {
            Console.WriteLine("      note: " + text);
            notes.Add(current + ": " + text);
        }

        public static void True(bool condition, string message)
        {
            if (!condition) throw new TestFailure(message);
        }

        public static void Eq<TV>(TV expected, TV actual, string what)
        {
            if (!EqualityComparer<TV>.Default.Equals(expected, actual))
                throw new TestFailure(what + ": expected <" + expected + "> but was <" + actual + ">");
        }

        public static void Problems(List<string> problems, string what, int show = 6)
        {
            if (problems.Count > 0)
                throw new TestFailure(what + ": " + problems.Count + " problem(s): " + string.Join(" | ", problems.Take(show)));
        }

        public static int Summary()
        {
            Console.WriteLine();
            Console.WriteLine("==== " + passed + " passed, " + failed + " failed, " + skipped + " skipped ====");
            foreach (string f in failures) Console.WriteLine("  FAILED " + f);
            if (notes.Count > 0)
            {
                Console.WriteLine("Notes:");
                foreach (string n in notes) Console.WriteLine("  " + n);
            }
            return failed == 0 ? 0 : 1;
        }
    }

    static class Priv
    {
        const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static TV Get<TV>(object o, string field) => (TV)o.GetType().GetField(field, Any).GetValue(o);

        public static void Set(object o, string field, object value) => o.GetType().GetField(field, Any).SetValue(o, value);

        public static object Call(object o, string method, params object[] args) => o.GetType().GetMethod(method, Any).Invoke(o, args);

        public static object CallStatic(Type t, string method, params object[] args) => t.GetMethod(method, Any).Invoke(null, args);
    }

    static class Img
    {
        public static int[] Read(Bitmap b, Rectangle r, PixelFormat format = PixelFormat.Format32bppArgb)
        {
            var px = new int[r.Width * r.Height];
            BitmapData d = b.LockBits(r, ImageLockMode.ReadOnly, format);
            try
            {
                for (int y = 0; y < r.Height; y++)
                    Marshal.Copy(d.Scan0 + y * d.Stride, px, y * r.Width, r.Width);
            }
            finally { b.UnlockBits(d); }
            return px;
        }

        public static int[] Read(Bitmap b) => Read(b, new Rectangle(Point.Empty, b.Size));

        // Raw 1:1 copy between two Format32bppPArgb bitmaps.
        public static void CopyRect(Bitmap src, Bitmap dst, Rectangle r)
        {
            var row = new int[r.Width];
            BitmapData s = src.LockBits(r, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            BitmapData d = dst.LockBits(r, ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < r.Height; y++)
                {
                    Marshal.Copy(s.Scan0 + y * s.Stride, row, 0, r.Width);
                    Marshal.Copy(row, 0, d.Scan0 + y * d.Stride, r.Width);
                }
            }
            finally
            {
                src.UnlockBits(s);
                dst.UnlockBits(d);
            }
        }

        public sealed class Diff
        {
            public int Count, Max;
            public Rectangle Box;
            public override string ToString() => Count + " px differ (max channel diff " + Max + ") within " + Box;
        }

        public static int ChannelDiff(int a, int b)
        {
            int m = 0;
            for (int s = 0; s < 32; s += 8)
                m = Math.Max(m, Math.Abs(((a >> s) & 0xFF) - ((b >> s) & 0xFF)));
            return m;
        }

        // Compares a's rect ra with b's rect rb (same size).
        public static Diff Compare(Bitmap a, Rectangle ra, Bitmap b, Rectangle rb, int tolerance = 0,
            PixelFormat format = PixelFormat.Format32bppArgb, Func<int, int, bool> ignore = null)
        {
            int[] pa = Read(a, ra, format), pb = Read(b, rb, format);
            var diff = new Diff();
            int l = int.MaxValue, t = int.MaxValue, r = int.MinValue, bt = int.MinValue;
            for (int y = 0; y < ra.Height; y++)
                for (int x = 0; x < ra.Width; x++)
                {
                    int i = y * ra.Width + x;
                    if (pa[i] == pb[i]) continue;
                    if (ignore != null && ignore(ra.X + x, ra.Y + y)) continue;
                    int d = ChannelDiff(pa[i], pb[i]);
                    diff.Max = Math.Max(diff.Max, d);
                    if (d <= tolerance) continue;
                    diff.Count++;
                    l = Math.Min(l, ra.X + x); t = Math.Min(t, ra.Y + y);
                    r = Math.Max(r, ra.X + x); bt = Math.Max(bt, ra.Y + y);
                }
            if (diff.Count > 0) diff.Box = Rectangle.FromLTRB(l, t, r + 1, bt + 1);
            return diff;
        }

        public static string PathOf(string name) => Path.Combine(T.OutDir, name + ".png");

        public static void Save(Bitmap b, string name) => b.Save(PathOf(name), ImageFormat.Png);

        public static void SaveCrop(Bitmap b, Rectangle r, string name, int zoom = 1)
        {
            r.Intersect(new Rectangle(Point.Empty, b.Size));
            if (r.IsEmpty) return;
            using (var crop = new Bitmap(r.Width * zoom, r.Height * zoom, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(crop))
                {
                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(b, new Rectangle(0, 0, crop.Width, crop.Height), r, GraphicsUnit.Pixel);
                }
                crop.Save(PathOf(name), ImageFormat.Png);
            }
        }

        public static Bitmap Clone(Bitmap src, PixelFormat format)
        {
            var copy = new Bitmap(src.Width, src.Height, format);
            using (var g = Graphics.FromImage(copy))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(Point.Empty, src.Size), new Rectangle(Point.Empty, src.Size), GraphicsUnit.Pixel);
            }
            return copy;
        }

        public static Color Half(Color c) => Color.FromArgb(c.R / 2, c.G / 2, c.B / 2);

        public static bool Near(Color a, Color b, int tolerance) =>
            Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance && Math.Abs(a.B - b.B) <= tolerance && Math.Abs(a.A - b.A) <= tolerance;
    }

    // Synthetic captures: busy gradient + grid + text + fake windows.
    static class Fx
    {
        public static readonly Rectangle M1 = new Rectangle(0, 0, 1920, 1080);
        public static readonly Rectangle M2 = new Rectangle(1920, 0, 1920, 1080);
        public static readonly Rectangle Desktop2 = new Rectangle(0, 0, 3840, 1080);
        public static readonly Rectangle Dialog = new Rectangle(420, 260, 520, 340);
        public static readonly Rectangle Offscreen = new Rectangle(3500, -40, 500, 300);
        public static readonly Rectangle Spanning = new Rectangle(1500, 600, 900, 350);
        public static readonly Rectangle MainWin = new Rectangle(120, 120, 1100, 760);
        public static readonly Rectangle M2Win = new Rectangle(2050, 100, 1300, 820);
        public static readonly Rectangle Taskbar1 = new Rectangle(0, 1020, 1920, 60);
        public static readonly Rectangle Taskbar2 = new Rectangle(1920, 1032, 1920, 48);
        public static readonly Point CursorPos = new Point(600, 400);

        public static CaptureResult TwoMonitors()
        {
            // z-order: top-most first
            var windows = new List<Rectangle> { Dialog, Offscreen, Spanning, MainWin, M2Win, Taskbar1, Taskbar2 };
            return new CaptureResult
            {
                Image = Busy(Desktop2.Size, windows),
                VirtualBounds = Desktop2,
                Monitors = new List<MonitorSnapshot>
                {
                    new MonitorSnapshot { Bounds = M1, Scale = 1.25f, Primary = true },
                    new MonitorSnapshot { Bounds = M2, Scale = 1f },
                },
                Windows = windows,
                Cursor = CursorPos,
            };
        }

        public static readonly Rectangle SingleDialog = new Rectangle(300, 200, 700, 500);
        public static readonly Rectangle SingleMain = new Rectangle(100, 80, 1500, 850);

        public static CaptureResult SingleMonitor(float scale = 1.5f)
        {
            var windows = new List<Rectangle> { SingleDialog, SingleMain, new Rectangle(0, 1008, 1920, 72) };
            return new CaptureResult
            {
                Image = Busy(M1.Size, windows),
                VirtualBounds = M1,
                Monitors = new List<MonitorSnapshot> { new MonitorSnapshot { Bounds = M1, Scale = scale, Primary = true } },
                Windows = windows,
                Cursor = CursorPos,
            };
        }

        const string Sample = "The quick brown fox jumps over the lazy dog 0123456789 — Shadowshot test pattern ";

        public static Bitmap Busy(Size size, IList<Rectangle> windows)
        {
            var bmp = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            var full = new Rectangle(Point.Empty, size);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                using (var lg = new LinearGradientBrush(full, Color.Red, Color.Blue, 0f))
                {
                    lg.InterpolationColors = new ColorBlend
                    {
                        Colors = new[]
                        {
                            Color.FromArgb(255, 94, 58), Color.FromArgb(255, 200, 40), Color.FromArgb(60, 200, 120), Color.FromArgb(40, 140, 255),
                            Color.FromArgb(150, 80, 220), Color.FromArgb(240, 240, 240), Color.FromArgb(30, 30, 40),
                        },
                        Positions = new[] { 0f, 0.17f, 0.33f, 0.5f, 0.67f, 0.83f, 1f },
                    };
                    g.FillRectangle(lg, full);
                }
                using (var vg = new LinearGradientBrush(new Rectangle(0, -1, size.Width, size.Height + 2), Color.FromArgb(0, 0, 0, 0), Color.FromArgb(130, 0, 0, 0), 90f))
                    g.FillRectangle(vg, full);
                using (var grid = new Pen(Color.FromArgb(70, 255, 255, 255)))
                {
                    for (int x = 0; x < size.Width; x += 64) g.DrawLine(grid, x, 0, x, size.Height);
                    for (int y = 0; y < size.Height; y += 64) g.DrawLine(grid, 0, y, size.Width, y);
                }
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                using (var font = new Font("Segoe UI", 13f, GraphicsUnit.Pixel))
                    for (int y = 4, row = 0; y < size.Height; y += 22, row++)
                        for (int x = -(row * 37 % 300); x < size.Width; x += 640)
                            g.DrawString(Sample, font, row % 2 == 0 ? Brushes.Black : Brushes.White, x, y);
                for (int i = windows.Count - 1; i >= 0; i--)
                    DrawWindow(g, windows[i], i);
            }
            return bmp;
        }

        static readonly Color[] TitleColors =
        {
            Color.FromArgb(0, 120, 212), Color.FromArgb(40, 40, 48), Color.FromArgb(16, 124, 16), Color.FromArgb(196, 43, 28), Color.FromArgb(92, 45, 145),
        };

        static void DrawWindow(Graphics g, Rectangle w, int index)
        {
            if (w.Height < 100)
            {
                using (var bar = new SolidBrush(Color.FromArgb(30, 32, 38)))
                    g.FillRectangle(bar, w);
                using (var icon = new SolidBrush(Color.FromArgb(90, 160, 255)))
                    for (int x = w.Left + w.Width / 2 - 140; x < w.Left + w.Width / 2 + 140; x += 48)
                        g.FillRectangle(icon, x, w.Top + w.Height / 2 - 12, 24, 24);
                return;
            }
            using (var body = new SolidBrush(Color.FromArgb(250, 250, 252)))
                g.FillRectangle(body, w);
            using (var title = new SolidBrush(TitleColors[index % TitleColors.Length]))
                g.FillRectangle(title, w.X, w.Y, w.Width, 34);
            using (var titleFont = new Font("Segoe UI Semibold", 15f, GraphicsUnit.Pixel))
            using (var font = new Font("Segoe UI", 14f, GraphicsUnit.Pixel))
            using (var mono = new Font("Consolas", 15f, GraphicsUnit.Pixel))
            {
                g.DrawString("Window " + index + " — Account settings", titleFont, Brushes.White, w.X + 12, w.Y + 7);
                string[] lines =
                {
                    "Username: jane.doe@example.com", "Password: hunter2-SECRET-9431", "API key: sk-test-4f9a8b7c6d5e3f2a1b0c",
                    "Server: build-agent-07.internal:8443", "Last login: 2026-10-27 22:16:30",
                };
                int y = w.Y + 50;
                foreach (string line in lines)
                {
                    if (y + 20 > w.Bottom) break;
                    g.DrawString(line, line.StartsWith("API") || line.StartsWith("Password") ? mono : font, Brushes.Black, w.X + 16, y);
                    y += 26;
                }
                if (w.Height > 260)
                {
                    using (var button = new SolidBrush(Color.FromArgb(0, 103, 192)))
                        g.FillRectangle(button, w.Right - 140, w.Bottom - 56, 120, 36);
                    g.DrawString("Save", titleFont, Brushes.White, w.Right - 100, w.Bottom - 47);
                }
            }
            using (var border = new Pen(Color.FromArgb(150, 150, 160)))
                g.DrawRectangle(border, w.X, w.Y, w.Width - 1, w.Height - 1);
        }
    }

    // Drives an Editor like CaptureOverlay does and keeps an incrementally repainted "screen":
    // after every input, the union of the invalidated rects is rendered (Render clip = bounding box,
    // like PaintEventArgs.ClipRectangle) and only the invalidated pixels are copied to the screen.
    sealed class Rig : IDisposable
    {
        public readonly CaptureResult Capture;
        public readonly Settings Settings;
        public readonly Editor E;
        public readonly List<EditorCommand> Commands = new List<EditorCommand>();
        public readonly List<Rectangle> Invalidations = new List<Rectangle>();
        public readonly Rectangle Desktop;
        public bool VerifyEach;
        public string Name = "rig";
        public Action<EditorCommand> OnCommand;

        readonly Bitmap screen, temp;
        readonly List<Rectangle> pending = new List<Rectangle>();
        readonly bool ownsCapture;
        int step;

        public Rig(CaptureResult capture, Settings settings = null, bool ownsCapture = true)
        {
            Capture = capture;
            Settings = settings ?? new Settings();
            this.ownsCapture = ownsCapture;
            Desktop = new Rectangle(Point.Empty, capture.Image.Size);
            E = new Editor(capture, Settings);
            E.Invalidate += r => { pending.Add(r); Invalidations.Add(r); };
            E.Command += c => { Commands.Add(c); OnCommand?.Invoke(c); };
            screen = new Bitmap(Desktop.Width, Desktop.Height, PixelFormat.Format32bppPArgb);
            temp = new Bitmap(Desktop.Width, Desktop.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(screen)) E.Render(g, Desktop);
        }

        public void Paint()
        {
            if (pending.Count == 0) return;
            Rectangle box = Rectangle.Empty;
            foreach (Rectangle r in pending) box = box.IsEmpty ? r : Rectangle.Union(box, r);
            box.Intersect(Desktop);
            if (!box.IsEmpty)
            {
                using (var g = Graphics.FromImage(temp)) E.Render(g, box);
                foreach (Rectangle r in pending)
                {
                    Rectangle c = Rectangle.Intersect(r, Desktop);
                    if (!c.IsEmpty) Img.CopyRect(temp, screen, c);
                }
            }
            pending.Clear();
            if (VerifyEach) CheckIncremental(Name + "_step" + (++step));
        }

        public void Down(Point p, Keys mods = Keys.None, int clicks = 1, MouseButtons b = MouseButtons.Left) { E.MouseDown(p, b, mods, clicks); Paint(); }
        public void Move(Point p, Keys mods = Keys.None, MouseButtons b = MouseButtons.None) { E.MouseMove(p, b, mods); Paint(); }
        public void Up(Point p, Keys mods = Keys.None, MouseButtons b = MouseButtons.Left) { E.MouseUp(p, b, mods); Paint(); }
        public void Wheel(int delta, Point? at = null) { E.MouseWheel(at ?? E.LastMouse, delta, Keys.None); Paint(); }
        public void Tick() { E.Tick(); Paint(); }
        public void SetSelection(Rectangle r) { E.Selection = r; Paint(); }

        public void Click(Point p, Keys mods = Keys.None, int clicks = 1)
        {
            Down(p, mods, clicks);
            Up(p, mods);
        }

        public void RightClick(Point p)
        {
            Down(p, Keys.None, 1, MouseButtons.Right);
            Up(p, Keys.None, MouseButtons.Right);
        }

        public void Drag(Point a, Point b, Keys mods = Keys.None, int steps = 6)
        {
            Down(a, mods);
            for (int i = 1; i <= steps; i++)
                Move(new Point(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps), mods, MouseButtons.Left);
            Up(b, mods);
        }

        public void DragPath(params Point[] points)
        {
            Down(points[0]);
            for (int i = 1; i < points.Length; i++) Move(points[i], Keys.None, MouseButtons.Left);
            Up(points[points.Length - 1]);
        }

        public bool Key(Keys k)
        {
            bool handled = E.KeyDown(k);
            Paint();
            return handled;
        }

        // Like WinForms: every key goes through ProcessCmdKey (KeyDown) first, then printable chars arrive as KeyPress.
        public void Type(string s)
        {
            foreach (char c in s)
            {
                if (c == '\n') { Key(Keys.Enter); continue; }
                Keys code = Keys.None;
                if (c >= 'a' && c <= 'z') code = Keys.A + (c - 'a');
                else if (c >= 'A' && c <= 'Z') code = (Keys.A + (c - 'A')) | Keys.Shift;
                else if (c >= '0' && c <= '9') code = Keys.D0 + (c - '0');
                else if (c == ' ') code = Keys.Space;
                if (code != Keys.None && E.KeyDown(code)) { Paint(); continue; }
                E.KeyPress(c);
                Paint();
            }
        }

        public TV F<TV>(string field) => Priv.Get<TV>(E, field);

        public Toolbar ToolBar => F<Toolbar>("toolBar");
        public Toolbar ActionBar => F<Toolbar>("actionBar");

        public Toolbar.Item Item(string tooltipPrefix)
        {
            foreach (Toolbar bar in new[] { ToolBar, ActionBar })
                foreach (Toolbar.Item item in bar.Items)
                    if (item.Tooltip != null && item.Tooltip.StartsWith(tooltipPrefix, StringComparison.Ordinal)) return item;
            throw new TestFailure("no toolbar item " + tooltipPrefix);
        }

        public Point ItemCenter(string tooltipPrefix)
        {
            Rectangle b = Item(tooltipPrefix).Bounds;
            return new Point(b.Left + b.Width / 2, b.Top + b.Height / 2);
        }

        public void ClickItem(string tooltipPrefix) => Click(ItemCenter(tooltipPrefix));

        public Rectangle PaletteCell(int i) => (Rectangle)Priv.Call(E, "PaletteCell", i);

        public TA Last<TA>() where TA : Annotation
        {
            T.True(E.Annotations.Count > 0, "expected an annotation");
            var a = E.Annotations[E.Annotations.Count - 1] as TA;
            T.True(a != null, "last annotation is " + E.Annotations[E.Annotations.Count - 1].GetType().Name + ", expected " + typeof(TA).Name);
            return a;
        }

        public Bitmap FullRender()
        {
            var full = new Bitmap(Desktop.Width, Desktop.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(full)) E.Render(g, Desktop);
            return full;
        }

        public void CheckIncremental(string name)
        {
            using (Bitmap full = FullRender())
            {
                Img.Diff diff = Img.Compare(screen, Desktop, full, Desktop, 0, PixelFormat.Format32bppPArgb);
                if (diff.Count == 0) return;
                Rectangle area = Rectangle.Inflate(diff.Box, 60, 60);
                Img.SaveCrop(screen, area, "FAIL_" + name + "_incremental");
                Img.SaveCrop(full, area, "FAIL_" + name + "_fullrepaint");
                throw new TestFailure(name + ": incremental repaint != full repaint: " + diff);
            }
        }

        public Rectangle UiArea()
        {
            Rectangle a = E.Selection;
            foreach (string f in new[] { "frameArea", "barsArea", "hudRect", "hintRect", "hoverRect" })
            {
                Rectangle r = F<Rectangle>(f);
                if (!r.IsEmpty) a = a.IsEmpty ? r : Rectangle.Union(a, r);
            }
            return a;
        }

        // Verifies incremental == full repaint, then saves a crop around the UI (and optionally the full frame).
        public void Snapshot(string name, bool full = false, int margin = 40)
        {
            CheckIncremental(name);
            using (Bitmap frame = FullRender())
            {
                Img.SaveCrop(frame, Rectangle.Inflate(UiArea(), margin, margin), name);
                if (full) Img.Save(frame, name + "_full");
            }
        }

        public void Dispose()
        {
            E.Dispose();
            screen.Dispose();
            temp.Dispose();
            if (ownsCapture) Capture.Dispose();
        }
    }
}
