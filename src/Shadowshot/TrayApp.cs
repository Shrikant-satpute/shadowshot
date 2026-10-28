using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Shadowshot
{
    public sealed class TrayApp : ApplicationContext, IOverlayHost
    {
        const int RegionHotkeyId = 1, FullSaveHotkeyId = 2, ClaudeHotkeyId = 3;

        readonly Settings settings = Settings.Load();
        readonly SynchronizationContext ui = new WindowsFormsSynchronizationContext();
        readonly HotkeyManager hotkeys = new HotkeyManager();
        readonly Icon icon = LoadTrayIcon();
        readonly ContextMenuStrip menu = new ContextMenuStrip();
        readonly NotifyIcon tray = new NotifyIcon();
        readonly ToolStripMenuItem regionItem, fullSaveItem, claudeItem;
        readonly List<EventWaitHandle> commandEvents = new List<EventWaitHandle>();
        readonly List<RegisteredWaitHandle> commandWaits = new List<RegisteredWaitHandle>();
        CaptureOverlay overlay;
        SettingsForm options;
        Action balloonClick;   // action of the most recent balloon

        public TrayApp(bool startCapture, bool openSettings, bool silent)
        {
            // First, so a second launch during the (possibly slow) tray setup below already finds them.
            // Signals that arrive early are posted and run once the message loop starts.
            ListenFor(AppInfo.EventCapture, CaptureRegion);
            ListenFor(AppInfo.EventSettings, ShowOptions);
            ListenFor(AppInfo.EventExit, ExitThread);

            regionItem = new ToolStripMenuItem("Capture area", null, (s, e) => AfterMenuCloses(CaptureRegion));
            fullSaveItem = new ToolStripMenuItem("Capture full screen (save)", null, (s, e) => AfterMenuCloses(CaptureFullScreen));
            claudeItem = new ToolStripMenuItem("Screen → Copy for Claude", null, (s, e) => AfterMenuCloses(CaptureForClaude));
            menu.Items.AddRange(new ToolStripItem[]
            {
                regionItem, fullSaveItem, claudeItem,
                new ToolStripSeparator(),
                new ToolStripMenuItem("Open screenshots folder", null, (s, e) => OpenFolder(settings.SaveFolder)),
                new ToolStripMenuItem("Open Claude folder", null, (s, e) => OpenFolder(settings.ClaudeFolder)),
                new ToolStripSeparator(),
                new ToolStripMenuItem("Options…", null, (s, e) => ShowOptions()),
                new ToolStripMenuItem("About Shadowshot", null, (s, e) => ShowAbout()),
                new ToolStripMenuItem("Exit", null, (s, e) => ExitThread()),
            });

            tray.Icon = icon;
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) AfterMenuCloses(CaptureRegion); };
            tray.BalloonTipClicked += (s, e) => balloonClick?.Invoke();
            tray.Visible = true;

            hotkeys.Pressed += OnHotkey;

            List<string> failures = RegisterHotkeys();
            if (failures.Count > 0) ShowHotkeyFailures(failures);
            else if (!startCapture && (!silent || !settings.FirstRunDone)) ShowWelcome();

            // Posted so they run inside the message loop, where exceptions reach the global handler.
            if (startCapture) ui.Post(_ => CaptureRegion(), null);
            else if (openSettings) ui.Post(_ => ShowOptions(), null);
        }

        public void Notify(string title, string text, string openPath)
        {
            if (!settings.ShowNotifications) return;
            ShowBalloon(title, text, openPath == null ? null : (Action)(() => Reveal(openPath)), ToolTipIcon.Info);
        }

        void OnHotkey(int id)
        {
            switch (id)
            {
                case RegionHotkeyId: CaptureRegion(); break;
                case FullSaveHotkeyId: CaptureFullScreen(); break;
                case ClaudeHotkeyId: CaptureForClaude(); break;
            }
        }

        void CaptureRegion()
        {
            if (ActivateOverlay()) return;
            CaptureResult capture = ScreenCapture.CaptureAll(settings.CaptureCursor, true);
            CaptureOverlay form;
            try
            {
                form = new CaptureOverlay(capture, settings, this);
            }
            catch
            {
                capture.Dispose();
                throw;
            }
            form.FormClosed += (s, e) =>
            {
                overlay = null;
                capture.Dispose();
                GC.Collect();   // release the screenshot-sized buffers now so the idle tray app stays small
            };
            overlay = form;
            form.Show();
        }

        void CaptureFullScreen()
        {
            if (ActivateOverlay()) return;
            string folder = string.IsNullOrEmpty(settings.LastSaveDir) ? settings.SaveFolder : settings.LastSaveDir;
            string path;
            using (CaptureResult capture = ScreenCapture.CaptureAll(settings.CaptureCursor, false))
                path = Exporter.SaveAuto(capture.Image, folder, settings.Format, settings.JpegQuality);
            Notify("Screenshot saved", "Saved to " + path + ". Click to open the folder.", path);
        }

        void CaptureForClaude()
        {
            if (ActivateOverlay()) return;
            string path;
            using (CaptureResult capture = ScreenCapture.CaptureAll(settings.CaptureCursor, false))
            using (Bitmap screen = ScreenCapture.Crop(capture.Image, capture.MonitorAt(capture.Cursor).Bounds))
                path = Exporter.CopyForClaude(screen, settings, null);
            Notify("Copied for Claude", "Press Ctrl+V in Claude Code to attach it.", path);
        }

        // Only one capture at a time; a second request (or Options) brings the open overlay back instead.
        bool ActivateOverlay()
        {
            if (overlay == null) return false;
            overlay.Activate();
            return true;
        }

        void ShowOptions()
        {
            if (ActivateOverlay()) return;
            if (options != null)
            {
                options.Activate();
                return;
            }
            hotkeys.Suspend();   // so the dialog's hotkey boxes can receive PrtScr and our other combos
            options = new SettingsForm(settings);
            options.FormClosed += (s, e) =>
            {
                options = null;
                List<string> failures = RegisterHotkeys();
                if (failures.Count > 0) ShowHotkeyFailures(failures);
            };
            options.Show();
            options.Activate();
        }

        List<string> RegisterHotkeys()
        {
            hotkeys.UnregisterAll();
            var failures = new List<string>();
            RegisterHotkey(RegionHotkeyId, settings.RegionHotkey, regionItem, failures);
            RegisterHotkey(FullSaveHotkeyId, settings.FullSaveHotkey, fullSaveItem, failures);
            RegisterHotkey(ClaudeHotkeyId, settings.ClaudeHotkey, claudeItem, failures);

            Hotkey region = Hotkey.Parse(settings.RegionHotkey);
            string tip = region.IsEmpty
                ? AppInfo.Name + " — click to capture"
                : AppInfo.Name + " — press " + region.ToDisplayString() + " to capture";
            tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;   // NotifyIcon throws above 63 chars
            return failures;
        }

        void RegisterHotkey(int id, string text, ToolStripMenuItem item, List<string> failures)
        {
            Hotkey hotkey = Hotkey.Parse(text);
            item.ShortcutKeyDisplayString = hotkey.ToDisplayString();
            if (!hotkey.IsEmpty && !hotkeys.Register(id, hotkey, out string error)) failures.Add(error);
        }

        void ShowHotkeyFailures(List<string> failures) =>
            ShowBalloon("Some hotkeys could not be registered", string.Join("\n", failures), ShowOptions, ToolTipIcon.Warning);

        void ShowWelcome()
        {
            Hotkey region = Hotkey.Parse(settings.RegionHotkey);
            string text = region.IsEmpty
                ? "Shadowshot is running. Click this icon to take a screenshot."
                : "Shadowshot is running. Press " + region.ToDisplayString() + " (or click this icon) to take a screenshot.";
            ShowBalloon(AppInfo.Name, text, null, ToolTipIcon.Info);

            if (!settings.FirstRunDone)
            {
                settings.FirstRunDone = true;
                settings.Save();
            }
        }

        void ShowBalloon(string title, string text, Action onClick, ToolTipIcon kind)
        {
            balloonClick = onClick;
            tray.ShowBalloonTip(5000, title, text, kind);
        }

        void ListenFor(string eventName, Action command)
        {
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            commandEvents.Add(signal);
            commandWaits.Add(ThreadPool.RegisterWaitForSingleObject(
                signal, (state, timedOut) => ui.Post(_ => command(), null), null, Timeout.Infinite, false));
        }

        // Menu and tray-icon click handlers run while the dropdown (or the tray overflow flyout) is still on
        // screen. Posting lets it close, and DwmFlush waits for a composed frame without it, so it doesn't
        // end up in the screenshot.
        void AfterMenuCloses(Action capture) => ui.Post(_ =>
        {
            Native.DwmFlush();
            capture();
        }, null);

        static void ShowAbout() =>
            MessageBox.Show(AppInfo.Name + " " + AppInfo.Version + "\n\nLightweight screenshot & annotation tool built for Claude Code",
                "About Shadowshot", MessageBoxButtons.OK, MessageBoxIcon.Information);

        static void Reveal(string path)
        {
            if (File.Exists(path)) StartExplorer("/select,\"" + path + "\"");
            else OpenFolder(Path.GetDirectoryName(path));
        }

        static void OpenFolder(string folder)
        {
            Directory.CreateDirectory(folder);
            StartExplorer("\"" + folder + "\"");
        }

        static void StartExplorer(string arguments)
        {
            using (Process.Start("explorer.exe", arguments)) { }
        }

        static Icon LoadTrayIcon()
        {
            Size size = SystemInformation.SmallIconSize;
            try
            {
                if (Native.LoadIconWithScaleDown(Native.GetModuleHandle(null), (IntPtr)Native.AppIconId, size.Width, size.Height, out IntPtr handle) == 0)
                {
                    try
                    {
                        using (Icon shared = Icon.FromHandle(handle))
                            return (Icon)shared.Clone();   // the clone owns a copy of the HICON
                    }
                    finally
                    {
                        Native.DestroyIcon(handle);
                    }
                }
            }
            catch (EntryPointNotFoundException)
            {
                // comctl32 v5 (no visual-styles activation context) lacks LoadIconWithScaleDown.
            }
            using (Stream stream = typeof(TrayApp).Assembly.GetManifestResourceStream("Shadowshot.ico"))
                return new Icon(stream, size);
        }

        protected override void ExitThreadCore()
        {
            tray.Visible = false;   // first, so closing forms below can't pop balloons
            overlay?.Close();
            options?.Close();
            foreach (RegisteredWaitHandle wait in commandWaits) wait.Unregister(null);
            foreach (EventWaitHandle signal in commandEvents) signal.Dispose();
            hotkeys.Dispose();
            tray.Dispose();
            menu.Dispose();
            icon.Dispose();
            base.ExitThreadCore();
        }

        static class Native
        {
            public const int AppIconId = 32512;   // Win32 icon group id the SDK gives <ApplicationIcon>

            [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
            public static extern int LoadIconWithScaleDown(IntPtr instance, IntPtr name, int width, int height, out IntPtr icon);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            public static extern IntPtr GetModuleHandle(string moduleName);

            [DllImport("user32.dll")]
            public static extern bool DestroyIcon(IntPtr icon);

            [DllImport("dwmapi.dll")]
            public static extern int DwmFlush();
        }
    }
}
