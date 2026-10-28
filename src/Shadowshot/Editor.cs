using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Shadowshot
{
    // The overlay's brain: selection, tools, annotations, input handling and painting, all in capture space.
    // It has no Form dependency so tests can drive it headless; the form forwards input and repaints what Invalidate asks for.
    public sealed class Editor : IDisposable
    {
        enum DragMode { None, Select, Move, Resize, Draw, Button }
        enum BarAction { Color, Undo, Redo }

        static readonly (ToolKind Kind, Keys Key, string Icon, string Tooltip)[] ToolDefs =
        {
            (ToolKind.Pen, Keys.P, "pen", "Pen (P)"),
            (ToolKind.Line, Keys.L, "line", "Line (L)"),
            (ToolKind.Arrow, Keys.A, "arrow", "Arrow (A)"),
            (ToolKind.Rectangle, Keys.R, "rectangle", "Rectangle (R)"),
            (ToolKind.Ellipse, Keys.E, "ellipse", "Ellipse (E)"),
            (ToolKind.Marker, Keys.H, "marker", "Highlighter (H)"),
            (ToolKind.Text, Keys.T, "text", "Text (T)"),
            (ToolKind.Step, Keys.N, "step", "Numbered step (N) — click, or drag to point"),
            (ToolKind.Pixelate, Keys.B, "pixelate", "Blur / redact secrets (B)"),
        };

        static readonly Color[] Swatches =
        {
            Rgb(0xFF0000), Rgb(0xFF9500), Rgb(0xFFCC00), Rgb(0x34C759), Rgb(0x32ADE6),
            Rgb(0x007AFF), Rgb(0xAF52DE), Rgb(0xFF2D55), Rgb(0xFFFFFF), Rgb(0x000000),
        };
        const int PaletteColumns = 5;
        const int CustomCell = 10; // palette cell index of "Custom…"

        // Handle directions: -1 = left/top edge, 1 = right/bottom edge, 0 = middle. Corners first so they win on tiny selections.
        static readonly Point[] Handles =
        {
            new Point(-1, -1), new Point(1, -1), new Point(1, 1), new Point(-1, 1),
            new Point(0, -1), new Point(1, 0), new Point(0, 1), new Point(-1, 0),
        };

        const string HintText = "Drag to select an area  •  Click to grab a window  •  Esc to cancel";
        const int ClickSlop = 4;          // px of movement before a press becomes a rubber band
        const int StepTipDistance = 8;    // px of drag before a step badge gets a leader arrow
        const int HudMilliseconds = 1000;
        const int MaxTextLength = 4000;   // chars of a Text annotation (GDI+ fails on very long lines)
        const int MaxLabelLength = 300;   // chars of a Step label

        readonly CaptureResult capture;
        readonly Settings settings;
        readonly Rectangle desktop;
        readonly Bitmap dimmed;
        readonly Chrome chrome = new Chrome();
        readonly Toolbar toolBar = new Toolbar(true);
        readonly Toolbar actionBar = new Toolbar(false);
        readonly Toolbar.Item colorItem, undoItem, redoItem;
        readonly List<Annotation> annotations = new List<Annotation>();
        readonly List<Annotation> redo = new List<Annotation>();

        Rectangle selection;
        Rectangle selectionBeforeFullScreen;
        ToolKind tool;
        Point lastMouse;

        DragMode drag;
        Point dragStart;
        Rectangle dragStartSelection;
        Point grabbedHandle;  // handle direction pressed at mouse-down
        Point resizeDir;      // same, mirrored on each axis dragged past the opposite edge (for the cursor)
        bool dragCtrl;        // Ctrl held when a rubber band started → auto Copy
        bool dragMoved;
        Annotation drawing;   // shape following the mouse
        Annotation editing;   // TextAnnotation (not yet in the list) or StepAnnotation (in the list) receiving typed text
        bool caretOn;
        int caretTicks;

        Toolbar.Item pressed;
        bool paletteOpen;
        string hudText;
        Point hudAnchor;
        int hudSince;
        int wheelRest;        // wheel delta not yet worth a whole step (120)
        Rectangle dirty;      // annotation pixels to repaint on the next Flush

        // Derived by Layout(); Render() and invalidation use exactly what was laid out.
        Rectangle hoverRect, hintRect, labelRect, paletteRect, tooltipRect, hudRect;
        Rectangle frameArea, barsArea, frameSelection;
        Toolbar.Item hover;
        int paletteHover = -1;
        string tooltipText, hudShown;
        Cursor cursor = Cursors.Cross;
        (Toolbar.Item Hover, Toolbar.Item Pressed, ToolKind Tool, int Color, bool CanUndo, bool CanRedo, int PaletteHover,
         Rectangle Palette, Rectangle Tooltip, Rectangle ToolBar, Rectangle ActionBar) barsKey;

        public Editor(CaptureResult capture, Settings settings)
        {
            this.capture = capture;
            this.settings = settings;
            desktop = new Rectangle(Point.Empty, capture.Image.Size);
            dimmed = CreateDimmed(capture.Image, desktop);

            foreach (var t in ToolDefs)
                toolBar.Add(t.Kind, t.Icon, t.Tooltip);
            toolBar.AddSeparator();
            colorItem = toolBar.Add(BarAction.Color, null, "Color");
            undoItem = toolBar.Add(BarAction.Undo, "undo", "Undo (Ctrl+Z)");
            redoItem = toolBar.Add(BarAction.Redo, "redo", "Redo (Ctrl+Y)");

            actionBar.Add(EditorCommand.CopyForClaude, "claude", "Copy for Claude (Enter) — then press Ctrl+V in Claude Code", wide: true);
            actionBar.Add(EditorCommand.Print, "print", "Print (Ctrl+P)");
            actionBar.Add(EditorCommand.Copy, "copy", "Copy (Ctrl+C)");
            actionBar.Add(EditorCommand.SaveAs, "save", "Save as… (quick save: Ctrl+S)");
            actionBar.AddSeparator();
            actionBar.Add(EditorCommand.Close, "close", "Close (Esc)");

            lastMouse = capture.Cursor;
            if (settings.KeepSelection)
                selection = Clip(settings.LastSelection);
            Layout();
        }

        public event Action<Rectangle> Invalidate;
        public event Action<EditorCommand> Command;

        public Rectangle Selection
        {
            get => selection;
            set
            {
                selection = Clip(value);
                Flush();
            }
        }

        public List<Annotation> Annotations => annotations;

        public ToolKind Tool
        {
            get => tool;
            set
            {
                CommitText();
                tool = value;
                paletteOpen = false;
                Flush();
            }
        }

        public Color Color => tool == ToolKind.Marker ? settings.MarkerColor : settings.Color;

        public Point LastMouse => lastMouse;
        public bool ToolbarsVisible => !selection.IsEmpty && !dragMoved;
        public Cursor Cursor => cursor;

        public void SetColor(Color c)
        {
            if (tool == ToolKind.Marker) settings.MarkerColor = c;
            else settings.Color = c;
            if (editing != null)
            {
                MarkDirty(editing.GetBounds());
                editing.Color = c;
            }
            Flush();
        }

        // ---------------------------------------------------------------- mouse

        public void MouseDown(Point p, MouseButtons b, Keys modifiers, int clicks)
        {
            lastMouse = p;
            if (drag != DragMode.None) return;
            if (b == MouseButtons.Right)
            {
                RaiseCommand(selection.IsEmpty ? EditorCommand.Close : EditorCommand.ContextMenu);
                Flush();
                return;
            }
            if (b != MouseButtons.Left) return;

            if (ToolbarsVisible && (toolBar.Bounds.Contains(p) || actionBar.Bounds.Contains(p)))
            {
                pressed = toolBar.HitTest(p) ?? actionBar.HitTest(p);
                if (pressed != null) drag = DragMode.Button;
            }
            else if (paletteOpen && paletteRect.Contains(p))
            {
                int cell = PaletteCellAt(p);
                if (cell >= 0) PickPaletteCell(cell);
            }
            else
            {
                paletteOpen = false;
                CommitText();
                int handle = selection.IsEmpty ? -1 : HandleAt(p);
                if (handle >= 0)
                {
                    grabbedHandle = resizeDir = Handles[handle];
                    StartDrag(DragMode.Resize, p);
                }
                else if (selection.Contains(p))
                {
                    if (tool == ToolKind.Text) StartText(p);
                    else if (tool != ToolKind.None) StartDrawing(p);
                    else if (clicks == 2) ToggleFullScreen();
                    else StartDrag(DragMode.Move, p);
                }
                else if (selection.IsEmpty || tool == ToolKind.None)
                {
                    StartDrag(DragMode.Select, p);
                    dragCtrl = (modifiers & Keys.Control) != 0;
                }
            }
            Flush();
        }

        public void MouseMove(Point p, MouseButtons b, Keys modifiers)
        {
            lastMouse = p;
            if (drag != DragMode.None && (b & MouseButtons.Left) == 0)
            {
                // The button-up was lost (a dialog or another app took the capture): end the drag here.
                MouseUp(p, MouseButtons.Left, modifiers);
                return;
            }
            int dx = p.X - dragStart.X, dy = p.Y - dragStart.Y;
            switch (drag)
            {
                case DragMode.Select:
                    dragMoved |= Math.Abs(dx) >= ClickSlop || Math.Abs(dy) >= ClickSlop;
                    if (dragMoved) selection = FromEdges(dragStart.X, dragStart.Y, p.X, p.Y);
                    break;
                case DragMode.Move:
                    dragMoved |= p != dragStart;
                    Rectangle moved = dragStartSelection;
                    moved.X = Clamp(moved.X + dx, 0, desktop.Width - moved.Width);
                    moved.Y = Clamp(moved.Y + dy, 0, desktop.Height - moved.Height);
                    selection = moved;
                    break;
                case DragMode.Resize:
                    dragMoved |= p != dragStart;
                    ResizeSelection(dx, dy);
                    break;
                case DragMode.Draw:
                    dragMoved |= p != dragStart;
                    UpdateDrawing(p, (modifiers & Keys.Shift) != 0);
                    break;
            }
            Flush();
        }

        public void MouseUp(Point p, MouseButtons b, Keys modifiers)
        {
            if (b != MouseButtons.Left || drag == DragMode.None) return;
            lastMouse = p;
            DragMode ended = drag;
            drag = DragMode.None;
            bool moved = dragMoved;
            dragMoved = false;
            switch (ended)
            {
                case DragMode.Button:
                    Toolbar.Item item = pressed;
                    pressed = null;
                    if (item == (toolBar.HitTest(p) ?? actionBar.HitTest(p))) Click(item);
                    break;
                case DragMode.Select:
                    if (!moved && dragStartSelection.IsEmpty) selection = HoverTarget(p);
                    if (dragCtrl && (moved || dragStartSelection.IsEmpty)) RaiseCommand(EditorCommand.Copy);
                    break;
                case DragMode.Draw:
                    FinishDrawing();
                    break;
            }
            Flush();
        }

        public void MouseWheel(Point p, int delta, Keys modifiers)
        {
            lastMouse = p;
            // Touchpads and smooth-scroll mice send small deltas: one step per 120, restarting when the direction flips.
            if ((delta ^ wheelRest) < 0) wheelRest = 0;
            wheelRest += delta;
            int steps = wheelRest / 120;
            wheelRest -= steps * 120;
            if (steps != 0) ChangeSize(steps);
            Flush();
        }

        void StartDrag(DragMode mode, Point p)
        {
            drag = mode;
            dragStart = p;
            dragStartSelection = selection;
            dragMoved = false;
        }

        void ResizeSelection(int dx, int dy)
        {
            Rectangle s = dragStartSelection;
            Point h = grabbedHandle;
            int l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;
            if (h.X < 0) l += dx; else if (h.X > 0) r += dx;
            if (h.Y < 0) t += dy; else if (h.Y > 0) b += dy;
            resizeDir = new Point(l > r ? -h.X : h.X, t > b ? -h.Y : h.Y);
            selection = FromEdges(l, t, r, b);
        }

        void Click(Toolbar.Item item)
        {
            if (!(item.Tag is BarAction.Color)) paletteOpen = false;
            switch (item.Tag)
            {
                case ToolKind kind: Tool = tool == kind ? ToolKind.None : kind; break;
                case EditorCommand command: RaiseCommand(command); break;
                case BarAction.Color: paletteOpen = !paletteOpen; break;
                case BarAction.Undo: Undo(); break;
                case BarAction.Redo: Redo(); break;
            }
        }

        void PickPaletteCell(int cell)
        {
            paletteOpen = false;
            // Not RaiseCommand: the text being typed stays open so the picked color applies to it, as a swatch does.
            if (cell == CustomCell) Command?.Invoke(EditorCommand.PickCustomColor);
            else SetColor(Swatches[cell]);
        }

        // ---------------------------------------------------------------- drawing

        void StartDrawing(Point p)
        {
            Annotation a = tool switch
            {
                ToolKind.Pen => new PenAnnotation { Points = { p } },
                ToolKind.Marker => new MarkerAnnotation { Points = { p } },
                ToolKind.Line => new LineAnnotation { Start = p, End = p },
                ToolKind.Arrow => new ArrowAnnotation { Start = p, End = p },
                ToolKind.Rectangle => new RectangleAnnotation { Rect = new Rectangle(p, Size.Empty) },
                ToolKind.Ellipse => new EllipseAnnotation { Rect = new Rectangle(p, Size.Empty) },
                ToolKind.Pixelate => new PixelateAnnotation { Rect = new Rectangle(p, Size.Empty) },
                _ => new StepAnnotation { Number = annotations.OfType<StepAnnotation>().Count() + 1, Center = p },
            };
            a.Color = Color;
            a.Size = settings.GetToolSize(tool);
            drawing = a;
            StartDrag(DragMode.Draw, p);
            MarkDirty(a.GetBounds());
        }

        void UpdateDrawing(Point p, bool shift)
        {
            // Freehand strokes only change around their new segment (outsets as in their GetBounds).
            if (drawing is PenAnnotation pen) { AddSegment(pen.Points, p, AnnotationGfx.HaloWidth(pen.Size) / 2); return; }
            if (drawing is MarkerAnnotation marker) { AddSegment(marker.Points, p, marker.Size / 2f); return; }
            Rectangle before = drawing.GetBounds();
            switch (drawing)
            {
                case LineAnnotation line: line.End = shift ? Snap45(line.Start, p) : p; break;
                case ArrowAnnotation arrow: arrow.End = shift ? Snap45(arrow.Start, p) : p; break;
                case RectangleAnnotation rect: rect.Rect = ShapeRect(dragStart, p, shift); break;
                case EllipseAnnotation ellipse: ellipse.Rect = ShapeRect(dragStart, p, shift); break;
                case PixelateAnnotation pixelate: pixelate.Rect = ShapeRect(dragStart, p, shift); break;
                case StepAnnotation step:
                    int dx = p.X - step.Center.X, dy = p.Y - step.Center.Y;
                    step.Tip = dx * dx + dy * dy >= StepTipDistance * StepTipDistance ? p : (Point?)null;
                    break;
            }
            MarkDirty(Chrome.Union(before, drawing.GetBounds()));
        }

        void FinishDrawing()
        {
            Annotation a = drawing;
            drawing = null;
            MarkDirty(a.GetBounds());
            if (a.IsEmpty)
            {
                a.Dispose();
                return;
            }
            Push(a);
            if (a is StepAnnotation) BeginEditing(a);
        }

        void Push(Annotation a)
        {
            annotations.Add(a);
            foreach (Annotation r in redo)
                r.Dispose();
            redo.Clear();
        }

        void Undo()
        {
            CommitText();
            if (annotations.Count == 0) return;
            Annotation a = annotations[annotations.Count - 1];
            annotations.RemoveAt(annotations.Count - 1);
            redo.Add(a);
            MarkDirty(a.GetBounds());
            Flush();
        }

        void Redo()
        {
            CommitText();
            if (redo.Count == 0) return;
            Annotation a = redo[redo.Count - 1];
            redo.RemoveAt(redo.Count - 1);
            annotations.Add(a);
            MarkDirty(a.GetBounds());
            Flush();
        }

        void ChangeSize(int step)
        {
            ToolKind kind = editing is TextAnnotation ? ToolKind.Text : editing is StepAnnotation ? ToolKind.Step : tool;
            if (kind == ToolKind.None) return;
            settings.SetToolSize(kind, settings.GetToolSize(kind) + step);
            int size = settings.GetToolSize(kind);
            if (editing != null)
            {
                MarkDirty(editing.GetBounds());
                editing.Size = size;
                MarkDirty(editing.GetBounds());
            }
            hudText = "Size " + size;
            hudAnchor = lastMouse;
            hudSince = Environment.TickCount;
            Flush();
        }

        // ---------------------------------------------------------------- text

        void StartText(Point p)
        {
            int size = settings.GetToolSize(ToolKind.Text);
            // Center the first line on the click so the caret appears where the user clicked.
            BeginEditing(new TextAnnotation { Color = Color, Size = size, Location = new Point(p.X, p.Y - size * 3 / 5) });
        }

        void BeginEditing(Annotation a)
        {
            editing = a;
            caretOn = true;
            caretTicks = 0;
            SetEditing(a, true, true);
            MarkDirty(a.GetBounds());
        }

        public void CommitText()
        {
            if (editing == null) return;
            Annotation a = editing;
            editing = null;
            MarkDirty(a.GetBounds());
            SetEditing(a, false, false);
            if (a is TextAnnotation)
            {
                if (a.IsEmpty) a.Dispose();
                else Push(a);
            }
            Flush();
        }

        string EditText => editing is StepAnnotation step ? step.Label : ((TextAnnotation)editing).Text;

        int EditRoom => (editing is StepAnnotation ? MaxLabelLength : MaxTextLength) - EditText.Length;

        void SetEditText(string value)
        {
            MarkDirty(editing.GetBounds());
            if (editing is StepAnnotation step) step.Label = value;
            else ((TextAnnotation)editing).Text = value;
            caretOn = true;
            caretTicks = 0;
            SetEditing(editing, true, true);
            MarkDirty(editing.GetBounds());
            Flush();
        }

        static void SetEditing(Annotation a, bool on, bool caret)
        {
            if (a is TextAnnotation text)
            {
                text.Editing = on;
                text.CaretVisible = caret;
            }
            else if (a is StepAnnotation step)
            {
                step.Editing = on;
                step.CaretVisible = caret;
            }
        }

        public void KeyPress(char c)
        {
            if (editing == null || char.IsControl(c)) return;
            string s = EditText;
            // Length cap that never splits a surrogate pair: a high surrogate needs room for both halves, a low one must follow it.
            bool fits = char.IsLowSurrogate(c) ? s.Length > 0 && char.IsHighSurrogate(s[s.Length - 1])
                                               : EditRoom >= (char.IsHighSurrogate(c) ? 2 : 1);
            if (fits) SetEditText(s + c);
        }

        void Backspace()
        {
            string s = EditText;
            if (s.Length == 0) return;
            int cut = s.Length >= 2 && char.IsLowSurrogate(s[s.Length - 1]) ? 2 : 1;
            SetEditText(s.Substring(0, s.Length - cut));
        }

        void Paste()
        {
            string text;
            try
            {
                text = Clipboard.GetText();
            }
            catch (ExternalException)
            {
                return; // clipboard busy: nothing to paste
            }
            text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');
            if (editing is StepAnnotation) text = text.Replace('\n', ' ');
            text = new string(text.Where(ch => ch == '\n' || !char.IsControl(ch)).ToArray());
            int room = Math.Max(EditRoom, 0);
            if (text.Length > room) // cap the length without splitting a surrogate pair
                text = text.Substring(0, room > 0 && char.IsHighSurrogate(text[room - 1]) ? room - 1 : room);
            if (text.Length > 0) SetEditText(EditText + text);
        }

        // ---------------------------------------------------------------- keyboard

        public bool KeyDown(Keys keyData)
        {
            if (editing != null)
            {
                switch (keyData)
                {
                    case Keys.Escape:
                    case Keys.Control | Keys.Enter:
                        CommitText();
                        return true;
                    case Keys.Enter:
                        if (editing is StepAnnotation) CommitText();
                        else if (EditRoom > 0) SetEditText(EditText + "\n");
                        return true;
                    case Keys.Back:
                        Backspace();
                        return true;
                    case Keys.Control | Keys.V:
                        Paste();
                        return true;
                }
                Keys chord = keyData & (Keys.Control | Keys.Alt), code = keyData & Keys.KeyCode;
                // A modifier pressed alone is the start of a chord (Ctrl+V, AltGr, Alt+numpad code), not a shortcut.
                if (code == Keys.ControlKey || code == Keys.Menu || code == Keys.ShiftKey) return false;
                if (chord == Keys.Alt && code >= Keys.NumPad0 && code <= Keys.NumPad9) return false;
                // Plain keys type through KeyPress (Ctrl+Alt is AltGr on many layouts); other shortcuts finish the text first.
                if (chord == Keys.None || chord == (Keys.Control | Keys.Alt)) return false;
                CommitText();
            }

            switch (keyData)
            {
                case Keys.Escape:
                    if (paletteOpen)
                    {
                        paletteOpen = false;
                        Flush();
                    }
                    else if (tool != ToolKind.None) Tool = ToolKind.None;
                    else RaiseCommand(EditorCommand.Close);
                    return true;
                case Keys.Control | Keys.X: RaiseCommand(EditorCommand.Close); return true;
                case Keys.Enter:
                case Keys.Control | Keys.Shift | Keys.C: RaiseAction(EditorCommand.CopyForClaude); return true;
                case Keys.Control | Keys.C: RaiseAction(EditorCommand.Copy); return true;
                case Keys.Control | Keys.S: RaiseAction(EditorCommand.QuickSave); return true;
                case Keys.Control | Keys.Shift | Keys.S: RaiseAction(EditorCommand.SaveAs); return true;
                case Keys.Control | Keys.P: RaiseAction(EditorCommand.Print); return true;
                case Keys.Control | Keys.Z:
                case Keys.Delete: Undo(); return true;
                case Keys.Control | Keys.Y:
                case Keys.Control | Keys.Shift | Keys.Z: Redo(); return true;
                case Keys.Control | Keys.A: ToggleFullScreen(); return true;
                case Keys.Control | Keys.Add:
                case Keys.Control | Keys.Oemplus: ChangeSize(1); return true;
                case Keys.Control | Keys.Subtract:
                case Keys.Control | Keys.OemMinus: ChangeSize(-1); return true;
            }

            Keys key = keyData & Keys.KeyCode, mods = keyData & Keys.Modifiers;
            if ((key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down) && (mods & Keys.Alt) == 0)
            {
                Nudge(key, mods);
                return true;
            }
            if (mods == Keys.None && !selection.IsEmpty)
                foreach (var t in ToolDefs)
                    if (t.Key == key)
                    {
                        Tool = tool == t.Kind ? ToolKind.None : t.Kind;
                        return true;
                    }
            return false;
        }

        void Nudge(Keys key, Keys mods)
        {
            if (selection.IsEmpty) return;
            int step = (mods & Keys.Control) != 0 ? 10 : 1;
            int dx = key == Keys.Left ? -step : key == Keys.Right ? step : 0;
            int dy = key == Keys.Up ? -step : key == Keys.Down ? step : 0;
            Rectangle r = selection;
            if ((mods & Keys.Shift) != 0)
            {
                r.Width = Clamp(r.Width + dx, 1, desktop.Right - r.X);
                r.Height = Clamp(r.Height + dy, 1, desktop.Bottom - r.Y);
            }
            else
            {
                r.X = Clamp(r.X + dx, 0, desktop.Width - r.Width);
                r.Y = Clamp(r.Y + dy, 0, desktop.Height - r.Height);
            }
            selection = r;
            Flush();
        }

        public void Tick()
        {
            if (editing != null && ++caretTicks % 2 == 0)
            {
                caretOn = !caretOn;
                SetEditing(editing, true, caretOn);
                MarkDirty(editing.GetBounds());
            }
            if (hudText != null && unchecked(Environment.TickCount - hudSince) >= HudMilliseconds)
                hudText = null;
            Flush();
        }

        // ---------------------------------------------------------------- selection & commands

        public void ToggleFullScreen()
        {
            Rectangle monitor = capture.MonitorAt(lastMouse).Bounds;
            if (selection != monitor)
            {
                selectionBeforeFullScreen = selection;
                selection = monitor;
            }
            else if (capture.Monitors.Count > 1) selection = desktop;
            else if (!selectionBeforeFullScreen.IsEmpty) selection = selectionBeforeFullScreen;
            Flush();
        }

        public void ClearSelection()
        {
            CommitText();
            paletteOpen = false;
            selection = Rectangle.Empty;
            Flush();
        }

        public Bitmap RenderResult()
        {
            CommitText();
            return Exporter.Compose(capture.Image, selection, annotations);
        }

        public string BuildClaudeNotes()
        {
            string notes = string.Join("; ", annotations.OfType<StepAnnotation>()
                .Where(s => !string.IsNullOrWhiteSpace(s.Label))
                .OrderBy(s => s.Number)
                .Select(s => "(" + s.Number + ") " + s.Label.Trim()));
            return notes.Length == 0 ? "" : "Screenshot notes: " + notes;
        }

        void RaiseCommand(EditorCommand c)
        {
            CommitText();
            Command?.Invoke(c);
        }

        void RaiseAction(EditorCommand c)
        {
            if (!selection.IsEmpty) RaiseCommand(c);
        }

        // ---------------------------------------------------------------- layout & invalidation

        // Recomputes the chrome, then repaints only the parts whose bounds or look changed, plus dirty annotation pixels.
        void Flush()
        {
            Rectangle oldFrame = frameArea, oldSelection = frameSelection, oldLabel = labelRect, oldHint = hintRect, oldBars = barsArea, oldHudRect = hudRect;
            var oldKey = barsKey;
            string oldHudText = hudShown;
            Layout();
            // frameArea alone can stay the same while the frame or label inside it moves.
            if (frameArea != oldFrame || selection != oldSelection || labelRect != oldLabel) Repaint(Chrome.Union(oldFrame, frameArea));
            if (hintRect != oldHint) Repaint(Chrome.Union(oldHint, hintRect));
            if (barsArea != oldBars || barsKey != oldKey) Repaint(Chrome.Union(oldBars, barsArea));
            if (hudRect != oldHudRect || hudShown != oldHudText) Repaint(Chrome.Union(oldHudRect, hudRect));
            if (!dirty.IsEmpty)
            {
                Repaint(dirty);
                dirty = Rectangle.Empty;
            }
        }

        void Repaint(Rectangle r)
        {
            r.Intersect(desktop);
            if (!r.IsEmpty) Invalidate?.Invoke(r);
        }

        void MarkDirty(Rectangle r) => dirty = Chrome.Union(dirty, r);

        void Layout()
        {
            MonitorSnapshot monitor = capture.MonitorAt(selection.IsEmpty ? lastMouse : Center(selection));
            Rectangle screen = monitor.Bounds;
            chrome.SetScale(monitor.Scale);
            frameSelection = selection;

            if (selection.IsEmpty)
            {
                hoverRect = HoverTarget(lastMouse);
                Size hint = chrome.PillSize(HintText, chrome.StrongFont, 16, 8);
                hintRect = new Rectangle(screen.Left + (screen.Width - hint.Width) / 2, screen.Top + screen.Height / 6 - hint.Height / 2, hint.Width, hint.Height);
                labelRect = Rectangle.Empty;
                frameArea = hoverRect;
            }
            else hoverRect = hintRect = Rectangle.Empty;

            hover = null;
            paletteHover = -1;
            paletteRect = tooltipRect = barsArea = Rectangle.Empty;
            tooltipText = null;
            if (ToolbarsVisible)
            {
                SyncBars();
                toolBar.Layout(selection, screen, chrome);
                actionBar.Layout(selection, screen, chrome);
                if (actionBar.Bounds.IntersectsWith(toolBar.Bounds))
                {
                    // Beside the tool bar (left, else right), else under (else above) it; always on the selection's monitor.
                    Rectangle a = actionBar.Bounds, t = toolBar.Bounds;
                    int gap = chrome.Px(5);
                    if (t.Left - gap - a.Width >= screen.Left) a.X = t.Left - gap - a.Width;
                    else if (t.Right + gap + a.Width <= screen.Right) a.X = t.Right + gap;
                    else a.Y = t.Bottom + gap + a.Height <= screen.Bottom ? t.Bottom + gap : t.Top - gap - a.Height;
                    a = Chrome.Fit(a, screen);
                    actionBar.Offset(a.X - actionBar.Bounds.X, a.Y - actionBar.Bounds.Y);
                }
                hover = toolBar.HitTest(lastMouse) ?? actionBar.HitTest(lastMouse);
                if (paletteOpen)
                {
                    paletteRect = toolBar.PopupBounds(colorItem, PaletteSize(), selection, screen, chrome);
                    Rectangle a = actionBar.Bounds;
                    if (paletteRect.IntersectsWith(a))
                    {
                        // Keep it clear of the action bar (both bars inside the selection's bottom-right corner).
                        paletteRect.Y = Center(paletteRect).Y < Center(a).Y ? a.Top - chrome.Px(6) - paletteRect.Height : a.Bottom + chrome.Px(6);
                        paletteRect = Chrome.Fit(paletteRect, screen);
                    }
                    paletteHover = PaletteCellAt(lastMouse);
                }
                else if (hover != null)
                {
                    Toolbar bar = toolBar.Items.Contains(hover) ? toolBar : actionBar;
                    tooltipText = hover.Tooltip;
                    tooltipRect = bar.PopupBounds(hover, chrome.PillSize(tooltipText, chrome.TextFont, 8, 5), selection, screen, chrome);
                }
                barsArea = Chrome.Union(Chrome.Union(toolBar.Bounds, actionBar.Bounds), Chrome.Union(paletteRect, tooltipRect));
            }

            if (!selection.IsEmpty)
            {
                labelRect = PlaceLabel(); // after the bars, so it can keep clear of them
                int reach = HandleHalf + 2;
                frameArea = Chrome.Union(Rectangle.Inflate(selection, reach, reach), labelRect);
            }

            hudShown = hudText;
            hudRect = Rectangle.Empty;
            if (hudText != null)
            {
                Size hud = chrome.PillSize(hudText, chrome.BoldFont, 8, 4);
                hudRect = Chrome.Fit(new Rectangle(hudAnchor.X + chrome.Px(16), hudAnchor.Y + chrome.Px(20), hud.Width, hud.Height),
                                     capture.MonitorAt(hudAnchor).Bounds);
            }

            barsKey = (hover, pressed, tool, Color.ToArgb(), annotations.Count > 0, redo.Count > 0, paletteHover,
                       paletteRect, tooltipRect, toolBar.Bounds, actionBar.Bounds);
            cursor = CurrentCursor();
        }

        // Just above the selection's top-left corner (inside it when there's no room), on the monitor holding that corner.
        // When a bar covers it: inside the corner, else the nearest spot beside the bars that is clear of both.
        Rectangle PlaceLabel()
        {
            Size size = chrome.PillSize(SizeText, chrome.BoldFont, 6, 3);
            int gap = chrome.Px(6);
            Rectangle screen = capture.MonitorAt(selection.Location).Bounds;
            Rectangle inside = Chrome.Fit(new Rectangle(new Point(selection.Left + gap, selection.Top + gap), size), screen);
            Rectangle label = new Rectangle(new Point(selection.Left - 1, selection.Top - gap - size.Height), size);
            label = label.Top < screen.Top ? inside : Chrome.Fit(label, screen);
            if (!ToolbarsVisible || ClearOfBars(label)) return label;
            if (ClearOfBars(inside)) return inside;

            Rectangle t = toolBar.Bounds, a = actionBar.Bounds, best = label;
            long bestDistance = long.MaxValue;
            foreach (int x in new[] { label.X, t.Left - gap - size.Width, t.Right + gap, a.Left - gap - size.Width, a.Right + gap })
                foreach (int y in new[] { label.Y, t.Top - gap - size.Height, t.Bottom + gap, a.Top - gap - size.Height, a.Bottom + gap })
                {
                    Rectangle r = Chrome.Fit(new Rectangle(new Point(x, y), size), screen);
                    long dx = r.X - label.X, dy = r.Y - label.Y;
                    if (dx * dx + dy * dy < bestDistance && ClearOfBars(r))
                    {
                        best = r;
                        bestDistance = dx * dx + dy * dy;
                    }
                }
            return best;
        }

        bool ClearOfBars(Rectangle r) => !r.IntersectsWith(toolBar.Bounds) && !r.IntersectsWith(actionBar.Bounds);

        void SyncBars()
        {
            foreach (Toolbar.Item item in toolBar.Items)
                item.Active = item.Tag is ToolKind kind && kind == tool;
            colorItem.Swatch = Color;
            undoItem.Enabled = annotations.Count > 0;
            redoItem.Enabled = redo.Count > 0;
        }

        Cursor CurrentCursor()
        {
            switch (drag)
            {
                case DragMode.Select:
                case DragMode.Draw: return Cursors.Cross;
                case DragMode.Move: return Cursors.SizeAll;
                case DragMode.Resize: return HandleCursor(resizeDir);
                case DragMode.Button: return Cursors.Default;
            }
            Point p = lastMouse;
            if (ToolbarsVisible && (toolBar.Bounds.Contains(p) || actionBar.Bounds.Contains(p) || paletteRect.Contains(p)))
                return Cursors.Default;
            if (selection.IsEmpty) return Cursors.Cross;
            int handle = HandleAt(p);
            if (handle >= 0) return HandleCursor(Handles[handle]);
            if (selection.Contains(p))
                return tool == ToolKind.None ? Cursors.SizeAll : tool == ToolKind.Text ? Cursors.IBeam : Cursors.Cross;
            return tool == ToolKind.None ? Cursors.Cross : Cursors.Default;
        }

        static Cursor HandleCursor(Point d) =>
            d.X == 0 ? Cursors.SizeNS : d.Y == 0 ? Cursors.SizeWE : d.X == d.Y ? Cursors.SizeNWSE : Cursors.SizeNESW;

        int HandleHalf => chrome.Px(3);

        Point HandleCenter(Point d) => new Point(
            d.X < 0 ? selection.Left - 1 : d.X > 0 ? selection.Right : selection.Left + selection.Width / 2,
            d.Y < 0 ? selection.Top - 1 : d.Y > 0 ? selection.Bottom : selection.Top + selection.Height / 2);

        int HandleAt(Point p)
        {
            int reach = HandleHalf + chrome.Px(3);
            for (int i = 0; i < Handles.Length; i++)
            {
                Point c = HandleCenter(Handles[i]);
                if (Math.Abs(p.X - c.X) <= reach && Math.Abs(p.Y - c.Y) <= reach) return i;
            }
            return -1;
        }

        Rectangle HoverTarget(Point p)
        {
            foreach (Rectangle w in capture.Windows)
                if (w.Contains(p))
                    return Rectangle.Intersect(w, desktop);
            return capture.MonitorAt(p).Bounds;
        }

        string SizeText => selection.Width + "x" + selection.Height;

        Size PaletteSize()
        {
            int pad = chrome.Px(8), cell = chrome.Px(22), gap = chrome.Px(6);
            int rows = Swatches.Length / PaletteColumns;
            return new Size(2 * pad + PaletteColumns * cell + (PaletteColumns - 1) * gap,
                            2 * pad + rows * (cell + gap) + chrome.Px(24));
        }

        Rectangle PaletteCell(int i)
        {
            int pad = chrome.Px(8), cell = chrome.Px(22), gap = chrome.Px(6);
            if (i == CustomCell)
            {
                int rows = Swatches.Length / PaletteColumns;
                return new Rectangle(paletteRect.X + pad, paletteRect.Y + pad + rows * (cell + gap), paletteRect.Width - 2 * pad, chrome.Px(24));
            }
            return new Rectangle(paletteRect.X + pad + i % PaletteColumns * (cell + gap),
                                 paletteRect.Y + pad + i / PaletteColumns * (cell + gap), cell, cell);
        }

        int PaletteCellAt(Point p)
        {
            for (int i = 0; i <= CustomCell; i++)
                if (PaletteCell(i).Contains(p)) return i;
            return -1;
        }

        // ---------------------------------------------------------------- painting

        public void Render(Graphics g, Rectangle clip)
        {
            clip.Intersect(desktop);
            if (clip.IsEmpty) return;

            // Half-pixel offset: 1:1 blits, annotations rendered exactly as Exporter.Compose does, and anti-aliased
            // chrome that fills exactly its integer rectangles (no bleed outside the invalidated areas).
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(dimmed, clip, clip, GraphicsUnit.Pixel);
            Rectangle lit = Rectangle.Intersect(selection.IsEmpty ? hoverRect : selection, clip);
            if (!lit.IsEmpty) g.DrawImage(capture.Image, lit, lit, GraphicsUnit.Pixel);

            g.CompositingMode = CompositingMode.SourceOver;
            DrawAnnotations(g, Rectangle.Intersect(selection, clip));

            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            if (selection.IsEmpty) DrawHover(g);
            else DrawFrame(g);
            if (!barsArea.IsEmpty && barsArea.IntersectsWith(clip)) DrawBars(g);
            if (!hudRect.IsEmpty)
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                chrome.DrawPill(g, hudRect, hudShown, chrome.BoldFont, chrome.PillBrush, chrome.LabelBrush, hudRect.Height / 2f);
            }
        }

        void DrawAnnotations(Graphics g, Rectangle area)
        {
            if (area.IsEmpty) return;
            GraphicsState state = g.Save();
            g.SetClip(area);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (Annotation a in annotations)
                DrawAnnotation(g, a, area);
            if (drawing != null) DrawAnnotation(g, drawing, area);
            if (editing is TextAnnotation) DrawAnnotation(g, editing, area);
            g.Restore(state);
        }

        void DrawAnnotation(Graphics g, Annotation a, Rectangle area)
        {
            if (a.GetBounds().IntersectsWith(area)) a.Draw(g, capture.Image);
        }

        void DrawHover(Graphics g)
        {
            Rectangle r = hoverRect;
            const int t = 2;
            g.FillRectangle(chrome.AccentBrush, r.Left, r.Top, r.Width, t);
            g.FillRectangle(chrome.AccentBrush, r.Left, r.Bottom - t, r.Width, t);
            g.FillRectangle(chrome.AccentBrush, r.Left, r.Top, t, r.Height);
            g.FillRectangle(chrome.AccentBrush, r.Right - t, r.Top, t, r.Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            chrome.DrawPill(g, hintRect, HintText, chrome.StrongFont, chrome.BarBrush, chrome.InkBrush, hintRect.Height / 2f);
        }

        void DrawFrame(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.None; // 1 px lines on integer coordinates
            var frame = new Rectangle(selection.X - 1, selection.Y - 1, selection.Width + 1, selection.Height + 1);
            g.DrawRectangle(Pens.White, frame);
            g.DrawRectangle(chrome.DashPen, frame);
            int h = HandleHalf;
            foreach (Point d in Handles)
            {
                Point c = HandleCenter(d);
                g.FillRectangle(Brushes.Black, c.X - h, c.Y - h, 2 * h + 1, 2 * h + 1);
                g.DrawRectangle(Pens.White, c.X - h, c.Y - h, 2 * h, 2 * h);
            }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            chrome.DrawPill(g, labelRect, SizeText, chrome.BoldFont, chrome.PillBrush, chrome.LabelBrush, chrome.Px(4));
        }

        void DrawBars(Graphics g)
        {
            toolBar.Paint(g, chrome, hover, pressed);
            actionBar.Paint(g, chrome, hover, pressed);
            if (!paletteRect.IsEmpty) DrawPalette(g);
            if (tooltipText != null)
                chrome.DrawPill(g, tooltipRect, tooltipText, chrome.TextFont, chrome.BarBrush, chrome.InkBrush, chrome.Px(4));
        }

        void DrawPalette(Graphics g)
        {
            float s = chrome.Scale;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Chrome.FillRounded(g, chrome.BarBrush, paletteRect, 6 * s);
            int current = Color.ToArgb();
            using (var outline = new Pen(Color.FromArgb(70, 255, 255, 255)))
            using (var ring = new Pen(Color.White, Math.Max(1.5f, 2 * s)))
            using (var hot = new Pen(Color.FromArgb(150, 255, 255, 255), Math.Max(1f, s)))
            {
                for (int i = 0; i < Swatches.Length; i++)
                {
                    Rectangle r = PaletteCell(i);
                    using (var fill = new SolidBrush(Swatches[i]))
                        Chrome.FillRounded(g, fill, r, 4 * s);
                    using (GraphicsPath edge = Chrome.RoundedRect(r, 4 * s))
                        g.DrawPath(outline, edge);
                    Pen marker = Swatches[i].ToArgb() == current ? ring : i == paletteHover ? hot : null;
                    if (marker != null)
                        using (GraphicsPath path = Chrome.RoundedRect(RectangleF.Inflate(r, 2.5f * s, 2.5f * s), 6 * s))
                            g.DrawPath(marker, path);
                }
                Rectangle custom = PaletteCell(CustomCell);
                if (paletteHover == CustomCell) Chrome.FillRounded(g, chrome.HoverBrush, custom, 4 * s);
                else
                    using (GraphicsPath edge = Chrome.RoundedRect(custom, 4 * s))
                        g.DrawPath(outline, edge);
                g.DrawString("Custom…", chrome.TextFont, chrome.InkBrush, custom, chrome.Centered);
            }
        }

        // ---------------------------------------------------------------- helpers

        static Bitmap CreateDimmed(Bitmap source, Rectangle bounds)
        {
            var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            using (var shade = new SolidBrush(Color.FromArgb(128, 0, 0, 0)))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(source, bounds, bounds, GraphicsUnit.Pixel);
                g.CompositingMode = CompositingMode.SourceOver;
                g.FillRectangle(shade, bounds);
            }
            return bmp;
        }

        // r clipped to the desktop; a zero-width or zero-height leftover (Rectangle.IsEmpty misses those) is no selection.
        Rectangle Clip(Rectangle r)
        {
            r.Intersect(desktop);
            return r.Width > 0 && r.Height > 0 ? r : Rectangle.Empty;
        }

        // Rectangle spanning two edges in any order, clamped to the desktop and at least 1×1.
        Rectangle FromEdges(int l, int t, int r, int b)
        {
            if (l > r) (l, r) = (r, l);
            if (t > b) (t, b) = (b, t);
            l = Clamp(l, 0, desktop.Width - 1);
            t = Clamp(t, 0, desktop.Height - 1);
            r = Clamp(r, l + 1, desktop.Width);
            b = Clamp(b, t + 1, desktop.Height);
            return Rectangle.FromLTRB(l, t, r, b);
        }

        static Rectangle ShapeRect(Point a, Point b, bool square)
        {
            int dx = b.X - a.X, dy = b.Y - a.Y;
            if (square)
            {
                int side = Math.Max(Math.Abs(dx), Math.Abs(dy));
                dx = dx < 0 ? -side : side;
                dy = dy < 0 ? -side : side;
            }
            return Rectangle.FromLTRB(Math.Min(a.X, a.X + dx), Math.Min(a.Y, a.Y + dy), Math.Max(a.X, a.X + dx), Math.Max(a.Y, a.Y + dy));
        }

        static Point Snap45(Point a, Point b)
        {
            int dx = b.X - a.X, dy = b.Y - a.Y;
            double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
            int ux = (int)Math.Round(Math.Cos(angle)), uy = (int)Math.Round(Math.Sin(angle));
            int length = ux != 0 && uy != 0 ? (Math.Abs(dx) + Math.Abs(dy)) / 2 : ux != 0 ? Math.Abs(dx) : Math.Abs(dy);
            return new Point(a.X + ux * length, a.Y + uy * length);
        }

        void AddSegment(List<Point> points, Point p, float outset)
        {
            Point prev = points[points.Count - 1];
            if (prev == p) return;
            points.Add(p);
            MarkDirty(AnnotationGfx.Bounds(AnnotationGfx.Enclose(new[] { prev, p }), outset));
        }

        static Point Center(Rectangle r) => new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);

        static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;

        static Color Rgb(int rgb) => Color.FromArgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);

        public void Dispose()
        {
            if (editing is TextAnnotation) editing.Dispose();
            editing = null;
            drawing?.Dispose();
            drawing = null;
            foreach (Annotation a in annotations.Concat(redo))
                a.Dispose();
            annotations.Clear();
            redo.Clear();
            dimmed.Dispose();
            chrome.Dispose();
        }
    }
}
