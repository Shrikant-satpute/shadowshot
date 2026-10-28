using System;
using System.Windows.Forms;

namespace ShadowshotSetup
{
    internal static class Program
    {
        private const string Usage = "Usage: ShadowshotSetup.exe [/S] [/D=<folder>] [/NOSTARTUP] [/NOLAUNCH]";

        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Installer.Log("Shadowshot Setup " + Installer.Version + " started. Arguments: " + string.Join(" ", args));

            var options = new InstallOptions();
            bool silent = false;
            string unknown = null;
            foreach (string arg in args)
            {
                string flag = arg.ToUpperInvariant();
                if (flag == "/S") silent = true;
                else if (flag.StartsWith("/D=", StringComparison.Ordinal)) options.InstallDir = arg.Substring(3);
                else if (flag == "/NOSTARTUP") options.StartWithWindows = false;
                else if (flag == "/NOLAUNCH") options.LaunchNow = false;
                else unknown ??= arg;
            }

            if (unknown != null)
            {
                string message = "Unknown option: " + unknown + Environment.NewLine + Usage;
                Installer.Log(message);
                if (silent) Console.Error.WriteLine(message);
                else MessageBox.Show(message, "Shadowshot Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }

            if (silent) return InstallSilently(options);

            Application.Run(new SetupForm(options));
            return 0;
        }

        private static int InstallSilently(InstallOptions options)
        {
            try
            {
                Installer.Install(options);
                return 0;
            }
            catch (Exception ex)
            {
                Installer.Log("Install failed: " + ex);
                Console.Error.WriteLine("Shadowshot Setup failed: " + ex.GetBaseException().Message);
                return 1;
            }
        }
    }
}
