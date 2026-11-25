using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using LuminaDB.Core.Import;

namespace LuminaDB.Core.Sql
{
    /// <summary>
    /// Saves a Notebook result grid as CSV, Excel or JSON, exactly as it was shown: the rows the query returned (up to
    /// the Notebook's row cap), in the server's order, NULL as empty (CSV), an empty cell (Excel) or null (JSON).
    /// </summary>
    public static class ResultExport
    {
        public static string Extension(ExportFormat format)
        {
            switch (format)
            {
                case ExportFormat.Csv: return ".csv";
                case ExportFormat.Excel: return ".xlsx";
                case ExportFormat.Json: return ".json";
                default: throw new ArgumentOutOfRangeException(nameof(format), "Results are saved as CSV, Excel or JSON.");
            }
        }

        /// <summary>Writes the table to <paramref name="path"/> and returns how many rows went in.</summary>
        public static long Write(ResultTable table, ExportFormat format, string path)
        {
            if (table == null) throw new ArgumentNullException(nameof(table));
            Extension(format);   // rejects SQL
            var names = table.Columns.Select(c => c.Name ?? "").ToList();
            var tmp = path + ".part";
            long n;
            try
            {
                switch (format)
                {
                    case ExportFormat.Excel:
                        using (var x = new XlsxWriter(tmp))
                        {
                            x.StartSheet("Result", names);
                            n = 0;
                            foreach (var row in Rows(table, typed: true))
                            {
                                if (n >= DataExporter.ExcelMaxRows) break;
                                x.Row(row);
                                n++;
                            }
                            x.EndSheet();
                        }
                        break;
                    case ExportFormat.Csv:
                        // with a BOM, so Excel opens Sinhala, Tamil and other text correctly
                        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(true)))
                            n = DataExporter.WriteCsv(w, names, Rows(table, typed: false));
                        break;
                    default:
                        using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)))
                            n = DataExporter.WriteJson(w, UniqueNames(names), Rows(table, typed: true));
                        break;
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                throw;
            }
            return n;
        }

        /// <summary>A file name for the result: the table it came from (or "result") and the time.</summary>
        public static string FileName(string title, ExportFormat format, DateTime when)
        {
            var name = DataExporter.Safe(string.IsNullOrWhiteSpace(title) ? "result" : title.Trim());
            if (name.Length > 60) name = name.Substring(0, 60);
            return name + "_" + when.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture) + Extension(format);
        }

        /// <summary>
        /// JSON objects can't repeat a key, but a JOIN often returns two "id" columns: the second becomes "id_2".
        /// </summary>
        public static List<string> UniqueNames(IList<string> names)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var list = new List<string>();
            foreach (var raw in names)
            {
                var name = string.IsNullOrEmpty(raw) ? "column" : raw;
                var candidate = name;
                for (int i = 2; !seen.Add(candidate); i++) candidate = name + "_" + i.ToString(CultureInfo.InvariantCulture);
                list.Add(candidate);
            }
            return list;
        }

        /// <summary>
        /// The grid holds display text. Typed: numbers in number columns go back to numbers (so Excel can sum them and
        /// JSON writes 3 rather than "3"), unless they have more digits than a spreadsheet keeps (then they stay text).
        /// </summary>
        private static IEnumerable<object[]> Rows(ResultTable table, bool typed)
        {
            var cols = table.Columns;
            if (table.Rows == null) yield break;
            foreach (DataRow dr in table.Rows.Rows)
            {
                var v = new object[cols.Count];
                for (int i = 0; i < cols.Count; i++)
                {
                    var cell = dr[cols[i].Key];
                    if (cell is DBNull || cell == null) { v[i] = null; continue; }
                    var s = cell as string ?? Convert.ToString(cell, CultureInfo.InvariantCulture);
                    v[i] = typed && cols[i].IsNumeric ? Number(s) : s;
                }
                yield return v;
            }
        }

        private static object Number(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            int digits = s.Count(char.IsDigit);
            if (digits > 15) return s;   // past what a double (Excel, most JSON readers) keeps exactly
            if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
            if (decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var d)) return d;
            return s;
        }
    }
}
