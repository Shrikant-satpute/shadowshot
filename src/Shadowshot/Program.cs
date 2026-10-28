using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Shadowshot
{
    static class Program
    {
        internal static string ErrorLogPath => Path.Combine(AppInfo.DataDir, "error.log");

        static bool showingError;

        [STAThread]
        static void Main(string[] args)
        {
            bool Has(string flag) => Array.Exists(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError("Unhandled exception", e.ExceptionObject as Exception);

            if (Has("--uninstall"))
            {
                Uninstaller.Run(Has("--silent"));
                return;
            }

            Mutex instance;
            try
            {
                instance = new Mutex(false, AppInfo.MutexName, out bool firstInstance);
                if (!firstInstance)
                {
                    using (instance)
                    {
                        // Let the running instance bring its overlay or dialog to the foreground.
                        Native.AllowSetForegroundWindow(Native.ASFW_ANY);
                        string command = Has("--exit") ? AppInfo.EventExit
                            : Has("--settings") ? AppInfo.EventSettings
                            : Has("--tray") ? null
                            : AppInfo.EventCapture;
                        if (command != null && EventWaitHandle.TryOpenExisting(command, out EventWaitHandle signal))
                            using (signal) signal.Set();
                    }
                    return;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // An elevated instance owns the mutex and events; their default DACL keeps this process out.
                if (!Has("--tray"))
                    MessageBox.Show("Shadowshot is already running as administrator. Exit it from its tray icon, then try again.",
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (instance)
            {
                if (Has("--exit")) return;
                Application.Run(new TrayApp(Has("--capture"), Has("--settings"), Has("--tray")));
            }
        }

        // Logs the error and shows a short message; nested errors while the message is open are only logged.
        internal static void ReportError(Exception ex)
        {
            LogError("Error", ex);
            if (showingError) return;
            showingError = true;
            try
            {
                MessageBox.Show(ex.Message + "\n\nDetails were written to " + ErrorLogPath,
                    AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                showingError = false;
            }
        }

        internal static void LogError(string context, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppInfo.DataDir);
                File.AppendAllText(ErrorLogPath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " " + context + ": " + ex + Environment.NewLine);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static class Native
        {
            public const int ASFW_ANY = -1;

            [DllImport("user32.dll")]
            public static extern bool AllowSetForegroundWindow(int processId);
        }
    }
}
