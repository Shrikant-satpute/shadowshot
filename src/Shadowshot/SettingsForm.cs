using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;

namespace Shadowshot
{
    // Modeless Options dialog. OK/Apply write into the shared Settings instance and save it;
    // the tray re-registers the hotkeys when the dialog closes.
    public sealed class SettingsForm : Form
    {
        static readonly string[] HotkeyNames = { "Capture area", "Full screen → save", "Screen → Copy for Claude" };
        static readonly string[] Formats = { "png", "jpg", "bmp" };

        readonly Settings settings;
        readonly Font uiFont = SystemFonts.MessageBoxFont;
        readonly Icon formIcon;
        readonly Hotkey[] hotkeys = new Hotkey[3];
        readonly TextBox[] hotkeyBoxes = new TextBox[3];
        readonly Label[] hotkeyStatus = new Label[3];
        readonly CheckBox startWithWindows = Check("Start with Windows");
        readonly CheckBox showNotifications = Check("Show notifications");
        readonly CheckBox captureCursor = Check("Capture mouse cursor");
        readonly CheckBox keepSelection = Check("Remember last selection");
        readonly CheckBox claudeNotes = Check("Also paste numbered-step labels as text (Claude reads them exactly)");
        readonly TextBox saveFolder = FolderBox();
        readonly TextBox claudeFolder = FolderBox();
        readonly ComboBox format = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 80, Anchor = AnchorStyles.Left };
        readonly NumericUpDown jpegQuality = new NumericUpDown { Minimum = 50, Maximum = 100, Width = 60, Anchor = AnchorStyles.Left };

        public SettingsForm(Settings settings)
        {
            this.settings = settings;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);   // all sizes below are in 96-dpi units
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = uiFont;
            Text = "Shadowshot Options — v" + AppInfo.Version;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(10);
            using (Stream stream = typeof(SettingsForm).Assembly.GetManifestResourceStream("Shadowshot.ico"))
                Icon = formIcon = new Icon(stream);

