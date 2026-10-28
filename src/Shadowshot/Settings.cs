using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace Shadowshot
{
    public sealed class Settings
    {
        static readonly string DefaultSaveFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Pictures", AppInfo.Name);

        public static string FilePath => Path.Combine(AppInfo.DataDir, "settings.ini");

        public string RegionHotkey = "PrintScreen";
        public string FullSaveHotkey = "Shift+PrintScreen";
        public string ClaudeHotkey = "Ctrl+Alt+PrintScreen";
        public string SaveFolder = DefaultSaveFolder;
        public string ClaudeFolder = Path.Combine(DefaultSaveFolder, "Claude");
        public string LastSaveDir = "";
        public string Format = "png";
        public int JpegQuality = 90;
        public bool ShowNotifications = true;
        public bool CaptureCursor = false;
        public bool KeepSelection = false;
        public Rectangle LastSelection;
        public bool ClaudeIncludeNotes = true;
        public bool FirstRunDone = false;
        public Color Color = Color.FromArgb(255, 0, 0);
        public Color MarkerColor = Color.FromArgb(255, 204, 0);   // #FFCC00, a palette swatch

        readonly Dictionary<ToolKind, int> toolSizes = new Dictionary<ToolKind, int>();

        public int GetToolSize(ToolKind t) => toolSizes.TryGetValue(t, out int size) ? size : SizeRange(t).Default;

        public void SetToolSize(ToolKind t, int size)
        {
            var range = SizeRange(t);
            toolSizes[t] = Math.Min(range.Max, Math.Max(range.Min, size));
        }

        static (int Default, int Min, int Max) SizeRange(ToolKind t) => t switch
        {
            ToolKind.Arrow => (4, 2, 30),
            ToolKind.Marker => (18, 6, 60),
            ToolKind.Text => (22, 10, 96),
            ToolKind.Step => (30, 18, 80),
            ToolKind.Pixelate => (10, 4, 40),
            _ => (3, 1, 30),
        };

        public static Settings Load()
        {
            var settings = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return settings;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0 && line[0] != ';') settings.Apply(line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim());
                }
            }
            catch (Exception ex)
            {
                Program.LogError("Loading settings", ex);
            }
            return settings;
        }

        public void Save()
        {
            try
            {
                var ini = new StringBuilder();
                ini.AppendLine("; Shadowshot settings");
                foreach (var pair in Values()) ini.Append(pair.Key).Append('=').AppendLine(pair.Value);

                Directory.CreateDirectory(AppInfo.DataDir);
                string temp = FilePath + ".tmp";
                File.WriteAllText(temp, ini.ToString(), new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(temp, FilePath, null);
                else File.Move(temp, FilePath);
            }
            catch (Exception ex)
            {
                Program.LogError("Saving settings", ex);
            }
        }

        IEnumerable<KeyValuePair<string, string>> Values()
        {
            KeyValuePair<string, string> V(string key, string value) => new KeyValuePair<string, string>(key, value);
            yield return V("RegionHotkey", RegionHotkey);
            yield return V("FullSaveHotkey", FullSaveHotkey);
            yield return V("ClaudeHotkey", ClaudeHotkey);
            yield return V("SaveFolder", SaveFolder);
            yield return V("ClaudeFolder", ClaudeFolder);
            yield return V("LastSaveDir", LastSaveDir);
            yield return V("Format", Format);
            yield return V("JpegQuality", JpegQuality.ToString(CultureInfo.InvariantCulture));
            yield return V("ShowNotifications", FormatBool(ShowNotifications));
            yield return V("CaptureCursor", FormatBool(CaptureCursor));
            yield return V("KeepSelection", FormatBool(KeepSelection));
            yield return V("LastSelection", string.Format(CultureInfo.InvariantCulture, "{0},{1},{2},{3}",
                LastSelection.X, LastSelection.Y, LastSelection.Width, LastSelection.Height));
            yield return V("ClaudeIncludeNotes", FormatBool(ClaudeIncludeNotes));
            yield return V("FirstRunDone", FormatBool(FirstRunDone));
            yield return V("Color", FormatColor(Color));
            yield return V("MarkerColor", FormatColor(MarkerColor));
            foreach (ToolKind t in Enum.GetValues(typeof(ToolKind)))
                if (t != ToolKind.None) yield return V("Size." + t, GetToolSize(t).ToString(CultureInfo.InvariantCulture));
        }

        // Unknown keys and unparsable values are ignored, so the field keeps its default.
        void Apply(string key, string value)
        {
            switch (key)
            {
                case "RegionHotkey": RegionHotkey = ParseHotkey(value, RegionHotkey); break;
                case "FullSaveHotkey": FullSaveHotkey = ParseHotkey(value, FullSaveHotkey); break;
                case "ClaudeHotkey": ClaudeHotkey = ParseHotkey(value, ClaudeHotkey); break;
                case "SaveFolder": if (IsFullPath(value)) SaveFolder = value; break;
                case "ClaudeFolder": if (IsFullPath(value)) ClaudeFolder = value; break;
                case "LastSaveDir": if (value.Length == 0 || IsFullPath(value)) LastSaveDir = value; break;
                case "Format":
                    string format = value.ToLowerInvariant();
                    if (format == "png" || format == "jpg" || format == "bmp") Format = format;
                    break;
                case "JpegQuality": if (TryParseInt(value, out int quality)) JpegQuality = Math.Min(100, Math.Max(50, quality)); break;
                case "ShowNotifications": ShowNotifications = ParseBool(value, ShowNotifications); break;
                case "CaptureCursor": CaptureCursor = ParseBool(value, CaptureCursor); break;
                case "KeepSelection": KeepSelection = ParseBool(value, KeepSelection); break;
                case "ClaudeIncludeNotes": ClaudeIncludeNotes = ParseBool(value, ClaudeIncludeNotes); break;
                case "FirstRunDone": FirstRunDone = ParseBool(value, FirstRunDone); break;
                case "LastSelection": if (TryParseRect(value, out Rectangle rect)) LastSelection = rect; break;
                case "Color": if (TryParseColor(value, out Color color)) Color = color; break;
                case "MarkerColor": if (TryParseColor(value, out Color marker)) MarkerColor = marker; break;
                default:
                    if (key.StartsWith("Size.", StringComparison.Ordinal)
                        && Enum.TryParse(key.Substring(5), out ToolKind tool) && tool != ToolKind.None
                        && Enum.IsDefined(typeof(ToolKind), tool) && TryParseInt(value, out int size))
                        SetToolSize(tool, size);
                    break;
            }
        }

        // "" means disabled; anything that doesn't parse as a hotkey is corrupt and keeps the default.
        static string ParseHotkey(string value, string fallback)
        {
            Hotkey hotkey = Hotkey.Parse(value);
            return value.Length == 0 || !hotkey.IsEmpty ? hotkey.ToString() : fallback;
        }

        internal static bool IsFullPath(string path)
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

        static string FormatBool(bool b) => b ? "true" : "false";

        static bool ParseBool(string s, bool fallback) => bool.TryParse(s, out bool b) ? b : fallback;

        static string FormatColor(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

        static bool TryParseInt(string s, out int value) =>
            int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

        static bool TryParseColor(string s, out Color color)
        {
            color = Color.Empty;
            if (s.Length != 7 || s[0] != '#'
                || !int.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
                return false;
            color = Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
            return true;
        }

        static bool TryParseRect(string s, out Rectangle rect)
        {
            rect = Rectangle.Empty;
            string[] parts = s.Split(',');
            if (parts.Length != 4
                || !TryParseInt(parts[0].Trim(), out int x) || !TryParseInt(parts[1].Trim(), out int y)
                || !TryParseInt(parts[2].Trim(), out int w) || !TryParseInt(parts[3].Trim(), out int h)
                || w < 0 || h < 0)
                return false;
            rect = new Rectangle(x, y, w, h);
            return true;
        }

        // ---- Registry-backed settings (not stored in the ini) ----

        public static bool StartWithWindows
        {
            get
            {
                using (RegistryKey run = Registry.CurrentUser.OpenSubKey(AppInfo.RunKey))
                    return run?.GetValue(AppInfo.Name) != null;
            }
            set
            {
                using (RegistryKey run = Registry.CurrentUser.CreateSubKey(AppInfo.RunKey))
                {
                    // Assembly.Location is the real file path; Application.ExecutablePath turns %xx into other characters.
                    if (value) run.SetValue(AppInfo.Name, "\"" + typeof(Settings).Assembly.Location + "\" --tray");
                    else run.DeleteValue(AppInfo.Name, false);
                }
            }
        }
    }
}
