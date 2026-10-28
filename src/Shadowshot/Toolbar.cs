using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace Shadowshot
{
    // Shared look of the overlay chrome (bars, pills, popups) plus small layout helpers.
    // Fonts are in pixels and follow the UI scale of the monitor in use.
    internal sealed class Chrome : IDisposable
    {
        public static readonly Color Accent = Color.FromArgb(0x7C, 0x5C, 0xFF);
        public static readonly Color Ink = Color.FromArgb(0xEC, 0xEC, 0xEC);

        public readonly SolidBrush BarBrush = new SolidBrush(Color.FromArgb(240, 32, 33, 38));
        public readonly SolidBrush HoverBrush = new SolidBrush(Color.FromArgb(62, 64, 72));
        public readonly SolidBrush PressedBrush = new SolidBrush(Color.FromArgb(84, 86, 96));
        public readonly SolidBrush AccentBrush = new SolidBrush(Accent);
        public readonly SolidBrush ClaudeBrush = new SolidBrush(Color.FromArgb(0xD9, 0x77, 0x57));
        public readonly SolidBrush ClaudeHoverBrush = new SolidBrush(Color.FromArgb(0xE4, 0x89, 0x6B));
        public readonly SolidBrush ClaudePressedBrush = new SolidBrush(Color.FromArgb(0xC0, 0x65, 0x48));
        public readonly SolidBrush PillBrush = new SolidBrush(Color.FromArgb(200, 0, 0, 0));
        public readonly SolidBrush InkBrush = new SolidBrush(Ink);
        public readonly SolidBrush LabelBrush = new SolidBrush(Color.FromArgb(0xDE, 0xDE, 0xDE));
        public readonly SolidBrush SeparatorBrush = new SolidBrush(Color.FromArgb(60, 255, 255, 255));
        public readonly Pen DashPen = new Pen(Color.Black) { DashPattern = new[] { 3f, 3f } };
        public readonly StringFormat Centered;

        // The getter returns a new wrapper of GDI+'s shared typographic format (no padding): never mutate it, clone instead.
        readonly StringFormat typographic = StringFormat.GenericTypographic;
        readonly Bitmap measureBitmap = new Bitmap(1, 1);
        readonly Graphics measure;

        public Chrome()
        {
            Centered = (StringFormat)typographic.Clone();
            Centered.Alignment = StringAlignment.Center;
            Centered.LineAlignment = StringAlignment.Center;
            Centered.FormatFlags |= StringFormatFlags.NoWrap;
            measure = Graphics.FromImage(measureBitmap);
            measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            SetScale(1f);
        }

        public float Scale { get; private set; }
        public Font TextFont { get; private set; }   // tooltips, palette
        public Font StrongFont { get; private set; } // Claude pill, hint
        public Font BoldFont { get; private set; }   // size label, size HUD

        public void SetScale(float scale)
        {
            if (scale == Scale) return;
            DisposeFonts();
            Scale = scale;
            TextFont = new Font("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            StrongFont = new Font("Segoe UI Semibold", 13f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            BoldFont = new Font("Segoe UI", 13.33f * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        }

        public int Px(float v) => (int)Math.Round(v * Scale);

        public Size Measure(string text, Font font) =>
            Size.Ceiling(measure.MeasureString(text, font, PointF.Empty, typographic));

        // A text pill sized to its text with the given padding.
        public Size PillSize(string text, Font font, float padX, float padY)
        {
            Size t = Measure(text, font);
            return new Size(t.Width + 2 * Px(padX), t.Height + 2 * Px(padY));
        }

        public void DrawPill(Graphics g, Rectangle r, string text, Font font, Brush back, Brush fore, float radius)
        {
            FillRounded(g, back, r, radius);
            g.DrawString(text, font, fore, r, Centered);
        }

        public static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = Math.Min(2 * radius, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            if (d <= 0)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Brush brush, RectangleF r, float radius)
        {
            using (GraphicsPath path = RoundedRect(r, radius))
                g.FillPath(brush, path);
        }

        // Moves r inside bounds (keeps its size; prefers the top-left edge when r is larger).
        public static Rectangle Fit(Rectangle r, Rectangle bounds)
        {
            r.X = Math.Max(bounds.Left, Math.Min(r.X, bounds.Right - r.Width));
            r.Y = Math.Max(bounds.Top, Math.Min(r.Y, bounds.Bottom - r.Height));
            return r;
        }

        public static Rectangle Union(Rectangle a, Rectangle b) =>
            a.IsEmpty ? b : b.IsEmpty ? a : Rectangle.Union(a, b);

        public void Dispose()
        {
            DisposeFonts();
            foreach (Brush b in new Brush[] { BarBrush, HoverBrush, PressedBrush, AccentBrush, ClaudeBrush, ClaudeHoverBrush, ClaudePressedBrush, PillBrush, InkBrush, LabelBrush, SeparatorBrush })
                b.Dispose();
            DashPen.Dispose();
            Centered.Dispose();
            typographic.Dispose();
            measure.Dispose();
            measureBitmap.Dispose();
        }

        void DisposeFonts()
        {
            TextFont?.Dispose();
            StrongFont?.Dispose();
            BoldFont?.Dispose();
        }
    }

    // A bar of icon buttons with separators; the editor uses one vertical (tools) and one horizontal (actions).
    internal sealed class Toolbar
    {
        public sealed class Item
        {
            public Item(object tag, string icon, string tooltip, bool wide)
            {
                Tag = tag;
                Icon = icon;
                Tooltip = tooltip;
                Wide = wide;
            }

            public readonly object Tag;      // null = separator
            public readonly string Icon;
            public readonly string Tooltip;
            public readonly bool Wide;       // the Claude pill: sparkle + "Claude"
            public bool Active;
            public bool Enabled = true;
            public Color? Swatch;            // drawn instead of an icon (color button)
            public Rectangle Bounds;

            public bool IsSeparator => Tag == null;
        }

        const string ClaudeText = "Claude";

        readonly bool vertical;
        readonly List<Item> items = new List<Item>();

        public Toolbar(bool vertical)
        {
            this.vertical = vertical;
        }

        public IReadOnlyList<Item> Items => items;
        public Rectangle Bounds { get; private set; }

        public Item Add(object tag, string icon, string tooltip, bool wide = false)
        {
            var item = new Item(tag, icon, tooltip, wide);
            items.Add(item);
            return item;
        }

        public void AddSeparator() => items.Add(new Item(null, null, null, false));

        // Vertical bar: right of the selection, bottom-aligned (fallbacks: left side, then inside the right edge).
        // Horizontal bar: below the selection, right-aligned (fallbacks: above, then inside the bottom edge).
        public void Layout(Rectangle selection, Rectangle monitor, Chrome chrome)
        {
            int button = chrome.Px(32), pad = chrome.Px(4), separator = chrome.Px(9), gap = chrome.Px(5);
            int wide = chrome.Px(38) + chrome.Measure(ClaudeText, chrome.StrongFont).Width;
            int along = pad;
            foreach (Item item in items)
            {
                int length = item.IsSeparator ? separator : item.Wide ? wide : button;
                item.Bounds = vertical ? new Rectangle(pad, along, button, length) : new Rectangle(along, pad, length, button);
                along += length;
            }
            along += pad;
            Size size = vertical ? new Size(button + 2 * pad, along) : new Size(along, button + 2 * pad);

            int x, y;
            if (vertical)
            {
                bool right = selection.Right + gap + size.Width <= monitor.Right, left = selection.Left - gap - size.Width >= monitor.Left;
                x = right ? selection.Right + gap : left ? selection.Left - gap - size.Width : selection.Right - gap - size.Width;
                y = right || left ? selection.Bottom - size.Height
                  : selection.Bottom - gap - size.Height; // inside: inset from the bottom like the action bar
            }
            else
            {
                y = selection.Bottom + gap + size.Height <= monitor.Bottom ? selection.Bottom + gap
                  : selection.Top - gap - size.Height >= monitor.Top ? selection.Top - gap - size.Height
                  : selection.Bottom - gap - size.Height;
                x = selection.Right - size.Width;
            }
            Bounds = Chrome.Fit(new Rectangle(new Point(x, y), size), monitor);
            foreach (Item item in items)
                item.Bounds.Offset(Bounds.Location);
        }

        public void Offset(int dx, int dy)
        {
            Rectangle b = Bounds;
            b.Offset(dx, dy);
            Bounds = b;
            foreach (Item item in items)
                item.Bounds.Offset(dx, dy);
        }

        public Item HitTest(Point p)
        {
            if (!Bounds.Contains(p)) return null;
            foreach (Item item in items)
                if (!item.IsSeparator && item.Enabled && item.Bounds.Contains(p))
                    return item;
            return null;
        }

        // Places a popup (tooltip, palette) beside an item on the side of the bar away from the selection,
        // flipping to the other side when it doesn't fit on the monitor.
        public Rectangle PopupBounds(Item item, Size size, Rectangle selection, Rectangle monitor, Chrome chrome)
        {
            int gap = chrome.Px(6);
            int x, y;
            if (vertical)
            {
                int after = Bounds.Right + gap, before = Bounds.Left - gap - size.Width;
                bool right = Bounds.Left + Bounds.Width / 2 >= selection.Left + selection.Width / 2;
                x = right ? (after + size.Width <= monitor.Right ? after : before)
                          : (before >= monitor.Left ? before : after);
                y = item.Bounds.Top + (item.Bounds.Height - size.Height) / 2;
            }
            else
            {
                int after = Bounds.Bottom + gap, before = Bounds.Top - gap - size.Height;
                bool below = Bounds.Top + Bounds.Height / 2 >= selection.Top + selection.Height / 2;
                y = below ? (after + size.Height <= monitor.Bottom ? after : before)
                          : (before >= monitor.Top ? before : after);
                x = item.Bounds.Left + (item.Bounds.Width - size.Width) / 2;
            }
            return Chrome.Fit(new Rectangle(new Point(x, y), size), monitor);
        }

        public void Paint(Graphics g, Chrome chrome, Item hover, Item pressed)
        {
            float s = chrome.Scale;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            Chrome.FillRounded(g, chrome.BarBrush, Bounds, 6 * s);
            foreach (Item item in items)
            {
                Rectangle b = item.Bounds;
                if (item.IsSeparator)
                {
                    int inset = chrome.Px(6);
                    if (vertical) g.FillRectangle(chrome.SeparatorBrush, b.Left + inset, b.Top + b.Height / 2, b.Width - 2 * inset, 1);
                    else g.FillRectangle(chrome.SeparatorBrush, b.Left + b.Width / 2, b.Top + inset, 1, b.Height - 2 * inset);
                    continue;
                }

                bool hot = item == hover && item.Enabled;
                bool down = hot && item == pressed;
                RectangleF face = RectangleF.Inflate(b, -2 * s, -2 * s);
                if (item.Wide)
                {
                    Chrome.FillRounded(g, down ? chrome.ClaudePressedBrush : hot ? chrome.ClaudeHoverBrush : chrome.ClaudeBrush, face, 5 * s);
                    float icon = 16 * s;
                    var iconBox = new RectangleF(Snap(face.X + 10 * s), Snap(face.Y + (face.Height - icon) / 2), icon, icon);
                    Icons.Draw(g, item.Icon, iconBox, Color.White);
                    var textBox = new RectangleF(iconBox.Right + 5 * s, face.Y, face.Right - iconBox.Right - 5 * s - 8 * s, face.Height);
                    g.DrawString(ClaudeText, chrome.StrongFont, Brushes.White, textBox, chrome.Centered);
                    continue;
                }

                Brush back = item.Active ? chrome.AccentBrush : down ? chrome.PressedBrush : hot ? chrome.HoverBrush : null;
                if (back != null) Chrome.FillRounded(g, back, face, 5 * s);
                if (item.Swatch is Color swatch)
                {
                    float side = 16 * s;
                    var r = new RectangleF(Snap(b.X + (b.Width - side) / 2), Snap(b.Y + (b.Height - side) / 2), side, side);
                    using (var fill = new SolidBrush(swatch))
                        Chrome.FillRounded(g, fill, r, 4 * s);
                    using (var ring = new Pen(Color.FromArgb(150, 255, 255, 255)))
                    using (GraphicsPath path = Chrome.RoundedRect(r, 4 * s))
                        g.DrawPath(ring, path);
                }
                else
                {
                    float side = 18 * s;
                    var box = new RectangleF(Snap(b.X + (b.Width - side) / 2), Snap(b.Y + (b.Height - side) / 2), side, side);
                    Icons.Draw(g, item.Icon, box, item.Enabled ? Chrome.Ink : Color.FromArgb(90, Chrome.Ink));
                }
            }
        }

        static float Snap(float v) => (float)Math.Round(v);
    }
}
