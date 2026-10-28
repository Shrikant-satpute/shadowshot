using System;
using System.IO;

namespace Shadowshot
{
    public enum ToolKind { None, Pen, Line, Arrow, Rectangle, Ellipse, Marker, Text, Step, Pixelate }

    public enum EditorCommand
    {
        CopyForClaude,   // Enter / Ctrl+Shift+C / Claude button
        Copy,            // Ctrl+C / Copy button
        QuickSave,       // Ctrl+S: auto-named file in the last save folder, no dialog
        SaveAs,          // Save button: Save As dialog
        Print,           // Ctrl+P / Print button
        Close,           // Esc / Ctrl+X / Close button
        PickCustomColor, // "Custom…" cell of the color palette
        ContextMenu,     // right-click: form shows its context menu at Editor.LastMouse
    }

    public interface IOverlayHost
    {
        // Tray balloon/toast. openPath (nullable): file to reveal in Explorer when the balloon is clicked.
        void Notify(string title, string text, string openPath);
    }

    public static class AppInfo
    {
        public const string Name = "Shadowshot";
        public const string MutexName = @"Local\Shadowshot.Instance";
        public const string EventCapture = @"Local\Shadowshot.Cmd.Capture";
        public const string EventSettings = @"Local\Shadowshot.Cmd.Settings";
        public const string EventExit = @"Local\Shadowshot.Cmd.Exit";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Shadowshot"; // HKCU
        public const string AppKey = @"Software\Shadowshot";                                          // HKCU
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";                // HKCU, value "Shadowshot"

        public static string Version => typeof(AppInfo).Assembly.GetName().Version.ToString(3);

        public static string DataDir => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);
    }
}
