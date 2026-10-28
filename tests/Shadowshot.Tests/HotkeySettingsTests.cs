using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Shadowshot.Tests
{
    static class HotkeyTests
    {
        public static void Run()
        {
            T.Run("Hotkey.ParseKnown", ParseKnown);
            T.Run("Hotkey.RoundTripAllKeysAndModifiers", RoundTripAll);
            T.Run("Hotkey.DisplayStringParsesBack", DisplayParsesBack);
            T.Run("Hotkey.JunkIsEmpty", Junk);
        }

        static bool Same(Hotkey a, Hotkey b) => a.Key == b.Key && a.Ctrl == b.Ctrl && a.Shift == b.Shift && a.Alt == b.Alt && a.Win == b.Win;

        static void Expect(string input, Keys key, bool ctrl, bool shift, bool alt, bool win, string canonical, string display)
        {
            Hotkey h = Hotkey.Parse(input);
            T.True(h.Key == key && h.Ctrl == ctrl && h.Shift == shift && h.Alt == alt && h.Win == win,
                "Parse(\"" + input + "\") = " + h.ToString() + " (key " + h.Key + ")");
            T.Eq(canonical, h.ToString(), "ToString of \"" + input + "\"");
            T.Eq(display, h.ToDisplayString(), "ToDisplayString of \"" + input + "\"");
        }

        static void ParseKnown()
        {
            Expect("PrintScreen", Keys.PrintScreen, false, false, false, false, "PrintScreen", "PrtScr");
            Expect("Shift+PrintScreen", Keys.PrintScreen, false, true, false, false, "Shift+PrintScreen", "Shift+PrtScr");
            Expect("Ctrl+Alt+PrintScreen", Keys.PrintScreen, true, false, true, false, "Ctrl+Alt+PrintScreen", "Ctrl+Alt+PrtScr");
            Expect(" ctrl + SHIFT + prtscr ", Keys.PrintScreen, true, true, false, false, "Ctrl+Shift+PrintScreen", "Ctrl+Shift+PrtScr");
            Expect("Control+PrtSc", Keys.PrintScreen, true, false, false, false, "Ctrl+PrintScreen", "Ctrl+PrtScr");
            Expect("PrintScreen+Shift", Keys.PrintScreen, false, true, false, false, "Shift+PrintScreen", "Shift+PrtScr");
            Expect("Alt+F1", Keys.F1, false, false, true, false, "Alt+F1", "Alt+F1");
            Expect("Win+Shift+S", Keys.S, false, true, false, true, "Shift+Win+S", "Shift+Win+S");
            Expect("Windows+Ctrl+Alt+Shift+F12", Keys.F12, true, true, true, true, "Ctrl+Alt+Shift+Win+F12", "Ctrl+Alt+Shift+Win+F12");
            Expect("Ctrl+5", Keys.D5, true, false, false, false, "Ctrl+D5", "Ctrl+5");
            Expect("Ctrl+D5", Keys.D5, true, false, false, false, "Ctrl+D5", "Ctrl+5");
            Expect("Ctrl+Enter", Keys.Enter, true, false, false, false, Hotkey.Parse("Ctrl+Enter").ToString(), "Ctrl+Enter");
            T.True(Hotkey.Parse("Ctrl+Return").Key == Keys.Enter, "Return alias");
            Expect("Ctrl+PageDown", Keys.PageDown, true, false, false, false, Hotkey.Parse("Ctrl+PageDown").ToString(), "Ctrl+PageDown");
            foreach (string d in new[] { new Settings().RegionHotkey, new Settings().FullSaveHotkey, new Settings().ClaudeHotkey })
                T.True(!Hotkey.Parse(d).IsEmpty && Hotkey.Parse(d).ToString() == d, "default hotkey " + d + " is canonical");
            T.True(new Hotkey().IsEmpty && new Hotkey().ToString() == "" && new Hotkey().ToDisplayString() == "", "empty hotkey strings");
        }

        static void RoundTripAll()
        {
            var problems = new List<string>();
            int count = 0;
            foreach (Keys k in Enum.GetValues(typeof(Keys)))
            {
                if (k == Keys.None || (uint)k >= (uint)Keys.KeyCode || Hotkey.IsModifierKey(k)) continue;
                for (int m = 0; m < 16; m++)
                {
                    var h = new Hotkey { Key = k, Ctrl = (m & 1) != 0, Shift = (m & 2) != 0, Alt = (m & 4) != 0, Win = (m & 8) != 0 };
                    string s = h.ToString();
                    Hotkey back = Hotkey.Parse(s);
                    count++;
                    if (!Same(h, back)) problems.Add(k + "/" + m + ": \"" + s + "\" -> " + back.Key);
                    if (s.IndexOf(' ') >= 0) problems.Add("space in \"" + s + "\"");
                }
            }
            T.Problems(problems, "Parse(ToString()) round trip over " + count + " hotkeys");
        }

        static void DisplayParsesBack()
        {
            var problems = new List<string>();
            foreach (Keys k in Enum.GetValues(typeof(Keys)))
            {
                if (k == Keys.None || (uint)k >= (uint)Keys.KeyCode || Hotkey.IsModifierKey(k)) continue;
                var h = new Hotkey { Key = k, Ctrl = true, Alt = true };
                string d = h.ToDisplayString();
                if (!Same(h, Hotkey.Parse(d))) problems.Add(k + ": \"" + d + "\"");
            }
            T.Problems(problems, "Parse(ToDisplayString())");
        }

        static void Junk()
        {
            string[] junk =
            {
                null, "", "   ", "+", "++", "Ctrl", "Ctrl+", "Ctrl+Shift", "Ctrl++", "Foo", "Ctrl+Foo", "A+B", "Ctrl+A+B", "123", "Ctrl+65",
                "A,B", "Ctrl+A,B", "ShiftKey", "Ctrl+ControlKey", "LWin", "Ctrl+None", "Ctrl+Modifiers", "Ctrl+KeyCode", "Ctrl+Shift+Alt",
                "Ctrl+ F 1", "Ctrl+-1", "Ctrl+ 0x2C", "\u00e9", "Ctrl+\u00e9", "Ctrl+Alt+Del+Del",
            };
            var problems = new List<string>();
            foreach (string s in junk)
            {
                Hotkey h;
                try { h = Hotkey.Parse(s); }
                catch (Exception ex) { problems.Add("\"" + s + "\" threw " + ex.GetType().Name); continue; }
                if (!h.IsEmpty || h.Ctrl || h.Shift || h.Alt || h.Win) problems.Add("\"" + s + "\" -> \"" + h + "\"");
            }
            T.Problems(problems, "junk must parse to an empty hotkey");
        }
    }

    static class SettingsTests
    {
        public static void Run()
        {
            T.Run("Settings.Defaults", Defaults);
            T.Run("Settings.ToolSizeClamping", Clamping);
            T.Run("Settings.SaveLoadRoundTrip", () => Guarded(RoundTrip));
            T.Run("Settings.LoadCorruptValuesKeepsDefaults", () => Guarded(Corrupt));
            T.Run("Settings.LoadMissingFileGivesDefaults", () => Guarded(Missing));
        }

        static void Defaults()
        {
            var s = new Settings();
            string pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures", "Shadowshot");
            T.Eq("PrintScreen", s.RegionHotkey, "RegionHotkey");
            T.Eq("Shift+PrintScreen", s.FullSaveHotkey, "FullSaveHotkey");
            T.Eq("Ctrl+Alt+PrintScreen", s.ClaudeHotkey, "ClaudeHotkey");
            T.Eq(pictures, s.SaveFolder, "SaveFolder");
            T.Eq(Path.Combine(pictures, "Claude"), s.ClaudeFolder, "ClaudeFolder");
            T.Eq("", s.LastSaveDir, "LastSaveDir");
            T.Eq("png", s.Format, "Format");
            T.Eq(90, s.JpegQuality, "JpegQuality");
            T.True(s.ShowNotifications && !s.CaptureCursor && !s.KeepSelection && s.ClaudeIncludeNotes && !s.FirstRunDone, "bool defaults");
            T.Eq(Rectangle.Empty, s.LastSelection, "LastSelection");
            T.Eq(Color.FromArgb(255, 0, 0).ToArgb(), s.Color.ToArgb(), "Color");
            T.Eq(Color.FromArgb(255, 204, 0).ToArgb(), s.MarkerColor.ToArgb(), "MarkerColor (#FFCC00, a palette swatch)");
            var swatches = (Color[])typeof(Editor).GetField("Swatches", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).GetValue(null);
            T.True(swatches.Any(c => c.ToArgb() == s.MarkerColor.ToArgb()), "default MarkerColor is one of the palette swatches");
            T.True(swatches.Any(c => c.ToArgb() == s.Color.ToArgb()), "default Color is one of the palette swatches");
            T.Eq(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shadowshot", "settings.ini"), Settings.FilePath, "FilePath");
            foreach (var (tool, def, _, _) in Ranges)
                T.Eq(def, s.GetToolSize(tool), "default size " + tool);
        }

        static readonly (ToolKind Tool, int Default, int Min, int Max)[] Ranges =
        {
            (ToolKind.Pen, 3, 1, 30), (ToolKind.Line, 3, 1, 30), (ToolKind.Arrow, 4, 2, 30), (ToolKind.Rectangle, 3, 1, 30),
            (ToolKind.Ellipse, 3, 1, 30), (ToolKind.Marker, 18, 6, 60), (ToolKind.Text, 22, 10, 96), (ToolKind.Step, 30, 18, 80),
            (ToolKind.Pixelate, 10, 4, 40),
        };

        static void Clamping()
        {
            var s = new Settings();
            foreach (var (tool, def, min, max) in Ranges)
            {
                s.SetToolSize(tool, min - 1); T.Eq(min, s.GetToolSize(tool), tool + " below min");
                s.SetToolSize(tool, int.MinValue); T.Eq(min, s.GetToolSize(tool), tool + " int.MinValue");
                s.SetToolSize(tool, max + 1); T.Eq(max, s.GetToolSize(tool), tool + " above max");
                s.SetToolSize(tool, int.MaxValue); T.Eq(max, s.GetToolSize(tool), tool + " int.MaxValue");
                s.SetToolSize(tool, (min + max) / 2); T.Eq((min + max) / 2, s.GetToolSize(tool), tool + " in range");
            }
        }

        // Settings.FilePath is fixed to %APPDATA%\Shadowshot\settings.ini: back up / restore whatever is there.
        static void Guarded(Action body)
        {
            string path = Settings.FilePath, dir = Path.GetDirectoryName(path);
            string errorLog = Path.Combine(dir, "error.log");
            bool dirExisted = Directory.Exists(dir), fileExisted = File.Exists(path), logExisted = File.Exists(errorLog);
            long logLength = logExisted ? new FileInfo(errorLog).Length : 0;
            byte[] backup = fileExisted ? File.ReadAllBytes(path) : null;
            if (fileExisted) File.WriteAllBytes(Path.Combine(T.OutDir, "settings.ini.backup"), backup);
            string newLog = null;
            try
            {
                body();
            }
            finally
            {
                if (fileExisted) File.WriteAllBytes(path, backup);
                else if (File.Exists(path)) File.Delete(path);
                if (File.Exists(path + ".tmp")) File.Delete(path + ".tmp");
                if (File.Exists(errorLog) && (!logExisted || new FileInfo(errorLog).Length != logLength))
                {
                    newLog = File.ReadAllText(errorLog);
                    if (!logExisted) File.Delete(errorLog);
                }
                if (!dirExisted && Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
                T.True(fileExisted == File.Exists(path), "user settings file restored");
            }
            T.True(newLog == null, "Settings wrote to error.log: " + newLog);
        }

        static void RoundTrip()
        {
            var s = new Settings
            {
                RegionHotkey = "Ctrl+Shift+F9",
                FullSaveHotkey = "",
                ClaudeHotkey = "Alt+PrintScreen",
                SaveFolder = @"C:\Temp\Shadow shot\Säve",
                ClaudeFolder = @"D:\Claude Shots",
                LastSaveDir = @"C:\Users\Public\Pictures",
                Format = "bmp",
                JpegQuality = 55,
                ShowNotifications = false,
                CaptureCursor = true,
                KeepSelection = true,
                LastSelection = new Rectangle(-1920, 12, 640, 480),
                ClaudeIncludeNotes = false,
                FirstRunDone = true,
                Color = Color.FromArgb(0x32, 0xAD, 0xE6),
                MarkerColor = Color.FromArgb(0xAF, 0x52, 0xDE),
            };
            int i = 0;
            foreach (var (tool, _, min, max) in Ranges) s.SetToolSize(tool, min + (++i) % (max - min));
            s.Save();
            T.True(File.Exists(Settings.FilePath), "settings.ini written");
            s.Save(); // second save exercises the File.Replace path
            string text = File.ReadAllText(Settings.FilePath, Encoding.UTF8);
            T.True(text.IndexOf('\uFEFF') < 0, "no BOM");
            Settings l = Settings.Load();
            T.Eq(s.RegionHotkey, l.RegionHotkey, "RegionHotkey");
            T.Eq("", l.FullSaveHotkey, "disabled FullSaveHotkey");
            T.Eq(s.ClaudeHotkey, l.ClaudeHotkey, "ClaudeHotkey");
            T.Eq(s.SaveFolder, l.SaveFolder, "SaveFolder (spaces + non-ASCII)");
            T.Eq(s.ClaudeFolder, l.ClaudeFolder, "ClaudeFolder");
            T.Eq(s.LastSaveDir, l.LastSaveDir, "LastSaveDir");
            T.Eq("bmp", l.Format, "Format");
            T.Eq(55, l.JpegQuality, "JpegQuality");
            T.True(!l.ShowNotifications && l.CaptureCursor && l.KeepSelection && !l.ClaudeIncludeNotes && l.FirstRunDone, "bools");
            T.Eq(s.LastSelection, l.LastSelection, "LastSelection");
            T.Eq(s.Color.ToArgb(), l.Color.ToArgb(), "Color");
            T.Eq(s.MarkerColor.ToArgb(), l.MarkerColor.ToArgb(), "MarkerColor");
            foreach (var (tool, _, _, _) in Ranges) T.Eq(s.GetToolSize(tool), l.GetToolSize(tool), "size " + tool);
        }

        static void Corrupt()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Settings.FilePath));
            File.WriteAllText(Settings.FilePath, string.Join("\r\n", new[]
            {
                "; header (file written with a BOM)",
                "garbage line without equals",
                "=no key",
                "RegionHotkey=Ctrl+Banana",
                "FullSaveHotkey=",
                "ClaudeHotkey = alt + prtscr ",
                "SaveFolder=relative\\folder",
                "ClaudeFolder=C:no-root",
                "LastSaveDir=\\\\server\\share\\shots",
                "Format=GIF",
                "JpegQuality=5",
                "ShowNotifications=maybe",
                "CaptureCursor=TRUE",
                "LastSelection=1,2,-3,4",
                "Color=red",
                "MarkerColor=#00ff00",
                "Size.Pen=999",
                "Size.Marker=-4",
                "Size.None=50",
                "Size.Banana=5",
                "Size.Text=abc",
                "UnknownKey=1",
                ";Format=jpg",
            }), new UTF8Encoding(true));
            Settings l = Settings.Load();
            var d = new Settings();
            T.Eq(d.RegionHotkey, l.RegionHotkey, "junk hotkey keeps default");
            T.Eq("", l.FullSaveHotkey, "empty hotkey = disabled");
            T.Eq("Alt+PrintScreen", l.ClaudeHotkey, "tolerant hotkey normalized");
            T.Eq(d.SaveFolder, l.SaveFolder, "relative SaveFolder rejected");
            T.Eq(d.ClaudeFolder, l.ClaudeFolder, "drive-relative ClaudeFolder rejected");
            T.Eq(@"\\server\share\shots", l.LastSaveDir, "UNC LastSaveDir accepted");
            T.Eq("png", l.Format, "unknown format keeps default");
            T.Eq(50, l.JpegQuality, "JpegQuality clamped to 50");
            T.Eq(true, l.ShowNotifications, "unparsable bool keeps default");
            T.Eq(true, l.CaptureCursor, "TRUE parses");
            T.Eq(Rectangle.Empty, l.LastSelection, "negative size rejected");
            T.Eq(d.Color.ToArgb(), l.Color.ToArgb(), "bad color keeps default");
            T.Eq(Color.FromArgb(0, 255, 0).ToArgb(), l.MarkerColor.ToArgb(), "lower-case hex color");
            T.Eq(30, l.GetToolSize(ToolKind.Pen), "Size.Pen clamped");
            T.Eq(6, l.GetToolSize(ToolKind.Marker), "Size.Marker clamped");
            T.Eq(22, l.GetToolSize(ToolKind.Text), "bad Size.Text keeps default");

            File.WriteAllText(Settings.FilePath, "JpegQuality=500\r\nFormat=JPG\r\n");
            l = Settings.Load();
            T.Eq(100, l.JpegQuality, "JpegQuality clamped to 100");
            T.Eq("jpg", l.Format, "Format is case-insensitive");
        }

        static void Missing()
        {
            if (File.Exists(Settings.FilePath)) File.Delete(Settings.FilePath);
            Settings l = Settings.Load();
            T.Eq(new Settings().SaveFolder, l.SaveFolder, "defaults");
            T.Eq(22, l.GetToolSize(ToolKind.Text), "default tool size");
        }
    }
}
