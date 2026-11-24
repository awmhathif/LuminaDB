using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace LuminaDB.Core.Import
{
    /// <summary>
    /// A small .xlsx writer (Office Open XML, no extra libraries): one sheet per table, a bold header row, numbers as
    /// numbers and everything else as text. Rows are streamed, so big tables don't sit in memory.
    /// </summary>
    public sealed class XlsxWriter : IDisposable
    {
        private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        private readonly FileStream _file;
        private readonly ZipArchive _zip;
        private readonly List<string> _sheets = new List<string>();
        private XmlWriter _sheet;
        private Stream _sheetStream;
        private int _row;

        public XlsxWriter(string path)
        {
            _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite);
            _zip = new ZipArchive(_file, ZipArchiveMode.Create, leaveOpen: false);
        }

        public void StartSheet(string name, IList<string> header)
        {
            if (_sheet != null) EndSheet();
            _sheets.Add(SheetName(name));
            var entry = _zip.CreateEntry("xl/worksheets/sheet" + _sheets.Count + ".xml", CompressionLevel.Optimal);
            _sheetStream = entry.Open();
            _sheet = XmlWriter.Create(_sheetStream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
            _sheet.WriteStartDocument(true);
            _sheet.WriteStartElement("worksheet", Main);
            _sheet.WriteStartElement("sheetViews", Main);
            _sheet.WriteStartElement("sheetView", Main);
            _sheet.WriteAttributeString("workbookViewId", "0");
            _sheet.WriteStartElement("pane", Main);   // header row stays in view
            _sheet.WriteAttributeString("ySplit", "1");
            _sheet.WriteAttributeString("topLeftCell", "A2");
            _sheet.WriteAttributeString("activePane", "bottomLeft");
            _sheet.WriteAttributeString("state", "frozen");
            _sheet.WriteEndElement();
            _sheet.WriteEndElement();
            _sheet.WriteEndElement();
            _sheet.WriteStartElement("sheetData", Main);
            _row = 0;
            WriteRow(header.Cast<object>().ToArray(), bold: true);
        }

        public void Row(object[] values) => WriteRow(values, bold: false);

        private void WriteRow(object[] values, bool bold)
        {
            _row++;
            _sheet.WriteStartElement("row", Main);
            _sheet.WriteAttributeString("r", _row.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < values.Length; i++)
            {
                var v = values[i];
                if (v == null) continue;
                _sheet.WriteStartElement("c", Main);
                _sheet.WriteAttributeString("r", Column(i) + _row.ToString(CultureInfo.InvariantCulture));
                if (bold) _sheet.WriteAttributeString("s", "1");
                string number = Number(v);
                if (number != null && !bold)
                {
                    _sheet.WriteElementString("v", Main, number);
                }
                else
                {
                    _sheet.WriteAttributeString("t", "inlineStr");
                    _sheet.WriteStartElement("is", Main);
                    _sheet.WriteStartElement("t", Main);
                    var text = Clean(DataExporter.Text(v) ?? "");
                    if (text.Length > 32767) text = text.Substring(0, 32767);   // Excel's cell limit
                    if (text.Length > 0 && (char.IsWhiteSpace(text[0]) || char.IsWhiteSpace(text[text.Length - 1])))
                        _sheet.WriteAttributeString("xml", "space", null, "preserve");
                    _sheet.WriteString(text);
                    _sheet.WriteEndElement();
                    _sheet.WriteEndElement();
                }
                _sheet.WriteEndElement();
            }
            _sheet.WriteEndElement();
        }

        public void EndSheet()
        {
            if (_sheet == null) return;
            _sheet.WriteEndElement();   // sheetData
            _sheet.WriteEndElement();   // worksheet
            _sheet.WriteEndDocument();
            _sheet.Dispose();
            _sheetStream.Dispose();
            _sheet = null;
        }

        public void Dispose()
        {
            EndSheet();
            if (_sheets.Count == 0) StartSheetEmpty();
            Write("[Content_Types].xml", ContentTypes());
            Write("_rels/.rels",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
            var wb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<workbook xmlns=\"" + Main + "\" xmlns:r=\"" + RelNs + "\"><sheets>");
            for (int i = 0; i < _sheets.Count; i++)
                wb.Append("<sheet name=\"").Append(Escape(_sheets[i])).Append("\" sheetId=\"").Append(i + 1).Append("\" r:id=\"rId").Append(i + 1).Append("\"/>");
            wb.Append("</sheets></workbook>");
            Write("xl/workbook.xml", wb.ToString());
            var rels = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < _sheets.Count; i++)
                rels.Append("<Relationship Id=\"rId").Append(i + 1).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet").Append(i + 1).Append(".xml\"/>");
            rels.Append("<Relationship Id=\"rId").Append(_sheets.Count + 1).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            rels.Append("</Relationships>");
            Write("xl/_rels/workbook.xml.rels", rels.ToString());
            Write("xl/styles.xml",
                "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<styleSheet xmlns=\"" + Main + "\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
                "</styleSheet>");
            _zip.Dispose();
        }

        private void StartSheetEmpty()
        {
            StartSheet("Sheet1", new string[0]);
            EndSheet();
        }

        private string ContentTypes()
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            for (int i = 0; i < _sheets.Count; i++)
                sb.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i + 1).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            return sb.Append("</Types>").ToString();
        }

        private void Write(string name, string content)
        {
            var e = _zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var s = e.Open())
            using (var w = new StreamWriter(s, new UTF8Encoding(false))) w.Write(content);
        }

        /// <summary>Excel sheet names: 31 characters, none of []:*?/\, unique.</summary>
        private string SheetName(string name)
        {
            var s = new string((name ?? "Sheet").Select(c => "[]:*?/\\".IndexOf(c) >= 0 ? '_' : c).ToArray()).Trim('\'');
            if (s.Length == 0) s = "Sheet";
            if (s.Length > 31) s = s.Substring(0, 31);
            var baseName = s;
            for (int i = 2; _sheets.Contains(s, StringComparer.OrdinalIgnoreCase); i++)
            {
                var suffix = " (" + i + ")";
                s = (baseName.Length + suffix.Length > 31 ? baseName.Substring(0, 31 - suffix.Length) : baseName) + suffix;
            }
            return s;
        }

        public static string Column(int index)
        {
            var sb = new StringBuilder();
            index++;
            while (index > 0) { int m = (index - 1) % 26; sb.Insert(0, (char)('A' + m)); index = (index - 1) / 26; }
            return sb.ToString();
        }

        /// <summary>Numbers Excel can hold exactly stay numbers; big integers (over 15 digits) become text so they aren't rounded.</summary>
        private static string Number(object v)
        {
            switch (v)
            {
                case sbyte _: case byte _: case short _: case ushort _: case int _: case uint _:
                    return Convert.ToString(v, CultureInfo.InvariantCulture);
                case long l: return Math.Abs(l) < 1_000_000_000_000_000L ? l.ToString(CultureInfo.InvariantCulture) : null;
                case ulong u: return u < 1_000_000_000_000_000UL ? u.ToString(CultureInfo.InvariantCulture) : null;
                case decimal d: return d.ToString(CultureInfo.InvariantCulture).TrimStart('-').Replace(".", "").Length <= 15 ? d.ToString(CultureInfo.InvariantCulture) : null;
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? null : d.ToString("R", CultureInfo.InvariantCulture);
                case float f: return float.IsNaN(f) || float.IsInfinity(f) ? null : f.ToString("R", CultureInfo.InvariantCulture);
                default: return null;
            }
        }

        /// <summary>XML can't hold most control characters.</summary>
        private static string Clean(string s)
        {
            if (s.All(c => c >= 0x20 || c == '\t' || c == '\n' || c == '\r')) return s;
            return new string(s.Where(c => c >= 0x20 || c == '\t' || c == '\n' || c == '\r').ToArray());
        }

        private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
    }

    /// <summary>Reads the first (or a named) sheet of an .xlsx as rows of text, with dates as yyyy-MM-dd.</summary>
    public static class XlsxReader
    {
        private const string Main = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

        public static List<string> SheetNames(string path)
        {
            using (var zip = ZipFile.OpenRead(path)) return Sheets(zip).Select(s => s.Name).ToList();
        }

        /// <summary>Rows of the sheet (all cells as text; empty cells ""), up to <paramref name="maxRows"/>.</summary>
        public static IEnumerable<string[]> Rows(string path, string sheet = null, int maxRows = int.MaxValue)
        {
            using (var zip = ZipFile.OpenRead(path))
            {
                var sheets = Sheets(zip);
                if (sheets.Count == 0) yield break;
                var target = sheet == null ? sheets[0] : sheets.FirstOrDefault(s => string.Equals(s.Name, sheet, StringComparison.OrdinalIgnoreCase)) ?? sheets[0];
                var shared = SharedStrings(zip);
                var dateStyles = DateStyles(zip);
                var entry = zip.GetEntry(target.Path);
                if (entry == null) yield break;
                int count = 0;
                using (var s = entry.Open())
                using (var x = XmlReader.Create(s, new XmlReaderSettings { IgnoreWhitespace = false, DtdProcessing = DtdProcessing.Prohibit }))
                {
                    int expectedRow = 1;
                    while (x.Read())
                    {
                        if (x.NodeType != XmlNodeType.Element || x.LocalName != "row") continue;
                        int r = int.TryParse(x.GetAttribute("r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rr) ? rr : expectedRow;
                        while (expectedRow < r && count < maxRows) { yield return new string[0]; expectedRow++; count++; }   // skipped empty rows
                        if (count >= maxRows) yield break;
                        var cells = new List<string>();
                        if (!x.IsEmptyElement)
                        {
                            var rowDepth = x.Depth;
                            while (x.Read() && !(x.NodeType == XmlNodeType.EndElement && x.Depth == rowDepth))
                            {
                                if (x.NodeType != XmlNodeType.Element || x.LocalName != "c") continue;
                                string reference = x.GetAttribute("r"), type = x.GetAttribute("t"), style = x.GetAttribute("s");
                                int col = reference != null ? ColumnIndex(reference) : cells.Count;
                                string value = CellValue(x, type, shared);
                                if (value != null && (type == null || type == "n") && style != null && int.TryParse(style, out var si) && dateStyles.Contains(si)
                                    && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var oa))
                                {
                                    try
                                    {
                                        var d = DateTime.FromOADate(oa);
                                        value = d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                                            : oa < 1 ? d.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                                    }
                                    catch (ArgumentException) { }
                                }
                                while (cells.Count < col) cells.Add("");
                                cells.Add(value ?? "");
                            }
                        }
                        yield return cells.ToArray();
                        expectedRow = r + 1;
                        if (++count >= maxRows) yield break;
                    }
                }
            }
        }

        /// <summary>The sheet's last row number from its dimension ("A1:F120"), or -1 when the file doesn't say.</summary>
        public static long RowCount(string path, string sheet = null)
        {
            try
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    var sheets = Sheets(zip);
                    if (sheets.Count == 0) return -1;
                    var target = sheet == null ? sheets[0] : sheets.FirstOrDefault(s => string.Equals(s.Name, sheet, StringComparison.OrdinalIgnoreCase)) ?? sheets[0];
                    var entry = zip.GetEntry(target.Path);
                    if (entry == null) return -1;
                    using (var s = entry.Open())
                    using (var x = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
                        while (x.Read())
                        {
                            if (x.NodeType != XmlNodeType.Element) continue;
                            if (x.LocalName == "sheetData") return -1;   // no dimension before the data
                            if (x.LocalName != "dimension") continue;
                            var refText = x.GetAttribute("ref") ?? "";
                            var end = refText.Contains(":") ? refText.Substring(refText.IndexOf(':') + 1) : refText;
                            var digits = new string(end.Where(char.IsDigit).ToArray());
                            return long.TryParse(digits, out var n) ? n : -1;
                        }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is XmlException) { }
            return -1;
        }

        private static string CellValue(XmlReader x, string type, List<string> shared)
        {
            if (x.IsEmptyElement) return "";
            int depth = x.Depth;
            string v = null;
            var inline = new StringBuilder();
            while (x.Read() && !(x.NodeType == XmlNodeType.EndElement && x.Depth == depth))
            {
                if (x.NodeType != XmlNodeType.Element) continue;
                if (x.LocalName == "v") v = x.ReadElementContentAsString();
                else if (x.LocalName == "t") inline.Append(x.ReadElementContentAsString());
                if (x.NodeType == XmlNodeType.EndElement && x.Depth == depth) break;
            }
            switch (type)
            {
                case "s": return int.TryParse(v, out var i) && i >= 0 && i < shared.Count ? shared[i] : "";
                case "inlineStr": return inline.ToString();
                case "b": return v == "1" ? "1" : "0";
                default: return v ?? (inline.Length > 0 ? inline.ToString() : "");
            }
        }

        public static int ColumnIndex(string reference)
        {
            int n = 0;
            foreach (char c in reference)
            {
                if (c < 'A' || c > 'Z') break;
                n = n * 26 + (c - 'A' + 1);
            }
            return Math.Max(0, n - 1);
        }

        private sealed class SheetRef { public string Name; public string Path; }

        private static List<SheetRef> Sheets(ZipArchive zip)
        {
            var list = new List<SheetRef>();
            var wb = Load(zip, "xl/workbook.xml");
            var rels = Load(zip, "xl/_rels/workbook.xml.rels");
            if (wb == null) return list;
            var targets = new Dictionary<string, string>();
            if (rels != null)
                foreach (XmlElement r in rels.GetElementsByTagName("Relationship"))
                {
                    var t = r.GetAttribute("Target");
                    t = t.StartsWith("/", StringComparison.Ordinal) ? t.TrimStart('/') : "xl/" + t;
                    targets[r.GetAttribute("Id")] = t;
                }
            foreach (XmlElement s in wb.GetElementsByTagName("sheet", Main))
            {
                var id = s.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                list.Add(new SheetRef { Name = s.GetAttribute("name"), Path = targets.TryGetValue(id, out var p) ? p : "xl/worksheets/sheet" + (list.Count + 1) + ".xml" });
            }
            return list;
        }

        private static List<string> SharedStrings(ZipArchive zip)
        {
            var list = new List<string>();
            var e = zip.GetEntry("xl/sharedStrings.xml");
            if (e == null) return list;
            using (var s = e.Open())
            using (var x = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
            {
                StringBuilder current = null;
                while (x.Read())
                {
                    if (x.NodeType == XmlNodeType.Element && x.LocalName == "si") current = new StringBuilder();
                    else if (x.NodeType == XmlNodeType.Element && x.LocalName == "t" && current != null) current.Append(x.ReadElementContentAsString());
                    else if (x.NodeType == XmlNodeType.Element && x.LocalName == "rPh") x.Skip();   // phonetic hints are not text
                    if (x.NodeType == XmlNodeType.EndElement && x.LocalName == "si" && current != null) { list.Add(current.ToString()); current = null; }
                }
            }
            return list;
        }

        /// <summary>Indexes of cell styles that show a date or time.</summary>
        private static HashSet<int> DateStyles(ZipArchive zip)
        {
            var result = new HashSet<int>();
            var doc = Load(zip, "xl/styles.xml");
            if (doc == null) return result;
            var custom = new Dictionary<int, string>();
            foreach (XmlElement f in doc.GetElementsByTagName("numFmt", Main))
                if (int.TryParse(f.GetAttribute("numFmtId"), out var id)) custom[id] = f.GetAttribute("formatCode");
            var xfs = doc.GetElementsByTagName("cellXfs", Main);
            if (xfs.Count == 0) return result;
            int index = 0;
            foreach (XmlNode n in xfs[0].ChildNodes)
            {
                if (!(n is XmlElement xf) || xf.LocalName != "xf") continue;
                if (int.TryParse(xf.GetAttribute("numFmtId"), out var fmt) && IsDateFormat(fmt, custom)) result.Add(index);
                index++;
            }
            return result;
        }

        private static bool IsDateFormat(int id, Dictionary<int, string> custom)
        {
            if (id >= 14 && id <= 22 || id >= 45 && id <= 47) return true;
            if (!custom.TryGetValue(id, out var code)) return false;
            code = System.Text.RegularExpressions.Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", "");   // quoted text, colours, escapes
            return code.IndexOfAny(new[] { 'y', 'Y', 'd', 'D' }) >= 0 || code.IndexOf("h", StringComparison.OrdinalIgnoreCase) >= 0 && code.IndexOf("m", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static XmlDocument Load(ZipArchive zip, string name)
        {
            var e = zip.GetEntry(name);
            if (e == null) return null;
            var doc = new XmlDocument { XmlResolver = null };
            using (var s = e.Open())
            using (var x = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit })) doc.Load(x);
            return doc;
        }
    }
}
