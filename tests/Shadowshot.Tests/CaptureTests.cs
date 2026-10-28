using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace Shadowshot.Tests
{
    // Read-only checks against the real desktop (2 monitors: 1920x1080 @125 % primary, 1920x1080 @100 % at x=1920).
    static class CaptureTests
    {
        static CaptureResult cap;

        static CaptureResult Cap => cap ?? throw new SkipTest("no capture");

        public static void Run()
        {
            T.Run("Capture.CaptureAllRealDesktop", () => { cap = ScreenCapture.CaptureAll(false, true); Basic(); });
            T.Run("Capture.MonitorsAndScales", Monitors);
            T.Run("Capture.PixelsOpaqueAndNotBlank", Opaque);
            T.Run("Capture.WindowsList", Windows);
            T.Run("Capture.MonitorAt", MonitorAt);
            T.Run("Capture.Crop", Crop);
            T.Run("Capture.WithCursorWithoutWindows", WithCursor);
            T.Run("Capture.RealDesktopEditorSnapshots", RealEditor);
            cap?.Dispose();
        }

        static void Basic()
        {
            Rectangle vs = SystemInformation.VirtualScreen;
            T.Eq(vs, Cap.VirtualBounds, "VirtualBounds == SystemInformation.VirtualScreen (physical px, PMv2)");
            T.Eq(new Rectangle(0, 0, 3840, 1080), Cap.VirtualBounds, "this machine's virtual desktop");
            T.Eq(Cap.VirtualBounds.Size, Cap.Image.Size, "image size == virtual desktop");
            T.Eq(PixelFormat.Format32bppPArgb, Cap.Image.PixelFormat, "pixel format");
            T.True(new Rectangle(Point.Empty, Cap.Image.Size).Contains(Cap.Cursor), "cursor inside the desktop: " + Cap.Cursor);
        }

        static void Monitors()
        {
            var ms = Cap.Monitors.OrderBy(m => m.Bounds.X).ToList();
            T.Eq(2, ms.Count, "monitor count");
            T.True(ms[0].Bounds == Fx.M1 && Math.Abs(ms[0].Scale - 1.25f) < 0.001 && ms[0].Primary, "monitor 1: " + ms[0].Bounds + " @" + ms[0].Scale + " primary=" + ms[0].Primary);
            T.True(ms[1].Bounds == Fx.M2 && Math.Abs(ms[1].Scale - 1f) < 0.001 && !ms[1].Primary, "monitor 2: " + ms[1].Bounds + " @" + ms[1].Scale);
        }

        static void Opaque()
        {
            int[] px = Img.Read(Cap.Image, new Rectangle(Point.Empty, Cap.Image.Size), PixelFormat.Format32bppPArgb);
            int transparent = px.Count(p => ((p >> 24) & 0xFF) != 255);
            T.Eq(0, transparent, "pixels with alpha != 255");
            var colors = new HashSet<int>();
            for (int i = 0; i < px.Length; i += 97) colors.Add(px[i]);
            T.True(colors.Count > 20, "screen content captured (" + colors.Count + " sampled colors)");
            foreach (MonitorSnapshot m in Cap.Monitors)
            {
                var c = new HashSet<int>();
                for (int y = m.Bounds.Top; y < m.Bounds.Bottom; y += 37)
                    for (int x = m.Bounds.Left; x < m.Bounds.Right; x += 41)
                        c.Add(px[y * Cap.Image.Width + x]);
                T.True(c.Count > 3, "monitor " + m.Bounds + " not blank (" + c.Count + " colors)");
            }
        }

        static void Windows()
        {
            var desktop = new Rectangle(Point.Empty, Cap.Image.Size);
            T.True(Cap.Windows.Count > 0, "windows found");
            var problems = new List<string>();
            foreach (Rectangle w in Cap.Windows)
            {
                if (w.Width < 8 || w.Height < 8) problems.Add("tiny " + w);
                if (!w.IntersectsWith(desktop)) problems.Add("off-desktop " + w);
            }
            int beyond = Cap.Windows.Count(w => !desktop.Contains(w));
            Console.WriteLine("      " + Cap.Windows.Count + " windows, " + beyond + " extend past the desktop (the editor clips them)");
            foreach (string line in Describe(Cap.Windows.Take(14))) Console.WriteLine("        " + line);
            bool taskbar = Cap.Windows.Any(w => w.Bottom == 1080 && w.Height < 120 && w.Width >= 1900 && w.Left == 0);
            if (!taskbar) T.Note("no primary taskbar rect found (auto-hide?)");
            T.Problems(problems, "window rects");
        }

        static void MonitorAt()
        {
            Rectangle m1 = Cap.Monitors.First(m => m.Bounds.X == 0).Bounds, m2 = Cap.Monitors.First(m => m.Bounds.X == 1920).Bounds;
            T.Eq(m1, Cap.MonitorAt(new Point(1919, 500)).Bounds, "x=1919");
            T.Eq(m2, Cap.MonitorAt(new Point(1920, 500)).Bounds, "x=1920");
            T.Eq(m1, Cap.MonitorAt(new Point(-100, -100)).Bounds, "nearest to top-left");
            T.Eq(m2, Cap.MonitorAt(new Point(5000, 2000)).Bounds, "nearest to bottom-right");
        }

        static void Crop()
        {
            foreach (var r in new[] { new Rectangle(100, 50, 300, 200), new Rectangle(1800, 900, 300, 180), new Rectangle(3790, 1030, 50, 50), new Rectangle(0, 0, 1, 1) })
                using (Bitmap c = ScreenCapture.Crop(Cap.Image, r))
                {
                    T.Eq(r.Size, c.Size, "crop size");
                    T.Eq(PixelFormat.Format32bppArgb, c.PixelFormat, "crop format");
                    Img.Diff d = Img.Compare(c, new Rectangle(Point.Empty, r.Size), Cap.Image, r);
                    T.True(d.Count == 0, "crop " + r + " exact: " + d);
                }
        }

        static void WithCursor()
        {
            using (CaptureResult c = ScreenCapture.CaptureAll(true, false))
            {
                T.Eq(0, c.Windows.Count, "no window enumeration");
                T.Eq(Cap.VirtualBounds, c.VirtualBounds, "same bounds");
            }
        }

        static void RealEditor()
        {
            using (var rig = new Rig(Cap, new Settings(), ownsCapture: false))
            {
                rig.Move(new Point(960, 540));
                rig.Snapshot("r01_real_hover_m1_125");
                rig.Move(new Point(2880, 540));
                rig.Snapshot("r02_real_hover_m2_100");
                rig.SetSelection(new Rectangle(560, 300, 800, 500));
                rig.Move(rig.ItemCenter("Arrow"));
                rig.Snapshot("r03_real_selection_m1_125");
                Img.SaveCrop(rig.FullRender(), rig.F<Rectangle>("barsArea"), "r03z_real_bars_zoom_125", 3);
                rig.SetSelection(new Rectangle(2480, 300, 800, 500));
                rig.Move(rig.ItemCenter("Copy for Claude"));
                rig.Snapshot("r04_real_selection_m2_100");
                Img.SaveCrop(rig.FullRender(), rig.F<Rectangle>("barsArea"), "r04z_real_bars_zoom_100", 3);
                rig.Key(Keys.Control | Keys.A);
                rig.Snapshot("r05_real_full_monitor", full: true);
            }
        }

        // Diagnostic only: class/process of the top window rects (matched by DWM frame bounds).
        static IEnumerable<string> Describe(IEnumerable<Rectangle> rects)
        {
            var all = new List<(Rectangle Rect, string Class, string Process)>();
            Native.EnumWindows((hwnd, data) =>
            {
                if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return true;
                if (Native.DwmGetWindowAttribute(hwnd, 14, out int cloaked, 4) == 0 && cloaked != 0) return true;
                if (Native.DwmGetWindowAttribute(hwnd, 9, out Native.RECT r, Marshal.SizeOf(typeof(Native.RECT))) != 0) return true;
                var cls = new StringBuilder(256);
                Native.GetClassName(hwnd, cls, 256);
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                string proc;
                try { proc = Process.GetProcessById((int)pid).ProcessName; } catch (Exception) { proc = "?"; }
                Rectangle rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                rect.Offset(-SystemInformation.VirtualScreen.X, -SystemInformation.VirtualScreen.Y);
                all.Add((rect, cls.ToString(), proc));
                return true;
            }, IntPtr.Zero);
            foreach (Rectangle w in rects)
            {
                var hit = all.FirstOrDefault(a => a.Rect == w);
                yield return w + "  " + (hit.Class ?? "(no match)") + "  " + hit.Process;
            }
        }

        static class Native
        {
            [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
            public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);
            [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr data);
            [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
            [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
            [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
            [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
            [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        }
    }
}
