using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace Shadowshot.Tests
{
    static class EditorTests
    {
        static readonly Rectangle S = new Rectangle(200, 150, 500, 350);          // on the 125 % monitor
        static readonly Rectangle Big = new Rectangle(100, 100, 1500, 850);       // room for every tool, bars still outside

        static Rig Two() => new Rig(Fx.TwoMonitors());

        static Point P(int x, int y) => new Point(x, y);

        public static void Run()
        {
            T.Run("Editor.Hover.WindowMonitorClippedSpanning", Hover);
            T.Run("Editor.ClickGrabsHoveredWindowOrMonitor", ClickGrab);
            T.Run("Editor.RubberBandSelection", RubberBand);
            T.Run("Editor.MoveSelection", MoveSelection);
            T.Run("Editor.ResizeEachHandle", ResizeHandles);
            T.Run("Editor.ResizeCrossingOppositeEdge", ResizeCrossing);
            T.Run("Editor.HandlesActiveWithTool", HandlesWithTool);
            T.Run("Editor.KeyboardNudgeAndResize", Nudge);
            T.Run("Editor.CtrlA.TwoMonitors", CtrlATwo);
            T.Run("Editor.CtrlA.SingleMonitor", CtrlASingle);
            T.Run("Editor.DoubleClickTogglesFullScreen", DoubleClick);
            T.Run("Editor.EveryToolDraws", EveryTool);
            T.Run("Editor.ToolbarsHiddenWhileDragging", BarsHiddenWhileDragging);
            T.Run("Editor.StepNumberingUndoRedo", StepNumbering);
            T.Run("Editor.StepDragSetsTip", StepTip);
            T.Run("Editor.StepLabelTyping", StepLabel);
            T.Run("Editor.TextTyping", TextTyping);
            T.Run("Editor.WheelSizePersistsAndHud", Wheel);
            T.Run("Editor.EscSemantics", Esc);
            T.Run("Editor.KeyboardCommands", Commands);
            T.Run("Editor.CommandsCommitTextFirst", CommandsCommit);
            T.Run("Editor.RightClick", RightClick);
            T.Run("Editor.CtrlDragAutoCopy", CtrlDrag);
            T.Run("Editor.ToolbarButtonsTooltipsPalette", Toolbar);
            T.Run("Editor.Cursors", CursorsTest);
            T.Run("Editor.BuildClaudeNotes", Notes);
            T.Run("Editor.RenderResultEqualsCompose", RenderResult);
            T.Run("Editor.ScreenMatchesExport (WYSIWYG)", Wysiwyg);
            T.Run("Editor.KeepSelectionRestoresLastSelection", KeepSelection);
            T.Run("Editor.AnnotationsSurviveNewSelection", AnnotationsSurvive);
            T.Run("Editor.ToolKeysNeedSelection", ToolKeysNeedSelection);
            T.Run("Editor.SingleMonitorSnapshots", SingleMonitor);
        }

        static void Hover()
        {
            using (Rig rig = Two())
            {
                T.True(rig.E.Selection.IsEmpty && !rig.E.ToolbarsVisible, "no selection/bars at start");
                T.Eq(Fx.Dialog, rig.F<Rectangle>("hoverRect"), "hover = top-most window under the cursor");
                T.Eq(Cursors.Cross, rig.E.Cursor, "cursor before selecting");
                using (Bitmap full = rig.FullRender())
                {
                    Color inside = full.GetPixel(600, 450), orig = rig.Capture.Image.GetPixel(600, 450);
                    T.True(Img.Near(inside, orig, 0), "hovered window undimmed: " + inside + " vs " + orig);
                    Color dim = full.GetPixel(300, 700), orig2 = rig.Capture.Image.GetPixel(300, 700);
                    T.True(Img.Near(dim, Img.Half(orig2), 2), "outside dimmed to 50%: " + dim + " vs " + orig2);
                    T.Eq(Chrome.Accent.ToArgb(), full.GetPixel(Fx.Dialog.Left, Fx.Dialog.Top + 100).ToArgb(), "2px accent border (outer px)");
                    T.Eq(Chrome.Accent.ToArgb(), full.GetPixel(Fx.Dialog.Left + 1, Fx.Dialog.Top + 100).ToArgb(), "2px accent border (inner px)");
                }
                Rectangle hint = rig.F<Rectangle>("hintRect");
                T.True(Fx.M1.Contains(hint), "hint inside monitor 1: " + hint);
                T.True(Math.Abs(hint.Left + hint.Width / 2 - 960) <= 1, "hint centered: " + hint);
                T.True(Math.Abs(hint.Top + hint.Height / 2 - 180) <= 1, "hint in the upper third: " + hint);
                int hint125 = hint.Height;
                rig.Snapshot("e01_hover_window_m1_125", full: true);

                rig.Move(P(2600, 980));
                T.Eq(Fx.M2, rig.F<Rectangle>("hoverRect"), "no window → monitor 2");
                hint = rig.F<Rectangle>("hintRect");
                T.True(Fx.M2.Contains(hint) && Math.Abs(hint.Left + hint.Width / 2 - 2880) <= 1, "hint follows the cursor's monitor: " + hint);
                T.True(Math.Abs(hint125 / (double)hint.Height - 1.25) < 0.12, "hint scales with DPI: " + hint125 + " vs " + hint.Height);
                rig.Snapshot("e02_hover_monitor_m2_100");

                rig.Move(P(3600, 100));
                T.Eq(Rectangle.FromLTRB(3500, 0, 3840, 260), rig.F<Rectangle>("hoverRect"), "off-desktop window clipped");
                rig.Move(P(1700, 700));
                T.Eq(Fx.Spanning, rig.F<Rectangle>("hoverRect"), "window spanning monitors");
                rig.Snapshot("e03_hover_spanning");
                rig.Move(P(960, 1050));
                T.Eq(Fx.Taskbar1, rig.F<Rectangle>("hoverRect"), "taskbar hover");
            }
        }

        static void ClickGrab()
        {
            using (Rig rig = Two())
            {
                rig.Click(P(600, 400));
                T.Eq(Fx.Dialog, rig.E.Selection, "click grabs the hovered window");
                T.True(rig.E.ToolbarsVisible, "bars shown");
                T.Eq(0, rig.Commands.Count, "no command");
                rig.Snapshot("e04_grab_dialog_bars", full: true);
            }
            using (Rig rig = Two())
            {
                rig.Down(P(600, 400));
                rig.Move(P(603, 402), Keys.None, MouseButtons.Left);
                T.True(rig.E.Selection.IsEmpty, "< 4 px: no rubber band yet");
                rig.Up(P(603, 402));
                T.Eq(Fx.Dialog, rig.E.Selection, "jitter < 4 px still grabs");
            }
            using (Rig rig = Two())
            {
                rig.Move(P(2600, 980));
                rig.Click(P(2600, 980));
                T.Eq(Fx.M2, rig.E.Selection, "click on empty desktop grabs the monitor");
            }
        }

        static void RubberBand()
        {
            using (Rig rig = Two())
            {
                rig.Down(P(200, 150));
                rig.Move(P(220, 170), Keys.None, MouseButtons.Left);
                T.Eq(Rectangle.FromLTRB(200, 150, 220, 170), rig.E.Selection, "live selection");
                T.True(!rig.E.ToolbarsVisible, "bars hidden while dragging");
                rig.Move(P(700, 500), Keys.None, MouseButtons.Left);
                rig.Snapshot("e05_rubber_band_dragging");
                rig.Up(P(700, 500));
                T.Eq(S, rig.E.Selection, "selection after release");
                T.True(rig.E.ToolbarsVisible, "bars shown on release");
                rig.Snapshot("e06_selection_bars_m1_125", full: true);
                Img.SaveCrop(rig.FullRender(), rig.F<Rectangle>("barsArea"), "e06z_bars_zoom_125", 3);
            }
            using (Rig rig = Two())
            {
                rig.Drag(P(900, 700), P(300, 200));
                T.Eq(Rectangle.FromLTRB(300, 200, 900, 700), rig.E.Selection, "reverse drag normalized");
            }
            using (Rig rig = Two())
            {
                rig.Drag(P(100, 100), P(-50, -60));
                T.Eq(Rectangle.FromLTRB(0, 0, 100, 100), rig.E.Selection, "clamped to the desktop");
                rig.Drag(P(3800, 1000), P(4000, 1200));
                T.Eq(Rectangle.FromLTRB(3800, 1000, 3840, 1080), rig.E.Selection, "clamped bottom-right");
            }
            using (Rig rig = Two())
            {
                rig.Drag(P(1700, 300), P(2300, 700));
                T.Eq(Rectangle.FromLTRB(1700, 300, 2300, 700), rig.E.Selection, "selection across monitors");
                rig.Snapshot("e06b_selection_spanning_monitors");
            }
        }

        static void MoveSelection()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(S);
                T.Eq(Cursors.SizeAll, CursorAt(rig, P(400, 300)), "SizeAll inside the selection");
                rig.Down(P(400, 300));
                rig.Move(P(500, 350), Keys.None, MouseButtons.Left);
                T.Eq(new Rectangle(300, 200, 500, 350), rig.E.Selection, "moved");
                T.True(!rig.E.ToolbarsVisible, "bars hidden while moving");
                rig.Up(P(500, 350));
                T.True(rig.E.ToolbarsVisible, "bars back");
                rig.Drag(P(450, 350), P(-1000, -1000));
                T.Eq(new Rectangle(0, 0, 500, 350), rig.E.Selection, "clamped top-left");
                rig.Drag(P(100, 100), P(9000, 9000));
                T.Eq(new Rectangle(3340, 730, 500, 350), rig.E.Selection, "clamped bottom-right");
                T.Eq(0, rig.E.Annotations.Count, "no annotations");
            }
        }

        static Cursor CursorAt(Rig rig, Point p)
        {
            rig.Move(p);
            return rig.E.Cursor;
        }

        static readonly Point[] HandleDirs =
        {
            new Point(-1, -1), new Point(1, -1), new Point(1, 1), new Point(-1, 1),
            new Point(0, -1), new Point(1, 0), new Point(0, 1), new Point(-1, 0),
        };

        static Point HandleCenter(Rectangle s, Point d) => new Point(
            d.X < 0 ? s.Left - 1 : d.X > 0 ? s.Right : s.Left + s.Width / 2,
            d.Y < 0 ? s.Top - 1 : d.Y > 0 ? s.Bottom : s.Top + s.Height / 2);

        static Cursor HandleCursor(Point d) =>
            d.X == 0 ? Cursors.SizeNS : d.Y == 0 ? Cursors.SizeWE : d.X == d.Y ? Cursors.SizeNWSE : Cursors.SizeNESW;

        static Rectangle Expected(Rectangle s, Point d, int dx, int dy)
        {
            int l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;
            if (d.X < 0) l += dx; else if (d.X > 0) r += dx;
            if (d.Y < 0) t += dy; else if (d.Y > 0) b += dy;
            return Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
        }

        static void ResizeHandles()
        {
            var s = new Rectangle(400, 300, 600, 400);
            foreach (Point d in HandleDirs)
                foreach (var delta in new[] { P(30, 20), P(-25, -15) })
                    using (Rig rig = Two())
                    {
                        rig.SetSelection(s);
                        Point c = HandleCenter(s, d);
                        T.Eq(HandleCursor(d), CursorAt(rig, c), "hover cursor on handle " + d);
                        rig.Down(c);
                        rig.Move(new Point(c.X + delta.X, c.Y + delta.Y), Keys.None, MouseButtons.Left);
                        T.True(!rig.E.ToolbarsVisible, "bars hidden while resizing");
                        T.Eq(HandleCursor(d), rig.E.Cursor, "cursor while resizing " + d);
                        rig.Up(new Point(c.X + delta.X, c.Y + delta.Y));
                        T.Eq(Expected(s, d, delta.X, delta.Y), rig.E.Selection, "resize via handle " + d + " by " + delta);
                        T.True(rig.E.ToolbarsVisible, "bars back after resize");
                    }
        }

        static void ResizeCrossing()
        {
            var s = new Rectangle(400, 300, 600, 400);
            foreach (Point d in HandleDirs)
                using (Rig rig = Two())
                {
                    rig.SetSelection(s);
                    Point c = HandleCenter(s, d);
                    int dx = -d.X * (s.Width + 100), dy = -d.Y * (s.Height + 60);
                    rig.Down(c);
                    // cross only horizontally first (cursor mirrors on that axis)
                    if (d.X != 0)
                    {
                        rig.Move(new Point(c.X + dx, c.Y), Keys.None, MouseButtons.Left);
                        T.Eq(HandleCursor(new Point(-d.X, d.Y)), rig.E.Cursor, "cursor after crossing horizontally with " + d);
                        T.Eq(Expected(s, d, dx, 0), rig.E.Selection, "crossed horizontally " + d);
                    }
                    rig.Move(new Point(c.X + dx, c.Y + dy), Keys.None, MouseButtons.Left);
                    T.Eq(HandleCursor(new Point(-d.X, -d.Y)), rig.E.Cursor, "cursor after crossing " + d);
                    rig.Up(new Point(c.X + dx, c.Y + dy));
                    T.Eq(Expected(s, d, dx, dy), rig.E.Selection, "crossed both edges with " + d);
                    if (d == HandleDirs[0]) rig.Snapshot("e07_resize_crossed");
                }
            using (Rig rig = Two())
            {
                rig.SetSelection(s);
                Point c = HandleCenter(s, new Point(1, 0));
                rig.Drag(c, new Point(s.Left, c.Y)); // right edge dragged exactly onto the left edge
                T.True(rig.E.Selection.Width >= 1, "never smaller than 1 px: " + rig.E.Selection);
            }
        }

        static void HandlesWithTool()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(S);
                rig.Key(Keys.R);
                T.Eq(ToolKind.Rectangle, rig.E.Tool, "rectangle tool");
                Point c = HandleCenter(S, new Point(1, 1));
                rig.Drag(c, new Point(c.X + 40, c.Y + 30));
                T.Eq(Rectangle.FromLTRB(S.Left, S.Top, S.Right + 40, S.Bottom + 30), rig.E.Selection, "handle resizes even with a tool");
                T.Eq(0, rig.E.Annotations.Count, "no rectangle drawn");
            }
        }

        static void Nudge()
        {
            using (Rig rig = Two())
            {
                var s = new Rectangle(400, 300, 600, 400);
                rig.SetSelection(s);
                T.True(rig.Key(Keys.Right), "Right handled");
                T.Eq(new Rectangle(401, 300, 600, 400), rig.E.Selection, "Right");
                rig.Key(Keys.Control | Keys.Right); T.Eq(new Rectangle(411, 300, 600, 400), rig.E.Selection, "Ctrl+Right");
                rig.Key(Keys.Up); T.Eq(new Rectangle(411, 299, 600, 400), rig.E.Selection, "Up");
                rig.Key(Keys.Control | Keys.Down); T.Eq(new Rectangle(411, 309, 600, 400), rig.E.Selection, "Ctrl+Down");
                rig.Key(Keys.Left); T.Eq(new Rectangle(410, 309, 600, 400), rig.E.Selection, "Left");
                rig.Key(Keys.Shift | Keys.Right); T.Eq(new Rectangle(410, 309, 601, 400), rig.E.Selection, "Shift+Right");
                rig.Key(Keys.Control | Keys.Shift | Keys.Down); T.Eq(new Rectangle(410, 309, 601, 410), rig.E.Selection, "Ctrl+Shift+Down");
                rig.Key(Keys.Shift | Keys.Up); T.Eq(new Rectangle(410, 309, 601, 409), rig.E.Selection, "Shift+Up");
                rig.Key(Keys.Control | Keys.Shift | Keys.Left); T.Eq(new Rectangle(410, 309, 591, 409), rig.E.Selection, "Ctrl+Shift+Left");
                T.True(!rig.Key(Keys.Alt | Keys.Left), "Alt+arrow not handled");
                rig.SetSelection(new Rectangle(500, 500, 3, 3));
                for (int i = 0; i < 10; i++) rig.Key(Keys.Shift | Keys.Left);
                T.Eq(new Rectangle(500, 500, 1, 3), rig.E.Selection, "minimum width 1");
                rig.SetSelection(new Rectangle(3830, 1070, 10, 10));
                for (int i = 0; i < 5; i++) { rig.Key(Keys.Right); rig.Key(Keys.Control | Keys.Down); }
                T.Eq(new Rectangle(3830, 1070, 10, 10), rig.E.Selection, "clamped at the desktop edge");
                rig.Key(Keys.Shift | Keys.Right);
                T.Eq(new Rectangle(3830, 1070, 10, 10), rig.E.Selection, "resize clamped at the desktop edge");
                rig.SetSelection(new Rectangle(0, 0, 10, 10));
                rig.Key(Keys.Control | Keys.Left); rig.Key(Keys.Up);
                T.Eq(new Rectangle(0, 0, 10, 10), rig.E.Selection, "clamped at the origin");
                rig.CheckIncremental("nudge");
            }
        }

        static void CtrlATwo()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(S);
                rig.Key(Keys.Control | Keys.A); T.Eq(Fx.M1, rig.E.Selection, "Ctrl+A → monitor under the cursor");
                rig.Key(Keys.Control | Keys.A); T.Eq(Fx.Desktop2, rig.E.Selection, "Ctrl+A again → whole desktop");
                rig.Snapshot("e08_full_desktop", full: true);
                rig.Key(Keys.Control | Keys.A); T.Eq(Fx.M1, rig.E.Selection, "third Ctrl+A → monitor");
            }
            using (Rig rig = Two())
            {
                rig.Key(Keys.Control | Keys.A);
                T.Eq(Fx.M1, rig.E.Selection, "Ctrl+A without selection selects the monitor");
                rig.Snapshot("e08b_full_monitor_m1", full: true);
            }
        }

        static void CtrlASingle()
        {
            using (var rig = new Rig(Fx.SingleMonitor()))
            {
                rig.SetSelection(S);
                rig.Key(Keys.Control | Keys.A); T.Eq(Fx.M1, rig.E.Selection, "→ monitor");
                rig.Key(Keys.Control | Keys.A); T.Eq(S, rig.E.Selection, "→ restored");
                rig.Key(Keys.Control | Keys.A); T.Eq(Fx.M1, rig.E.Selection, "→ monitor again");
                rig.E.ToggleFullScreen(); rig.Paint();
                T.Eq(S, rig.E.Selection, "public ToggleFullScreen");
            }
        }

        static void DoubleClick()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(S);
                rig.Click(P(400, 300));
                rig.Click(P(400, 300), Keys.None, 2);
                T.Eq(Fx.M1, rig.E.Selection, "double-click toggles to the monitor");
                rig.Key(Keys.P);
                int n = rig.E.Annotations.Count;
                rig.Click(P(400, 300));
                rig.Click(P(400, 300), Keys.None, 2);
                T.Eq(Fx.M1, rig.E.Selection, "with a tool, a double-click doesn't toggle");
                T.Eq(n + 2, rig.E.Annotations.Count, "two pen dots");
            }
        }

        static void EveryTool()
        {
            using (Rig rig = Two())
            {
                Color red = rig.Settings.Color;
                rig.SetSelection(Big);
                rig.Key(Keys.P); T.Eq(ToolKind.Pen, rig.E.Tool, "P");
                rig.DragPath(P(200, 200), P(230, 240), P(260, 210), P(300, 260));
                var pen = rig.Last<PenAnnotation>();
                T.Eq(4, pen.Points.Count, "pen points");
                T.True(pen.Color.ToArgb() == red.ToArgb() && pen.Size == 3, "pen color/size");
                rig.Click(P(330, 300));
                T.Eq(1, rig.Last<PenAnnotation>().Points.Count, "pen click = dot");

                rig.Key(Keys.L); T.Eq(ToolKind.Line, rig.E.Tool, "L");
                rig.Drag(P(400, 200), P(600, 260));
                var line = rig.Last<LineAnnotation>();
                T.True(line.Start == P(400, 200) && line.End == P(600, 260), "line ends");
                rig.Drag(P(400, 300), P(600, 310), Keys.Shift);
                T.Eq(P(600, 300), rig.Last<LineAnnotation>().End, "Shift snaps to horizontal");
                rig.Drag(P(400, 350), P(500, 440), Keys.Shift);
                T.Eq(P(495, 445), rig.Last<LineAnnotation>().End, "Shift snaps to 45°");
                rig.Drag(P(650, 300), P(660, 400), Keys.Shift);
                T.Eq(P(650, 400), rig.Last<LineAnnotation>().End, "Shift snaps to vertical");
                int n = rig.E.Annotations.Count;
                rig.Click(P(450, 500));
                T.Eq(n, rig.E.Annotations.Count, "zero-length line discarded");

                rig.Key(Keys.A); T.Eq(ToolKind.Arrow, rig.E.Tool, "A");
                rig.Drag(P(700, 200), P(900, 300));
                var arrow = rig.Last<ArrowAnnotation>();
                T.True(arrow.Start == P(700, 200) && arrow.End == P(900, 300) && arrow.Size == 4, "arrow");
                rig.Drag(P(700, 330), P(905, 350), Keys.Shift);
                T.Eq(P(905, 330), rig.Last<ArrowAnnotation>().End, "Shift snaps arrow");

                rig.Key(Keys.R); T.Eq(ToolKind.Rectangle, rig.E.Tool, "R");
                rig.Drag(P(200, 400), P(400, 520));
                T.Eq(new Rectangle(200, 400, 200, 120), rig.Last<RectangleAnnotation>().Rect, "rectangle");
                rig.Drag(P(450, 400), P(650, 450), Keys.Shift);
                T.Eq(new Rectangle(450, 400, 200, 200), rig.Last<RectangleAnnotation>().Rect, "Shift = square");
                rig.Drag(P(900, 700), P(850, 600), Keys.Shift);
                T.Eq(Rectangle.FromLTRB(800, 600, 900, 700), rig.Last<RectangleAnnotation>().Rect, "Shift square dragging up-left");
                n = rig.E.Annotations.Count;
                rig.Drag(P(300, 700), P(301, 750));
                T.Eq(n, rig.E.Annotations.Count, "1 px wide rectangle discarded");

                rig.Key(Keys.E); T.Eq(ToolKind.Ellipse, rig.E.Tool, "E");
                rig.Drag(P(700, 400), P(900, 520));
                T.Eq(new Rectangle(700, 400, 200, 120), rig.Last<EllipseAnnotation>().Rect, "ellipse");
                rig.Drag(P(950, 400), P(1000, 560), Keys.Shift);
                T.Eq(new Rectangle(950, 400, 160, 160), rig.Last<EllipseAnnotation>().Rect, "Shift = circle");

                rig.Key(Keys.H); T.Eq(ToolKind.Marker, rig.E.Tool, "H");
                T.Eq(rig.Settings.MarkerColor.ToArgb(), rig.E.Color.ToArgb(), "marker uses MarkerColor");
                rig.DragPath(P(200, 600), P(400, 600), P(420, 640));
                var marker = rig.Last<MarkerAnnotation>();
                T.True(marker.Color.ToArgb() == rig.Settings.MarkerColor.ToArgb() && marker.Size == 18, "marker color/size");
                n = rig.E.Annotations.Count;
                rig.Click(P(500, 650));
                T.Eq(n, rig.E.Annotations.Count, "marker click discarded");

                rig.Key(Keys.T); T.Eq(ToolKind.Text, rig.E.Tool, "T");
                rig.Click(P(1000, 200));
                rig.Type("Hello");
                rig.Key(Keys.Escape);
                var text = rig.Last<TextAnnotation>();
                T.True(text.Text == "Hello" && text.Size == 22 && text.Location == P(1000, 200 - 22 * 3 / 5) && !text.Editing, "text committed: " + text.Location);
                T.Eq(ToolKind.Text, rig.E.Tool, "Esc while typing only commits");

                rig.Key(Keys.N); T.Eq(ToolKind.Step, rig.E.Tool, "N");
                rig.Click(P(1000, 400));
                var step = rig.Last<StepAnnotation>();
                T.True(step.Number == 1 && step.Center == P(1000, 400) && step.Size == 30 && step.Editing, "step placed and editing");
                rig.Key(Keys.Escape);
                T.True(!step.Editing, "step committed");

                rig.Key(Keys.B); T.Eq(ToolKind.Pixelate, rig.E.Tool, "B");
                rig.Drag(P(1100, 500), P(1300, 600));
                T.True(rig.Last<PixelateAnnotation>().Rect == new Rectangle(1100, 500, 200, 100) && rig.Last<PixelateAnnotation>().Size == 10, "pixelate");
                rig.Drag(P(1100, 650), P(1180, 700), Keys.Shift);
                T.Eq(new Rectangle(1100, 650, 80, 80), rig.Last<PixelateAnnotation>().Rect, "Shift = square pixelate");
                rig.Drag(P(130, 165), P(560, 250)); // redact the fake username/password/API key lines of the main window

                rig.Key(Keys.P);
                n = rig.E.Annotations.Count;
                Rectangle before = rig.E.Selection;
                rig.Drag(P(1700, 980), P(1800, 1000));
                T.True(rig.E.Annotations.Count == n && rig.E.Selection == before, "with a tool, mouse-down outside the selection is ignored");
                rig.Key(Keys.P);
                T.Eq(ToolKind.None, rig.E.Tool, "pressing the active tool's key deselects it");
                rig.Snapshot("e10_all_tools", full: true);
                using (Bitmap result = rig.E.RenderResult()) Img.Save(result, "e10b_all_tools_export");
            }
        }

        static void BarsHiddenWhileDragging()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(Big);
                rig.Key(Keys.R);
                rig.Down(P(300, 300));
                T.True(rig.E.ToolbarsVisible, "bars still visible on press");
                rig.Move(P(400, 400), Keys.None, MouseButtons.Left);
                T.True(!rig.E.ToolbarsVisible, "bars hidden while drawing");
                rig.Snapshot("e11_drawing_bars_hidden");
                rig.Up(P(400, 400));
                T.True(rig.E.ToolbarsVisible, "bars shown again");
            }
        }

        static void StepNumbering()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(Big);
                rig.Key(Keys.N);
                rig.Click(P(300, 300));
                rig.Click(P(500, 300));   // commits #1's (empty) label, places #2
                rig.Key(Keys.Escape);     // letters would type into #2's label
                rig.Key(Keys.R);          // shapes in between don't count
                rig.Drag(P(600, 600), P(700, 700));
                rig.Key(Keys.N);
                rig.Click(P(700, 300));
                rig.Key(Keys.Escape);
                Func<string> nums = () => string.Join(",", rig.E.Annotations.OfType<StepAnnotation>().Select(s => s.Number));
                T.Eq("1,2,3", nums(), "numbers");
                rig.Key(Keys.Control | Keys.Z); T.Eq("1,2", nums(), "undo");
                rig.Key(Keys.Control | Keys.Z); T.Eq("1,2", nums(), "undo removes the rectangle");
                rig.Key(Keys.Delete); T.Eq("1", nums(), "Delete = undo");
                rig.Key(Keys.Control | Keys.Y); T.Eq("1,2", nums(), "redo");
                rig.Click(P(900, 300));
                rig.Key(Keys.Escape);
                T.Eq("1,2,3", nums(), "new step after undo/redo");
                rig.Key(Keys.Control | Keys.Y);
                T.Eq(3, rig.E.Annotations.Count, "new annotation cleared the redo stack");
                rig.Key(Keys.Control | Keys.Z); rig.Key(Keys.Control | Keys.Z); rig.Key(Keys.Control | Keys.Z);
                rig.Key(Keys.Control | Keys.Shift | Keys.Z); rig.Key(Keys.Control | Keys.Shift | Keys.Z); rig.Key(Keys.Control | Keys.Shift | Keys.Z);
                T.Eq("1,2,3", nums(), "undo/redo all keeps the numbers");
                rig.CheckIncremental("step_numbering");
            }
        }

        static void StepTip()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(Big);
                rig.Key(Keys.N);
                rig.Down(P(400, 300));
                rig.Move(P(420, 300), Keys.None, MouseButtons.Left);
                rig.Move(P(560, 420), Keys.None, MouseButtons.Left);
                rig.Up(P(560, 420));
                var s = rig.Last<StepAnnotation>();
                T.True(s.Center == P(400, 300) && s.Tip == P(560, 420), "badge stays at the press, Tip at the release: " + s.Center + " " + s.Tip);
                rig.Type("Click the Save button");
                rig.Key(Keys.Enter);
                rig.Down(P(400, 500));
                rig.Move(P(405, 505), Keys.None, MouseButtons.Left);
                rig.Up(P(405, 505));
                T.True(rig.Last<StepAnnotation>().Tip == null, "drag < 8 px: no leader");
                rig.Type("Short drag");
                rig.Key(Keys.Enter);
                rig.Drag(P(900, 600), P(700, 450));
                rig.Type("Leader up-left");
                rig.Key(Keys.Enter);
                rig.Drag(P(1200, 300), P(1250, 800));
                rig.Snapshot("e12_steps_leader_labels", full: true);
                rig.Key(Keys.Escape);
            }
        }

        static void StepLabel()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(Big);
                rig.Key(Keys.N);
                rig.Click(P(400, 300));
                T.True(!rig.Key(Keys.N), "letters type while editing (KeyDown not handled)");
                T.Eq(ToolKind.Step, rig.E.Tool, "tool unchanged by typing N");
                rig.E.KeyPress('N');
                rig.Type("ew file");
                rig.Snapshot("e13_step_label_editing");
                rig.Key(Keys.Back);
                rig.Type("e");
                var s = rig.Last<StepAnnotation>();
                T.Eq("New file", s.Label, "label");
                rig.Key(Keys.Enter);
                T.True(!s.Editing, "Enter commits the label");
                T.Eq(0, rig.Commands.Count, "Enter while editing is not Copy for Claude");
                rig.Key(Keys.Enter);
                T.Eq(EditorCommand.CopyForClaude, rig.Commands.Last(), "Enter afterwards = Copy for Claude");
                T.Eq("Screenshot notes: (1) New file", rig.E.BuildClaudeNotes(), "notes");
                rig.Click(P(600, 300));
                rig.Key(Keys.Escape);
                T.Eq(2, rig.E.Annotations.OfType<StepAnnotation>().Count(), "unlabeled badge stays");
            }
        }

        static void TextTyping()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(Big);
                rig.Key(Keys.T);
                T.Eq(Cursors.IBeam, CursorAt(rig, P(500, 500)), "IBeam for text");
                rig.Click(P(400, 300));
                T.Eq(0, rig.E.Annotations.Count, "text being edited is not in the list yet");
                rig.Type("Hello");
                rig.Key(Keys.Enter);
                rig.Type("World");
                var editing = rig.F<Annotation>("editing") as TextAnnotation;
                T.True(editing != null && editing.Text == "Hello\nWorld" && editing.Editing && editing.CaretVisible, "typed text with newline");
                rig.Snapshot("e14_text_editing_caret");
                rig.Tick(); rig.Tick();
                T.True(!editing.CaretVisible, "caret blinks off after 2 ticks");
                rig.CheckIncremental("caret_blink");
                rig.Tick(); rig.Tick();
                T.True(editing.CaretVisible, "caret back on");
                rig.Key(Keys.Back);
                T.Eq("Hello\nWorl", editing.Text, "Backspace");
                rig.Click(P(800, 600));
                T.Eq(1, rig.E.Annotations.Count, "click elsewhere commits");
                T.Eq("Hello\nWorl", rig.Last<TextAnnotation>().Text, "committed text");
                T.True(!rig.Last<TextAnnotation>().Editing, "committed text not editing");
                rig.Click(P(800, 700));
                T.Eq(1, rig.E.Annotations.Count, "empty text discarded");
                rig.Type("   ");
                rig.Key(Keys.Escape);
                T.Eq(1, rig.E.Annotations.Count, "whitespace-only text discarded");
                rig.Click(P(300, 800));
                rig.Type("Ctrl+Enter");
                rig.Key(Keys.Control | Keys.Enter);
                T.Eq(2, rig.E.Annotations.Count, "Ctrl+Enter commits");
                rig.Click(P(300, 700));
                rig.Type("outside click");
                rig.Click(P(1700, 1000)); // outside the selection
                T.Eq(3, rig.E.Annotations.Count, "click outside the selection commits");
                T.True(rig.F<Annotation>("editing") == null, "not editing after outside click");
                rig.Key(Keys.Escape);
                T.Eq(ToolKind.None, rig.E.Tool, "Esc (not typing) deselects the tool");
                rig.Key(Keys.Escape);
                T.Eq(EditorCommand.Close, rig.Commands.Last(), "Esc again closes");
                rig.CheckIncremental("text_typing");
            }
        }

        static void Wheel()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(S);
                rig.Move(P(400, 300));
                rig.Wheel(120);
                T.True(rig.F<string>("hudText") == null, "wheel without a tool does nothing");
                rig.Key(Keys.P);
                rig.Wheel(120); rig.Wheel(120); rig.Wheel(120);
                T.Eq(6, rig.Settings.GetToolSize(ToolKind.Pen), "pen size persisted in settings");
                T.Eq("Size 6", rig.F<string>("hudText"), "HUD text");
                Rectangle hud = rig.F<Rectangle>("hudRect");
                T.True(Fx.M1.Contains(hud) && hud.Left > 400 && hud.Top > 300, "HUD near the cursor: " + hud);
                rig.Snapshot("e15_hud");
                for (int i = 0; i < 10; i++) rig.Wheel(-120);
                T.Eq(1, rig.Settings.GetToolSize(ToolKind.Pen), "clamped at the minimum");
                rig.Key(Keys.Control | Keys.Add); rig.Key(Keys.Control | Keys.Oemplus);
                T.Eq(3, rig.Settings.GetToolSize(ToolKind.Pen), "Ctrl+Numpad+ / Ctrl+=");
                rig.Key(Keys.Control | Keys.Subtract);
                rig.Key(Keys.Control | Keys.OemMinus);
                T.Eq(1, rig.Settings.GetToolSize(ToolKind.Pen), "Ctrl+Numpad- / Ctrl+-");
                rig.Key(Keys.H);
                rig.Wheel(120);
                T.Eq(19, rig.Settings.GetToolSize(ToolKind.Marker), "marker size");
                rig.Key(Keys.T);
                rig.Click(P(400, 300));
                rig.Type("Size");
                rig.Wheel(120); rig.Wheel(120);
                var t = rig.F<Annotation>("editing");
                T.True(t.Size == 24 && rig.Settings.GetToolSize(ToolKind.Text) == 24, "wheel resizes the text being edited live");
                rig.CheckIncremental("wheel_text");
                // HUD expiry
                Priv.Set(rig.E, "hudSince", Environment.TickCount - 5000);
                rig.Tick();
                T.True(rig.F<string>("hudText") == null && rig.F<Rectangle>("hudRect").IsEmpty, "HUD expires after ~1 s");
                rig.CheckIncremental("hud_expired");
            }
        }

        static void Esc()
        {
            using (Rig rig = Two())
            {
                rig.Key(Keys.Escape);
                T.Eq(EditorCommand.Close, rig.Commands.Single(), "Esc without selection closes");
            }
            using (Rig rig = Two())
            {
                rig.SetSelection(S);
                rig.Key(Keys.A);
                rig.Key(Keys.Escape);
                T.True(rig.E.Tool == ToolKind.None && rig.Commands.Count == 0, "Esc with a tool deselects it");
                rig.ClickItem("Color");
                T.True(!rig.F<Rectangle>("paletteRect").IsEmpty, "palette open");
                rig.Key(Keys.Escape);
                T.True(rig.F<Rectangle>("paletteRect").IsEmpty && rig.Commands.Count == 0, "Esc closes the palette first");
                rig.Key(Keys.Escape);
                T.Eq(EditorCommand.Close, rig.Commands.Single(), "then Esc closes");
            }
        }

        static void Commands()
        {
            var map = new (Keys Key, EditorCommand Command)[]
            {
                (Keys.Enter, EditorCommand.CopyForClaude), (Keys.Control | Keys.Shift | Keys.C, EditorCommand.CopyForClaude),
                (Keys.Control | Keys.C, EditorCommand.Copy), (Keys.Control | Keys.S, EditorCommand.QuickSave),
                (Keys.Control | Keys.Shift | Keys.S, EditorCommand.SaveAs), (Keys.Control | Keys.P, EditorCommand.Print),
                (Keys.Control | Keys.X, EditorCommand.Close),
            };
            using (Rig rig = Two())
            {
                rig.SetSelection(S);
                foreach (var (key, command) in map)
                {
                    rig.Commands.Clear();
                    T.True(rig.Key(key), key + " handled");
                    T.True(rig.Commands.Count == 1 && rig.Commands[0] == command, key + " → " + command + " (got " + string.Join(",", rig.Commands) + ")");
                }
            }
            using (Rig rig = Two())
            {
                foreach (var (key, command) in map)
                {
                    rig.Commands.Clear();
                    rig.Key(key);
                    if (command == EditorCommand.Close) T.Eq(1, rig.Commands.Count, "Ctrl+X closes without selection");
                    else T.Eq(0, rig.Commands.Count, key + " needs a selection");
                }
            }
        }

        static void CommandsCommit()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(Big);
                int countAtCommand = -1;
                rig.OnCommand = c => countAtCommand = rig.E.Annotations.Count;
                rig.Key(Keys.T);
                rig.Click(P(400, 300));
                rig.Type("pending");
                rig.Key(Keys.Control | Keys.C);
                T.Eq(1, countAtCommand, "text committed before the Copy command fires");
                rig.Key(Keys.N);
                rig.Click(P(600, 300));
                rig.Type("label");
                rig.ClickItem("Copy for Claude");
                T.Eq(EditorCommand.CopyForClaude, rig.Commands.Last(), "Claude button");
                T.True(!rig.Last<StepAnnotation>().Editing, "label committed by the button");
                rig.Click(P(800, 300));
                rig.Type("render");
                using (Bitmap img = rig.E.RenderResult())
                    T.True(!rig.Last<StepAnnotation>().Editing && rig.Last<StepAnnotation>().Label == "render", "RenderResult commits text");
            }
        }

        static void RightClick()
        {
            using (Rig rig = Two())
            {
                rig.RightClick(P(500, 500));
                T.Eq(EditorCommand.Close, rig.Commands.Single(), "right-click without selection closes");
            }
            using (Rig rig = Two())
            {
                rig.SetSelection(Big);
                rig.Key(Keys.T);
                rig.Click(P(400, 300));
                rig.Type("abc");
                rig.RightClick(P(500, 520));
                T.Eq(EditorCommand.ContextMenu, rig.Commands.Single(), "right-click → context menu");
                T.Eq(P(500, 520), rig.E.LastMouse, "LastMouse = menu position");
                T.Eq(1, rig.E.Annotations.Count, "text committed first");
            }
        }

        static void CtrlDrag()
        {
            using (Rig rig = Two())
            {
                rig.Drag(P(300, 300), P(700, 600), Keys.Control);
                T.Eq(Rectangle.FromLTRB(300, 300, 700, 600), rig.E.Selection, "selection");
                T.Eq(EditorCommand.Copy, rig.Commands.Single(), "Ctrl-drag auto copies");
            }
            using (Rig rig = Two())
            {
                rig.Click(P(600, 400), Keys.Control);
                T.True(rig.E.Selection == Fx.Dialog && rig.Commands.SequenceEqual(new[] { EditorCommand.Copy }), "Ctrl-click grabs the window and copies");
            }
            using (Rig rig = Two())
            {
                rig.Drag(P(300, 300), P(700, 600));
                T.Eq(0, rig.Commands.Count, "plain drag doesn't copy");
                rig.Click(P(1000, 900), Keys.Control);
                T.True(rig.Commands.Count == 0 && rig.E.Selection == Rectangle.FromLTRB(300, 300, 700, 600), "Ctrl-click outside an existing selection does nothing");
            }
        }

        static void Toolbar()
        {
            using (Rig rig = Two())
            {
                var sel = new Rectangle(300, 400, 500, 350);
                rig.SetSelection(sel);
                var tb = rig.ToolBar.Bounds;
                var ab = rig.ActionBar.Bounds;
                int gap = (int)Math.Round(5 * 1.25);
                T.True(tb.Left == sel.Right + gap && tb.Bottom == sel.Bottom, "tool bar right of the selection, bottom-aligned: " + tb);
                T.True(ab.Top == sel.Bottom + gap && ab.Right == sel.Right, "action bar below, right-aligned: " + ab);
                T.Eq((int)Math.Round(32 * 1.25), rig.Item("Pen").Bounds.Width, "button size scales with the monitor (125%)");

                rig.Move(rig.ItemCenter("Pen"));
                T.Eq(Cursors.Default, rig.E.Cursor, "default cursor over the bar");
                T.Eq("Pen (P)", rig.F<string>("tooltipText"), "tooltip");
                Rectangle tip = rig.F<Rectangle>("tooltipRect");
                T.True(tip.Left >= tb.Right && Fx.M1.Contains(tip), "tool tooltip away from the selection: " + tip);
                rig.Snapshot("e16_tooltip_tool");
                rig.Move(rig.ItemCenter("Numbered step"));
                rig.Snapshot("e16b_tooltip_step_long");
                rig.Move(rig.ItemCenter("Copy for Claude"));
                tip = rig.F<Rectangle>("tooltipRect");
                T.True(tip.Top >= ab.Bottom && Fx.M1.Contains(tip), "action tooltip below the action bar: " + tip);
                rig.Snapshot("e17_tooltip_claude");
                rig.Move(rig.ItemCenter("Close"));
                rig.Snapshot("e17b_tooltip_close");

                rig.ClickItem("Pen"); T.Eq(ToolKind.Pen, rig.E.Tool, "click selects the tool");
                rig.Snapshot("e18_tool_active");
                rig.ClickItem("Pen"); T.Eq(ToolKind.None, rig.E.Tool, "click again deselects");
                rig.ClickItem("Undo (Ctrl+Z)"); // disabled, nothing to undo
                rig.ClickItem("Rectangle"); rig.Drag(P(400, 500), P(500, 600));
                T.Eq(1, rig.E.Annotations.Count, "drawn");
                rig.ClickItem("Undo"); T.Eq(0, rig.E.Annotations.Count, "Undo button");
                rig.ClickItem("Redo"); T.Eq(1, rig.E.Annotations.Count, "Redo button");

                rig.ClickItem("Color");
                Rectangle palette = rig.F<Rectangle>("paletteRect");
                T.True(!palette.IsEmpty && Fx.M1.Contains(palette) && palette.Left >= tb.Right, "palette beside the bar: " + palette);
                rig.Move(Center(rig.PaletteCell(4)));
                rig.Snapshot("e19_palette_open", full: true);
                Img.SaveCrop(rig.FullRender(), Rectangle.Union(palette, rig.Item("Color").Bounds), "e19z_palette_zoom", 3);
                rig.Click(Center(rig.PaletteCell(3)));
                T.Eq(Color.FromArgb(0x34, 0xC7, 0x59).ToArgb(), rig.Settings.Color.ToArgb(), "swatch sets the color");
                T.True(rig.F<Rectangle>("paletteRect").IsEmpty, "palette closes after a pick");
                rig.ClickItem("Color");
                rig.Click(Center(rig.PaletteCell(10)));
                T.Eq(EditorCommand.PickCustomColor, rig.Commands.Last(), "Custom… cell");
                rig.Key(Keys.H);
                rig.ClickItem("Color");
                rig.Snapshot("e19b_palette_marker");
                rig.Click(Center(rig.PaletteCell(0)));
                T.True(rig.Settings.MarkerColor.ToArgb() == Color.Red.ToArgb() && rig.Settings.Color.ToArgb() == Color.FromArgb(0x34, 0xC7, 0x59).ToArgb(), "marker has its own color");
                rig.Key(Keys.H);

                rig.Commands.Clear();
                foreach (var (name, command) in new[] { ("Copy for Claude", EditorCommand.CopyForClaude), ("Print", EditorCommand.Print), ("Copy (", EditorCommand.Copy), ("Save as", EditorCommand.SaveAs), ("Close", EditorCommand.Close) })
                {
                    rig.ClickItem(name);
                    T.Eq(command, rig.Commands.Last(), name + " button");
                }
                int before = rig.Commands.Count;
                rig.Down(rig.ItemCenter("Print"));
                rig.Up(P(10, 10));
                T.Eq(before, rig.Commands.Count, "press on a button and release elsewhere does nothing");
                rig.CheckIncremental("toolbar");
            }
        }

        static Point Center(Rectangle r) => new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);

        static void CursorsTest()
        {
            using (Rig rig = Two())
            {
                T.Eq(Cursors.Cross, CursorAt(rig, P(100, 100)), "no selection");
                rig.SetSelection(S);
                T.Eq(Cursors.SizeAll, CursorAt(rig, P(400, 300)), "inside, no tool");
                T.Eq(Cursors.Cross, CursorAt(rig, P(1000, 800)), "outside, no tool");
                T.Eq(Cursors.SizeNWSE, CursorAt(rig, P(S.Left, S.Top)), "top-left handle");
                T.Eq(Cursors.SizeNESW, CursorAt(rig, P(S.Right, S.Top)), "top-right handle");
                T.Eq(Cursors.SizeNS, CursorAt(rig, P(S.Left + S.Width / 2, S.Bottom)), "bottom handle");
                T.Eq(Cursors.SizeWE, CursorAt(rig, P(S.Right, S.Top + S.Height / 2)), "right handle");
                rig.Key(Keys.P);
                T.Eq(Cursors.Cross, CursorAt(rig, P(400, 300)), "pen inside");
                T.Eq(Cursors.Default, CursorAt(rig, P(1000, 800)), "tool active, outside");
                rig.Key(Keys.T);
                T.Eq(Cursors.IBeam, CursorAt(rig, P(400, 300)), "text inside");
                T.Eq(Cursors.Default, CursorAt(rig, rig.ItemCenter("Ellipse")), "over the tool bar");
            }
        }

        static void Notes()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(Big);
                T.Eq("", rig.E.BuildClaudeNotes(), "no steps");
                rig.Key(Keys.N);
                rig.Click(P(300, 300)); rig.Type("  first  "); rig.Key(Keys.Enter);
                rig.Click(P(300, 400));                       // no label
                rig.Click(P(300, 500)); rig.Type("third; with \"quotes\""); rig.Key(Keys.Enter);
                T.Eq("Screenshot notes: (1) first; (3) third; with \"quotes\"", rig.E.BuildClaudeNotes(), "notes format");
                T.True(rig.E.BuildClaudeNotes().IndexOf('\n') < 0, "single line");
                rig.Key(Keys.Control | Keys.Z);
                T.Eq("Screenshot notes: (1) first", rig.E.BuildClaudeNotes(), "undone step not in the notes");
            }
        }

        static void Decorate(Rig rig)
        {
            rig.Key(Keys.A); rig.Drag(P(300, 250), P(600, 420));
            rig.Key(Keys.R); rig.Drag(P(650, 300), P(900, 450));
            rig.Key(Keys.N); rig.Click(P(350, 520)); rig.Type("Label"); rig.Key(Keys.Enter);
            rig.Drag(P(700, 600), P(900, 520)); rig.Key(Keys.Escape);
            rig.Key(Keys.H); rig.DragPath(P(300, 700), P(800, 700));
            rig.Key(Keys.B); rig.Drag(P(160, 150), P(560, 205));
            rig.Key(Keys.T); rig.Click(P(950, 250)); rig.Type("Text\nline 2"); rig.Key(Keys.Escape);
            rig.Key(Keys.E); rig.Drag(P(1000, 500), P(1300, 650));
            rig.Key(Keys.P); rig.DragPath(P(1300, 200), P(1350, 260), P(1400, 210));
            rig.Key(Keys.L); rig.Drag(P(1100, 750), P(1500, 900)); // runs past the selection's bottom edge
            rig.Key(Keys.Escape);
        }

        static void RenderResult()
        {
            using (Rig rig = Two())
            {
                var sel = new Rectangle(150, 140, 1300, 700);
                rig.SetSelection(sel);
                Decorate(rig);
                using (Bitmap result = rig.E.RenderResult())
                using (Bitmap compose = Exporter.Compose(rig.Capture.Image, sel, rig.E.Annotations))
                {
                    T.Eq(sel.Size, result.Size, "RenderResult size == selection");
                    Img.Diff d = Img.Compare(result, new Rectangle(Point.Empty, sel.Size), compose, new Rectangle(Point.Empty, sel.Size));
                    T.True(d.Count == 0, "RenderResult == Exporter.Compose: " + d);
                    Img.Save(result, "e20_render_result");
                }
            }
        }

        static void Wysiwyg()
        {
            using (Rig rig = Two())
            {
                var sel = new Rectangle(150, 140, 1300, 700);
                rig.SetSelection(sel);
                Decorate(rig);
                rig.Move(P(1700, 1000)); // no hover
                using (Bitmap screen = rig.FullRender())
                using (Bitmap result = rig.E.RenderResult())
                {
                    // the selection interior minus handles and the in-selection chrome
                    var skip = new List<Rectangle> { rig.F<Rectangle>("labelRect"), rig.ToolBar.Bounds, rig.ActionBar.Bounds };
                    int reach = 8;
                    foreach (Point d in HandleDirs)
                    {
                        Point c = HandleCenter(sel, d);
                        skip.Add(new Rectangle(c.X - reach, c.Y - reach, 2 * reach + 1, 2 * reach + 1));
                    }
                    Img.Diff diff = Img.Compare(screen, sel, result, new Rectangle(Point.Empty, sel.Size), 2, PixelFormat.Format32bppArgb,
                        (x, y) => skip.Any(r => r.Contains(x, y)));
                    if (diff.Count > 0)
                    {
                        Img.SaveCrop(screen, Rectangle.Inflate(diff.Box, 30, 30), "FAIL_wysiwyg_screen");
                        throw new TestFailure("on-screen annotations differ from the exported image: " + diff);
                    }
                    if (diff.Max > 0) T.Note("screen vs export max channel diff " + diff.Max + " (within tolerance 2)");
                }
            }
        }

        static void KeepSelection()
        {
            var s = new Settings { KeepSelection = true, LastSelection = new Rectangle(100, 120, 300, 200) };
            using (var rig = new Rig(Fx.TwoMonitors(), s))
                T.Eq(new Rectangle(100, 120, 300, 200), rig.E.Selection, "restored");
            s.LastSelection = new Rectangle(3700, 1000, 400, 400);
            using (var rig = new Rig(Fx.TwoMonitors(), s))
                T.Eq(Rectangle.FromLTRB(3700, 1000, 3840, 1080), rig.E.Selection, "clipped to the desktop");
            s.KeepSelection = false;
            using (var rig = new Rig(Fx.TwoMonitors(), s))
                T.True(rig.E.Selection.IsEmpty, "not restored when off");
        }

        static void AnnotationsSurvive()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(S);
                rig.Key(Keys.R); rig.Drag(P(300, 200), P(400, 300)); rig.Key(Keys.Escape);
                rig.Drag(P(1000, 600), P(1400, 900));
                T.True(rig.E.Selection == Rectangle.FromLTRB(1000, 600, 1400, 900) && rig.E.Annotations.Count == 1, "new selection keeps annotations");
                rig.E.ClearSelection(); rig.Paint();
                T.True(rig.E.Selection.IsEmpty && rig.E.Annotations.Count == 1 && !rig.E.ToolbarsVisible, "ClearSelection");
                rig.CheckIncremental("clear_selection");
            }
        }

        static void ToolKeysNeedSelection()
        {
            using (Rig rig = Two())
            {
                T.True(!rig.Key(Keys.P) && rig.E.Tool == ToolKind.None, "tool keys ignored without a selection");
                rig.SetSelection(S);
                T.True(rig.Key(Keys.P) && rig.E.Tool == ToolKind.Pen, "P with a selection");
                T.True(!rig.Key(Keys.Shift | Keys.L) && rig.E.Tool == ToolKind.Pen, "Shift+L is not a tool key");
            }
        }

        static void SingleMonitor()
        {
            using (var rig = new Rig(Fx.SingleMonitor(1.5f)))
            {
                T.Eq(Fx.SingleDialog, rig.F<Rectangle>("hoverRect"), "hover");
                rig.Snapshot("e30_single_150_hover", full: true);
                rig.Click(P(600, 400));
                T.Eq(Fx.SingleDialog, rig.E.Selection, "grab");
                T.Eq(48, rig.Item("Pen").Bounds.Width, "button size at 150%");
                rig.Move(rig.ItemCenter("Text"));
                rig.Snapshot("e31_single_150_bars_tooltip");
                rig.Key(Keys.Control | Keys.A);
                T.Eq(Fx.M1, rig.E.Selection, "full monitor");
                rig.Snapshot("e32_single_150_full_monitor", full: true);
            }
        }
    }
}
