using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Shadowshot.Tests
{
    // Bar/label/popup placement over a sweep of selections, plus edge-case snapshots.
    static class LayoutTests
    {
        sealed class Case
        {
            public string Fixture;
            public Rectangle Sel, Mon, LabelMon, Tool, Action, Label, Palette;
            public float Scale;
            public List<(string Tip, Rectangle Rect, Rectangle Bar)> Tooltips = new List<(string, Rectangle, Rectangle)>();
            public override string ToString() => Fixture + " sel " + Sel;
        }

        static List<Case> cases;

        public static void Run()
        {
            T.Run("Layout.Sweep.ToolBarInsideSelectionMonitor", () => Check((c, gap) => c.Mon.Contains(c.Tool)));
            T.Run("Layout.Sweep.ActionBarInsideSelectionMonitor", () => Check((c, gap) => c.Mon.Contains(c.Action)));
            T.Run("Layout.Sweep.BarsDontOverlapEachOther", () => Check((c, gap) => !c.Tool.IntersectsWith(c.Action)));
            T.Run("Layout.Sweep.ToolBarOutsideSelectionWhenThereIsRoom", () => Check((c, gap) =>
            {
                bool room = c.Sel.Right + gap + c.Tool.Width <= c.Mon.Right || c.Sel.Left - gap - c.Tool.Width >= c.Mon.Left;
                return !room || !c.Tool.IntersectsWith(c.Sel);
            }));
            T.Run("Layout.Sweep.ActionBarOutsideSelectionWhenThereIsRoom", () => Check((c, gap) =>
            {
                bool room = c.Sel.Bottom + gap + c.Action.Height <= c.Mon.Bottom || c.Sel.Top - gap - c.Action.Height >= c.Mon.Top;
                return !room || !c.Action.IntersectsWith(c.Sel);
            }));
            T.Run("Layout.Sweep.SizeLabelNotCoveredByToolBar", () => Check((c, gap) => !c.Label.IntersectsWith(c.Tool)));
            T.Run("Layout.Sweep.SizeLabelNotCoveredByActionBar", () => Check((c, gap) => !c.Label.IntersectsWith(c.Action)));
            // R2-03: the label belongs to the selection's top-left corner, so it is fitted to the monitor holding that corner.
            T.Run("Layout.Sweep.SizeLabelInsideTopLeftCornerMonitor", () => Check((c, gap) => c.LabelMon.Contains(c.Label)));
            T.Run("Layout.Sweep.SizeLabelNearTopLeftCorner", () => Check((c, gap) => LabelNearCorner(c)));
            // The palette as the editor lays it out (T-R1-06 moves it off the action bar).
            T.Run("Layout.Sweep.PaletteInsideMonitorClearOfBothBars", () => Check((c, gap) =>
                c.Mon.Contains(c.Palette) && !c.Palette.IntersectsWith(c.Tool) && !c.Palette.IntersectsWith(c.Action)));
            T.Run("Layout.Sweep.TooltipsInsideMonitorAndOffTheirBar", () => Check((c, gap) => c.Tooltips.All(t => c.Mon.Contains(t.Rect) && !t.Rect.IntersectsWith(t.Bar))));
            T.Run("Layout.EdgeCaseSnapshots", Snapshots);
        }

        static void Check(Func<Case, int, bool> ok)
        {
            List<Case> all = Cases();
            var bad = all.Where(c => !ok(c, (int)Math.Round(5 * c.Scale))).ToList();
            if (bad.Count == 0) return;
            Func<int, string> bucket = v => v < 50 ? "<50" : v < 150 ? "50-149" : v < 400 ? "150-399" : ">=400";
            string byWidth = string.Join(", ", bad.GroupBy(c => bucket(c.Sel.Width)).OrderBy(g => g.Key).Select(g => "w" + g.Key + ": " + g.Count()));
            string byHeight = string.Join(", ", bad.GroupBy(c => bucket(c.Sel.Height)).OrderBy(g => g.Key).Select(g => "h" + g.Key + ": " + g.Count()));
            Console.WriteLine("      breakdown: " + byWidth + " | " + byHeight);
            var big = bad.Where(c => c.Sel.Width >= 150 && c.Sel.Height >= 150).ToList();
            var problems = big.Concat(bad.Except(big)).Take(6).Select(c => c + " tool " + c.Tool + " action " + c.Action + " label " + c.Label + " palette " + c.Palette).ToList();
            throw new TestFailure(bad.Count + "/" + all.Count + " selections violate it (" + big.Count + " of them >= 150x150), e.g. " + string.Join(" | ", problems));
        }

        static List<Case> Cases()
        {
            if (cases != null) return cases;
            cases = new List<Case>();
            Sweep("two", Fx.TwoMonitors(), true);
            Sweep("single150", Fx.SingleMonitor(1.5f), false);
            Console.WriteLine("      (layout sweep: " + cases.Count + " selections)");
            return cases;
        }

        static void Sweep(string name, CaptureResult capture, bool spanning)
        {
            using (capture)
            using (var editor = new Editor(capture, new Settings()))
            {
                var tb = Priv.Get<Toolbar>(editor, "toolBar");
                var ab = Priv.Get<Toolbar>(editor, "actionBar");
                var chrome = Priv.Get<Chrome>(editor, "chrome");
                Priv.Set(editor, "paletteOpen", true); // bars and label don't depend on it; paletteRect is then laid out too
                var selections = new List<Rectangle>();
                int[] sizes = { 1, 6, 40, 150, 300, 520, 900, 1400, 1920 };
                foreach (MonitorSnapshot m in capture.Monitors)
                    foreach (int w in sizes)
                        foreach (int h in sizes)
                        {
                            if (w > m.Bounds.Width || h > m.Bounds.Height) continue;
                            var xs = new[] { m.Bounds.Left, m.Bounds.Left + 100, m.Bounds.Left + (m.Bounds.Width - w) / 2, m.Bounds.Right - w - 60, m.Bounds.Right - w };
                            var ys = new[] { m.Bounds.Top, m.Bounds.Top + 50, m.Bounds.Top + (m.Bounds.Height - h) / 2, m.Bounds.Bottom - h - 40, m.Bounds.Bottom - h };
                            foreach (int x in xs.Distinct())
                                foreach (int y in ys.Distinct())
                                    if (x >= m.Bounds.Left && y >= m.Bounds.Top) selections.Add(new Rectangle(x, y, w, h));
                        }
                if (spanning)
                    foreach (int x in new[] { 1500, 1800 })
                        foreach (int w in new[] { 300, 900, 1400 })
                            foreach (int y in new[] { 0, 300, 700 })
                                selections.Add(new Rectangle(x, y, w, Math.Min(380, 1080 - y)));

                int i = 0;
                foreach (Rectangle sel in selections)
                {
                    editor.Selection = sel;
                    MonitorSnapshot mon = capture.MonitorAt(new Point(sel.Left + sel.Width / 2, sel.Top + sel.Height / 2));
                    var c = new Case
                    {
                        Fixture = name, Sel = sel, Mon = mon.Bounds, Scale = mon.Scale, Tool = tb.Bounds, Action = ab.Bounds,
                        LabelMon = capture.MonitorAt(sel.Location).Bounds,
                        Label = Priv.Get<Rectangle>(editor, "labelRect"),
                        Palette = Priv.Get<Rectangle>(editor, "paletteRect"),
                    };
                    if (i++ % 7 == 0)
                        foreach (Toolbar bar in new[] { tb, ab })
                            foreach (Toolbar.Item item in bar.Items)
                                if (!item.IsSeparator)
                                    c.Tooltips.Add((item.Tooltip, bar.PopupBounds(item, chrome.PillSize(item.Tooltip, chrome.TextFont, 8, 5), sel, mon.Bounds, chrome), bar.Bounds));
                    cases.Add(c);
                }
            }
        }

        // The label stays attached to the selection: when no bar is in the way it touches the top-left corner
        // (above it, or inside it at the top of the monitor); otherwise it sits within a bar's length of the corner.
        static bool LabelNearCorner(Case c)
        {
            int gap = (int)Math.Round(6 * c.Scale);
            Rectangle corner = new Rectangle(c.Sel.Left - 1, c.Sel.Top, 1, 1);
            Rectangle near = Rectangle.Inflate(corner, c.Label.Width + 2 * gap, c.Label.Height + 2 * gap);
            if (near.IntersectsWith(c.Label)) return true;
            int reach = Math.Max(Math.Max(c.Tool.Width, c.Tool.Height), Math.Max(c.Action.Width, c.Action.Height)) + c.Label.Width + 2 * gap;
            return Rectangle.Inflate(corner, reach, reach).IntersectsWith(c.Label)
                && (c.Label.IntersectsWith(Rectangle.Inflate(c.Tool, 3 * gap, 3 * gap)) || c.Label.IntersectsWith(Rectangle.Inflate(c.Action, 3 * gap, 3 * gap)));
        }

        static Point P(int x, int y) => new Point(x, y);

        static void Shot(Rig rig, Rectangle sel, string name, string hoverItem = null, bool palette = false)
        {
            rig.SetSelection(sel);
            if (palette) rig.ClickItem("Color");
            if (hoverItem != null) rig.Move(rig.ItemCenter(hoverItem));
            else rig.Move(P(sel.Left + sel.Width / 2, sel.Top + sel.Height / 2));
            rig.Snapshot(name);
            if (palette) rig.Key(Keys.Escape);
        }

        static void Snapshots()
        {
            var problems = new List<string>();
            using (Rig rig = new Rig(Fx.TwoMonitors()))
            {
                var right = new Rectangle(1500, 300, 400, 400);
                Shot(rig, right, "l01_edge_right_m1_tooltip", "Arrow");
                if (rig.ToolBar.Bounds.Right > right.Left) problems.Add("l01: tool bar should flip to the left of the selection: " + rig.ToolBar.Bounds);
                Shot(rig, right, "l01b_edge_right_m1_palette", null, true);

                var bottom = new Rectangle(600, 800, 700, 260);
                Shot(rig, bottom, "l02_edge_bottom_m1", "Copy for Claude");
                if (rig.ActionBar.Bounds.Bottom > bottom.Top) problems.Add("l02: action bar should flip above: " + rig.ActionBar.Bounds);

                var narrowBottom = new Rectangle(600, 820, 300, 240);
                Shot(rig, narrowBottom, "l03_narrow_bottom_label_vs_actionbar");
                if (rig.F<Rectangle>("labelRect").IntersectsWith(rig.ActionBar.Bounds))
                    problems.Add("l03: size label " + rig.F<Rectangle>("labelRect") + " hidden under the action bar " + rig.ActionBar.Bounds);

                Shot(rig, new Rectangle(300, 0, 600, 300), "l04_top_edge_label_inside");
                Shot(rig, new Rectangle(1700, 900, 150, 120), "l05_corner_bottom_right_small", "Redo");
                Shot(rig, new Rectangle(2300, 250, 700, 500), "l06_m2_100_tooltip", "Blur");
                Shot(rig, new Rectangle(900, 500, 1, 1), "l07_tiny_1x1");
                Shot(rig, Fx.M1, "l08_full_monitor_m1_palette", null, true);
                Shot(rig, Fx.M1, "l08b_full_monitor_m1_tooltip", "Numbered step");
                Shot(rig, new Rectangle(1700, 300, 600, 400), "l09_spanning_monitors", "Copy (");
                Shot(rig, new Rectangle(3500, 600, 340, 300), "l10_m2_right_edge", "Text");
                Shot(rig, new Rectangle(900, 50, 40, 980), "l11_narrow_tall");
                Shot(rig, Fx.M2, "l12_full_monitor_m2_100", "Close");
                Shot(rig, new Rectangle(20, 20, 260, 160), "l13_top_left_small", "Pen");

                var corner = new Rectangle(0, 0, 150, 150);
                Shot(rig, corner, "l14_top_left_150_actionbar_offscreen");
                if (rig.ActionBar.Bounds.Left < 0)
                    problems.Add("l14: action bar " + rig.ActionBar.Bounds + " starts off-screen (Claude button unreachable) for selection " + corner);
                var m2Left = new Rectangle(1920, 60, 150, 150);
                Shot(rig, m2Left, "l15_m2_left_edge_actionbar_on_m1");
                if (!Fx.M2.Contains(rig.ActionBar.Bounds))
                    problems.Add("l15: action bar " + rig.ActionBar.Bounds + " spills onto monitor 1 for selection " + m2Left);
            }
            T.Problems(problems, "edge-case layout");
        }
    }

    static class IncrementalTests
    {
        public static void Run()
        {
            T.Run("Incremental.EveryStepEqualsFullRepaint", Sequence);
            T.Run("Incremental.NoInvalidationForNoOpMoves", NoOp);
            T.Run("Incremental.MouseMoveNeverInvalidatesWholeDesktop", Small);
        }

        static Point P(int x, int y) => new Point(x, y);

        static void Sequence()
        {
            using (var rig = new Rig(Fx.TwoMonitors()) { VerifyEach = true, Name = "inc" })
            {
                foreach (Point p in new[] { P(600, 400), P(300, 300), P(1700, 700), P(2600, 980), P(3600, 100), P(2500, 500), P(960, 1050), P(600, 400) })
                    rig.Move(p);
                rig.Drag(P(200, 150), P(900, 650));                   // rubber band
                foreach (string item in new[] { "Pen", "Line", "Numbered step", "Redo", "Copy for Claude", "Close", "Print" })
                    rig.Move(rig.ItemCenter(item));                     // tooltips
                rig.ClickItem("Color");
                for (int i = 0; i <= 10; i++)
                {
                    Rectangle cell = rig.PaletteCell(i);
                    rig.Move(new Point(cell.X + cell.Width / 2, cell.Y + cell.Height / 2));
                }
                Rectangle blue = rig.PaletteCell(5);
                rig.Click(new Point(blue.X + 3, blue.Y + 3));
                rig.Key(Keys.P); rig.DragPath(P(300, 300), P(350, 340), P(420, 310));
                rig.Key(Keys.A); rig.Drag(P(300, 400), P(600, 500), Keys.None, 8);
                rig.Key(Keys.R); rig.Drag(P(650, 200), P(850, 300), Keys.Shift);
                rig.Key(Keys.E); rig.Drag(P(650, 350), P(850, 450));
                rig.Key(Keys.H); rig.DragPath(P(300, 550), P(600, 560), P(620, 600));
                rig.Key(Keys.B); rig.Drag(P(400, 180), P(700, 260));
                rig.Key(Keys.T); rig.Click(P(500, 600)); rig.Type("ab\ncd"); rig.Tick(); rig.Tick(); rig.Wheel(120); rig.Wheel(120); rig.Key(Keys.Back); rig.Key(Keys.Escape);
                rig.Key(Keys.N); rig.Drag(P(750, 520), P(880, 620)); rig.Type("step"); rig.Tick(); rig.Tick(); rig.Wheel(-120); rig.Key(Keys.Enter);
                rig.Click(P(260, 620)); rig.Type("edge"); rig.Key(Keys.Enter);
                rig.Key(Keys.L); rig.Drag(P(800, 640), P(1100, 800));  // leaves the selection
                Priv.Set(rig.E, "hudSince", Environment.TickCount - 5000); rig.Tick();
                rig.Key(Keys.Control | Keys.Z); rig.Key(Keys.Control | Keys.Z); rig.Key(Keys.Control | Keys.Y);
                rig.Key(Keys.Escape);
                rig.Drag(P(500, 400), P(700, 500));                    // move the selection
                rig.Drag(P(1100, 750), P(1300, 900));                  // bottom-right handle
                rig.Drag(P(399, 249), P(1500, 1000));                  // top-left handle past the opposite corner
                rig.Key(Keys.Right); rig.Key(Keys.Control | Keys.Down); rig.Key(Keys.Shift | Keys.Left);
                rig.Key(Keys.Control | Keys.A); rig.Key(Keys.Control | Keys.A); rig.Key(Keys.Control | Keys.A);
                rig.Drag(P(1700, 300), P(2400, 700));                  // the selection is the monitor: drag inside it moves; fine either way
                rig.E.ClearSelection(); rig.Paint();
                rig.Move(P(2600, 500)); rig.Move(P(600, 400));
            }
        }

        static void NoOp()
        {
            using (var rig = new Rig(Fx.TwoMonitors()))
            {
                rig.Move(P(600, 400));
                rig.Invalidations.Clear();
                rig.Move(P(601, 401)); rig.Move(P(650, 420));
                T.True(rig.Invalidations.Count == 0, "hover moves inside one window invalidate nothing: " + string.Join(", ", rig.Invalidations));
                rig.SetSelection(new Rectangle(200, 150, 500, 350));
                rig.Move(P(400, 300));
                rig.Invalidations.Clear();
                rig.Move(P(410, 310)); rig.Move(P(1000, 900));
                T.True(rig.Invalidations.Count == 0, "moves without visual change invalidate nothing: " + string.Join(", ", rig.Invalidations));
            }
        }

        static void Small()
        {
            using (var rig = new Rig(Fx.TwoMonitors()))
            {
                rig.SetSelection(new Rectangle(300, 400, 500, 350));
                rig.Invalidations.Clear();
                foreach (string item in new[] { "Pen", "Line", "Arrow", "Text", "Undo", "Copy for Claude", "Save as", "Close" })
                    rig.Move(rig.ItemCenter(item));
                rig.Move(P(500, 500));
                long desktop = 3840L * 1080;
                var large = rig.Invalidations.Where(r => (long)r.Width * r.Height > desktop / 8).ToList();
                T.Note("largest tooltip-hover invalidation: " + rig.Invalidations.Max(r => (long)r.Width * r.Height) + " px");
                T.True(large.Count == 0, "tooltip hover invalidations are small: " + string.Join(", ", large));
                rig.Key(Keys.P);
                rig.Invalidations.Clear();
                rig.Down(P(400, 500));
                for (int i = 1; i <= 20; i++) rig.Move(P(400 + i * 10, 500 + i * 5), Keys.None, MouseButtons.Left);
                rig.Up(P(600, 600));
                T.True(rig.Invalidations.All(r => r != new Rectangle(0, 0, 3840, 1080)), "no full-desktop invalidation while drawing");
                long max = rig.Invalidations.Max(r => (long)r.Width * r.Height);
                T.Note("largest invalidation while drawing: " + max + " px (" + (100.0 * max / desktop).ToString("F1") + "% of the desktop; includes bars hide/show)");
            }
        }
    }
}
