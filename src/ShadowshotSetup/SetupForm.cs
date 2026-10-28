using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShadowshotSetup
{
    internal sealed class SetupForm : Form
    {
        private readonly Font bodyFont = new Font("Segoe UI", 9F);
        private readonly Font titleFont = new Font("Segoe UI Semibold", 14F);
        private readonly Font messageFont = new Font("Segoe UI Semibold", 11F);
        private readonly Icon windowIcon;
        private readonly Bitmap headerImage;

        private readonly Panel optionsPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };
        private readonly Panel donePanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Visible = false };
        private readonly TextBox folderBox = new TextBox { Location = new Point(24, 34), Width = 416 };
        private readonly CheckBox startMenuBox;
        private readonly CheckBox desktopBox;
        private readonly CheckBox startupBox;
        private readonly CheckBox launchBox;
        private readonly Label statusLabel = new Label { AutoSize = true, Location = new Point(24, 18) };
        private readonly Button installButton = new Button { Text = "Install", Location = new Point(352, 12), Size = new Size(88, 28) };
        private readonly Button cancelButton = new Button { Text = "Cancel", Location = new Point(448, 12), Size = new Size(88, 28) };
        private bool installing;

        public SetupForm(InstallOptions options)
        {
            // Layout is in 96-dpi pixels; AutoScaleMode.Dpi scales it once ResumeLayout runs.
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = bodyFont;
            ClientSize = new Size(560, 365);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;
            Text = "Shadowshot Setup";

            var header = new Panel { Dock = DockStyle.Top, Height = 76, BackColor = Color.White };
            var iconBox = new PictureBox { Location = new Point(24, 14), Size = new Size(48, 48), SizeMode = PictureBoxSizeMode.Zoom };
            header.Controls.Add(iconBox);
            header.Controls.Add(new Label
            {
                AutoSize = true, Location = new Point(82, 13), Font = titleFont,
                ForeColor = Color.FromArgb(0x1F, 0x1F, 0x1F), Text = "Shadowshot " + Installer.Version,
            });
            header.Controls.Add(new Label
            {
                AutoSize = true, Location = new Point(84, 43), UseMnemonic = false,
                ForeColor = Color.FromArgb(0x5F, 0x63, 0x68), Text = Installer.Tagline,
            });
            var accentLine = new Panel { Dock = DockStyle.Top, Height = 2, BackColor = Color.FromArgb(0x7C, 0x5C, 0xFF) };

            optionsPanel.Controls.Add(new Label { AutoSize = true, Location = new Point(24, 14), Text = "Install folder" });
            folderBox.Text = options.InstallDir;
            optionsPanel.Controls.Add(folderBox);
            var browseButton = new Button { Text = "Browse…", Location = new Point(448, 33), Size = new Size(88, 25) };
            browseButton.Click += OnBrowseClick;
            optionsPanel.Controls.Add(browseButton);
            startMenuBox = AddCheckBox("Start menu shortcut", options.StartMenuShortcut, 70);
            desktopBox = AddCheckBox("Desktop shortcut", options.DesktopShortcut, 95);
            startupBox = AddCheckBox("Start Shadowshot when Windows starts", options.StartWithWindows, 120);
            launchBox = AddCheckBox("Launch Shadowshot now", options.LaunchNow, 145);
            if (Installer.IsLightshotRunning()) optionsPanel.Controls.Add(CreateLightshotNote());

            donePanel.Controls.Add(new Label
            {
                AutoSize = true, Location = new Point(24, 24), Font = messageFont,
                Text = "Shadowshot is installed. Press PrtScr to take a screenshot.",
            });

            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.FromArgb(0xF3, 0xF3, 0xF3) };
            installButton.Click += OnInstallClick;
            cancelButton.Click += (sender, e) => Close();
            footer.Controls.Add(statusLabel);
            footer.Controls.Add(installButton);
            footer.Controls.Add(cancelButton);
            AcceptButton = installButton;
            CancelButton = cancelButton;

            // Docking takes the last-added control first: header, accent line, footer, then the fill panels.
            Controls.Add(optionsPanel);
            Controls.Add(donePanel);
            Controls.Add(footer);
            Controls.Add(accentLine);
            Controls.Add(header);
            ResumeLayout(false);
            PerformLayout();

            // Loaded after scaling so each icon is rendered at its final pixel size.
            windowIcon = LoadAppIcon(SystemInformation.IconSize.Width);
            Icon = windowIcon;
            using (Icon headerIcon = LoadAppIcon(iconBox.Width)) headerImage = headerIcon.ToBitmap();
            iconBox.Image = headerImage;
            ActiveControl = installButton;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing mid-install would abandon a half-done install; the buttons come back when it ends.
            if (installing) e.Cancel = true;
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (!disposing) return;
            bodyFont.Dispose();
            titleFont.Dispose();
            messageFont.Dispose();
            windowIcon.Dispose();
            headerImage.Dispose();
        }

        private CheckBox AddCheckBox(string text, bool isChecked, int y)
        {
            var box = new CheckBox { AutoSize = true, Location = new Point(24, y), Text = text, Checked = isChecked };
            optionsPanel.Controls.Add(box);
            return box;
        }

        private static Panel CreateLightshotNote()
        {
            var note = new Panel { Location = new Point(24, 179), Size = new Size(512, 42), BackColor = Color.FromArgb(0xFF, 0xF4, 0xCE) };
            note.Controls.Add(new Label
            {
                Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 0), TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(0x4D, 0x37, 0x00),
                Text = "Lightshot is running and may hold the PrtScr hotkeys. Quit Lightshot (tray icon → Exit) so Shadowshot can use them.",
            });
            // Added after the label so docking reserves the amber strip before the label fills the rest.
            note.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 4, BackColor = Color.FromArgb(0xF0, 0xA8, 0x00) });
            return note;
        }

        private void OnBrowseClick(object sender, EventArgs e)
        {
            using (var dialog = new FolderBrowserDialog { Description = "Choose the folder to install Shadowshot into.", SelectedPath = folderBox.Text })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK) folderBox.Text = dialog.SelectedPath;
            }
        }

        private async void OnInstallClick(object sender, EventArgs e)
        {
            var options = new InstallOptions
            {
                InstallDir = folderBox.Text,
                StartMenuShortcut = startMenuBox.Checked,
                DesktopShortcut = desktopBox.Checked,
                StartWithWindows = startupBox.Checked,
                LaunchNow = launchBox.Checked,
            };
            SetInstalling(true);
            try
            {
                await Task.Run(() => Installer.Install(options));
            }
            catch (Exception ex)
            {
                Installer.Log("Install failed: " + ex);
                SetInstalling(false);
                MessageBox.Show(this, "Shadowshot could not be installed:" + Environment.NewLine + Environment.NewLine
                    + ex.GetBaseException().Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            SetInstalling(false);
            optionsPanel.Visible = false;
            donePanel.Visible = true;
            installButton.Visible = false;
            cancelButton.Text = "Finish";
            AcceptButton = cancelButton;
            cancelButton.Focus();
        }

        private void SetInstalling(bool value)
        {
            installing = value;
            UseWaitCursor = value;
            optionsPanel.Enabled = !value;
            installButton.Enabled = !value;
            cancelButton.Enabled = !value;
            statusLabel.Text = value ? "Installing…" : "";
        }

        // ApplicationIcon is compiled in as Win32 icon group 32512; loading it at the exact size keeps it crisp.
        private static Icon LoadAppIcon(int size)
        {
            Marshal.ThrowExceptionForHR(Native.LoadIconWithScaleDown(Native.GetModuleHandle(null), (IntPtr)32512, size, size, out IntPtr handle));
            try
            {
                using (Icon borrowed = Icon.FromHandle(handle)) return (Icon)borrowed.Clone();
            }
            finally
            {
                Native.DestroyIcon(handle);
            }
        }

        private static class Native
        {
            [DllImport("comctl32.dll")]
            public static extern int LoadIconWithScaleDown(IntPtr hinst, IntPtr name, int cx, int cy, out IntPtr icon);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            public static extern IntPtr GetModuleHandle(string moduleName);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            public static extern bool DestroyIcon(IntPtr icon);
        }
    }
}
