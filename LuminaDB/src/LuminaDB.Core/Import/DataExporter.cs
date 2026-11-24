using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LuminaDB.Core.Projects;
using LuminaDB.Core.Sql;
using MySqlConnector;

namespace LuminaDB.Core.Import
{
    public enum ExportFormat { Sql, Csv, Json, Excel }

    /// <summary>"For LuminaDB / MariaDB" keeps the dump as MariaDB writes it; "Compatible" also opens in XAMPP, MySQL 8 and phpMyAdmin.</summary>
    public enum SqlFlavour { MariaDb, Compatible }

    public sealed class ExportRequest
    {
        /// <summary>One or more databases. With <see cref="Tables"/>, exactly one.</summary>
        public List<string> Databases { get; set; } = new List<string>();
        /// <summary>Only these tables of Databases[0]; empty = whole databases.</summary>
        public List<string> Tables { get; set; } = new List<string>();
        public ExportFormat Format { get; set; } = ExportFormat.Sql;
        public SqlFlavour Flavour { get; set; } = SqlFlavour.Compatible;
        /// <summary>Folder to write into (LuminaDB's exports folder by default).</summary>
        public string Folder { get; set; }
    }

    public sealed class ExportResult
    {
        public List<string> Files { get; } = new List<string>();
        public long Rows { get; set; }
        public long Bytes => Files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
        public List<string> Notes { get; } = new List<string>();
    }

    /// <summary>
    /// Export (SPEC §5.6): SQL through mariadb-dump (optionally made XAMPP/MySQL 8 friendly), and CSV, JSON or Excel
    /// read straight from the server. CSV is UTF-8 with a byte-order mark so Excel shows Sinhala and Tamil correctly.
    /// </summary>
    public static class DataExporter
    {
        public const int ExcelMaxRows = 1048575;   // plus the header row

