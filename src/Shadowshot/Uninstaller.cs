using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Shadowshot
{
    public static class Uninstaller
    {
        public static void Run(bool silent)
        {
            if (!silent && MessageBox.Show("Uninstall Shadowshot? Your saved screenshots will be kept.", "Uninstall Shadowshot",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            string exeDir = Path.GetDirectoryName(ExePath);
            bool deleteProgram;
            try
            {
                StopOtherInstances();
                deleteProgram = IsInstallLocation(exeDir);   // read before the Uninstall key is deleted
                RemoveRegistration();
                DeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs\Shadowshot.lnk"));
                DeleteFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Shadowshot.lnk"));
                DeleteDataFolder();
            }
            catch (Exception ex)
            {
                Program.LogError("Uninstall", ex);
                Environment.ExitCode = 1;   // so QuietUninstallString callers see the failure
                if (!silent) MessageBox.Show("Uninstall failed: " + ex.Message, "Uninstall Shadowshot", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (!silent) MessageBox.Show("Shadowshot has been uninstalled.", "Uninstall Shadowshot", MessageBoxButtons.OK, MessageBoxIcon.Information);
            // Last, so this process exits well within the helper's 2-second delay and the exe is unlocked.
            if (deleteProgram) ScheduleProgramDeletion(exeDir);
        }

        // Assembly.Location is the real file path; Application.ExecutablePath turns %xx into other characters.
        static string ExePath => typeof(Uninstaller).Assembly.Location;

        static void StopOtherInstances()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(AppInfo.EventExit, out EventWaitHandle exit))
                    using (exit) exit.Set();
            }
            catch (UnauthorizedAccessException)
            {
                // An elevated instance owns the event; it can't be signalled from here.
            }

            using (Process self = Process.GetCurrentProcess())
            {
                DateTime deadline = DateTime.UtcNow.AddSeconds(3);
                foreach (Process process in Process.GetProcessesByName(self.ProcessName))
                {
                    using (process)
                    {
                        // Other sessions belong to other users (or their own installs) and can't be opened anyway.
                        if (process.Id == self.Id || process.SessionId != self.SessionId) continue;
                        int remaining = Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                        try
                        {
                            if (process.WaitForExit(remaining) || !SamePath(ExecutablePath(process), ExePath)) continue;
                            process.Kill();
                            process.WaitForExit(2000);
                        }
                        catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
                        {
                            // Already exited or exiting, or not ours to open (elevated); the delayed exe deletion
                            // copes with a leftover lock.
                        }
                    }
                }
            }
        }

        static string ExecutablePath(Process process)
        {
            try
            {
                return process.MainModule.FileName;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is Win32Exception)
            {
                return null;
            }
        }

        static bool IsInstallLocation(string dir)
        {
            using (RegistryKey uninstall = Registry.CurrentUser.OpenSubKey(AppInfo.UninstallKey))
                return SamePath(uninstall?.GetValue("InstallLocation") as string, dir);
        }

        static void RemoveRegistration()
        {
            using (RegistryKey run = Registry.CurrentUser.OpenSubKey(AppInfo.RunKey, true))
                run?.DeleteValue(AppInfo.Name, false);
            Registry.CurrentUser.DeleteSubKeyTree(AppInfo.AppKey, false);
            Registry.CurrentUser.DeleteSubKeyTree(AppInfo.UninstallKey, false);
        }

        // Only our own files, then a non-recursive rmdir: a screenshot folder configured inside
        // %APPDATA%\Shadowshot keeps the folder (and the screenshots) alive.
        static void DeleteDataFolder()
        {
            DeleteFile(Settings.FilePath);
            DeleteFile(Settings.FilePath + ".tmp");
            DeleteFile(Program.ErrorLogPath);
            try
            {
                if (Directory.Exists(AppInfo.DataDir)) Directory.Delete(AppInfo.DataDir, false);
            }
            catch (IOException)
            {
                // Not empty.
            }
        }

        static void DeleteFile(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        // A helper cmd waits for this process to exit, then deletes the exe and removes the folder only
        // if it is empty (rmdir without /s).
        static void ScheduleProgramDeletion(string dir)
        {
            string exe = Path.Combine(dir, "Shadowshot.exe");
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                "/c ping -n 3 127.0.0.1 >nul & del /f /q \"" + exe + "\" & rmdir \"" + dir + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = Path.GetTempPath(),   // a working directory inside dir would block rmdir
            };
            using (Process.Start(start)) { }
        }

        static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException)
            {
                return false;
            }
        }

        static string Normalize(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
