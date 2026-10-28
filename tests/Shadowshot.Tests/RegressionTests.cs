using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Shadowshot.Tests
{
    // Regression tests for the fixes of review round 1 (R1-xx, R2-xx) and tester round 1 (T-R1-xx).
    static class RegressionTests
    {
        static readonly Rectangle S = new Rectangle(200, 150, 500, 350);
        static readonly Rectangle Big = new Rectangle(100, 100, 1500, 850);

        static Rig Two() => new Rig(Fx.TwoMonitors());

        static Point P(int x, int y) => new Point(x, y);

        static Point C(Rectangle r) => new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);

        public static void Run()
        {
            T.Run("Regression.ModifierKeysDontCommitText (R1-01)", ModifiersKeepTyping);
            T.Run("Regression.TextAndLabelLengthCaps (R1-05)", LengthCaps);
            T.Run("Regression.CustomColorKeepsEditing (R1-06)", CustomColor);
            T.Run("Regression.WheelAccumulatesSmallDeltas (R1-07)", WheelSmall);
            T.Run("Regression.SingleMonitorToggleKeepsSelection (R1-08)", SingleToggle);
            T.Run("Regression.LostButtonUpEndsDrag (R1-09)", LostButtonUp);
            T.Run("Regression.ZeroSizeSelectionIgnored (R1-04)", ZeroSize);
            T.Run("Regression.GhostPaletteRepaint (R2-01)", GhostPalette);
            T.Run("Regression.RubberBandAndNudgeRepaint (R2-02)", RubberBandRepaint);
            T.Run("Regression.LayoutChainIncremental (R2-01/R2-02)", LayoutChain);
            T.Run("Regression.FreehandInvalidatesNewSegmentOnly (R2-04)", Freehand);
            T.Run("Regression.ActionBarCollisionStaysOnMonitor (R1-03/T-R1-01)", ActionBarCases);
            T.Run("Regression.FullMonitorBarsInsetAlike (T-R1-06)", FullMonitorInset);
            T.Run("Regression.SizeLabelOnTopLeftCorner (R2-03/T-R1-05)", LabelSpanning);
            T.Run("Regression.VisualSnapshots (bars, corners, full monitor + palette)", Visual);
        }

        static Annotation Editing(Rig rig) => rig.F<Annotation>("editing");

        static void ModifiersKeepTyping()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(Big);
                rig.Key(Keys.T);
                rig.Click(P(300, 300));
                rig.Type("ab");
                var text = (TextAnnotation)Editing(rig);
                var alone = new[]
                {
                    Keys.ControlKey | Keys.Control, Keys.Menu | Keys.Alt, Keys.ShiftKey | Keys.Shift,
                    Keys.ShiftKey | Keys.Shift | Keys.Control, Keys.ControlKey | Keys.Control | Keys.Shift, Keys.Menu | Keys.Alt | Keys.Control,
                    Keys.ControlKey | Keys.Control | Keys.Alt,
                };
                foreach (Keys k in alone)
                {
                    T.True(!rig.Key(k), "[" + k + "] pressed alone is not handled");
                    T.True(Editing(rig) == text && text.Editing, "[" + k + "] pressed alone keeps the text open");
                }
                T.Eq(0, rig.E.Annotations.Count, "nothing committed by modifiers");

                // AltGr on a German layout: LCtrl down, RAlt down (Ctrl+Alt), Q with Ctrl+Alt, then WM_CHAR '@'.
                rig.Key(Keys.ControlKey | Keys.Control);
                rig.Key(Keys.Menu | Keys.Control | Keys.Alt);
                T.True(!rig.Key(Keys.Q | Keys.Control | Keys.Alt), "AltGr+Q is not a shortcut");
                rig.E.KeyPress('@'); rig.Paint();
                rig.Key(Keys.ControlKey | Keys.Control);
                T.True(!rig.Key(Keys.E | Keys.Control | Keys.Alt), "AltGr+E is not a shortcut");
                rig.E.KeyPress('€'); rig.Paint();
                // Alt+0233 (NumLock on): Alt down, numpad digits with Alt, WM_CHAR 'é' when Alt is released.
                rig.Key(Keys.Menu | Keys.Alt);
                foreach (Keys d in new[] { Keys.NumPad0, Keys.NumPad2, Keys.NumPad3, Keys.NumPad3 })
                    T.True(!rig.Key(d | Keys.Alt), "Alt+" + d + " is not a shortcut while typing");
                rig.E.KeyPress('é'); rig.Paint();
                T.Eq("ab@€é", text.Text, "AltGr and Alt+numpad characters reach the text");
                T.True(Editing(rig) == text, "still typing");
                rig.CheckIncremental("modifiers_text");

                // A real shortcut still finishes the text first.
                rig.Key(Keys.ControlKey | Keys.Control);
                T.True(rig.Key(Keys.C | Keys.Control), "Ctrl+C handled");
                T.True(Editing(rig) == null && rig.E.Annotations.Count == 1 && rig.Commands.Last() == EditorCommand.Copy, "Ctrl+C commits the text, then copies");
                T.Eq("ab@€é", rig.Last<TextAnnotation>().Text, "committed text");

                // Same for a Step label.
                rig.Key(Keys.N);
                rig.Click(P(700, 300));
                rig.Type("Go");
                foreach (Keys k in alone) T.True(!rig.Key(k) && Editing(rig) is StepAnnotation, "[" + k + "] alone keeps the label open");
                rig.Key(Keys.Q | Keys.Control | Keys.Alt);
                rig.E.KeyPress('@'); rig.Paint();
                rig.Key(Keys.Enter);
                T.Eq("Go@", rig.Last<StepAnnotation>().Label, "label with an AltGr char");
                T.Eq("Screenshot notes: (1) Go@", rig.E.BuildClaudeNotes(), "notes");
                rig.CheckIncremental("modifiers_label");
            }
        }

        static void LengthCaps()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(Big);
                rig.Key(Keys.T);
                rig.Click(P(200, 300));
                var text = (TextAnnotation)Editing(rig);
                text.Text = new string('a', 3998);
                rig.E.KeyPress('b');
                T.Eq(3999, text.Text.Length, "below the cap");
                rig.E.KeyPress('\uD83D'); rig.E.KeyPress('\uDE00'); // 😀 needs 2 chars, only 1 left
                T.Eq(3999, text.Text.Length, "a surrogate pair that doesn't fit is rejected whole");
                rig.E.KeyPress('c'); rig.E.KeyPress('d');
                T.Eq(4000, text.Text.Length, "capped at 4000");
                T.True(text.Text.EndsWith("bc"), "last chars typed before the cap");
                T.True(rig.Key(Keys.Enter), "Enter handled");
                T.Eq(4000, text.Text.Length, "Enter doesn't pass the cap");
                Rectangle bounds = text.GetBounds();   // a 4000-char line must not hit the GDI+ limit
                T.True(bounds.Width > 0, "GetBounds of a capped line");
                for (int i = 0; i < 4; i++) rig.Tick();
                rig.Key(Keys.Escape);
                T.Eq(4000, rig.Last<TextAnnotation>().Text.Length, "committed at the cap");
                using (Bitmap img = rig.E.RenderResult()) T.Eq(Big.Size, img.Size, "export with a capped text");

                rig.Click(P(300, 700));
                var t2 = (TextAnnotation)Editing(rig);
                rig.E.KeyPress('x'); rig.E.KeyPress('\uD83D'); rig.E.KeyPress('\uDE00');
                T.Eq("x\U0001F600", t2.Text, "a surrogate pair is accepted when it fits");
                rig.E.KeyPress('\uDE00');
                T.Eq("x\U0001F600", t2.Text, "a lone low surrogate is rejected");
                rig.Key(Keys.Back);
                T.Eq("x", t2.Text, "Backspace removes the whole pair");
                rig.Key(Keys.Escape);

                rig.Key(Keys.N);
                rig.Click(P(900, 500));
                var step = (StepAnnotation)Editing(rig);
                for (int i = 0; i < 310; i++) rig.E.KeyPress('z');
                T.Eq(300, step.Label.Length, "Step label capped at 300");
                rig.Paint();
                rig.Key(Keys.Enter);
                rig.CheckIncremental("caps");
            }
        }

        static void CustomColor()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(Big);
                rig.Key(Keys.T);
                rig.Click(P(300, 300));
                rig.Type("hi");
                var text = Editing(rig);
                rig.ClickItem("Color");
                T.True(!rig.F<Rectangle>("paletteRect").IsEmpty, "palette open while typing");
                T.True(Editing(rig) == text, "opening the palette keeps the text open");
                rig.Click(C(rig.PaletteCell(10)));
                T.Eq(EditorCommand.PickCustomColor, rig.Commands.Single(), "Custom… raises PickCustomColor (only)");
                T.True(Editing(rig) == text, "Custom… doesn't commit the text being typed");
                T.True(rig.F<Rectangle>("paletteRect").IsEmpty, "palette closed");
                rig.E.SetColor(Color.FromArgb(0x12, 0x34, 0x56)); rig.Paint(); // what CaptureOverlay does after the ColorDialog
                T.Eq(Color.FromArgb(0x12, 0x34, 0x56).ToArgb(), text.Color.ToArgb(), "custom color applied to the text being typed");
                rig.CheckIncremental("custom_color_text");
                rig.Type("!");
                rig.Key(Keys.Escape);
                T.True(rig.Last<TextAnnotation>().Text == "hi!" && rig.Last<TextAnnotation>().Color.ToArgb() == Color.FromArgb(0x12, 0x34, 0x56).ToArgb(), "committed in the custom color");

                rig.Key(Keys.N);
                rig.Click(P(700, 300));
                rig.Type("step");
                var step = Editing(rig);
                rig.ClickItem("Color");
                rig.Click(C(rig.PaletteCell(10)));
                T.True(Editing(rig) == step, "Custom… keeps a Step label open");
                rig.E.SetColor(Color.FromArgb(0x65, 0x43, 0x21)); rig.Paint();
                T.Eq(Color.FromArgb(0x65, 0x43, 0x21).ToArgb(), step.Color.ToArgb(), "custom color applied to the step");
                rig.CheckIncremental("custom_color_step");
                rig.ClickItem("Copy for Claude");
                T.True(Editing(rig) == null && rig.Commands.Last() == EditorCommand.CopyForClaude, "other commands still commit first");
            }
        }

        static void WheelSmall()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(S);
                rig.Move(P(400, 300));
                rig.Key(Keys.P);
                for (int i = 0; i < 11; i++) rig.Wheel(10);
                T.Eq(3, rig.Settings.GetToolSize(ToolKind.Pen), "110 of 120: no step yet");
                rig.Wheel(10);
                T.Eq(4, rig.Settings.GetToolSize(ToolKind.Pen), "12 x 10 = one step");
                T.Eq("Size 4", rig.F<string>("hudText"), "HUD on the step");
                rig.Wheel(100);
                rig.Wheel(-60); rig.Wheel(-60);
                T.Eq(3, rig.Settings.GetToolSize(ToolKind.Pen), "direction flip drops the 100 left over, then -120 = one step down");
                rig.Wheel(240);
                T.Eq(5, rig.Settings.GetToolSize(ToolKind.Pen), "240 = two steps");
                rig.Wheel(-360);
                T.Eq(2, rig.Settings.GetToolSize(ToolKind.Pen), "-360 = three steps down");
                for (int i = 0; i < 30; i++) rig.Wheel(30);
                T.Eq(9, rig.Settings.GetToolSize(ToolKind.Pen), "30 x 30 = 900 = 7 steps");
                rig.Key(Keys.T);
                rig.Click(P(400, 300));
                rig.Type("Size");
                for (int i = 0; i < 6; i++) rig.Wheel(20);
                var t = Editing(rig);
                T.True(t.Size == 23 && rig.Settings.GetToolSize(ToolKind.Text) == 23, "6 x 20 resizes the text being typed by one: " + t.Size);
                rig.CheckIncremental("wheel_small");
            }
        }

        static void SingleToggle()
        {
            using (var rig = new Rig(Fx.SingleMonitor(1.5f)))
            {
                rig.Move(P(1700, 40));
                rig.Click(P(1700, 40));
                T.Eq(Fx.M1, rig.E.Selection, "wallpaper click grabs the monitor");
                rig.Click(P(1700, 40), Keys.None, 2);
                T.Eq(Fx.M1, rig.E.Selection, "double-click with nothing remembered keeps the selection");
                rig.Key(Keys.Control | Keys.A);
                T.Eq(Fx.M1, rig.E.Selection, "Ctrl+A with nothing remembered keeps the selection");
                T.True(rig.E.ToolbarsVisible, "bars still shown");
                // something remembered: Ctrl+A still toggles back to it
                rig.SetSelection(new Rectangle(300, 200, 400, 300));
                rig.Key(Keys.Control | Keys.A); T.Eq(Fx.M1, rig.E.Selection, "→ monitor");
                rig.Key(Keys.Control | Keys.A); T.Eq(new Rectangle(300, 200, 400, 300), rig.E.Selection, "→ restored");
                rig.CheckIncremental("single_toggle");
            }
        }

        static void LostButtonUp()
        {
            using (Rig rig = Two())
            {
                rig.VerifyEach = true;
                rig.Name = "lost_up";
                rig.SetSelection(S);
                rig.Down(P(400, 300));
                rig.Move(P(410, 310), Keys.None, MouseButtons.Left);
                Rectangle moved = rig.E.Selection;
                T.Eq(new Rectangle(210, 160, 500, 350), moved, "moving");
                rig.Move(P(800, 600)); // no button: the up was lost
                T.Eq(moved, rig.E.Selection, "a move without the button doesn't move the selection");
                T.True(rig.E.ToolbarsVisible, "the drag ended: bars back");
                rig.Move(P(900, 700));
                T.Eq(moved, rig.E.Selection, "further hovering changes nothing");
                rig.ClickItem("Rectangle");
                T.Eq(ToolKind.Rectangle, rig.E.Tool, "the next click is not swallowed");

                rig.Key(Keys.P);
                rig.Down(P(300, 300));
                rig.Move(P(350, 320), Keys.None, MouseButtons.Left);
                rig.Move(P(500, 400)); // lost up while drawing
                T.Eq(1, rig.E.Annotations.Count, "the stroke is committed");
                var pen = rig.Last<PenAnnotation>();
                T.True(!pen.Points.Contains(P(500, 400)), "the buttonless point is not part of the stroke");
                rig.Move(P(600, 450));
                T.Eq(1, rig.E.Annotations.Count, "no new stroke from hovering");

                rig.Key(Keys.P);
                rig.Down(P(1200, 900)); // rubber band outside the selection
                rig.Move(P(1300, 950), Keys.None, MouseButtons.Left);
                rig.Move(P(1500, 1000)); // lost up
                T.Eq(Rectangle.FromLTRB(1200, 900, 1300, 950), rig.E.Selection, "the rubber band ends where the button was last held");
                T.True(rig.E.ToolbarsVisible, "bars shown after the lost up");
                rig.Up(P(1500, 1000)); // a late button-up is harmless
                T.Eq(Rectangle.FromLTRB(1200, 900, 1300, 950), rig.E.Selection, "late button-up ignored");
                rig.CheckIncremental("lost_up_end");
            }
        }

        static void ZeroSize()
        {
            var s = new Settings { KeepSelection = true, LastSelection = new Rectangle(1920, 0, 1920, 1080) };
            using (var rig = new Rig(Fx.SingleMonitor(1.5f), s))
            {
                T.Eq(Rectangle.Empty, rig.E.Selection, "a remembered rect that only touches the desktop is no selection");
                T.True(!rig.E.ToolbarsVisible && !rig.F<Rectangle>("hintRect").IsEmpty, "hint shown, no bars");
                rig.Key(Keys.Enter);
                rig.Key(Keys.Control | Keys.C);
                T.Eq(0, rig.Commands.Count, "no action without a selection");
            }
            s.LastSelection = new Rectangle(0, 1080, 500, 100);
            using (var rig = new Rig(Fx.TwoMonitors(), s))
                T.Eq(Rectangle.Empty, rig.E.Selection, "zero-height leftover");
            s.LastSelection = new Rectangle(10, 10, 200, 100);
            using (var rig = new Rig(Fx.TwoMonitors(), s))
                T.Eq(new Rectangle(10, 10, 200, 100), rig.E.Selection, "a normal rect is still restored");
            using (Rig rig = Two())
            {
                foreach (Rectangle r in new[] { new Rectangle(100, 100, 0, 50), new Rectangle(100, 100, 50, 0), new Rectangle(3840, 0, 100, 100), new Rectangle(-50, 10, 50, 50), new Rectangle(0, -40, 50, 40) })
                {
                    rig.SetSelection(r);
                    T.Eq(Rectangle.Empty, rig.E.Selection, "Selection = " + r);
                }
                rig.SetSelection(new Rectangle(-50, 10, 51, 50));
                T.Eq(new Rectangle(0, 10, 1, 50), rig.E.Selection, "a 1 px leftover is kept");
                rig.CheckIncremental("zero_size");
            }
        }

        static void GhostPalette()
        {
            var problems = new List<string>();
            foreach (var (name, single, mouse) in new[] { ("m1", false, P(960, 540)), ("m2", false, P(2880, 540)), ("single", true, P(960, 540)) })
                using (var rig = single ? new Rig(Fx.SingleMonitor(1.5f)) : Two())
                {
                    rig.Move(mouse);
                    rig.Key(Keys.Control | Keys.A); // full monitor: both bars and the palette inside its bottom-right corner
                    Rectangle tb = rig.ToolBar.Bounds, ab = rig.ActionBar.Bounds;
                    Action open = () =>
                    {
                        rig.ClickItem("Color");
                        T.True(!rig.F<Rectangle>("paletteRect").IsEmpty, name + ": palette open");
                    };
                    open();
                    Rectangle palette = rig.F<Rectangle>("paletteRect");
                    if (palette.IntersectsWith(ab) || palette.IntersectsWith(tb)) problems.Add(name + ": palette " + palette + " overlaps a bar (tool " + tb + ", action " + ab + ")");
                    if (!Chrome.Union(tb, ab).Contains(palette)) T.Note(name + ": palette " + palette + " is outside the bars' union " + Chrome.Union(tb, ab));
                    rig.Move(C(palette));
                    rig.CheckIncremental("ghost_" + name + "_open");
                    rig.Move(P(mouse.X - 300, mouse.Y - 200));   // off the swatches
                    rig.Key(Keys.Escape);
                    rig.CheckIncremental("ghost_" + name + "_esc");
                    open();
                    rig.Click(P(mouse.X - 300, mouse.Y - 200));  // click in the selection closes it
                    rig.CheckIncremental("ghost_" + name + "_click_inside");
                    open();
                    rig.Click(C(rig.PaletteCell(4)));          // a pick closes it
                    rig.CheckIncremental("ghost_" + name + "_pick");
                    open();
                    rig.ClickItem("Color");                    // the button toggles it
                    rig.CheckIncremental("ghost_" + name + "_toggle");
                    open();
                    rig.Key(Keys.P);                           // picking a tool with the palette open
                    rig.CheckIncremental("ghost_" + name + "_tool");
                    rig.Key(Keys.Escape);
                    rig.CheckIncremental("ghost_" + name + "_tool_esc");
                }
            T.Problems(problems, "palette placement");
        }

        static void RubberBandRepaint()
        {
            using (Rig rig = Two())
            {
                rig.VerifyEach = true;
                rig.Name = "hband";
                rig.Down(P(700, 300));
                rig.Move(P(700, 700), Keys.None, MouseButtons.Left);
                foreach (int x in new[] { 703, 706, 709, 730, 760, 790 }) rig.Move(P(x, 700), Keys.None, MouseButtons.Left);
                rig.Up(P(790, 700));
                rig.CheckIncremental("hband_up");
            }
            using (Rig rig = Two())
            {
                rig.VerifyEach = true;
                rig.Name = "vband";
                rig.Down(P(1000, 300));
                rig.Move(P(1200, 300), Keys.None, MouseButtons.Left);
                foreach (int y in new[] { 303, 306, 309, 320, 340 }) rig.Move(P(1200, y), Keys.None, MouseButtons.Left);
                rig.Up(P(1200, 340));
            }
            using (Rig rig = Two())
            {
                // narrow selections at the right edge of each monitor, where the label is clamped to the monitor
                rig.VerifyEach = true;
                rig.Name = "edge_nudge";
                foreach (Rectangle r in new[] { new Rectangle(1890, 500, 10, 40), new Rectangle(3830, 500, 4, 30), new Rectangle(1906, 300, 8, 8) })
                {
                    rig.SetSelection(r);
                    rig.CheckIncremental("edge_" + r.X);
                    for (int i = 0; i < 4; i++) { rig.Key(Keys.Shift | Keys.Right); rig.CheckIncremental("edge_grow_" + r.X + "_" + i); }
                    for (int i = 0; i < 4; i++) { rig.Key(Keys.Right); rig.CheckIncremental("edge_move_" + r.X + "_" + i); }
                    for (int i = 0; i < 4; i++) { rig.Key(Keys.Shift | Keys.Left); rig.CheckIncremental("edge_shrink_" + r.X + "_" + i); }
                }
            }
        }

        // Jumps between many selections (incl. with the palette open and a tooltip shown): the incremental screen must
        // equal a full repaint after every jump, so moved bars, palette, label and frame are always repainted.
        static void LayoutChain()
        {
            var sels = new List<Rectangle>();
            foreach (Rectangle m in new[] { Fx.M1, Fx.M2 })
                foreach (var (w, h) in new[] { (1, 1), (40, 40), (150, 150), (300, 240), (900, 520) })
                    foreach (var (fx, fy) in new[] { (0, 0), (1, 0), (0, 1), (1, 1), (2, 2) })
                        sels.Add(new Rectangle(fx == 2 ? m.Left + (m.Width - w) / 2 : fx == 0 ? m.Left : m.Right - w,
                                               fy == 2 ? m.Top + (m.Height - h) / 2 : fy == 0 ? m.Top : m.Bottom - h, w, h));
            sels.AddRange(new[] { new Rectangle(1500, 0, 900, 380), new Rectangle(1800, 700, 1400, 380), Fx.M1, Fx.M2, Fx.Desktop2, new Rectangle(1919, 1079, 2, 1) });
            using (Rig rig = Two())
            {
                int i = 0;
                foreach (Rectangle r in sels)
                {
                    rig.SetSelection(r);
                    rig.CheckIncremental("chain_" + i);
                    if (i % 3 == 0)
                    {
                        rig.ClickItem("Color");
                        rig.CheckIncremental("chain_" + i + "_palette");
                        // the next selection is set while the palette is still open
                    }
                    else if (i % 3 == 1)
                    {
                        rig.Move(rig.ItemCenter("Numbered step"));
                        rig.CheckIncremental("chain_" + i + "_tooltip");
                        rig.Move(P(5, 5));
                    }
                    else rig.Key(Keys.Escape); // closes a palette if one is open
                    i++;
                }
                rig.CheckIncremental("chain_end");
            }
        }

        static void Freehand()
        {
            using (Rig rig = Two())
            {
                rig.VerifyEach = true;
                rig.Name = "freehand";
                rig.SetSelection(new Rectangle(100, 100, 1700, 900));
                foreach (Keys tool in new[] { Keys.P, Keys.H })
                {
                    rig.Key(tool);
                    rig.Down(P(200, 200));
                    for (int i = 1; i <= 20; i++) rig.Move(P(200 + i * 60, 200 + (i % 2) * 600), Keys.None, MouseButtons.Left);
                    rig.Invalidations.Clear();
                    rig.Move(P(1450, 300), Keys.None, MouseButtons.Left);
                    long max = rig.Invalidations.Max(r => (long)r.Width * r.Height);
                    T.True(max < 120000, tool + ": a new segment of a long stroke repaints only around it: " + string.Join(", ", rig.Invalidations));
                    rig.Invalidations.Clear();
                    rig.Move(P(1450, 300), Keys.None, MouseButtons.Left);
                    T.True(rig.Invalidations.Count == 0, tool + ": a repeated point invalidates nothing");
                    rig.Up(P(1450, 300));
                }
                T.Eq(22, rig.E.Annotations.OfType<PenAnnotation>().Single().Points.Count, "pen points (start + 21 moves)");
                rig.CheckIncremental("freehand_end");
            }
        }

        static void CheckBars(Rig rig, Rectangle sel, List<string> problems)
        {
            Rectangle mon = rig.Capture.MonitorAt(C(sel)).Bounds, labelMon = rig.Capture.MonitorAt(sel.Location).Bounds;
            Rectangle a = rig.ActionBar.Bounds, t = rig.ToolBar.Bounds, l = rig.F<Rectangle>("labelRect");
            string where = "sel " + sel + " tool " + t + " action " + a + " label " + l + ": ";
            if (!mon.Contains(a)) problems.Add(where + "action bar not inside the selection's monitor " + mon);
            if (!mon.Contains(t)) problems.Add(where + "tool bar not inside the selection's monitor " + mon);
            if (a.IntersectsWith(t)) problems.Add(where + "bars overlap");
            if (l.IntersectsWith(a) || l.IntersectsWith(t)) problems.Add(where + "label covered by a bar");
            if (!labelMon.Contains(l)) problems.Add(where + "label not inside the top-left corner's monitor " + labelMon);
            foreach (Toolbar.Item item in rig.ActionBar.Items.Concat(rig.ToolBar.Items))
                if (!item.IsSeparator && !mon.Contains(item.Bounds)) problems.Add(where + "button '" + item.Tooltip + "' off the monitor: " + item.Bounds);
        }

        static void ActionBarCases()
        {
            var problems = new List<string>();
            using (Rig rig = Two())
                foreach (Rectangle s in new[]
                {
                    new Rectangle(0, 0, 200, 100), new Rectangle(20, 950, 120, 100), new Rectangle(1925, 40, 150, 80),
                    new Rectangle(0, 0, 150, 150), new Rectangle(1920, 60, 150, 150), new Rectangle(0, 0, 1, 1), new Rectangle(1919, 1079, 1, 1),
                    new Rectangle(3839, 1079, 1, 1), new Rectangle(1920, 0, 1, 1), new Rectangle(1919, 0, 1, 1), new Rectangle(600, 820, 300, 240),
                    new Rectangle(900, 500, 1, 1), new Rectangle(0, 600, 1, 1), new Rectangle(3700, 0, 140, 1080), new Rectangle(0, 1000, 1920, 80),
                })
                {
                    rig.SetSelection(s);
                    CheckBars(rig, s, problems);
                    rig.CheckIncremental("actionbar_" + s.X + "_" + s.Y);
                }
            using (var rig = new Rig(Fx.SingleMonitor(1.5f)))
                foreach (Rectangle s in new[] { new Rectangle(0, 0, 150, 150), new Rectangle(1770, 930, 150, 150), new Rectangle(0, 930, 150, 150), new Rectangle(1770, 0, 150, 150) })
                {
                    rig.SetSelection(s);
                    CheckBars(rig, s, problems);
                }
            T.Problems(problems, "bar layout", 8);
        }

        static void FullMonitorInset()
        {
            foreach (var (name, single, mouse, mon, scale) in new[]
            {
                ("m1", false, P(960, 540), Fx.M1, 1.25f), ("m2", false, P(2880, 540), Fx.M2, 1f), ("single", true, P(960, 540), Fx.M1, 1.5f),
            })
                using (var rig = single ? new Rig(Fx.SingleMonitor(1.5f)) : Two())
                {
                    rig.Move(mouse);
                    rig.Key(Keys.Control | Keys.A);
                    T.Eq(mon, rig.E.Selection, name + ": full monitor");
                    int gap = (int)Math.Round(5 * scale);
                    Rectangle t = rig.ToolBar.Bounds, a = rig.ActionBar.Bounds;
                    T.Eq(mon.Bottom - gap, t.Bottom, name + ": tool bar inset from the bottom");
                    T.Eq(mon.Bottom - gap, a.Bottom, name + ": action bar inset alike");
                    T.Eq(mon.Right - gap, t.Right, name + ": tool bar inset from the right");
                    T.True(!t.IntersectsWith(a) && mon.Contains(t) && mon.Contains(a), name + ": bars apart and on the monitor: " + t + " " + a);
                }
        }

        static void LabelSpanning()
        {
            using (Rig rig = Two())
            {
                rig.SetSelection(new Rectangle(1700, 100, 600, 750)); // centre on monitor 2, corner on monitor 1
                Rectangle l = rig.F<Rectangle>("labelRect");
                T.True(l.X == 1699 && l.Bottom <= 100 && Fx.M1.Contains(l), "label just above the corner on monitor 1: " + l);
                rig.SetSelection(new Rectangle(1000, 100, 2000, 600));
                l = rig.F<Rectangle>("labelRect");
                T.True(l.X == 999 && l.Bottom <= 100, "label at the corner: " + l);
                rig.SetSelection(Fx.Desktop2);
                l = rig.F<Rectangle>("labelRect");
                T.True(l.X <= 20 && l.Y <= 20 && Fx.M1.Contains(l), "full desktop: label inside the top-left corner of monitor 1: " + l);
                rig.SetSelection(new Rectangle(1800, 0, 400, 300));
                l = rig.F<Rectangle>("labelRect");
                T.True(Fx.M1.Contains(l) && l.Y < 50 && l.Right > 1800 - 10, "corner at the top of monitor 1: label inside the corner, on monitor 1: " + l);
                rig.Snapshot("v05_label_spanning_corner_m1");
            }
        }

        static void Visual()
        {
            var problems = new List<string>();
            using (Rig rig = Two())
            {
                foreach (var (name, sel) in new[] { ("v01_bars_m1_125", new Rectangle(300, 250, 700, 450)), ("v02_bars_m2_100", new Rectangle(2300, 250, 700, 450)) })
                {
                    rig.SetSelection(sel);
                    rig.Move(P(sel.Left + 50, sel.Top + 50));
                    CheckBars(rig, sel, problems);
                    rig.Snapshot(name);
                }
                int k = 10;
                foreach (var (mname, m) in new[] { ("m1", Fx.M1), ("m2", Fx.M2) })
                    foreach (var (cname, fx, fy) in new[] { ("top_left", 0, 0), ("top_right", 1, 0), ("bottom_left", 0, 1), ("bottom_right", 1, 1) })
                    {
                        var sel = new Rectangle(fx == 0 ? m.Left : m.Right - 140, fy == 0 ? m.Top : m.Bottom - 100, 140, 100);
                        rig.SetSelection(sel);
                        rig.Move(C(sel));
                        CheckBars(rig, sel, problems);
                        rig.Snapshot("v" + k++ + "_small_" + mname + "_" + cname, margin: 60);
                    }
                foreach (var (name, mouse) in new[] { ("v20_full_m1_palette", P(960, 540)), ("v21_full_m2_palette", P(2880, 540)) })
                {
                    rig.Move(mouse);
                    rig.SetSelection(rig.Capture.MonitorAt(mouse).Bounds);
                    rig.ClickItem("Color");
                    rig.Move(C(rig.PaletteCell(2)));
                    Rectangle tb = rig.ToolBar.Bounds, ab = rig.ActionBar.Bounds, pal = rig.F<Rectangle>("paletteRect");
                    CheckBars(rig, rig.E.Selection, problems);
                    if (pal.IntersectsWith(tb) || pal.IntersectsWith(ab)) problems.Add(name + ": palette " + pal + " overlaps a bar");
                    if (!rig.Capture.MonitorAt(mouse).Bounds.Contains(pal)) problems.Add(name + ": palette " + pal + " off the monitor");
                    // gap between the bars: horizontal if they share rows, else vertical
                    int gap = tb.Top < ab.Bottom && ab.Top < tb.Bottom ? Math.Max(ab.Left - tb.Right, tb.Left - ab.Right) : Math.Max(ab.Top - tb.Bottom, tb.Top - ab.Bottom);
                    T.Note(name + ": tool " + tb + ", action " + ab + ", palette " + pal + ", gap between bars " + gap + " px");
                    rig.Snapshot(name, full: true);
                    Img.SaveCrop(rig.FullRender(), Rectangle.Inflate(rig.F<Rectangle>("barsArea"), 20, 20), name + "_zoom", 2);
                    rig.Key(Keys.Escape);
                }
            }
            T.Problems(problems, "bar layout in the snapshots");
        }
    }
}
