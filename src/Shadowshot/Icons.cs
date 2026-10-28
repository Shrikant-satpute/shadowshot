using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace Shadowshot
{
    // Toolbar glyphs, drawn as anti-aliased GDI+ vectors on a 24-unit design grid scaled into the box.
    public static class Icons
    {
        const float Grid = 24f;
        const float Stroke = 2.1f; // ≈1.6 px at an 18 px box

        public static void Draw(Graphics g, string name, RectangleF box, Color color)
        {
            GraphicsState state = g.Save();
            try
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(box.X, box.Y);
                g.ScaleTransform(box.Width / Grid, box.Height / Grid);
                using (var pen = new Pen(color, Stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                using (var brush = new SolidBrush(color))
                {
                    switch (name)
                    {
                        case "pen": Pencil(g, pen); break;
                        case "line": g.DrawLine(pen, 5f, 19f, 19f, 5f); break;
                        case "arrow":
                            g.DrawLine(pen, 5f, 19f, 18f, 6f);
                            g.DrawLines(pen, new[] { new PointF(10f, 6f), new PointF(18f, 6f), new PointF(18f, 14f) });
                            break;
                        case "rectangle": DrawRounded(g, pen, new RectangleF(3.5f, 5.5f, 17f, 13f), 2f); break;
                        case "ellipse": g.DrawEllipse(pen, 3f, 5f, 18f, 14f); break;
                        case "marker": Highlighter(g, pen, brush, color); break;
                        case "text":
                            g.DrawLines(pen, new[] { new PointF(5f, 7.5f), new PointF(5f, 5f), new PointF(19f, 5f), new PointF(19f, 7.5f) });
                            g.DrawLine(pen, 12f, 5f, 12f, 19f);
                            g.DrawLine(pen, 9f, 19f, 15f, 19f);
                            break;
                        case "step":
                            g.DrawEllipse(pen, 3f, 3f, 18f, 18f);
                            g.DrawLines(pen, new[] { new PointF(9.8f, 9.4f), new PointF(12.6f, 7.5f), new PointF(12.6f, 16.5f) });
                            break;
                        case "pixelate": Checker(g, color); break;
                        case "undo": CurvedArrow(g, pen); break;
                        case "redo":
                            using (var mirror = new Matrix(-1f, 0f, 0f, 1f, Grid, 0f))
                                g.MultiplyTransform(mirror);
                            CurvedArrow(g, pen);
                            break;
                        case "claude":
                            Sparkle(g, brush, new PointF(10.5f, 13.5f), 8.5f);
                            Sparkle(g, brush, new PointF(19f, 5f), 3.5f);
                            break;
                        case "print": Printer(g, pen, brush); break;
                        case "copy": Copy(g, pen); break;
                        case "save": Floppy(g, pen); break;
                        case "close":
                            g.DrawLine(pen, 6.5f, 6.5f, 17.5f, 17.5f);
                            g.DrawLine(pen, 17.5f, 6.5f, 6.5f, 17.5f);
                            break;
                        default: throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown icon");
                    }
                }
            }
            finally
            {
                g.Restore(state);
            }
        }

        static void Pencil(Graphics g, Pen pen)
        {
            g.DrawPolygon(pen, new[] { new PointF(4f, 20f), new PointF(5f, 15.5f), new PointF(15.5f, 5f), new PointF(19f, 8.5f), new PointF(8.5f, 19f) });
            g.DrawLine(pen, 5f, 15.5f, 8.5f, 19f);
            g.DrawLine(pen, 13f, 7.5f, 16.5f, 11f);
        }

        static void Highlighter(Graphics g, Pen pen, Brush brush, Color color)
        {
            using (var ink = new SolidBrush(Color.FromArgb(color.A * 110 / 255, color)))
                g.FillRectangle(ink, 3f, 18f, 18f, 3.5f);
            GraphicsState state = g.Save();
            // Drawn pointing down in local coordinates, then tilted so the chisel tip touches the ink bar.
            g.TranslateTransform(12.5f, 10.5f);
            g.RotateTransform(45f);
            DrawRounded(g, pen, new RectangleF(-3.5f, -8.5f, 7f, 9f), 1.5f);
            g.DrawPolygon(pen, new[] { new PointF(-3.5f, 0.5f), new PointF(3.5f, 0.5f), new PointF(2f, 3.5f), new PointF(-2f, 3.5f) });
            g.FillPolygon(brush, new[] { new PointF(-2f, 3.5f), new PointF(2f, 3.5f), new PointF(2f, 5f), new PointF(-2f, 6.5f) });
            g.Restore(state);
        }

        static void Checker(Graphics g, Color color)
        {
            const float origin = 4f, cell = 16f / 3f;
            using (var solid = new SolidBrush(color))
            using (var faint = new SolidBrush(Color.FromArgb(color.A * 80 / 255, color)))
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 3; col++)
                        g.FillRectangle((row + col) % 2 == 0 ? solid : faint, origin + col * cell, origin + row * cell, cell, cell);
        }

        static void CurvedArrow(Graphics g, Pen pen)
        {
            using (var path = new GraphicsPath())
            {
                path.AddLine(5f, 9f, 14.5f, 9f);
                path.AddArc(9.5f, 9f, 10f, 10f, 270f, 180f);
                path.AddLine(14.5f, 19f, 9f, 19f);
                g.DrawPath(pen, path);
            }
            g.DrawLines(pen, new[] { new PointF(9f, 5f), new PointF(5f, 9f), new PointF(9f, 13f) });
        }

        static void Sparkle(Graphics g, Brush brush, PointF c, float r)
        {
            // Four tips joined by curves pulled toward the center give the concave "twinkle" outline.
            float k = r * 0.14f;
            using (var path = new GraphicsPath())
            {
                path.AddBezier(c.X, c.Y - r, c.X + k, c.Y - k, c.X + k, c.Y - k, c.X + r, c.Y);
                path.AddBezier(c.X + r, c.Y, c.X + k, c.Y + k, c.X + k, c.Y + k, c.X, c.Y + r);
                path.AddBezier(c.X, c.Y + r, c.X - k, c.Y + k, c.X - k, c.Y + k, c.X - r, c.Y);
                path.AddBezier(c.X - r, c.Y, c.X - k, c.Y - k, c.X - k, c.Y - k, c.X, c.Y - r);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }

        static void Printer(Graphics g, Pen pen, Brush brush)
        {
            g.DrawLines(pen, new[] { new PointF(7f, 9.5f), new PointF(7f, 3.5f), new PointF(17f, 3.5f), new PointF(17f, 9.5f) });
            g.DrawLines(pen, new[]
            {
                new PointF(7f, 16.5f), new PointF(3.5f, 16.5f), new PointF(3.5f, 9.5f),
                new PointF(20.5f, 9.5f), new PointF(20.5f, 16.5f), new PointF(17f, 16.5f),
            });
            g.DrawRectangle(pen, 7f, 13.5f, 10f, 7f);
            g.FillEllipse(brush, 16.6f, 11f, 1.9f, 1.9f);
        }

        static void Copy(Graphics g, Pen pen)
        {
            DrawRounded(g, pen, new RectangleF(9f, 9f, 11.5f, 11.5f), 2f);
            // The back sheet is hidden where the front sheet (plus a small gap) covers it.
            using (var front = new Region(new RectangleF(6.5f, 6.5f, 16.5f, 16.5f)))
                g.ExcludeClip(front);
            DrawRounded(g, pen, new RectangleF(3.5f, 3.5f, 11.5f, 11.5f), 2f);
        }

        static void Floppy(Graphics g, Pen pen)
        {
            g.DrawPolygon(pen, new[] { new PointF(4f, 4f), new PointF(16.5f, 4f), new PointF(20f, 7.5f), new PointF(20f, 20f), new PointF(4f, 20f) });
            g.DrawLines(pen, new[] { new PointF(8f, 4f), new PointF(8f, 8.5f), new PointF(15f, 8.5f), new PointF(15f, 4f) });
            g.DrawLines(pen, new[] { new PointF(7.5f, 20f), new PointF(7.5f, 13.5f), new PointF(16.5f, 13.5f), new PointF(16.5f, 20f) });
        }

        static void DrawRounded(Graphics g, Pen pen, RectangleF r, float radius)
        {
            using (GraphicsPath path = Chrome.RoundedRect(r, radius))
                g.DrawPath(pen, path);
        }
    }
}
