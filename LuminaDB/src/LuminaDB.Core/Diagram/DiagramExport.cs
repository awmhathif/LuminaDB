using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace LuminaDB.Core.Diagram
{
    /// <summary>Everything a renderer needs: the cards, the relations and a title.</summary>
    public sealed class DiagramScene
    {
        public DiagramScene(string title, IReadOnlyList<DiagramTable> tables)
        {
            Title = title ?? "";
            Tables = tables;
            Relations = DiagramGeometry.Relations(tables);
            var b = DiagramGeometry.Bounds(tables, Relations);
            // room for the title line above the cards
            X = b.X; Y = b.Y - TitleSpace; Width = b.Width; Height = b.Height + TitleSpace;
            Width = Math.Max(Width, 40 + Title.Length * 9);
        }

        public const double TitleSpace = 34;
        public string Title { get; }
        public IReadOnlyList<DiagramTable> Tables { get; }
        public List<DiagramRelation> Relations { get; }
        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }
    }

    /// <summary>The Ledger palette, as hex, for files that leave the app.</summary>
    internal static class Ink
    {
        public const string Paper = "#FFFFFF";
        public const string Card = "#FFFFFF";
        public const string Header = "#F3F1EC";
        public const string Rule = "#D8D4CA";
        public const string Text = "#1F1D1A";
        public const string Muted = "#77736B";
        public const string Line = "#3D5A80";
        public const string Key = "#9A6A00";
        public const string New = "#2E7D4F";
        public const string Changed = "#3D5A80";
        public const string Dropped = "#B3261E";
    }

    /// <summary>SVG export: crisp at any size, opens in a browser, Word and Inkscape.</summary>
    public static class DiagramSvg
    {
        public static string Render(DiagramScene s)
        {
            var sb = new StringBuilder();
            string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
            double ox = -s.X, oy = -s.Y;
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(F(s.Width)).Append("\" height=\"").Append(F(s.Height))
              .Append("\" viewBox=\"0 0 ").Append(F(s.Width)).Append(' ').Append(F(s.Height)).Append("\" font-family=\"Consolas, 'Courier New', monospace\">\n");
            sb.Append("<title>").Append(Esc(s.Title)).Append("</title>\n");
            sb.Append("<rect width=\"100%\" height=\"100%\" fill=\"").Append(Ink.Paper).Append("\"/>\n");
            sb.Append("<text x=\"24\" y=\"26\" font-size=\"15\" font-weight=\"bold\" fill=\"").Append(Ink.Text).Append("\">").Append(Esc(s.Title)).Append("</text>\n");

            sb.Append("<g fill=\"none\" stroke=\"").Append(Ink.Line).Append("\" stroke-width=\"1.4\">\n");
            foreach (var r in s.Relations)
            {
                sb.Append("<g><title>").Append(Esc(r.Label + ": " + r.Meaning)).Append("</title>");
                sb.Append("<polyline points=\"").Append(string.Join(" ", r.Points.Select(p => F(p.X + ox) + "," + F(p.Y + oy)))).Append("\"");
                if (r.ParentOptional) sb.Append(" stroke-dasharray=\"5 3\"");
                sb.Append("/>");
                foreach (var m in r.Marks)
                    sb.Append("<line x1=\"").Append(F(m.A.X + ox)).Append("\" y1=\"").Append(F(m.A.Y + oy)).Append("\" x2=\"").Append(F(m.B.X + ox)).Append("\" y2=\"").Append(F(m.B.Y + oy)).Append("\"/>");
                foreach (var c in r.Circles)
                    sb.Append("<circle cx=\"").Append(F(c.Center.X + ox)).Append("\" cy=\"").Append(F(c.Center.Y + oy)).Append("\" r=\"").Append(F(c.Radius)).Append("\" fill=\"").Append(Ink.Paper).Append("\"/>");
                sb.Append("</g>\n");
            }
            sb.Append("</g>\n");

            foreach (var t in s.Tables)
            {
                double x = t.X + ox, y = t.Y + oy;
                string edge = EdgeColor(t.State);
                sb.Append("<g>\n");
                sb.Append("<rect x=\"").Append(F(x)).Append("\" y=\"").Append(F(y)).Append("\" width=\"").Append(F(t.Width)).Append("\" height=\"").Append(F(t.Height))
                  .Append("\" rx=\"3\" fill=\"").Append(Ink.Card).Append("\" stroke=\"").Append(edge).Append("\" stroke-width=\"").Append(t.State == DiagramTableState.Live ? "1" : "2").Append("\"");
                if (t.State == DiagramTableState.Dropped) sb.Append(" stroke-dasharray=\"6 3\"");
                sb.Append("/>\n");
                sb.Append("<path d=\"M").Append(F(x + 0.5)).Append(',').Append(F(y + DiagramGeometry.HeaderHeight)).Append(" V").Append(F(y + 3)).Append(" Q").Append(F(x + 0.5)).Append(',').Append(F(y + 0.5)).Append(' ').Append(F(x + 3)).Append(',').Append(F(y + 0.5))
                  .Append(" H").Append(F(x + t.Width - 3)).Append(" Q").Append(F(x + t.Width - 0.5)).Append(',').Append(F(y + 0.5)).Append(' ').Append(F(x + t.Width - 0.5)).Append(',').Append(F(y + 3))
                  .Append(" V").Append(F(y + DiagramGeometry.HeaderHeight)).Append(" Z\" fill=\"").Append(Ink.Header).Append("\"/>\n");
                sb.Append("<line x1=\"").Append(F(x)).Append("\" y1=\"").Append(F(y + DiagramGeometry.HeaderHeight)).Append("\" x2=\"").Append(F(x + t.Width)).Append("\" y2=\"").Append(F(y + DiagramGeometry.HeaderHeight)).Append("\" stroke=\"").Append(Ink.Rule).Append("\"/>\n");
                sb.Append("<text x=\"").Append(F(x + DiagramGeometry.SidePad)).Append("\" y=\"").Append(F(y + 20)).Append("\" font-size=\"13\" font-weight=\"bold\" fill=\"").Append(Ink.Text).Append("\">").Append(Esc(Clip(t.Name, (t.Width - 2 * DiagramGeometry.SidePad) / DiagramGeometry.HeaderCharWidth))).Append("</text>\n");
                var rows = DiagramGeometry.Rows(t.Design);
                double nameX = x + DiagramGeometry.SidePad + DiagramGeometry.BadgeWidth;
                double typeRight = x + t.Width - DiagramGeometry.SidePad;
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    double ry = y + DiagramGeometry.HeaderHeight + i * DiagramGeometry.RowHeight + 14;
                    if (row.Badge.Length > 0)
                        sb.Append("<text x=\"").Append(F(x + DiagramGeometry.SidePad)).Append("\" y=\"").Append(F(ry)).Append("\" font-size=\"10\" fill=\"").Append(row.IsPrimaryKey ? Ink.Key : Ink.Line).Append("\">").Append(row.Badge).Append("</text>");
                    sb.Append("<text x=\"").Append(F(nameX)).Append("\" y=\"").Append(F(ry)).Append("\" font-size=\"12\" fill=\"").Append(Ink.Text).Append("\"");
                    if (row.IsPrimaryKey) sb.Append(" font-weight=\"bold\"");
                    if (row.IsPrimaryKey) sb.Append(" text-decoration=\"underline\"");
                    sb.Append('>').Append(Esc(row.Name)).Append("</text>");
                    sb.Append("<text x=\"").Append(F(typeRight)).Append("\" y=\"").Append(F(ry)).Append("\" font-size=\"12\" text-anchor=\"end\" fill=\"").Append(Ink.Muted).Append("\">").Append(Esc(row.Type + (row.NotNull ? "" : " ?"))).Append("</text>\n");
                }
                sb.Append("</g>\n");
            }
            sb.Append("</svg>\n");
            return sb.ToString();
        }

        internal static string EdgeColor(DiagramTableState s) =>
            s == DiagramTableState.New ? Ink.New : s == DiagramTableState.Changed ? Ink.Changed : s == DiagramTableState.Dropped ? Ink.Dropped : Ink.Rule;

        internal static string Clip(string s, double chars)
        {
            int n = Math.Max(3, (int)Math.Floor(chars));
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        private static string Esc(string s) =>
            (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    /// <summary>
    /// PDF export, drawn as vectors with the PDF's built-in Courier fonts (no font files, prints sharp).
    /// One page sized to the diagram, scaled down to fit A3 landscape when it is bigger.
    /// Characters outside Windows-1252 print as "?" — use SVG or PNG for Sinhala/Tamil names.
    /// </summary>
    public static class DiagramPdf
    {
        public static byte[] Render(DiagramScene s)
        {
            const double PxToPt = 0.75;               // 96 dpi → 72 pt
            const double MaxW = 1190.55, MaxH = 841.89; // A3 landscape
            double w = s.Width * PxToPt, h = s.Height * PxToPt;
            double scale = PxToPt * Math.Min(1, Math.Min(MaxW / w, MaxH / h));
            double pw = Math.Max(200, s.Width * scale), ph = Math.Max(120, s.Height * scale);

            var c = new StringBuilder();
            string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            // y grows downward in the scene, upward in PDF
            double X(double x) => (x - s.X) * scale;
            double Y(double y) => ph - (y - s.Y) * scale;
            string Rgb(string hex) => F(Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0) + " " + F(Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0) + " " + F(Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0);
            void Text(string font, double size, double x, double y, string color, string text, bool alignRight = false)
            {
                double fs = size * scale;
                double tx = alignRight ? X(x) - text.Length * 0.6 * fs : X(x);
                c.Append("BT /").Append(font).Append(' ').Append(F(fs)).Append(" Tf ").Append(Rgb(color)).Append(" rg ").Append(F(tx)).Append(' ').Append(F(Y(y))).Append(" Td (").Append(PdfString(text)).Append(") Tj ET\n");
            }

            c.Append("1 1 1 rg 0 0 ").Append(F(pw)).Append(' ').Append(F(ph)).Append(" re f\n");
            Text("F2", 15, s.X + 24, s.Y + 26, Ink.Text, s.Title);

            c.Append(Rgb(Ink.Line)).Append(" RG ").Append(F(1.4 * scale)).Append(" w 1 J 1 j\n");
            foreach (var r in s.Relations)
            {
                c.Append(r.ParentOptional ? "[" + F(5 * scale) + " " + F(3 * scale) + "] 0 d\n" : "[] 0 d\n");
                for (int i = 0; i < r.Points.Count; i++)
                    c.Append(F(X(r.Points[i].X))).Append(' ').Append(F(Y(r.Points[i].Y))).Append(i == 0 ? " m " : " l ");
                c.Append("S\n[] 0 d\n");
                foreach (var m in r.Marks)
                    c.Append(F(X(m.A.X))).Append(' ').Append(F(Y(m.A.Y))).Append(" m ").Append(F(X(m.B.X))).Append(' ').Append(F(Y(m.B.Y))).Append(" l S\n");
                foreach (var cc in r.Circles)
                {
                    c.Append("1 1 1 rg\n");
                    Circle(c, X(cc.Center.X), Y(cc.Center.Y), cc.Radius * scale, F);
                    c.Append("B\n");
                }
            }

            foreach (var t in s.Tables)
            {
                double x = X(t.X), yTop = Y(t.Y), tw = t.Width * scale, th = t.Height * scale, hh = DiagramGeometry.HeaderHeight * scale;
                string edge = DiagramSvg.EdgeColor(t.State);
                c.Append("1 1 1 rg ").Append(Rgb(edge)).Append(" RG ").Append(F((t.State == DiagramTableState.Live ? 1 : 2) * scale)).Append(" w ");
                c.Append(t.State == DiagramTableState.Dropped ? "[" + F(6 * scale) + " " + F(3 * scale) + "] 0 d " : "[] 0 d ");
                c.Append(F(x)).Append(' ').Append(F(yTop - th)).Append(' ').Append(F(tw)).Append(' ').Append(F(th)).Append(" re B\n[] 0 d\n");
                c.Append(Rgb(Ink.Header)).Append(" rg ").Append(F(x + 0.5 * scale)).Append(' ').Append(F(yTop - hh)).Append(' ').Append(F(tw - scale)).Append(' ').Append(F(hh - 0.5 * scale)).Append(" re f\n");
                c.Append(Rgb(Ink.Rule)).Append(" RG ").Append(F(0.8 * scale)).Append(" w ").Append(F(x)).Append(' ').Append(F(yTop - hh)).Append(" m ").Append(F(x + tw)).Append(' ').Append(F(yTop - hh)).Append(" l S\n");
                Text("F2", 13, t.X + DiagramGeometry.SidePad, t.Y + 20, Ink.Text, DiagramSvg.Clip(t.Name, (t.Width - 2 * DiagramGeometry.SidePad) / DiagramGeometry.HeaderCharWidth));
                var rows = DiagramGeometry.Rows(t.Design);
                for (int i = 0; i < rows.Count; i++)
                {
                    var row = rows[i];
                    double ry = t.Y + DiagramGeometry.HeaderHeight + i * DiagramGeometry.RowHeight + 14;
                    if (row.Badge.Length > 0) Text("F1", 10, t.X + DiagramGeometry.SidePad, ry, row.IsPrimaryKey ? Ink.Key : Ink.Line, row.Badge);
                    Text(row.IsPrimaryKey ? "F2" : "F1", 12, t.X + DiagramGeometry.SidePad + DiagramGeometry.BadgeWidth, ry, Ink.Text, row.Name);
                    Text("F1", 12, t.Right - DiagramGeometry.SidePad, ry, Ink.Muted, row.Type + (row.NotNull ? "" : " ?"), alignRight: true);
                }
            }

            return Assemble(c.ToString(), pw, ph, s.Title);
        }

        private static void Circle(StringBuilder c, double cx, double cy, double r, Func<double, string> F)
        {
            const double k = 0.5523;
            c.Append(F(cx + r)).Append(' ').Append(F(cy)).Append(" m ");
            c.Append(F(cx + r)).Append(' ').Append(F(cy + k * r)).Append(' ').Append(F(cx + k * r)).Append(' ').Append(F(cy + r)).Append(' ').Append(F(cx)).Append(' ').Append(F(cy + r)).Append(" c ");
            c.Append(F(cx - k * r)).Append(' ').Append(F(cy + r)).Append(' ').Append(F(cx - r)).Append(' ').Append(F(cy + k * r)).Append(' ').Append(F(cx - r)).Append(' ').Append(F(cy)).Append(" c ");
            c.Append(F(cx - r)).Append(' ').Append(F(cy - k * r)).Append(' ').Append(F(cx - k * r)).Append(' ').Append(F(cy - r)).Append(' ').Append(F(cx)).Append(' ').Append(F(cy - r)).Append(" c ");
            c.Append(F(cx + k * r)).Append(' ').Append(F(cy - r)).Append(' ').Append(F(cx + r)).Append(' ').Append(F(cy - k * r)).Append(' ').Append(F(cx + r)).Append(' ').Append(F(cy)).Append(" c\n");
        }

        private static readonly Encoding Win1252 = Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback);

        /// <summary>A PDF literal string body in Windows-1252, with ( ) \ escaped and anything else non-printable as octal.</summary>
        internal static string PdfString(string s)
        {
            var bytes = Win1252.GetBytes((s ?? "").Replace("…", "..."));
            var sb = new StringBuilder();
            foreach (var b in bytes)
            {
                if (b == '(' || b == ')' || b == '\\') sb.Append('\\').Append((char)b);
                else if (b < 32 || b > 126) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                else sb.Append((char)b);
            }
            return sb.ToString();
        }

        private static byte[] Assemble(string content, double pw, double ph, string title)
        {
            string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
            var stream = Deflate(Encoding.ASCII.GetBytes(content));
            var objects = new List<byte[]>
            {
                Ascii("<< /Type /Catalog /Pages 2 0 R >>"),
                Ascii("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
                Ascii("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 " + F(pw) + " " + F(ph) + "] /Resources << /Font << /F1 5 0 R /F2 6 0 R >> >> /Contents 4 0 R >>"),
                Concat(Ascii("<< /Length " + stream.Length + " /Filter /FlateDecode >>\nstream\n"), stream, Ascii("\nendstream")),
                Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>"),
                Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Courier-Bold /Encoding /WinAnsiEncoding >>"),
                Ascii("<< /Title (" + PdfString(title) + ") /Producer (LuminaDB) >>"),
            };
            using (var ms = new MemoryStream())
            {
                void W(byte[] b) => ms.Write(b, 0, b.Length);
                W(Ascii("%PDF-1.4\n"));
                W(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' });
                var offsets = new List<long>();
                for (int i = 0; i < objects.Count; i++)
                {
                    offsets.Add(ms.Position);
                    W(Ascii((i + 1) + " 0 obj\n"));
                    W(objects[i]);
                    W(Ascii("\nendobj\n"));
                }
                long xref = ms.Position;
                var x = new StringBuilder();
                x.Append("xref\n0 ").Append(objects.Count + 1).Append("\n0000000000 65535 f \n");
                foreach (var o in offsets) x.Append(o.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
                x.Append("trailer\n<< /Size ").Append(objects.Count + 1).Append(" /Root 1 0 R /Info 7 0 R >>\nstartxref\n").Append(xref).Append("\n%%EOF\n");
                W(Ascii(x.ToString()));
                return ms.ToArray();
            }
        }

        /// <summary>zlib stream (header + raw deflate + Adler-32), which is what /FlateDecode expects.</summary>
        internal static byte[] Deflate(byte[] data)
        {
            using (var ms = new MemoryStream())
            {
                ms.WriteByte(0x78); ms.WriteByte(0x9C);
                using (var d = new DeflateStream(ms, CompressionLevel.Optimal, leaveOpen: true)) d.Write(data, 0, data.Length);
                uint a = 1, b = 0;
                foreach (var x in data) { a = (a + x) % 65521; b = (b + a) % 65521; }
                uint adler = (b << 16) | a;
                ms.WriteByte((byte)(adler >> 24)); ms.WriteByte((byte)(adler >> 16)); ms.WriteByte((byte)(adler >> 8)); ms.WriteByte((byte)adler);
                return ms.ToArray();
            }
        }

        private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);
        private static byte[] Concat(params byte[][] parts)
        {
            var r = new byte[parts.Sum(p => p.Length)];
            int o = 0;
            foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }
    }
}