            Button ok = MakeButton("OK", (s, e) => { if (Apply()) Close(); });
            Button cancel = MakeButton("Cancel", (s, e) => Close());
            Button apply = MakeButton("Apply", (s, e) => { if (Apply()) TestHotkeys(); });
            AcceptButton = ok;
            CancelButton = cancel;
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Dock = DockStyle.Fill,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 6, 0, 0),
            };
            buttons.Controls.AddRange(new Control[] { apply, cancel, ok });

            var root = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            root.Controls.Add(Group("Hotkeys", BuildHotkeys()));
            root.Controls.Add(Group("General", Stack(startWithWindows, showNotifications, captureCursor, keepSelection)));
            root.Controls.Add(Group("Saving", BuildSaving()));
            root.Controls.Add(Group("Copy for Claude", BuildClaude()));
            root.Controls.Add(buttons);
            Controls.Add(root);

            LoadValues();
            ResumeLayout(false);
            PerformLayout();
        }

        Control BuildHotkeys()
        {
            TableLayoutPanel grid = Grid(4);
            // Wrap long registration errors instead of widening the dialog.
            int statusWidth = LogicalToDeviceUnits(240);
            for (int i = 0; i < hotkeys.Length; i++)
            {
                int row = i;
                var box = new TextBox { ReadOnly = true, BackColor = SystemColors.Window, ShortcutsEnabled = false, Width = 170, Anchor = AnchorStyles.Left };
                box.KeyDown += (s, e) => RecordHotkey(row, e);
                box.KeyUp += (s, e) => { if (e.KeyCode == Keys.PrintScreen) RecordHotkey(row, e); };   // PrtScr never raises KeyDown
                Button clear = MakeButton("×", (s, e) => SetHotkey(row, new Hotkey()));
                clear.AutoSize = false;
                clear.Size = new Size(26, 23);
                clear.AccessibleName = "Clear " + HotkeyNames[i];
                var status = new Label { AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(statusWidth, 0) };
                hotkeyBoxes[i] = box;
                hotkeyStatus[i] = status;

                grid.Controls.Add(Caption(HotkeyNames[i]), 0, i);
                grid.Controls.Add(box, 1, i);
                grid.Controls.Add(clear, 2, i);
                grid.Controls.Add(status, 3, i);
            }
            return grid;
        }

        Control BuildSaving()
        {
            TableLayoutPanel grid = Grid(3);
            grid.Controls.Add(Caption("Screenshots folder:"), 0, 0);
            grid.Controls.Add(saveFolder, 1, 0);
            grid.Controls.Add(MakeButton("Browse…", (s, e) => Browse(saveFolder)), 2, 0);
            grid.Controls.Add(Caption("Default format:"), 0, 1);
            grid.Controls.Add(format, 1, 1);
            grid.Controls.Add(Caption("JPEG quality:"), 0, 2);
            grid.Controls.Add(jpegQuality, 1, 2);
            format.Items.AddRange(new object[] { "PNG", "JPG", "BMP" });
            return grid;
        }

        Control BuildClaude()
        {
            TableLayoutPanel grid = Grid(4);
            grid.Controls.Add(Caption("Claude folder:"), 0, 0);
            grid.Controls.Add(claudeFolder, 1, 0);
            grid.Controls.Add(MakeButton("Browse…", (s, e) => Browse(claudeFolder)), 2, 0);
            grid.Controls.Add(MakeButton("Open", (s, e) => OpenFolder(claudeFolder)), 3, 0);
            grid.Controls.Add(claudeNotes, 0, 1);
            grid.SetColumnSpan(claudeNotes, 4);
            return grid;
        }

        void LoadValues()
        {
            string[] hotkeyTexts = { settings.RegionHotkey, settings.FullSaveHotkey, settings.ClaudeHotkey };
            for (int i = 0; i < hotkeys.Length; i++) SetHotkey(i, Hotkey.Parse(hotkeyTexts[i]));
            startWithWindows.Checked = Settings.StartWithWindows;
            showNotifications.Checked = settings.ShowNotifications;
            captureCursor.Checked = settings.CaptureCursor;
            keepSelection.Checked = settings.KeepSelection;
            saveFolder.Text = settings.SaveFolder;
            format.SelectedIndex = Math.Max(0, Array.IndexOf(Formats, settings.Format));
            jpegQuality.Value = Math.Min(100, Math.Max(50, settings.JpegQuality));
            claudeFolder.Text = settings.ClaudeFolder;
            claudeNotes.Checked = settings.ClaudeIncludeNotes;
        }

        bool Apply()
        {
            string save = saveFolder.Text.Trim(), claude = claudeFolder.Text.Trim();
            if (!Settings.IsFullPath(save) || !Settings.IsFullPath(claude))
            {
                MessageBox.Show(this, "Please choose full folder paths, for example C:\\Users\\you\\Pictures\\Shadowshot.",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            settings.RegionHotkey = hotkeys[0].ToString();
            settings.FullSaveHotkey = hotkeys[1].ToString();
            settings.ClaudeHotkey = hotkeys[2].ToString();
            settings.ShowNotifications = showNotifications.Checked;
            settings.CaptureCursor = captureCursor.Checked;
            settings.KeepSelection = keepSelection.Checked;
            // Ctrl+S and Shift+PrtScr prefer the last Save As folder; a new screenshots folder takes over from it.
            if (!string.Equals(save, settings.SaveFolder, StringComparison.OrdinalIgnoreCase)) settings.LastSaveDir = "";
            settings.SaveFolder = save;
            settings.Format = Formats[format.SelectedIndex];
            settings.JpegQuality = (int)jpegQuality.Value;
            settings.ClaudeFolder = claude;
            settings.ClaudeIncludeNotes = claudeNotes.Checked;
            settings.Save();

            try
            {
                if (startWithWindows.Checked != Settings.StartWithWindows) Settings.StartWithWindows = startWithWindows.Checked;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException || ex is SecurityException || ex is IOException)
            {
                MessageBox.Show(this, "Could not change the Windows setting: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return true;
        }

        // The tray's hotkeys are unregistered while this dialog is open, so a test registration shows
        // whether another app holds the combo.
        void TestHotkeys()
        {
            using (var manager = new HotkeyManager())
            {
                for (int i = 0; i < hotkeys.Length; i++)
                {
                    if (hotkeys[i].IsEmpty) ShowStatus(i, "Disabled", SystemColors.GrayText);
                    else if (manager.Register(i + 1, hotkeys[i], out string error)) ShowStatus(i, "OK", Color.ForestGreen);
                    else ShowStatus(i, error, Color.Firebrick);
                }
            }
        }

        void RecordHotkey(int row, KeyEventArgs e)
        {
            e.SuppressKeyPress = true;   // also sets Handled; keeps Alt+letter from triggering anything
            Keys key = e.KeyCode;
            if (Hotkey.IsModifierKey(key)) return;
            if ((key == Keys.Back || key == Keys.Delete) && e.Modifiers == Keys.None)
            {
                SetHotkey(row, new Hotkey());
                return;
            }

            var hotkey = new Hotkey { Key = key, Ctrl = e.Control, Alt = e.Alt, Shift = e.Shift, Win = Native.IsWinKeyDown() };
            // A bare letter, digit, Space or arrow would be taken away from every other app.
            bool standalone = key == Keys.PrintScreen || (key >= Keys.F1 && key <= Keys.F24)
                || key == Keys.Pause || key == Keys.Scroll || key == Keys.Insert;
            if (!hotkey.Ctrl && !hotkey.Alt && !hotkey.Win && !standalone)
            {
                ShowStatus(row, "Add Ctrl, Alt or Win", Color.Firebrick);
                return;
            }
            for (int i = 0; i < hotkeys.Length; i++)
            {
                if (i != row && hotkeys[i].ToString() == hotkey.ToString())
                {
                    ShowStatus(row, "Already used for " + HotkeyNames[i], Color.Firebrick);
                    return;
                }
            }
            SetHotkey(row, hotkey);
        }

        void SetHotkey(int row, Hotkey hotkey)
        {
            hotkeys[row] = hotkey;
            hotkeyBoxes[row].Text = hotkey.IsEmpty ? "None" : hotkey.ToDisplayString();
            ShowStatus(row, "", ForeColor);
        }

        void ShowStatus(int row, string text, Color color)
        {
            hotkeyStatus[row].Text = text;
            hotkeyStatus[row].ForeColor = color;
        }

        void Browse(TextBox box)
        {
            using (var dialog = new FolderBrowserDialog { SelectedPath = box.Text, ShowNewFolderButton = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) box.Text = dialog.SelectedPath;
        }

        void OpenFolder(TextBox box)
        {
            string folder = box.Text.Trim();
            if (!Settings.IsFullPath(folder)) return;
            Directory.CreateDirectory(folder);
            using (Process.Start("explorer.exe", "\"" + folder + "\"")) { }
        }

        static GroupBox Group(string title, Control content)
        {
            content.Dock = DockStyle.Fill;
            var group = new GroupBox
            {
                Text = title, Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 4, 8, 6),
            };
            group.Controls.Add(content);
            return group;
        }

        static TableLayoutPanel Grid(int columns) =>
            new TableLayoutPanel { ColumnCount = columns, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

        static FlowLayoutPanel Stack(params Control[] controls)
        {
            var stack = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
            stack.Controls.AddRange(controls);
            return stack;
        }

        static Label Caption(string text) =>
            new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 3, 8, 3) };

        static CheckBox Check(string text) => new CheckBox { Text = text, AutoSize = true };

        static TextBox FolderBox() => new TextBox { Width = 300, Anchor = AnchorStyles.Left };

        static Button MakeButton(string text, EventHandler click)
        {
            var button = new Button { Text = text, AutoSize = true, UseVisualStyleBackColor = true };
            button.Click += click;
            return button;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                formIcon.Dispose();
                uiFont.Dispose();
            }
        }

        static class Native
        {
            const int VK_LWIN = 0x5B, VK_RWIN = 0x5C;

            [DllImport("user32.dll")]
            static extern short GetKeyState(int virtualKey);

            public static bool IsWinKeyDown() => GetKeyState(VK_LWIN) < 0 || GetKeyState(VK_RWIN) < 0;
        }
    }
}
