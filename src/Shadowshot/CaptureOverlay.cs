using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Shadowshot
{
    // Borderless top-most window covering the virtual desktop 1:1 (client coordinates == capture space).
    // It only routes input/paint to the Editor and carries out the Editor's commands.
    public sealed class CaptureOverlay : Form
    {
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_DPICHANGED = 0x02E0;

        readonly CaptureResult capture;
        readonly Settings settings;
        readonly IOverlayHost host;
        readonly Editor editor;
        readonly Timer timer = new Timer { Interval = 250 };
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly List<ToolStripItem> selectionItems = new List<ToolStripItem>();
        bool busy;

        public CaptureOverlay(CaptureResult capture, Settings settings, IOverlayHost host)
        {
            this.capture = capture;
            this.settings = settings;
            this.host = host;

            Text = AppInfo.Name;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
            Bounds = capture.VirtualBounds;

            editor = new Editor(capture, settings);
            editor.Invalidate += r => Invalidate(r);
            // Run commands after the editor's input handler has returned: they may open dialogs or close the form.
            editor.Command += c => BeginInvoke((Action)(() => Execute(c)));
            timer.Tick += (s, e) => editor.Tick();
            BuildMenu();
            Cursor = editor.Cursor;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Rectangle vb = capture.VirtualBounds;
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, vb.X, vb.Y, vb.Width, vb.Height, Native.SWP_NOACTIVATE);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Activate();
            Native.SetForegroundWindow(Handle);
            timer.Start();
        }

        protected override void WndProc(ref Message m)
        {
            // The form must keep covering the virtual desktop pixel for pixel, so it never rescales itself.
            if (m.Msg == WM_DPICHANGED) return;
            base.WndProc(ref m);
            // A new screen layout no longer matches the frozen image.
            if (m.Msg == WM_DISPLAYCHANGE) Close();
        }

        protected override void OnPaint(PaintEventArgs e) => editor.Render(e.Graphics, e.ClipRectangle);

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            editor.MouseDown(e.Location, e.Button, ModifierKeys, e.Clicks);
            Cursor = editor.Cursor;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            editor.MouseMove(e.Location, e.Button, ModifierKeys);
            Cursor = editor.Cursor;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            editor.MouseUp(e.Location, e.Button, ModifierKeys);
            Cursor = editor.Cursor;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            editor.MouseWheel(e.Location, e.Delta, ModifierKeys);
            Cursor = editor.Cursor;
        }

        // Arrows/Enter/Tab/Esc never reach OnKeyDown on a form (dialog keys), so all keys are routed from here.
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (editor.KeyDown(keyData))
            {
                Cursor = editor.Cursor;
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            editor.KeyPress(e.KeyChar);
            e.Handled = true;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            timer.Stop();
            if (!editor.Selection.IsEmpty) settings.LastSelection = editor.Selection;
            settings.Save();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose();
                menu.Dispose();
                editor.Dispose();
            }
            base.Dispose(disposing);
        }

        void Execute(EditorCommand command)
        {
            // Commands posted before a dialog opened (e.g. key repeat) would otherwise run inside its modal loop.
            if (busy || IsDisposed) return;
            busy = true;
            try
            {
                switch (command)
                {
                    case EditorCommand.Close: Close(); break;
                    case EditorCommand.PickCustomColor: PickColor(); break;
                    case EditorCommand.ContextMenu: ShowMenu(); break;
                    default: RunAction(command); break;
                }
            }
            finally
            {
                busy = false;
            }
        }

        void RunAction(EditorCommand command)
        {
            try
            {
                using (Bitmap image = editor.RenderResult())
                    if (!Export(command, image)) return; // dialog cancelled: keep editing
            }
            catch (Exception ex)
            {
                Program.LogError("Action " + command, ex);
                MessageBox.Show(this, ex.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            Close();
        }

        bool Export(EditorCommand command, Bitmap image)
        {
            switch (command)
            {
                case EditorCommand.CopyForClaude:
                    string notes = settings.ClaudeIncludeNotes ? editor.BuildClaudeNotes() : null;
                    string claudePath = Exporter.CopyForClaude(image, settings, notes);
                    host.Notify("Copied for Claude", "Press Ctrl+V in Claude Code to attach it.", claudePath);
                    return true;
                case EditorCommand.Copy:
                    Exporter.CopyImage(image);
                    host.Notify("Copied", "Your screenshot is copied to the clipboard.", null);
                    return true;
                case EditorCommand.QuickSave:
                    string folder = string.IsNullOrEmpty(settings.LastSaveDir) ? settings.SaveFolder : settings.LastSaveDir;
                    NotifySaved(Exporter.SaveAuto(image, folder, settings.Format, settings.JpegQuality));
                    return true;
                case EditorCommand.SaveAs:
                    string path = Exporter.SaveAs(this, image, settings);
                    if (path == null) return false;
                    NotifySaved(path);
                    return true;
                case EditorCommand.Print:
                    return Exporter.Print(this, image);
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command, null);
            }
        }

        void NotifySaved(string path) =>
            host.Notify("Screenshot saved", "Saved to " + path + ". Click to open the folder.", path);

        void PickColor()
        {
            using (var dialog = new ColorDialog { FullOpen = true, Color = editor.Color })
                if (dialog.ShowDialog(this) == DialogResult.OK)
                    editor.SetColor(dialog.Color);
        }

        void BuildMenu()
        {
            AddMenuItem("Copy for Claude", "Enter", true, () => Execute(EditorCommand.CopyForClaude));
            AddMenuItem("Copy", "Ctrl+C", true, () => Execute(EditorCommand.Copy));
            AddMenuItem("Save", "Ctrl+S", true, () => Execute(EditorCommand.QuickSave));
            AddMenuItem("Save as…", null, true, () => Execute(EditorCommand.SaveAs));
            AddMenuItem("Print", "Ctrl+P", true, () => Execute(EditorCommand.Print));
            menu.Items.Add(new ToolStripSeparator());
            AddMenuItem("Select full screen", "Ctrl+A", false, editor.ToggleFullScreen);
            AddMenuItem("Clear selection", null, true, editor.ClearSelection);
            menu.Items.Add(new ToolStripSeparator());
            AddMenuItem("Cancel", "Esc", false, Close);
        }

        void AddMenuItem(string text, string shortcut, bool needsSelection, Action action)
        {
            // Posted so the menu has finished closing before the action may open a dialog or close the form.
            var item = new ToolStripMenuItem(text, null, (s, e) => BeginInvoke(action)) { ShortcutKeyDisplayString = shortcut };
            if (needsSelection) selectionItems.Add(item);
            menu.Items.Add(item);
        }

        void ShowMenu()
        {
            foreach (ToolStripItem item in selectionItems)
                item.Enabled = !editor.Selection.IsEmpty;
            menu.Show(this, editor.LastMouse);
        }

        static class Native
        {
            public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
            public const uint SWP_NOACTIVATE = 0x0010;

            [DllImport("user32.dll", SetLastError = true)]
            public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

            [DllImport("user32.dll")]
            public static extern bool SetForegroundWindow(IntPtr hWnd);
        }
    }
}
