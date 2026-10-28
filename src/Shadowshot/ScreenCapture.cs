using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace Shadowshot
{
    public sealed class MonitorSnapshot
    {
        public Rectangle Bounds;   // capture space
        public float Scale;        // effective dpi / 96
        public bool Primary;
    }

    public sealed class CaptureResult : IDisposable
    {
        public Bitmap Image;                 // Format32bppPArgb, size == virtual desktop
        public Rectangle VirtualBounds;      // screen coordinates of the virtual desktop
        public List<MonitorSnapshot> Monitors = new List<MonitorSnapshot>();
        public List<Rectangle> Windows = new List<Rectangle>();
        public Point Cursor;                 // capture space

        public MonitorSnapshot MonitorAt(Point p)
        {
            MonitorSnapshot nearest = null;
            long nearestDistance = long.MaxValue;
            foreach (MonitorSnapshot m in Monitors)
            {
                if (m.Bounds.Contains(p)) return m;
                long dx = Math.Max(0, Math.Max(m.Bounds.Left - p.X, p.X - (m.Bounds.Right - 1)));
                long dy = Math.Max(0, Math.Max(m.Bounds.Top - p.Y, p.Y - (m.Bounds.Bottom - 1)));
                long distance = dx * dx + dy * dy;
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = m;
                }
            }
            return nearest;
        }

        public void Dispose()
        {
            Image?.Dispose();
            Image = null;
        }
    }

    public static class ScreenCapture
    {
        public static CaptureResult CaptureAll(bool includeCursor, bool collectWindows)
        {
            var virtualBounds = new Rectangle(
                Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN),
                Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN), Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN));

            var result = new CaptureResult { VirtualBounds = virtualBounds };
            result.Image = CaptureScreen(virtualBounds, includeCursor);
            result.Monitors = CollectMonitors(virtualBounds);
            if (collectWindows) result.Windows = CollectWindows(virtualBounds);
            Native.GetCursorPos(out Native.POINT cursor);
            result.Cursor = new Point(cursor.X - virtualBounds.X, cursor.Y - virtualBounds.Y);
            return result;
        }

        public static Bitmap Crop(Bitmap src, Rectangle r)
        {
            var bitmap = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
            }
            return bitmap;
        }

        // One BitBlt into a top-down 32bpp DIB section, then copy into a GDI+ bitmap with alpha forced
        // to 255: GDI leaves the alpha byte at 0, which GDI+ would treat as fully transparent.
        // Peak memory is two full-desktop buffers (DIB + result).
        static Bitmap CaptureScreen(Rectangle bounds, bool includeCursor)
        {
            int width = bounds.Width, height = bounds.Height;
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memoryDc = Native.CreateCompatibleDC(screenDc);
            var header = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)),
                biWidth = width,
                biHeight = -height,   // negative = top-down rows, same order as GDI+
                biPlanes = 1,
                biBitCount = 32,
            };
            IntPtr dib = Native.CreateDIBSection(screenDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
            IntPtr previous = IntPtr.Zero;
            try
            {
                if (dib == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not allocate the screenshot buffer.");
                previous = Native.SelectObject(memoryDc, dib);
                if (!Native.BitBlt(memoryDc, 0, 0, width, height, screenDc, bounds.X, bounds.Y, Native.SRCCOPY | Native.CAPTUREBLT))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not capture the screen.");
                if (includeCursor) DrawCursor(memoryDc, bounds.Location);
                Native.GdiFlush();
                return ToOpaqueBitmap(bits, width, height);
            }
            finally
            {
                if (previous != IntPtr.Zero) Native.SelectObject(memoryDc, previous);
                if (dib != IntPtr.Zero) Native.DeleteObject(dib);
                Native.DeleteDC(memoryDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        static Bitmap ToOpaqueBitmap(IntPtr bits, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var row = new int[width];
                long sourceStride = width * 4L;
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(new IntPtr(bits.ToInt64() + y * sourceStride), row, 0, width);
                    for (int x = 0; x < width; x++) row[x] |= unchecked((int)0xFF000000);
                    Marshal.Copy(row, 0, new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), width);
                }
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
            return bitmap;
        }

        static void DrawCursor(IntPtr hdc, Point origin)
        {
            var info = new Native.CURSORINFO { cbSize = Marshal.SizeOf(typeof(Native.CURSORINFO)) };
            if (!Native.GetCursorInfo(ref info) || (info.flags & Native.CURSOR_SHOWING) == 0) return;
            IntPtr icon = Native.CopyIcon(info.hCursor);
            if (icon == IntPtr.Zero) return;
            try
            {
                if (!Native.GetIconInfo(icon, out Native.ICONINFO iconInfo)) return;
                if (iconInfo.hbmMask != IntPtr.Zero) Native.DeleteObject(iconInfo.hbmMask);
                if (iconInfo.hbmColor != IntPtr.Zero) Native.DeleteObject(iconInfo.hbmColor);
                Native.DrawIconEx(hdc,
                    info.ptScreenPos.X - iconInfo.xHotspot - origin.X, info.ptScreenPos.Y - iconInfo.yHotspot - origin.Y,
                    icon, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL);
            }
            finally
            {
                Native.DestroyIcon(icon);
            }
        }

        static List<MonitorSnapshot> CollectMonitors(Rectangle virtualBounds)
        {
            var monitors = new List<MonitorSnapshot>();
            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, hdc, rect, data) =>
            {
                var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
                if (!Native.GetMonitorInfo(monitor, ref info)) return true;
                Rectangle bounds = info.rcMonitor.ToRectangle();
                bounds.Offset(-virtualBounds.X, -virtualBounds.Y);
                float scale = Native.GetDpiForMonitor(monitor, Native.MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 ? dpiX / 96f : 1f;
                monitors.Add(new MonitorSnapshot { Bounds = bounds, Scale = scale, Primary = (info.dwFlags & Native.MONITORINFOF_PRIMARY) != 0 });
                return true;
            }, IntPtr.Zero);

            // Callers rely on MonitorAt never returning null.
            if (monitors.Count == 0)
                monitors.Add(new MonitorSnapshot { Bounds = new Rectangle(Point.Empty, virtualBounds.Size), Scale = 1f, Primary = true });
            return monitors;
        }

        static List<Rectangle> CollectWindows(Rectangle virtualBounds)
        {
            var windows = new List<Rectangle>();
            uint ownProcess = Native.GetCurrentProcessId();
            var className = new StringBuilder(256);
            Native.EnumWindows((hwnd, data) =>   // z-order, top-most first
            {
                if (TryGetWindowBounds(hwnd, virtualBounds, ownProcess, className, out Rectangle bounds))
                {
                    bounds.Offset(-virtualBounds.X, -virtualBounds.Y);
                    windows.Add(bounds);
                }
                return true;
            }, IntPtr.Zero);
            return windows;
        }

        static bool TryGetWindowBounds(IntPtr hwnd, Rectangle virtualBounds, uint ownProcess, StringBuilder className, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (!Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)) return false;
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
            Native.GetWindowThreadProcessId(hwnd, out uint process);
            if (process == ownProcess) return false;

            className.Clear();
            Native.GetClassName(hwnd, className, className.Capacity);
            string cls = className.ToString();
            if (cls == "Progman" || cls == "WorkerW") return false;

            int exStyle = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            if ((exStyle & Native.WS_EX_TRANSPARENT) != 0) return false;
            bool taskbar = cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";
            if ((exStyle & Native.WS_EX_TOOLWINDOW) != 0 && !taskbar) return false;

            // Extended frame bounds exclude the invisible resize borders that GetWindowRect includes.
            if (Native.DwmGetWindowAttribute(hwnd, Native.DWMWA_EXTENDED_FRAME_BOUNDS, out Native.RECT rect, Marshal.SizeOf(typeof(Native.RECT))) != 0
                && !Native.GetWindowRect(hwnd, out rect))
                return false;
            bounds = rect.ToRectangle();
            if (bounds.Width < 8 || bounds.Height < 8) return false;
            // Full-desktop layered windows are invisible overlays (screen recorders, game bars, ...).
            if ((exStyle & Native.WS_EX_LAYERED) != 0 && bounds.Contains(virtualBounds)) return false;
            return true;
        }

        static class Native
        {
            public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
            public const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
            public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
            public const int GWL_EXSTYLE = -20;
            public const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000;
            public const int CURSOR_SHOWING = 0x1, DI_NORMAL = 0x3;
            public const int MONITORINFOF_PRIMARY = 0x1, MDT_EFFECTIVE_DPI = 0;

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT
            {
                public int Left, Top, Right, Bottom;
                public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X, Y; }

            [StructLayout(LayoutKind.Sequential)]
            public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public int dwFlags; }

            [StructLayout(LayoutKind.Sequential)]
            public struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }

            [StructLayout(LayoutKind.Sequential)]
            public struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

            [StructLayout(LayoutKind.Sequential)]
            public struct BITMAPINFOHEADER
            {
                public int biSize, biWidth, biHeight;
                public short biPlanes, biBitCount;
                public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
            }

            public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr data);
            public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

            [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
            [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
            [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
            [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
            [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
            [DllImport("gdi32.dll", SetLastError = true)]
            public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER header, uint usage, out IntPtr bits, IntPtr section, uint offset);
            [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
            [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
            [DllImport("gdi32.dll", SetLastError = true)]
            public static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height, IntPtr src, int srcX, int srcY, int rop);
            [DllImport("gdi32.dll")] public static extern bool GdiFlush();

            [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
            [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
            [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

            [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc proc, IntPtr data);
            [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
            [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
            [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
            [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int maxCount);
            [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
            [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
            [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
            [DllImport("kernel32.dll")] public static extern uint GetCurrentProcessId();

            [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
            [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO info);
            [DllImport("user32.dll")] public static extern IntPtr CopyIcon(IntPtr icon);
            [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
            [DllImport("user32.dll")]
            public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);
            [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        }
    }
}