        public static async Task<ExportResult> ExportAsync(MariaDbService server, ExportRequest req, IProgress<string> progress = null, CancellationToken ct = default)
        {
            if (server == null || server.State != ServerState.Running) throw new LuminaException(ErrorKind.ServerNotRunning, "Start the database server first.");
            if (req.Databases.Count == 0) throw new LuminaException(ErrorKind.InvalidInput, "Choose what to export.");
            Directory.CreateDirectory(req.Folder);
            var result = new ExportResult();
            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmm", CultureInfo.InvariantCulture);
            string baseName = req.Tables.Count == 1 ? req.Databases[0] + "." + req.Tables[0]
                : req.Databases.Count == 1 ? req.Databases[0] : "databases";
            baseName = Safe(baseName);

            if (req.Format == ExportFormat.Sql)
            {
                var file = Unique(Path.Combine(req.Folder, baseName + "_" + stamp + ".sql"));
                progress?.Report("Writing " + Path.GetFileName(file) + "…");
                await DatabaseDumper.DumpAsync(server, req.Databases, req.Tables, file, ct).ConfigureAwait(false);
                if (req.Flavour == SqlFlavour.Compatible)
                {
                    progress?.Report("Making it XAMPP and MySQL 8 friendly…");
                    int changed = MakeCompatible(file);
                    if (changed > 0) result.Notes.Add("Changed " + changed + " MariaDB-only line" + (changed == 1 ? "" : "s") + " so XAMPP, MySQL 8 and phpMyAdmin read it.");
                }
                result.Files.Add(file);
                return result;
            }

            // one table at a time from the server
            var tables = new List<(string Db, string Table)>();
            using (var c = await server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
            {
                if (req.Tables.Count > 0) tables.AddRange(req.Tables.Select(t => (req.Databases[0], t)));
                else
                    foreach (var db in req.Databases)
                        using (var cmd = new MySqlCommand("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @d AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME", c))
                        {
                            cmd.Parameters.AddWithValue("@d", db);
                            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                                while (await r.ReadAsync(ct).ConfigureAwait(false)) tables.Add((db, r.GetString(0)));
                        }
            }
            if (tables.Count == 0) throw new LuminaException(ErrorKind.InvalidInput, "There are no tables to export there.");
            bool many = tables.Count > 1;
            string Prefix((string Db, string Table) t) => req.Databases.Count > 1 ? t.Db + "." + t.Table : t.Table;

            if (req.Format == ExportFormat.Excel)
            {
                var file = Unique(Path.Combine(req.Folder, baseName + "_" + stamp + ".xlsx"));
                using (var x = new XlsxWriter(file))
                {
                    foreach (var t in tables)
                    {
                        progress?.Report("Writing " + Prefix(t) + "…");
                        result.Rows += await ReadTableAsync(server, t.Db, t.Table, ct, (cols, rows) =>
                        {
                            x.StartSheet(Prefix(t), cols);
                            long n = 0;
                            foreach (var row in rows)
                            {
                                if (n >= ExcelMaxRows) { result.Notes.Add(Prefix(t) + " has more rows than Excel can hold; the first " + ExcelMaxRows.ToString("N0", CultureInfo.InvariantCulture) + " are in the file. Use CSV for all of them."); break; }
                                x.Row(row);
                                n++;
                            }
                            x.EndSheet();
                            return n;
                        }).ConfigureAwait(false);
                    }
                }
                result.Files.Add(file);
                return result;
            }

            // CSV and JSON: one file per table (in a folder when there are several)
            string folder = req.Folder;
            if (many) { folder = Unique(Path.Combine(req.Folder, baseName + "_" + stamp)); Directory.CreateDirectory(folder); }
            foreach (var t in tables)
            {
                string ext = req.Format == ExportFormat.Csv ? ".csv" : ".json";
                var file = Unique(Path.Combine(folder, Safe(many ? Prefix(t) : baseName) + (many ? "" : "_" + stamp) + ext));
                progress?.Report("Writing " + Path.GetFileName(file) + "…");
                using (var w = new StreamWriter(file, false, new UTF8Encoding(req.Format == ExportFormat.Csv)))
                {
                    result.Rows += await ReadTableAsync(server, t.Db, t.Table, ct, (cols, rows) =>
                        req.Format == ExportFormat.Csv ? WriteCsv(w, cols, rows) : WriteJson(w, cols, rows)).ConfigureAwait(false);
                }
                result.Files.Add(file);
            }
            return result;
        }

        // ------------------------------------------------------------------ reading a table

        private static async Task<long> ReadTableAsync(MariaDbService server, string db, string table, CancellationToken ct,
            Func<List<string>, IEnumerable<object[]>, long> write)
        {
            using (var c = await server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
            using (var cmd = new MySqlCommand("SELECT * FROM " + SqlLexer.Quote(db) + "." + SqlLexer.Quote(table), c) { CommandTimeout = 0 })
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                var cols = Enumerable.Range(0, r.FieldCount).Select(r.GetName).ToList();
                IEnumerable<object[]> Rows()
                {
                    while (r.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        var v = new object[r.FieldCount];
                        for (int i = 0; i < v.Length; i++)
                        {
                            try { v[i] = r.IsDBNull(i) ? null : r.GetValue(i); }
                            catch (Exception ex) when (ex is InvalidCastException || ex is FormatException || ex is OverflowException) { v[i] = r.GetString(i); }
                        }
                        yield return v;
                    }
                }
                return write(cols, Rows());
            }
        }

        // ------------------------------------------------------------------ CSV

        public static long WriteCsv(TextWriter w, IList<string> cols, IEnumerable<object[]> rows)
        {
            w.Write(string.Join(",", cols.Select(CsvField)));
            w.Write("\r\n");
            long n = 0;
            foreach (var row in rows)
            {
                for (int i = 0; i < row.Length; i++)
                {
                    if (i > 0) w.Write(',');
                    if (row[i] != null) w.Write(CsvField(Text(row[i])));
                }
                w.Write("\r\n");
                n++;
            }
            return n;
        }

        private static string CsvField(string s)
        {
            if (s == null) return "";
            bool quote = s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || s.Length > 0 && (char.IsWhiteSpace(s[0]) || char.IsWhiteSpace(s[s.Length - 1]));
            return quote ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
        }

        // ------------------------------------------------------------------ JSON

        public static long WriteJson(TextWriter w, IList<string> cols, IEnumerable<object[]> rows)
        {
            w.Write("[");
            long n = 0;
            foreach (var row in rows)
            {
                w.Write(n == 0 ? "\n  {" : ",\n  {");
                for (int i = 0; i < row.Length; i++)
                {
                    if (i > 0) w.Write(", ");
                    w.Write(JsonString(cols[i]));
                    w.Write(": ");
                    w.Write(JsonValue(row[i]));
                }
                w.Write("}");
                n++;
            }
            w.Write(n == 0 ? "]\n" : "\n]\n");
            return n;
        }

        private static string JsonValue(object v)
        {
            switch (v)
            {
                case null: return "null";
                case bool b: return b ? "true" : "false";
                case sbyte _: case byte _: case short _: case ushort _: case int _: case uint _: case long _: case ulong _:
                    return Convert.ToString(v, CultureInfo.InvariantCulture);
                case decimal d: return d.ToString(CultureInfo.InvariantCulture);
                case double d: return double.IsNaN(d) || double.IsInfinity(d) ? "null" : d.ToString("R", CultureInfo.InvariantCulture);
                case float f: return float.IsNaN(f) || float.IsInfinity(f) ? "null" : f.ToString("R", CultureInfo.InvariantCulture);
                default: return JsonString(Text(v));
            }
        }

        public static string JsonString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ------------------------------------------------------------------ values as text

        /// <summary>How a value is written in CSV, JSON strings and Excel text cells.</summary>
        public static string Text(object v)
        {
            switch (v)
            {
                case null: return null;
                case string s: return s;
                case DateTime d:
                    if (d == DateTime.MinValue) return "0000-00-00";
                    return d.TimeOfDay == TimeSpan.Zero && d.Millisecond == 0 ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                case DateTimeOffset o: return o.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                case TimeSpan t: return (t < TimeSpan.Zero ? "-" : "") + ((int)Math.Abs(t.TotalHours)).ToString("00", CultureInfo.InvariantCulture) + t.ToString(@"\:mm\:ss", CultureInfo.InvariantCulture);
                case byte[] b: return "0x" + BitConverter.ToString(b).Replace("-", "");
                case bool b: return b ? "1" : "0";
                case Guid g: return g.ToString();
                case IFormattable f: return f.ToString(null, CultureInfo.InvariantCulture);
                default: return v.ToString();
            }
        }

        // ------------------------------------------------------------------ SQL for XAMPP / MySQL 8

        private static readonly Regex Uca = new Regex(@"\butf8mb([34])_uca1400_ai_ci\b", RegexOptions.Compiled);

        /// <summary>
        /// Rewrites a MariaDB 11 dump in place so XAMPP (MariaDB 10.4), MySQL 8 and phpMyAdmin import it: the sandbox
        /// line goes and uca1400 collations become utf8mb4_unicode_ci. Returns how many lines changed.
        /// </summary>
        public static int MakeCompatible(string file)
        {
            var tmp = file + ".part";
            int changed = 0;
            using (var r = new StreamReader(file, new UTF8Encoding(false)))
            using (var w = new StreamWriter(tmp, false, new UTF8Encoding(false)) { NewLine = "\n" })
            {
                string line;
                bool first = true;
                while ((line = r.ReadLine()) != null)
                {
                    if (first && line.StartsWith("/*M!999999\\- enable the sandbox mode */", StringComparison.Ordinal)) { changed++; first = false; continue; }
                    first = false;
                    if (line.IndexOf("uca1400", StringComparison.Ordinal) >= 0)
                    {
                        var fixedLine = Uca.Replace(line, m => "utf8mb" + m.Groups[1].Value + "_unicode_ci");
                        if (fixedLine != line) { changed++; line = fixedLine; }
                    }
                    w.WriteLine(line);
                }
            }
            File.Delete(file);
            File.Move(tmp, file);
            return changed;
        }

        // ------------------------------------------------------------------ file names

        public static string Safe(string name)
        {
            var bad = Path.GetInvalidFileNameChars();
            var s = new string((name ?? "export").Select(ch => bad.Contains(ch) ? '_' : ch).ToArray()).Trim('.', ' ');
            return s.Length == 0 ? "export" : s;
        }

        public static string Unique(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path), name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                var p = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(p) && !Directory.Exists(p)) return p;
            }
        }
    }
}
