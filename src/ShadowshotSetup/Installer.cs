using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;

namespace ShadowshotSetup
{
    internal sealed class InstallOptions
    {
        public string InstallDir = Installer.DefaultInstallDir;
        public bool StartMenuShortcut = true;
        public bool DesktopShortcut;
        public bool StartWithWindows = true;
        public bool LaunchNow = true;
    }

    internal static class Installer
    {
        public const string Tagline = "Lightweight screenshot & annotation tool \u2014 built for Claude Code";

        // Mirrors Shadowshot.AppInfo: setup only embeds the app exe, it does not reference its assembly.
        private const string AppName = "Shadowshot";
        private const string ExitEventName = @"Local\Shadowshot.Cmd.Exit";
        private const string AppKey = @"Software\Shadowshot";
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Shadowshot";
        private const string PayloadResource = "Payload.Shadowshot.exe";

        public static string Version => typeof(Installer).Assembly.GetName().Version.ToString(3);

        // An upgrade goes to the folder the previous install recorded (step 6), not a second copy elsewhere.
        public static string DefaultInstallDir
        {
            get
            {
                using (RegistryKey app = Registry.CurrentUser.OpenSubKey(AppKey))
                {
                    if (app?.GetValue("InstallDir") is string dir && IsFullPath(dir)) return dir;
                }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
            }
        }

        private static string LogPath => Path.Combine(Path.GetTempPath(), "ShadowshotSetup.log");

        public static bool IsLightshotRunning() => CountProcesses("Lightshot") > 0;

        /// <summary>Runs install steps 1-7; throws with a user-readable message on failure.</summary>
        public static void Install(InstallOptions options)
        {
            string dir = NormalizeDir(options.InstallDir);
            string exe = Path.Combine(dir, AppName + ".exe");
            Log("Installing to " + dir);

            CloseRunningApp(exe);

            Directory.CreateDirectory(dir);
            WriteAppExe(exe);

            if (options.StartMenuShortcut)
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs", AppName + ".lnk"), exe, dir);
            if (options.DesktopShortcut)
                CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    AppName + ".lnk"), exe, dir);

            using (RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (options.StartWithWindows) run.SetValue(AppName, $"\"{exe}\" --tray");
                else run.DeleteValue(AppName, false);
            }
            Log("Start with Windows: " + options.StartWithWindows);

            RegisterUninstall(dir, exe);

            if (options.LaunchNow)
            {
                if (new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
                {
                    // Setup runs elevated: let the shell start the app un-elevated, or its single-instance
                    // objects would lock out every later normal launch.
                    using (Process.Start("explorer.exe", "\"" + exe + "\"")) { }
                    Log("Launched " + exe + " through explorer (setup is elevated)");
                }
                else
                {
                    Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir, UseShellExecute = false }).Dispose();
                    Log("Launched " + exe);
                }
            }
            Log("Install finished.");
        }

        public static void Log(string message)
        {
            try
            {
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ", CultureInfo.InvariantCulture)
                    + message + Environment.NewLine);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                // Logging is best effort; it must never fail the install.
            }
        }

        private static bool IsFullPath(string path)
        {
            try
            {
                Path.GetFullPath(path);
                return Path.GetPathRoot(path).Length >= 3;   // "C:\" or "\\server\share"; rejects "\x" and "C:x"
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        private static string NormalizeDir(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("Choose an install folder.");
            string full = Path.GetFullPath(dir.Trim());
            // Keep the separator of a drive root ("D:\"); "D:" alone would mean "current directory on D:".
            return full.Length > Path.GetPathRoot(full).Length ? full.TrimEnd(Path.DirectorySeparatorChar) : full;
        }

        private static int CountProcesses(string name)
        {
            Process[] processes = Process.GetProcessesByName(name);
            foreach (Process process in processes) process.Dispose();
            return processes.Length;
        }

        private static void CloseRunningApp(string exe)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ExitEventName, out EventWaitHandle exitEvent))
                {
                    using (exitEvent) exitEvent.Set();
                    Log("Asked the running Shadowshot to exit.");
                }
            }
            catch (UnauthorizedAccessException)
            {
                // An elevated instance owns the event; fall through to the wait/kill below.
                Log("The running Shadowshot is elevated and could not be asked to exit.");
            }

            Stopwatch waited = Stopwatch.StartNew();
            while (CountProcesses(AppName) > 0 && waited.ElapsedMilliseconds < 3000) Thread.Sleep(100);

            foreach (Process process in Process.GetProcessesByName(AppName))
            {
                using (process)
                {
                    try
                    {
                        if (!string.Equals(process.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                        Log("Stopping Shadowshot process " + process.Id);
                        process.Kill();
                        process.WaitForExit(3000);
                    }
                    catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException)
                    {
                        // Exited meanwhile, or not ours to inspect (other user / elevated). If it really
                        // holds the exe, replacing it below fails with a clear "file in use" error.
                        Log("Skipped process " + process.Id + ": " + ex.Message);
                    }
                }
            }
        }

        private static void WriteAppExe(string exe)
        {
            string tmp = exe + ".tmp";
            try
            {
                using (Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResource))
                using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    payload.CopyTo(file);
                }
                if (File.Exists(exe)) File.Replace(tmp, exe, null);
                else File.Move(tmp, exe);
            }
            catch
            {
                File.Delete(tmp);
                throw;
            }
            Log("Wrote " + exe);
        }

        private static void CreateShortcut(string linkPath, string exe, string dir)
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell", true);
            object shell = Activator.CreateInstance(shellType);
            object link = null;
            try
            {
                link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { linkPath });
                SetComProperty(link, "TargetPath", exe);
                SetComProperty(link, "WorkingDirectory", dir);
                SetComProperty(link, "Description", Tagline);
                SetComProperty(link, "IconLocation", exe + ",0");
                link.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
            }
            finally
            {
                if (link != null) Marshal.FinalReleaseComObject(link);
                Marshal.FinalReleaseComObject(shell);
            }
            Log("Created shortcut " + linkPath);
        }

        private static void SetComProperty(object target, string name, string value) =>
            target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new object[] { value });

        private static void RegisterUninstall(string dir, string exe)
        {
            int sizeKb = (int)((new FileInfo(exe).Length + 1023) / 1024);
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                key.SetValue("DisplayName", AppName);
                key.SetValue("DisplayVersion", Version);
                key.SetValue("Publisher", AppName);
                key.SetValue("DisplayIcon", exe + ",0");
                key.SetValue("InstallLocation", dir);
                key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
                key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --silent");
                key.SetValue("EstimatedSize", sizeKb, RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            }
            using (RegistryKey app = Registry.CurrentUser.CreateSubKey(AppKey))
            {
                app.SetValue("InstallDir", dir);
            }
            Log("Registered the uninstaller.");
        }
    }
}
