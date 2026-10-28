using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Shadowshot.Tests
{
    static class ExporterTests
    {
        static string Folder => Path.Combine(T.OutDir, "export");

        public static void Run()
        {
            T.Run("Exporter.ComposeNoAnnotationsIsExactCrop", ComposePlain);
            T.Run("Exporter.ComposeAnnotationsOnlyChangeTheirBounds", ComposeAnnotated);
            T.Run("Exporter.NewFileNameFormatAndCollisions", NewFileName);
            T.Run("Exporter.SaveAutoPng", () => SaveAuto("png"));
            T.Run("Exporter.SaveAutoJpg", () => SaveAuto("jpg"));
            T.Run("Exporter.SaveAutoBmp", () => SaveAuto("bmp"));
            T.Run("Exporter.SaveAutoUniqueNamesAndFolderCreation", SaveAutoUnique);
            T.Run("Exporter.SaveAutoUnknownFormatFallsBackToPng", SaveAutoUnknown);
            T.Run("Exporter.JpegQualityAffectsSize", JpegQuality);
            T.Run("Exporter.ClaudeNotesSanitizer", Notes);
        }

        static Bitmap Source() => Fx.Busy(new Size(900, 700), new List<Rectangle> { new Rectangle(80, 60, 500, 400) });

        static void ComposePlain()
        {
            using (Bitmap src = Source())
                foreach (var sel in new[] { new Rectangle(13, 17, 321, 211), new Rectangle(0, 0, 900, 700), new Rectangle(890, 690, 10, 10), new Rectangle(5, 5, 1, 1) })
                    using (Bitmap img = Exporter.Compose(src, sel, new Annotation[0]))
                    {
                        T.Eq(sel.Size, img.Size, "size");
                        T.Eq(PixelFormat.Format32bppArgb, img.PixelFormat, "format");
                        Img.Diff d = Img.Compare(img, new Rectangle(Point.Empty, sel.Size), src, sel);
                        T.True(d.Count == 0, "crop " + sel + ": " + d);
                    }
        }

        static void ComposeAnnotated()
        {
            using (Bitmap src = Source())
            {
                var sel = new Rectangle(100, 80, 600, 450);
                var list = new List<Annotation>
                {
                    new ArrowAnnotation { Color = Color.Red, Size = 4, Start = new Point(150, 150), End = new Point(400, 300) },
                    new RectangleAnnotation { Color = Color.Blue, Size = 3, Rect = new Rectangle(600, 400, 200, 200) }, // crosses the selection edge
                    new StepAnnotation { Color = Color.Red, Size = 30, Number = 1, Center = new Point(110, 500), Label = "edge label" },
                    new PixelateAnnotation { Size = 10, Rect = new Rectangle(300, 100, 120, 60) },
                    new TextAnnotation { Color = Color.Yellow, Size = 22, Location = new Point(200, 380), Text = "Hello" },
                };
                using (Bitmap img = Exporter.Compose(src, sel, list))
                {
                    var bounds = list.Select(a => { Rectangle b = a.GetBounds(); b.Offset(-sel.X, -sel.Y); return b; }).ToList();
                    Img.Diff outside = Img.Compare(img, new Rectangle(Point.Empty, sel.Size), src, sel, 0, PixelFormat.Format32bppArgb,
                        (x, y) => bounds.Any(b => b.Contains(x, y)));
                    T.True(outside.Count == 0, "pixels changed outside annotation bounds: " + outside);
                    foreach (Rectangle b in bounds)
                    {
                        Rectangle inside = Rectangle.Intersect(b, new Rectangle(Point.Empty, sel.Size));
                        Rectangle srcRect = inside; srcRect.Offset(sel.Location);
                        T.True(Img.Compare(img, inside, src, srcRect).Count > 0, "annotation at " + b + " visible in the export");
                    }
                    Img.Save(img, "x01_compose_annotated");
                }
                foreach (Annotation a in list) a.Dispose();
            }
        }

        static readonly Regex NamePattern = new Regex(@"^(?<p>[A-Za-z]+)_(?<d>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})(_(?<n>\d+))?\.(?<e>png|jpg|bmp)$");

        static void CheckName(string path, string prefix, string ext)
        {
            T.True(Path.IsPathRooted(path), "absolute path: " + path);
            string name = Path.GetFileName(path);
            Match m = NamePattern.Match(name);
            T.True(m.Success, "name format: " + name);
            T.Eq(prefix, m.Groups["p"].Value, "prefix");
            T.Eq(ext, m.Groups["e"].Value, "extension");
            T.True(name.IndexOf(' ') < 0 && name.All(ch => ch < 128), "no spaces / ASCII: " + name);
            DateTime stamp = DateTime.ParseExact(m.Groups["d"].Value, "yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            T.True(Math.Abs((DateTime.Now - stamp).TotalSeconds) < 10, "timestamp is now: " + stamp);
        }

        static void CleanFolder(string dir)
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        static void NewFileName()
        {
            string dir = Path.Combine(Folder, "names");
            CleanFolder(dir);
            Directory.CreateDirectory(dir);
            string a = Exporter.NewFileName(dir, "Screenshot", "png");
            CheckName(a, "Screenshot", "png");
            T.Eq(Path.GetFullPath(dir), Path.GetDirectoryName(a), "folder");
            File.WriteAllText(a, "x");
            string b = Exporter.NewFileName(dir, "Screenshot", "png");
            if (Path.GetFileNameWithoutExtension(b) + ".png" == Path.GetFileName(a)) throw new TestFailure("collision not avoided");
            // Only deterministic when the clock didn't tick between the calls.
            if (Path.GetFileName(b).StartsWith(Path.GetFileNameWithoutExtension(a)))
                T.Eq(Path.GetFileNameWithoutExtension(a) + "_2.png", Path.GetFileName(b), "second name");
            File.WriteAllText(b, "x");
            string c = Exporter.NewFileName(dir, "Screenshot", "png");
            T.True(!File.Exists(c), "third name is free: " + c);
            string claude = Exporter.NewFileName(dir, "claude", "png");
            CheckName(claude, "claude", "png");
            string relative = Exporter.NewFileName(".", "Screenshot", "jpg");
            T.True(Path.IsPathRooted(relative), "relative folder becomes absolute");
        }

        // Opaque busy background + transparent and half-transparent patches.
        static Bitmap Sample()
        {
            using (Bitmap src = Source())
            {
                Bitmap img = Exporter.Compose(src, new Rectangle(50, 40, 320, 200), new Annotation[0]);
                using (var g = Graphics.FromImage(img))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    using (var clear = new SolidBrush(Color.FromArgb(0, 0, 0, 0)))
                        g.FillRectangle(clear, 10, 10, 40, 30);
                    using (var half = new SolidBrush(Color.FromArgb(128, 0, 0, 255)))
                        g.FillRectangle(half, 60, 10, 40, 30);
                }
                return img;
            }
        }

        static void SaveAuto(string format)
        {
            string dir = Path.Combine(Folder, "auto_" + format);
            CleanFolder(dir);
            using (Bitmap img = Sample())
            {
                string path = Exporter.SaveAuto(img, dir, format, 90);
                CheckName(path, "Screenshot", format);
                T.True(File.Exists(path), "file written");
                using (var loaded = new Bitmap(path))
                {
                    T.Eq(img.Size, loaded.Size, "size");
                    Guid expected = format == "png" ? ImageFormat.Png.Guid : format == "jpg" ? ImageFormat.Jpeg.Guid : ImageFormat.Bmp.Guid;
                    T.Eq(expected, loaded.RawFormat.Guid, "file format");
                    T.Eq(format == "png" ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb, loaded.PixelFormat, "bit depth");
                    if (format == "png")
                    {
                        Img.Diff d = Img.Compare(loaded, new Rectangle(Point.Empty, img.Size), img, new Rectangle(Point.Empty, img.Size));
                        T.True(d.Count == 0, "PNG is lossless incl. alpha: " + d);
                    }
                    else
                    {
                        int tol = format == "bmp" ? 1 : 40;
                        T.True(Img.Near(loaded.GetPixel(30, 25), Color.White, format == "bmp" ? 0 : 6), "transparent flattened on white: " + loaded.GetPixel(30, 25));
                        T.True(Img.Near(loaded.GetPixel(80, 25), Color.FromArgb(127, 127, 255), format == "bmp" ? 1 : 12), "50% blue on white: " + loaded.GetPixel(80, 25));
                        // opaque region
                        var r = new Rectangle(120, 60, 180, 120);
                        if (format == "bmp")
                        {
                            Img.Diff d = Img.Compare(loaded, r, img, r, tol);
                            T.True(d.Count == 0, "BMP opaque pixels exact: " + d);
                        }
                        else
                        {
                            int[] a = Img.Read(loaded, r), b = Img.Read(img, r);
                            double sum = 0;
                            for (int i = 0; i < a.Length; i++) sum += Img.ChannelDiff(a[i] | unchecked((int)0xFF000000), b[i]);
                            T.True(sum / a.Length < 12, "JPEG mean error small: " + (sum / a.Length).ToString("F1"));
                        }
                    }
                }
            }
        }

        static void SaveAutoUnique()
        {
            string dir = Path.Combine(Folder, "unique", "nested", "deeper");
            CleanFolder(Path.Combine(Folder, "unique"));
            using (Bitmap img = Sample())
            {
                var paths = new List<string>();
                for (int i = 0; i < 4; i++) paths.Add(Exporter.SaveAuto(img, dir, "png", 90));
                T.Eq(4, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "distinct paths");
                T.True(paths.All(File.Exists), "all files exist");
                foreach (string p in paths) CheckName(p, "Screenshot", "png");
            }
        }

        static void SaveAutoUnknown()
        {
            string dir = Path.Combine(Folder, "unknown");
            CleanFolder(dir);
            using (Bitmap img = Sample())
            {
                string gif = Exporter.SaveAuto(img, dir, "gif", 90);
                T.True(gif.EndsWith(".png"), "gif -> png: " + gif);
                T.True(Exporter.SaveAuto(img, dir, null, 90).EndsWith(".png"), "null -> png");
                // T-R1-09: the format is case-insensitive
                foreach (var (format, ext, guid) in new[] { ("JPG", ".jpg", ImageFormat.Jpeg.Guid), ("Jpg", ".jpg", ImageFormat.Jpeg.Guid), ("BMP", ".bmp", ImageFormat.Bmp.Guid), ("PNG", ".png", ImageFormat.Png.Guid) })
                {
                    string path = Exporter.SaveAuto(img, dir, format, 90);
                    T.True(path.EndsWith(ext, StringComparison.Ordinal), "SaveAuto(\"" + format + "\") -> " + ext + ": " + path);
                    using (var loaded = new Bitmap(path))
                        T.Eq(guid, loaded.RawFormat.Guid, "SaveAuto(\"" + format + "\") file format");
                }
            }
        }

        static void JpegQuality()
        {
            string dir = Path.Combine(Folder, "quality");
            CleanFolder(dir);
            using (Bitmap img = Sample())
            {
                long low = new FileInfo(Exporter.SaveAuto(img, dir, "jpg", 50)).Length;
                long high = new FileInfo(Exporter.SaveAuto(img, dir, "jpg", 100)).Length;
                T.True(low < high, "q50 (" + low + " B) smaller than q100 (" + high + " B)");
            }
        }

        static string OneLine(string s) => (string)Priv.CallStatic(typeof(Exporter), "OneLine", s);

        // Exporter.CopyForClaude writes the clipboard, so only its private sanitizer is tested here; the
        // "notes + ' ' + path" / "path" composition is inline in CopyForClaude (reviewed, not executed).
        static void Notes()
        {
            T.Eq("", OneLine(null), "null");
            T.Eq("", OneLine(""), "empty");
            T.Eq("", OneLine(" \r\n\t "), "whitespace only");
            T.Eq("a b c d", OneLine("a\r\nb\tc  d"), "CR/LF/TAB collapse");
            T.Eq("say hi", OneLine("say \"hi\""), "quotes stripped");
            T.Eq("lead and trail", OneLine("  lead and trail  \n"), "trimmed");
            T.Eq("x y z", OneLine("x" + (char)0xA0 + "y" + (char)0x2028 + "z"), "unicode whitespace");
            T.Eq("a b", OneLine("a\u0001b"), "control chars");
            T.Eq("Screenshot notes: (1) Click Save; (2) Type name", OneLine("Screenshot notes: (1) Click \"Save\"; (2) Type name"), "typical notes");
            string longText = new string('x', 700);
            T.Eq(600, OneLine(longText).Length, "600 cap");
            string boundary = new string('x', 599) + "\U0001F600" + "tail";
            string cut = OneLine(boundary);
            T.True(cut.Length <= 600 && !char.IsHighSurrogate(cut[cut.Length - 1]), "no split surrogate pair at the cap (len " + cut.Length + ")");
            string spaced = new string('x', 599) + " yyy";
            string s = OneLine(spaced);
            T.True(s.Length <= 600 && !s.EndsWith(" "), "no trailing space after the cap");
            T.True(OneLine("a  \"\"  b") == "a b", "collapse around removed quotes: '" + OneLine("a  \"\"  b") + "'");

            // R4-01: Claude Code splits pasted text at a U+0020 before "/" or "X:\" and drops parts ending in an
            // image extension, so those spaces become U+00A0 and a trailing image extension gets a ';'.
            const string Nbsp = "\u00A0";
            T.Eq("Open" + Nbsp + "/usr/bin/app", OneLine("Open /usr/bin/app"), "space before / becomes NBSP");
            T.Eq("Edit" + Nbsp + "C:\\Temp\\a.txt now", OneLine("Edit C:\\Temp\\a.txt now"), "space before a drive path becomes NBSP");
            T.Eq("Edit" + Nbsp + "d:\\x", OneLine("Edit d:\\x"), "lower-case drive letter");
            T.Eq("a/b and C: drive, x :\\y", OneLine("a/b and C: drive, x :\\y"), "no space before / and no drive path: unchanged");
            T.Eq("(1) see" + Nbsp + "/a;" + Nbsp + "/b", OneLine("(1) see /a; /b"), "every split point");
            T.Eq("x" + Nbsp + "/y", OneLine("x\t/y"), "a tab before / collapses to a space, which becomes NBSP");
            T.True(OneLine("a /b C:\\c").IndexOf(" /", StringComparison.Ordinal) < 0 && !Regex.IsMatch(OneLine("a /b C:\\c"), @" (?=/|[A-Za-z]:\\)"),
                "no U+0020 left before a split point");
            foreach (string ext in new[] { "png", "PNG", "jpg", "Jpeg", "jpeg", "gif", "webp", "WEBP" })
                T.Eq("Replace with icon." + ext + ";", OneLine("Replace with icon." + ext), "trailing ." + ext + " gets ';'");
            T.Eq("Replace with icon.png;", OneLine("Replace with icon.png  "), "trailing whitespace trimmed before the check");
            foreach (string s2 in new[] { "icon.pngx", "icon.png now", "icon.bmp", "icon.svg", "png" })
                T.Eq(s2, OneLine(s2), "no ';' for '" + s2 + "'");
            T.Eq("Screenshot notes: (1) Click Save; (2) Type name", OneLine("Screenshot notes: (1) Click Save; (2) Type name"), "typical notes unchanged");
            string capped = OneLine(new string('x', 596) + ".png" + "tail");
            T.True(capped.Length <= 601, "cap still holds (+1 for the ';'): " + capped.Length);
            T.True(!Regex.IsMatch(capped, @"\.(png|jpe?g|gif|webp)$", RegexOptions.IgnoreCase), "a cut that ends in an image extension still gets the ';'");
        }
    }
}
