using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace Shadowshot
{
    public static class Exporter
    {
        static readonly string[] Formats = { "png", "jpg", "bmp" };   // same order as the Save As filter
        const int MaxNotesLength = 600;

        public static Bitmap Compose(Bitmap source, Rectangle selection, IEnumerable<Annotation> annotations)
        {
            var result = new Bitmap(selection.Width, selection.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                DrawExact(g, source, new Rectangle(Point.Empty, selection.Size), selection);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TranslateTransform(-selection.X, -selection.Y);
                foreach (var annotation in annotations) annotation.Draw(g, source);
            }
            return result;
        }

        public static string CopyForClaude(Bitmap image, Settings s, string notes)
        {
            byte[] png = EncodePng(image);
            Directory.CreateDirectory(s.ClaudeFolder);
            string path = NewFileName(s.ClaudeFolder, "claude", "png");
            File.WriteAllBytes(path, png);   // complete and closed before the path reaches the clipboard

            string line = path[1] == ':' ? OneLine(notes) : "";   // Claude Code splits notes off only before a drive path, not UNC
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, false, line.Length == 0 ? path : line + " " + path);
            data.SetData(DataFormats.Bitmap, false, image);
            data.SetData("PNG", false, new MemoryStream(png));
            Clipboard.SetDataObject(data, true, 10, 100);
            return path;
        }

        public static void CopyImage(Bitmap image)
        {
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, false, image);
            data.SetData("PNG", false, new MemoryStream(EncodePng(image)));
            Clipboard.SetDataObject(data, true, 10, 100);
        }

        public static string SaveAs(IWin32Window owner, Bitmap image, Settings s)
        {
            string dir = Directory.Exists(s.LastSaveDir) ? s.LastSaveDir : s.SaveFolder;
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { dir = null; }   // e.g. drive unplugged: let the user pick another folder
            string format = Normalize(s.Format);
            using (var dialog = new SaveFileDialog())
            {
                dialog.Filter = "PNG|*.png|JPEG|*.jpg|BMP|*.bmp";
                dialog.FilterIndex = Array.IndexOf(Formats, format) + 1;
                if (dir != null) dialog.InitialDirectory = dir;
                dialog.FileName = Path.GetFileName(NewFileName(dir ?? Path.GetTempPath(), "Screenshot", format));
                dialog.DefaultExt = format;          // lets the dialog swap the extension when the type changes
                dialog.OverwritePrompt = true;
                dialog.RestoreDirectory = true;      // don't leave the tray process's current directory in the chosen folder
                if (dialog.ShowDialog(owner) != DialogResult.OK) return null;

                string path = dialog.FileName;
                format = FormatOf(path) ?? Formats[dialog.FilterIndex - 1];
                File.WriteAllBytes(path, Encode(image, format, s.JpegQuality));
                s.LastSaveDir = Path.GetDirectoryName(path);
                s.Format = format;
                return path;
            }
        }

        public static string SaveAuto(Bitmap image, string folder, string format, int jpegQuality)
        {
            format = Normalize(format);
            byte[] bytes = Encode(image, format, jpegQuality);
            Directory.CreateDirectory(folder);
            string path = NewFileName(folder, "Screenshot", format);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public static bool Print(IWin32Window owner, Bitmap image)
        {
            using (var document = new PrintDocument { DocumentName = AppInfo.Name })
            using (var dialog = new PrintDialog { Document = document, UseEXDialog = true })
            {
                document.DefaultPageSettings.Landscape = image.Width > image.Height;
                if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                document.PrintPage += (sender, e) =>
                {
                    // MarginBounds is measured from the paper edge; the printer Graphics origin is the printable area's corner.
                    RectangleF m = e.MarginBounds;
                    m.Offset(-e.PageSettings.HardMarginX, -e.PageSettings.HardMarginY);
                    float scale = Math.Min(m.Width / image.Width, m.Height / image.Height);
                    float w = image.Width * scale, h = image.Height * scale;
                    e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    e.Graphics.DrawImage(image, m.X + (m.Width - w) / 2, m.Y + (m.Height - h) / 2, w, h);
                };
                document.PrintController = new StandardPrintController();   // no unowned "Printing" window hidden under the overlay
                document.Print();
                return true;
            }
        }

        public static string NewFileName(string folder, string prefix, string ext)
        {
            string stem = Path.Combine(Path.GetFullPath(folder),
                prefix + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture));
            string path = stem + "." + ext;
            for (int n = 2; File.Exists(path); n++)
                path = stem + "_" + n.ToString(CultureInfo.InvariantCulture) + "." + ext;
            return path;
        }

        static string Normalize(string format)
        {
            format = format?.ToLowerInvariant();
            return Array.IndexOf(Formats, format) >= 0 ? format : "png";
        }

        static string FormatOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "png",
            ".jpg" or ".jpeg" => "jpg",
            ".bmp" => "bmp",
            _ => null,
        };

        static byte[] EncodePng(Bitmap image)
        {
            using (var stream = new MemoryStream())
            {
                image.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        // Encoding to memory first turns GDI+'s opaque "generic error" into real IO exceptions on write.
        static byte[] Encode(Bitmap image, string format, int jpegQuality)
        {
            if (format == "png") return EncodePng(image);
            using (var flat = FlattenOnWhite(image))
            using (var stream = new MemoryStream())
            {
                if (format == "bmp")
                    flat.Save(stream, ImageFormat.Bmp);
                else
                    using (var parameters = new EncoderParameters(1))
                    {
                        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)jpegQuality);
                        flat.Save(stream, Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid), parameters);
                    }
                return stream.ToArray();
            }
        }

        static Bitmap FlattenOnWhite(Bitmap image)
        {
            var flat = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(flat))
            {
                g.Clear(Color.White);
                DrawExact(g, image, new Rectangle(Point.Empty, image.Size), new Rectangle(Point.Empty, image.Size));
            }
            return flat;
        }

        // 1:1 pixel copy: no resampling, no half-pixel shift, no DPI scaling.
        static void DrawExact(Graphics g, Image image, Rectangle dest, Rectangle src)
        {
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(image, dest, src, GraphicsUnit.Pixel);
        }

        // Claude Code takes the pasted text as one line: whitespace/control chars collapse to single spaces,
        // quotes are dropped, length is capped. Claude Code splits pasted text at a space before "/" or "X:\"
        // and drops parts ending in an image extension, so those spaces become no-break spaces and a
        // trailing image extension gets a ';'.
        static string OneLine(string notes)
        {
            if (string.IsNullOrEmpty(notes)) return "";
            var sb = new StringBuilder(notes.Length);
            foreach (char c in notes)
            {
                if (c == '"') continue;
                if (char.IsWhiteSpace(c) || char.IsControl(c))
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] != ' ') sb.Append(' ');
                }
                else sb.Append(c);
            }
            string line = sb.ToString().TrimEnd();
            if (line.Length > MaxNotesLength)
                line = line.Substring(0, char.IsHighSurrogate(line[MaxNotesLength - 1]) ? MaxNotesLength - 1 : MaxNotesLength).TrimEnd();
            line = Regex.Replace(line, @" (?=/|[A-Za-z]:\\)", "\u00A0");
            if (Regex.IsMatch(line, @"\.(png|jpe?g|gif|webp)$", RegexOptions.IgnoreCase)) line += ";";
            return line;
        }
    }
}
