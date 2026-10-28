using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace Shadowshot.Tests
{
    // Clipboard tests run on a private window station (each window station has its own clipboard), so the user's
    // clipboard is never read or written. If the private station can't be set up, the tests are skipped.
    static class ClipboardTests
    {
        public static void Run()
        {
            T.Run("Clipboard.CtrlVPastesIntoTextAndLabel (R1-01)", () => Isolated.Run(Paste));
            T.Run("Clipboard.PasteIsCapped (R1-05)", () => Isolated.Run(PasteCap));
            T.Run("Clipboard.CopyForClaudeDriveVsUnc (R4-01/R4-06)", () => Isolated.Run(CopyForClaude));
        }

        static Point P(int x, int y) => new Point(x, y);

        static void Paste()
        {
            using (var rig = new Rig(Fx.TwoMonitors()))
            {
                rig.SetSelection(new Rectangle(100, 100, 1500, 850));
                rig.Key(Keys.T);
                rig.Click(P(300, 300));
                rig.Type("ab");
                var text = (TextAnnotation)rig.F<Annotation>("editing");
                Clipboard.SetText("pasted\r\nline2\tx\u0007y");
                T.True(!rig.Key(Keys.ControlKey | Keys.Control), "Ctrl pressed alone is not handled");
                T.True(rig.Key(Keys.V | Keys.Control), "Ctrl+V handled while typing");
                T.True(rig.F<Annotation>("editing") == text && text.Editing, "still typing after the paste");
                T.Eq("abpasted\nline2 xy", text.Text, "pasted (CRLF → newline, TAB → space, control chars dropped)");
                rig.Type("!");
                T.Eq("abpasted\nline2 xy!", text.Text, "typing continues after the paste");
                rig.CheckIncremental("paste_text");
                rig.Key(Keys.Escape);
                T.Eq("abpasted\nline2 xy!", rig.Last<TextAnnotation>().Text, "committed");

                rig.Key(Keys.N);
                rig.Click(P(800, 300));
                Clipboard.SetText("first\r\nsecond\nthird");
                rig.Key(Keys.ControlKey | Keys.Control);
                T.True(rig.Key(Keys.V | Keys.Control), "Ctrl+V handled in a Step label");
                var step = rig.Last<StepAnnotation>();
                T.True(step.Editing, "label still being typed");
                T.Eq("first second third", step.Label, "a Step label paste is one line");
                rig.Key(Keys.Enter);
                T.Eq("Screenshot notes: (1) first second third", rig.E.BuildClaudeNotes(), "notes");

                int n = rig.E.Annotations.Count;
                rig.Commands.Clear();
                T.True(!rig.Key(Keys.V | Keys.Control), "Ctrl+V does nothing when not typing");
                T.True(rig.E.Annotations.Count == n && rig.Commands.Count == 0, "no annotation, no command");

                Clipboard.Clear();
                rig.Key(Keys.T);
                rig.Click(P(300, 600));
                rig.Type("z");
                T.True(rig.Key(Keys.V | Keys.Control), "Ctrl+V with an empty clipboard");
                T.Eq("z", ((TextAnnotation)rig.F<Annotation>("editing")).Text, "nothing pasted");
                rig.CheckIncremental("paste_end");
            }
        }

        static void PasteCap()
        {
            using (var rig = new Rig(Fx.TwoMonitors()))
            {
                rig.SetSelection(new Rectangle(100, 100, 1500, 850));
                rig.Key(Keys.T);
                rig.Click(P(200, 300));
                var text = (TextAnnotation)rig.F<Annotation>("editing");
                text.Text = new string('a', 3998);
                Clipboard.SetText("XYZ");
                rig.Key(Keys.V | Keys.Control);
                T.Eq(4000, text.Text.Length, "paste capped at 4000");
                T.True(text.Text.EndsWith("aXY"), "the start of the clipboard text is kept");
                rig.Key(Keys.V | Keys.Control);
                T.Eq(4000, text.Text.Length, "no room: nothing pasted");
                rig.Key(Keys.Escape);

                rig.Click(P(200, 500));
                text = (TextAnnotation)rig.F<Annotation>("editing");
                text.Text = new string('a', 3999);
                Clipboard.SetText("\U0001F600abc");
                rig.Key(Keys.V | Keys.Control);
                T.Eq(3999, text.Text.Length, "a surrogate pair is never split at the cap");
                rig.Key(Keys.Escape);

                // A 40,000-char line used to throw "A generic error occurred in GDI+" on every caret blink.
                rig.Click(P(200, 700));
                text = (TextAnnotation)rig.F<Annotation>("editing");
                Clipboard.SetText(new string('k', 40000));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                rig.Key(Keys.V | Keys.Control);
                T.Eq(4000, text.Text.Length, "a huge one-line paste is capped");
                for (int i = 0; i < 4; i++) rig.Tick();
                rig.CheckIncremental("paste_huge");
                rig.Key(Keys.Escape);
                using (Bitmap img = rig.E.RenderResult()) T.True(img.Width > 0, "export works");
                T.Note("40,000-char paste + 4 caret blinks + full repaint: " + sw.ElapsedMilliseconds + " ms");

                // A big multi-line paste stays responsive.
                rig.Click(P(900, 200));
                text = (TextAnnotation)rig.F<Annotation>("editing");
                var lines = new StringBuilder();
                for (int i = 0; i < 1000; i++) lines.Append(new string('m', 78)).Append("\r\n");
                Clipboard.SetText(lines.ToString());
                sw.Restart();
                rig.Key(Keys.V | Keys.Control);
                rig.Tick(); rig.Tick();
                long ms = sw.ElapsedMilliseconds;
                T.True(text.Text.Length == 4000 && text.Text.IndexOf('\r') < 0, "multi-line paste capped, CRLF normalised (len " + text.Text.Length + ")");
                T.Note("1000-line paste + caret blink: " + ms + " ms");
                rig.Key(Keys.Escape);

                rig.Key(Keys.N);
                rig.Click(P(1200, 800));
                Clipboard.SetText(new string('z', 1000));
                rig.Key(Keys.V | Keys.Control);
                T.Eq(300, rig.Last<StepAnnotation>().Label.Length, "Step label paste capped at 300");
                rig.Key(Keys.Enter);
                rig.CheckIncremental("paste_cap_end");
            }
        }

        static string OneLine(string s) => (string)Priv.CallStatic(typeof(Exporter), "OneLine", s);

        // What Claude Code does with pasted text (scratchpad recon): split at a space before "/" or "X:\", then at
        // newlines; parts ending in an image extension are image candidates, read only if they are absolute paths.
        static (List<string> Text, List<string> Images) ClaudeCodeSplit(string clip)
        {
            var parts = Regex.Split(clip, @" (?=/|[A-Za-z]:\\)").SelectMany(p => p.Split('\n')).ToList();
            var images = parts.Where(p => Regex.IsMatch(p.Trim(), @"\.(png|jpe?g|gif|webp)$", RegexOptions.IgnoreCase)).ToList();
            return (parts.Except(images).ToList(), images);
        }

        static void CopyForClaude()
        {
            string local = Path.Combine(T.OutDir, "claude");
            var s = new Settings { ClaudeFolder = local };
            const string Notes = "Screenshot notes: (1) Open /usr/bin/app; (2) Edit C:\\Temp\\a.txt; (3) Replace with icon.png";
            using (var img = new Bitmap(64, 48))
            {
                using (var g = Graphics.FromImage(img)) g.Clear(Color.FromArgb(0x34, 0xC7, 0x59));

                string path = Exporter.CopyForClaude(img, s, Notes);
                T.True(File.Exists(path) && path.Length > 2 && path[1] == ':', "PNG written to the drive folder: " + path);
                string line = OneLine(Notes);
                string clip = Clipboard.GetText();
                T.Eq(line + " " + path, clip, "drive folder: notes + space + path");
                T.True(Clipboard.ContainsImage(), "bitmap on the clipboard");
                using (var png = Clipboard.GetData("PNG") as MemoryStream)
                {
                    T.True(png != null, "PNG stream on the clipboard");
                    using (var decoded = new Bitmap(png)) T.Eq(img.Size, decoded.Size, "PNG size");
                }
                var (textParts, images) = ClaudeCodeSplit(clip);
                T.True(images.Count == 1 && images[0].Trim() == path, "Claude Code sees exactly one image, the saved file: " + string.Join(" | ", images));
                T.True(textParts.Count == 1 && textParts[0] == line, "the notes stay in one piece: " + string.Join(" | ", textParts));

                path = Exporter.CopyForClaude(img, s, "");
                T.Eq(path, Clipboard.GetText(), "no notes: the path alone");

                // UNC folder: Claude Code can't split notes off a UNC path, so only the path goes on the clipboard.
                string unc = UncFolder(local, out string kind);
                s.ClaudeFolder = unc;
                path = Exporter.CopyForClaude(img, s, Notes);
                T.True(path.StartsWith(@"\\", StringComparison.Ordinal) && File.Exists(path), kind + " path written: " + path);
                T.Eq(path, Clipboard.GetText(), kind + " folder: the path alone, no notes");
                T.Note("UNC check used a " + kind + " folder");
            }
            foreach (string f in Directory.GetFiles(local, "claude_*.png")) File.Delete(f);
        }

        // A UNC path to the local folder: the C$ admin share via localhost if reachable, else the \\?\ form.
        static string UncFolder(string local, out string kind)
        {
            string root = Path.GetPathRoot(local);
            if (root.Length >= 2 && root[1] == ':')
            {
                string share = @"\\localhost\" + char.ToUpperInvariant(root[0]) + "$" + local.Substring(2);
                try
                {
                    Directory.CreateDirectory(share);
                    File.WriteAllText(Path.Combine(share, "probe.txt"), "x");
                    File.Delete(Path.Combine(share, "probe.txt"));
                    kind = "UNC (\\\\localhost\\" + char.ToUpperInvariant(root[0]) + "$)";
                    return share;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
            kind = "\\\\?\\ long-path";
            return @"\\?\" + local;
        }
    }

    static class Isolated
    {
        const uint WINSTA_ALL_ACCESS = 0x37F;
        const uint DESKTOP_ALL_ACCESS = 0x000F01FF;
        const int UOI_NAME = 2;

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateWindowStation(string name, uint flags, uint access, IntPtr attributes);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr GetProcessWindowStation();

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetProcessWindowStation(IntPtr station);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool CloseWindowStation(IntPtr station);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr attributes);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetThreadDesktop(IntPtr desktop);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr GetThreadDesktop(uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder info, int length, out int needed);

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        static string NameOf(IntPtr handle)
        {
            var sb = new StringBuilder(256);
            return GetUserObjectInformation(handle, UOI_NAME, sb, sb.Capacity * 2, out _) ? sb.ToString() : "?";
        }

        // Runs body on a new STA thread attached to a private window station + desktop, then restores the process station.
        public static void Run(Action body)
        {
            string name = "ShadowshotTests" + System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr original = GetProcessWindowStation();
            IntPtr station = CreateWindowStation(name, 0, WINSTA_ALL_ACCESS, IntPtr.Zero);
            if (station == IntPtr.Zero)
            {
                // Named stations can be refused (error 5); an unnamed one gets a per-logon-session name (as Chromium's sandbox does).
                int namedError = Marshal.GetLastWin32Error();
                station = CreateWindowStation(null, 0, WINSTA_ALL_ACCESS, IntPtr.Zero);
                if (station == IntPtr.Zero)
                    throw new SkipTest("CreateWindowStation failed (named " + namedError + ", unnamed " + Marshal.GetLastWin32Error() + ")");
                name = NameOf(station);
            }
            if (string.Equals(name, "WinSta0", StringComparison.OrdinalIgnoreCase) || name == "?")
            {
                CloseWindowStation(station);
                throw new SkipTest("private window station has an unexpected name '" + name + "'");
            }
            Exception error = null;
            try
            {
                if (!SetProcessWindowStation(station)) throw new SkipTest("SetProcessWindowStation failed (" + Marshal.GetLastWin32Error() + ")");
                IntPtr desktop = IntPtr.Zero;
                try
                {
                    desktop = CreateDesktop("Default", IntPtr.Zero, IntPtr.Zero, 0, DESKTOP_ALL_ACCESS, IntPtr.Zero);
                    if (desktop == IntPtr.Zero) throw new SkipTest("CreateDesktop failed (" + Marshal.GetLastWin32Error() + ")");
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            if (!SetThreadDesktop(desktop)) throw new SkipTest("SetThreadDesktop failed (" + Marshal.GetLastWin32Error() + ")");
                            // Safety: never touch a clipboard unless this thread is really on the private station.
                            string current = NameOf(GetProcessWindowStation());
                            if (current != name || GetThreadDesktop(GetCurrentThreadId()) != desktop || string.Equals(current, "WinSta0", StringComparison.OrdinalIgnoreCase))
                                throw new SkipTest("not isolated (window station '" + current + "')");
                            body();
                        }
                        catch (Exception ex) { error = ex; }
                    });
                    thread.SetApartmentState(ApartmentState.STA);
                    thread.Start();
                    thread.Join();
                }
                finally
                {
                    SetProcessWindowStation(original);
                    if (desktop != IntPtr.Zero) CloseDesktop(desktop);
                }
            }
            finally
            {
                CloseWindowStation(station);
            }
            if (NameOf(GetProcessWindowStation()) != NameOf(original)) throw new TestFailure("process window station not restored");
            if (error is TestFailure || error is SkipTest) throw error;
            if (error != null) throw new TestFailure(error.GetType().Name + ": " + error.Message + Environment.NewLine + error.StackTrace);
        }
    }
}
