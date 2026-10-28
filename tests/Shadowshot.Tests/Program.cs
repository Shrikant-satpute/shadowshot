using System;
using System.IO;

namespace Shadowshot.Tests
{
    // Usage: Shadowshot.Tests.exe [outDir] [nameFilter]
    // Headless: no Form is shown, the clipboard is never written, the registry is never touched.
    internal static class Program
    {
        static readonly string DefaultOut = Path.Combine(Path.GetTempPath(), "ShadowshotTests");

        [STAThread]
        static int Main(string[] args)
        {
            T.OutDir = Path.GetFullPath(args.Length > 0 && args[0].Length > 0 ? args[0] : DefaultOut);
            T.Filter = args.Length > 1 ? args[1] : null;
            Directory.CreateDirectory(T.OutDir);
            Console.WriteLine("Shadowshot tests -> " + T.OutDir);

            HotkeyTests.Run();
            SettingsTests.Run();
            AnnotationTests.Run();
            ExporterTests.Run();
            EditorTests.Run();
            LayoutTests.Run();
            IncrementalTests.Run();
            RegressionTests.Run();
            CaptureTests.Run();
            ClipboardTests.Run();   // last: runs on a private window station, never the user's clipboard

            return T.Summary();
        }
    }
}
