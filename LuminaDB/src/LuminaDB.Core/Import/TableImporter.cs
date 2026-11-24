using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LuminaDB.Core.Sql;
using MySqlConnector;

namespace LuminaDB.Core.Import
{
    public enum DateOrder { DayFirst, MonthFirst }

    /// <summary>One column of a spreadsheet or CSV file, with the type LuminaDB would give it.</summary>
    public sealed class ImportColumn
    {
        /// <summary>Position in the file (0-based).</summary>
        public int Index { get; set; }
        /// <summary>Header text as written in the file.</summary>
        public string Header { get; set; }
        /// <summary>Column name in the table.</summary>
        public string Name { get; set; }
        /// <summary>SQL type for a new table, e.g. INT, DECIMAL(10,2), DATE, VARCHAR(100).</summary>
        public string Type { get; set; }
        public bool Include { get; set; } = true;
        /// <summary>Some values in the sample.</summary>
        public List<string> Examples { get; } = new List<string>();
        public bool HasEmpty { get; set; }
        /// <summary>Every sampled value is there and different from the others.</summary>
        public bool IsUnique { get; set; }
    }

    public sealed class TableFilePreview
    {
        public string File { get; set; }
        public bool IsExcel { get; set; }
        public List<string> Sheets { get; } = new List<string>();
        public string Sheet { get; set; }
        public char Delimiter { get; set; } = ',';
        public string EncodingName { get; set; }
        public bool HasHeader { get; set; } = true;
        public DateOrder DateOrder { get; set; } = DateOrder.DayFirst;
        public List<ImportColumn> Columns { get; } = new List<ImportColumn>();
        /// <summary>First rows exactly as read (header included when there is one).</summary>
        public List<string[]> Rows { get; } = new List<string[]>();
        /// <summary>Data rows read for the preview, and whether that was the whole file.</summary>
        public int SampledRows { get; set; }
        public bool SawWholeFile { get; set; }
        /// <summary>A table name taken from the file name.</summary>
        public string SuggestedTable { get; set; }
    }

    public sealed class TableImportOptions
    {
        public string File { get; set; }
        public string Sheet { get; set; }
        public char Delimiter { get; set; } = ',';
        public bool HasHeader { get; set; } = true;
        public DateOrder DateOrder { get; set; } = DateOrder.DayFirst;
        public string Database { get; set; }
        public string Table { get; set; }
        /// <summary>True: create the table (it must not exist yet). False: add rows to an existing table.</summary>
        public bool CreateTable { get; set; } = true;
        /// <summary>For a new table: add an id INT AUTO_INCREMENT PRIMARY KEY when no column is called id.</summary>
        public bool AddId { get; set; } = true;
        public List<ImportColumn> Columns { get; set; } = new List<ImportColumn>();
        public int BatchSize { get; set; } = 500;
        public int MaxProblems { get; set; } = 100;
    }

    public sealed class TableImportResult
    {
        public string Database { get; set; }
        public string Table { get; set; }
        public bool Created { get; set; }
        public bool AddedId { get; set; }
        public long Rows { get; set; }
        public long Inserted { get; set; }
        public long Failed { get; set; }
        public List<ImportProblem> Problems { get; } = new List<ImportProblem>();
        /// <summary>File columns that matched no column of an existing table.</summary>
        public List<string> Skipped { get; } = new List<string>();
        public bool Cancelled { get; set; }
        public string StoppedBecause { get; set; }
        public TimeSpan Duration { get; set; }
        public bool Clean => Failed == 0 && StoppedBecause == null && !Cancelled;
    }

    /// <summary>
    /// CSV and Excel into a table (SPEC §5.6): previews the file, guesses a type for each column, creates the table
    /// or matches an existing one by column name, and inserts in batches. A batch that fails is retried row by row,
    /// so one bad row costs that row only.
    /// </summary>
    public static class TableImporter
    {
        public const int SampleRows = 2000;
        public static readonly string[] Extensions = { ".csv", ".tsv", ".txt", ".xlsx" };

        public static bool IsExcel(string file) => string.Equals(Path.GetExtension(file), ".xlsx", StringComparison.OrdinalIgnoreCase);

        // ------------------------------------------------------------------ preview

        public static TableFilePreview Preview(string file, string sheet = null, char? delimiter = null, bool? hasHeader = null)
        {
            if (!File.Exists(file)) throw new LuminaException(ErrorKind.InvalidInput, "That file isn't there any more.");
            var p = new TableFilePreview { File = file, IsExcel = IsExcel(file), SuggestedTable = TableName(Path.GetFileNameWithoutExtension(file)) };
            List<string[]> rows;
            if (p.IsExcel)
            {
                try { p.Sheets.AddRange(XlsxReader.SheetNames(file)); }
                catch (Exception ex) when (ex is InvalidDataException || ex is System.Xml.XmlException || ex is IOException)
                {
                    throw new LuminaException(ErrorKind.InvalidInput, "This doesn't look like an Excel .xlsx file (older .xls files need saving as .xlsx first).", ex);
                }
                p.Sheet = sheet != null && p.Sheets.Contains(sheet) ? sheet : p.Sheets.FirstOrDefault();
                rows = XlsxReader.Rows(file, p.Sheet, SampleRows + 2).ToList();
                p.EncodingName = "Excel";
                if (rows.Count <= SampleRows + 1) p.SawWholeFile = true;
            }
            else
            {
                var enc = DetectEncoding(file);
                p.EncodingName = enc.WebName == "utf-8" ? "UTF-8" : enc.EncodingName;
                p.Delimiter = delimiter ?? DetectDelimiter(file, enc);
                rows = new List<string[]>();
                using (var r = new StreamReader(file, enc, true))
                {
                    var csv = new CsvReader(r, p.Delimiter);
                    string[] row;
                    while (rows.Count <= SampleRows + 1 && (row = csv.Next()) != null) rows.Add(row);
                    p.SawWholeFile = csv.Next() == null;
                }
            }
            rows = rows.Where(r => r.Any(v => !string.IsNullOrWhiteSpace(v))).ToList();
            if (rows.Count == 0) throw new LuminaException(ErrorKind.InvalidInput, "The file has no rows in it.");

            p.HasHeader = hasHeader ?? LooksLikeHeader(rows);
            var header = p.HasHeader ? rows[0] : new string[0];
            var data = p.HasHeader ? rows.Skip(1).ToList() : rows;
            if (data.Count > SampleRows) { data = data.Take(SampleRows).ToList(); p.SawWholeFile = false; }
            p.SampledRows = data.Count;
            p.Rows.AddRange(rows.Take(30));

            int width = rows.Max(r => r.Length);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var guesser = new TypeGuesser();
            p.DateOrder = guesser.DateOrderFor(data);
            for (int i = 0; i < width; i++)
            {
                string h = i < header.Length ? header[i]?.Trim() : null;
                var values = data.Select(r => i < r.Length ? r[i] : "").ToList();
                if (string.IsNullOrEmpty(h) && values.All(string.IsNullOrWhiteSpace)) continue;   // an empty trailing column
                var col = new ImportColumn
                {
                    Index = i,
                    Header = string.IsNullOrEmpty(h) ? "column " + (i + 1) : h,
                    Name = UniqueName(ColumnName(h, i), names),
                    Type = guesser.Guess(values, p.DateOrder),
                    HasEmpty = values.Any(string.IsNullOrWhiteSpace),
                };
                col.IsUnique = !col.HasEmpty && values.Count > 0 && values.Select(v => v.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() == values.Count;
                col.Examples.AddRange(values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().Take(3));
                p.Columns.Add(col);
            }
            return p;
        }

        private static bool LooksLikeHeader(List<string[]> rows)
        {
            if (rows.Count < 2) return true;
            var first = rows[0];
            if (first.Any(string.IsNullOrWhiteSpace) && first.All(string.IsNullOrWhiteSpace)) return false;
            // a header row has no numbers or dates in columns where the data has them
            var g = new TypeGuesser();
            for (int i = 0; i < first.Length; i++)
            {
                var v = first[i]?.Trim() ?? "";
                if (v.Length == 0) continue;
                if (g.IsNumber(v) || TypeGuesser.TryDate(v, DateOrder.DayFirst, out _, out _)) return false;
            }
            return true;
        }

        // ------------------------------------------------------------------ import

        public static async Task<TableImportResult> ImportAsync(MariaDbService server, TableImportOptions o, IProgress<ImportProgress> progress = null, CancellationToken ct = default)
        {
            if (server == null || server.State != ServerState.Running) throw new LuminaException(ErrorKind.ServerNotRunning, "Start the database server first.");
            if (string.IsNullOrWhiteSpace(o.Database) || string.IsNullOrWhiteSpace(o.Table)) throw new LuminaException(ErrorKind.InvalidInput, "Choose a database and a table name.");
            var used = o.Columns.Where(c => c.Include).ToList();
            if (used.Count == 0) throw new LuminaException(ErrorKind.InvalidInput, "Tick at least one column to import.");
            var started = DateTime.UtcNow;
            var result = new TableImportResult { Database = o.Database, Table = o.Table };
            string target = SqlLexer.Quote(o.Database) + "." + SqlLexer.Quote(o.Table);

            using (var c = await server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
            {
                await ExecAsync(c, "CREATE DATABASE IF NOT EXISTS " + SqlLexer.Quote(o.Database) + " CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", ct).ConfigureAwait(false);
                bool exists = await TableExistsAsync(c, o.Database, o.Table, ct).ConfigureAwait(false);

                // which file column goes into which table column, and how its text is changed on the way
                var plan = new List<(int Index, string Column, string Kind)>();
                if (o.CreateTable)
                {
                    if (exists) throw new LuminaException(ErrorKind.InvalidInput, "There is already a table called " + o.Table + " in " + o.Database + ". Pick another name, or choose to add the rows to it.");
                    var sql = CreateTableSql(o, out bool addedId);
                    await ExecAsync(c, sql, ct).ConfigureAwait(false);
                    result.Created = true;
                    result.AddedId = addedId;
                    plan.AddRange(used.Select(col => (col.Index, col.Name, KindOf(col.Type))));
                }
                else
                {
                    if (!exists) throw new LuminaException(ErrorKind.InvalidInput, "There is no table called " + o.Table + " in " + o.Database + ".");
                    var existing = await ColumnsAsync(c, o.Database, o.Table, ct).ConfigureAwait(false);
                    foreach (var col in used)
                    {
                        var match = existing.FirstOrDefault(e => string.Equals(e.Name, col.Name, StringComparison.OrdinalIgnoreCase));
                        if (match.Name == null) match = existing.FirstOrDefault(e => Loose(e.Name) == Loose(col.Header) || Loose(e.Name) == Loose(col.Name));
                        if (match.Name == null) { result.Skipped.Add(col.Header); continue; }
                        if (plan.Any(pl => pl.Column == match.Name)) { result.Skipped.Add(col.Header); continue; }
                        plan.Add((col.Index, match.Name, KindOf(match.Type)));
                    }
                    if (plan.Count == 0) throw new LuminaException(ErrorKind.InvalidInput, "None of the file's columns have the same names as the columns of " + o.Table + ".");
                }

                int perBatch = Math.Max(1, Math.Min(o.BatchSize, 60000 / plan.Count));
                string head = "INSERT INTO " + target + " (" + string.Join(", ", plan.Select(pl => SqlLexer.Quote(pl.Column))) + ") VALUES ";
                var batch = new List<(int Line, string[] Values)>();

                using (var src = RowSource.Open(o))
                {
                    foreach (var (line, raw) in src.Rows())
                    {
                        if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
                        if (raw.All(string.IsNullOrWhiteSpace)) continue;
                        result.Rows++;
                        batch.Add((line, plan.Select(pl => Convert(pl.Index < raw.Length ? raw[pl.Index] : null, pl.Kind, o.DateOrder)).ToArray()));
                        if (batch.Count >= perBatch)
                        {
                            if (!await FlushAsync(c, head, plan.Count, batch, result, o, ct).ConfigureAwait(false)) break;
                            progress?.Report(new ImportProgress { Fraction = src.Fraction, Statements = (int)Math.Min(int.MaxValue, result.Rows), Errors = (int)Math.Min(int.MaxValue, result.Failed), Line = line, Done = result.Inserted });
                        }
                    }
                    if (batch.Count > 0 && !result.Cancelled && result.StoppedBecause == null)
                        await FlushAsync(c, head, plan.Count, batch, result, o, ct).ConfigureAwait(false);
                    progress?.Report(new ImportProgress { Fraction = 1, Statements = (int)Math.Min(int.MaxValue, result.Rows), Errors = (int)Math.Min(int.MaxValue, result.Failed), Done = result.Inserted });
                }
            }
            result.Duration = DateTime.UtcNow - started;
            return result;
        }

        /// <summary>Inserts the batch; on failure, row by row. False when the import has to stop.</summary>
        private static async Task<bool> FlushAsync(MySqlConnection c, string head, int width, List<(int Line, string[] Values)> batch,
            TableImportResult result, TableImportOptions o, CancellationToken ct)
        {
            try
            {
                await InsertAsync(c, head, width, batch, ct).ConfigureAwait(false);
                result.Inserted += batch.Count;
                batch.Clear();
                return true;
            }
            catch (MySqlException ex) when (!IsConnectionLost(ex) && batch.Count > 1) { /* find the bad rows */ }
            catch (MySqlException ex) when (IsConnectionLost(ex))
            {
                result.StoppedBecause = "The connection to the server was lost.";
                result.Failed += batch.Count;
                batch.Clear();
                return false;
            }
            catch (MySqlException ex)
            {
                AddProblem(result, o, batch[0].Line, ex);
                batch.Clear();
                return true;
            }

            foreach (var row in batch)
            {
                try
                {
                    await InsertAsync(c, head, width, new List<(int, string[])> { row }, ct).ConfigureAwait(false);
                    result.Inserted++;
                }
                catch (MySqlException ex)
                {
                    if (IsConnectionLost(ex)) { result.StoppedBecause = "The connection to the server was lost."; batch.Clear(); return false; }
                    AddProblem(result, o, row.Line, ex);
                }
            }
            batch.Clear();
            return true;
        }

        private static void AddProblem(TableImportResult r, TableImportOptions o, int line, MySqlException ex)
        {
            r.Failed++;
            if (r.Problems.Count >= o.MaxProblems) return;
            string why = null;
            try
            {
                var e = ErrorExplainer.Explain((int)ex.ErrorCode, ex.Message, null, SchemaSnapshot.Empty, o.Database);
                if (e != null && !string.IsNullOrEmpty(e.Body)) why = (e.Title != null ? e.Title.TrimEnd('.') + ". " : "") + e.Body;
            }
            catch (Exception) { }
            r.Problems.Add(new ImportProblem { Line = line, Headline = "Row on line " + line, Code = (int)ex.ErrorCode, Message = ex.Message, Explanation = why });
        }

        private static async Task InsertAsync(MySqlConnection c, string head, int width, List<(int Line, string[] Values)> rows, CancellationToken ct)
        {
            var sb = new StringBuilder(head, head.Length + rows.Count * width * 6);
            using (var cmd = new MySqlCommand { Connection = c })
            {
                int n = 0;
                for (int r = 0; r < rows.Count; r++)
                {
                    sb.Append(r == 0 ? "(" : ", (");
                    for (int i = 0; i < width; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        var v = rows[r].Values[i];
                        if (v == null) { sb.Append("NULL"); continue; }
                        var name = "@p" + n++;
                        sb.Append(name);
                        cmd.Parameters.AddWithValue(name, v);
                    }
                    sb.Append(')');
                }
                cmd.CommandText = sb.ToString();
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        private static bool IsConnectionLost(MySqlException ex) =>
            ex.ErrorCode == MySqlErrorCode.UnableToConnectToHost || (int)ex.ErrorCode == 2006 || (int)ex.ErrorCode == 2013;

        // ------------------------------------------------------------------ new table

        public static string CreateTableSql(TableImportOptions o, out bool addedId)
        {
            var used = o.Columns.Where(c => c.Include).ToList();
            addedId = o.AddId && !used.Any(c => string.Equals(c.Name, "id", StringComparison.OrdinalIgnoreCase));
            // the file's own id column becomes the key when it is a whole number that is never blank or repeated
            var ownId = addedId ? null : used.FirstOrDefault(c => string.Equals(c.Name, "id", StringComparison.OrdinalIgnoreCase)
                && c.IsUnique && (c.Type == "INT" || c.Type == "BIGINT"));
            var parts = new List<string>();
            if (addedId) parts.Add("  `id` INT NOT NULL AUTO_INCREMENT PRIMARY KEY");
            foreach (var c in used)
                parts.Add("  " + SqlLexer.Quote(c.Name) + " " + (string.IsNullOrWhiteSpace(c.Type) ? "VARCHAR(255)" : c.Type)
                          + (c == ownId ? " NOT NULL PRIMARY KEY" : " NULL"));
            return "CREATE TABLE " + SqlLexer.Quote(o.Database) + "." + SqlLexer.Quote(o.Table) + " (\n" + string.Join(",\n", parts) +
                   "\n) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci";
        }

        private static async Task<bool> TableExistsAsync(MySqlConnection c, string db, string table, CancellationToken ct)
        {
            using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = @d AND TABLE_NAME = @t", c))
            {
                cmd.Parameters.AddWithValue("@d", db);
                cmd.Parameters.AddWithValue("@t", table);
                return System.Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
            }
        }

        private static async Task<List<(string Name, string Type)>> ColumnsAsync(MySqlConnection c, string db, string table, CancellationToken ct)
        {
            var list = new List<(string, string)>();
            using (var cmd = new MySqlCommand("SELECT COLUMN_NAME, DATA_TYPE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @d AND TABLE_NAME = @t AND EXTRA NOT LIKE '%GENERATED%' ORDER BY ORDINAL_POSITION", c))
            {
                cmd.Parameters.AddWithValue("@d", db);
                cmd.Parameters.AddWithValue("@t", table);
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add((r.GetString(0), r.GetString(1)));
            }
            return list;
        }

        private static async Task ExecAsync(MySqlConnection c, string sql, CancellationToken ct)
        {
            using (var cmd = new MySqlCommand(sql, c)) await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ values

        /// <summary>"number", "date", "datetime", "time", "bit" or "text", from a SQL type.</summary>
        public static string KindOf(string sqlType)
        {
            var t = (sqlType ?? "").Trim().ToLowerInvariant();
            int paren = t.IndexOf('(');
            if (paren >= 0) t = t.Substring(0, paren);
            t = t.Replace(" unsigned", "").Trim();
            switch (t)
            {
                case "tinyint": case "smallint": case "mediumint": case "int": case "integer": case "bigint":
                case "decimal": case "numeric": case "float": case "double": case "real":
                    return "number";
                case "date": return "date";
                case "datetime": case "timestamp": return "datetime";
                case "time": return "time";
                case "year": return "number";
                case "bit": case "bool": case "boolean": return "bit";
                default: return "text";
            }
        }

        /// <summary>The text sent to the server for one cell (null = NULL).</summary>
        public static string Convert(string raw, string kind, DateOrder order)
        {
            if (raw == null) return null;
            var v = raw.Trim();
            if (v == "\\N" || kind != "text" && (v.Length == 0 || string.Equals(v, "NULL", StringComparison.OrdinalIgnoreCase))) return null;
            switch (kind)
            {
                case "number":
                    {
                        var n = v.Replace(",", "").Replace(" ", "");
                        if (n.EndsWith("%", StringComparison.Ordinal)) n = n.TrimEnd('%');
                        if (n.StartsWith("(", StringComparison.Ordinal) && n.EndsWith(")", StringComparison.Ordinal)) n = "-" + n.Trim('(', ')');
                        return n;
                    }
                case "date":
                case "datetime":
                    if (TypeGuesser.TryDate(v, order, out var d, out bool hasTime))
                        return kind == "date" || !hasTime ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    return v;   // the server explains what's wrong with it
                case "bit":
                    switch (v.ToLowerInvariant())
                    {
                        case "true": case "yes": case "y": case "t": return "1";
                        case "false": case "no": case "n": case "f": return "0";
                        default: return v;
                    }
                default:
                    return raw.Length > 0 && (raw[raw.Length - 1] == '\r') ? raw.TrimEnd('\r') : raw;
            }
        }

        // ------------------------------------------------------------------ names

        public static string ColumnName(string header, int index)
        {
            var s = (header ?? "").Trim();
            s = Regex.Replace(s, @"[\s\-/\\.]+", "_");
            s = new string(s.Where(ch => SqlLexer.IsWordChar(ch)).ToArray()).Trim('_');
            if (s.Length == 0) s = "column_" + (index + 1);
            if (s.Length > 64) s = s.Substring(0, 64);
            return s.ToLowerInvariant();
        }

        public static string TableName(string fileName)
        {
            var s = ColumnName(Regex.Replace(fileName ?? "", @"[_\s-]*\d{4}-\d{2}-\d{2}.*$", ""), 0);
            return s.StartsWith("column_", StringComparison.Ordinal) ? "imported" : s.ToLowerInvariant();
        }

        private static string UniqueName(string name, HashSet<string> used)
        {
            var s = name;
            for (int i = 2; used.Contains(s); i++) s = (name.Length > 60 ? name.Substring(0, 60) : name) + "_" + i;
            used.Add(s);
            return s;
        }

        private static string Loose(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        // ------------------------------------------------------------------ files

        public static Encoding DetectEncoding(string file)
        {
            var buf = new byte[Math.Min(1 << 20, Math.Max(0, new FileInfo(file).Length))];
            int n;
            using (var fs = File.OpenRead(file)) n = fs.Read(buf, 0, buf.Length);
            if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) return new UTF8Encoding(false);
            if (n >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) return Encoding.Unicode;
            if (n >= 2 && buf[0] == 0xFE && buf[1] == 0xFF) return Encoding.BigEndianUnicode;
            // valid UTF-8 (a cut-off character at the very end of the sample is fine)
            int end = n;
            while (end > 0 && n - end < 4 && (buf[end - 1] & 0xC0) == 0x80) end--;
            if (end > 0 && buf[end - 1] >= 0xC0) end--;
            try
            {
                new UTF8Encoding(false, true).GetString(buf, 0, end);
                return new UTF8Encoding(false);
            }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(1252); }   // Excel's "CSV" on English Windows
        }

        public static char DetectDelimiter(string file, Encoding enc)
        {
            if (string.Equals(Path.GetExtension(file), ".tsv", StringComparison.OrdinalIgnoreCase)) return '\t';
            var lines = new List<string>();
            using (var r = new StreamReader(file, enc, true))
            {
                string l;
                while (lines.Count < 30 && (l = r.ReadLine()) != null) if (l.Trim().Length > 0) lines.Add(l);
            }
            if (lines.Count == 0) return ',';
            char best = ',';
            double bestScore = 0;
            foreach (var d in new[] { ',', ';', '\t', '|' })
            {
                var counts = lines.Select(l => CountOutsideQuotes(l, d)).ToList();
                if (counts[0] == 0) continue;
                int same = counts.Count(n => n == counts[0]);
                double score = counts[0] * ((double)same / counts.Count) + (same == counts.Count ? 1000 : 0);
                if (score > bestScore) { bestScore = score; best = d; }
            }
            return best;
        }

        private static int CountOutsideQuotes(string line, char d)
        {
            int n = 0;
            bool q = false;
            foreach (char c in line)
            {
                if (c == '"') q = !q;
                else if (c == d && !q) n++;
            }
            return n;
        }

        /// <summary>Rows of the file being imported, with their line (CSV) or row (Excel) number.</summary>
        private abstract class RowSource : IDisposable
        {
            public abstract IEnumerable<(int Line, string[] Row)> Rows();
            public abstract double Fraction { get; }
            public virtual void Dispose() { }

            public static RowSource Open(TableImportOptions o) => IsExcel(o.File) ? (RowSource)new ExcelSource(o) : new CsvSource(o);
        }

        private sealed class CsvSource : RowSource
        {
            private readonly FileStream _fs;
            private readonly StreamReader _r;
            private readonly CsvReader _csv;
            private readonly bool _skipHeader;

            public CsvSource(TableImportOptions o)
            {
                var enc = DetectEncoding(o.File);
                _fs = new FileStream(o.File, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
                _r = new StreamReader(_fs, enc, true, 1 << 16);
                _csv = new CsvReader(_r, o.Delimiter);
                _skipHeader = o.HasHeader;
            }

            public override double Fraction => _fs.Length == 0 ? 1 : (double)_fs.Position / _fs.Length;

            public override IEnumerable<(int Line, string[] Row)> Rows()
            {
                bool first = true;
                string[] row;
                while (true)
                {
                    int line = _csv.Line;
                    if ((row = _csv.Next()) == null) yield break;
                    if (first && row.Any(v => !string.IsNullOrWhiteSpace(v)))
                    {
                        first = false;
                        if (_skipHeader) continue;   // the header row
                    }
                    yield return (line, row);
                }
            }

            public override void Dispose() { _r.Dispose(); _fs.Dispose(); }
        }

        private sealed class ExcelSource : RowSource
        {
            private readonly TableImportOptions _o;
            private long _done;
            private readonly long _total;

            public ExcelSource(TableImportOptions o)
            {
                _o = o;
                _total = XlsxReader.RowCount(o.File, o.Sheet);
            }

            public override double Fraction => _total <= 0 ? -1 : Math.Min(1, (double)_done / _total);

            public override IEnumerable<(int Line, string[] Row)> Rows()
            {
                int n = 0;
                bool headerSkipped = !_o.HasHeader;
                foreach (var row in XlsxReader.Rows(_o.File, _o.Sheet))
                {
                    n++;
                    _done = n;
                    if (!headerSkipped)
                    {
                        if (row.All(string.IsNullOrWhiteSpace)) continue;   // blank rows above the header
                        headerSkipped = true;
                        continue;
                    }
                    yield return (n, row);
                }
            }
        }
    }

    /// <summary>RFC 4180 CSV: quoted fields may hold the delimiter, quotes ("") and line breaks.</summary>
    public sealed class CsvReader
    {
        private readonly TextReader _r;
        private readonly char _d;
        private readonly StringBuilder _field = new StringBuilder();

        public CsvReader(TextReader reader, char delimiter) { _r = reader; _d = delimiter; }

        /// <summary>1-based line where the next row starts.</summary>
        public int Line { get; private set; } = 1;

        public string[] Next()
        {
            int c = _r.Read();
            if (c < 0) return null;
            var row = new List<string>();
            _field.Clear();
            bool quoted = false, wasQuoted = false;
            while (true)
            {
                if (c < 0)
                {
                    row.Add(Field(wasQuoted));
                    return row.ToArray();
                }
                char ch = (char)c;
                if (quoted)
                {
                    if (ch == '"')
                    {
                        if (_r.Peek() == '"') { _field.Append('"'); _r.Read(); }
                        else quoted = false;
                    }
                    else
                    {
                        if (ch == '\n') Line++;
                        _field.Append(ch);
                    }
                }
                else if (ch == '"' && _field.ToString().Trim().Length == 0 && !wasQuoted)
                {
                    _field.Clear();
                    quoted = wasQuoted = true;
                }
                else if (ch == _d)
                {
                    row.Add(Field(wasQuoted));
                    _field.Clear();
                    wasQuoted = false;
                }
                else if (ch == '\r' || ch == '\n')
                {
                    if (ch == '\r' && _r.Peek() == '\n') _r.Read();
                    Line++;
                    row.Add(Field(wasQuoted));
                    return row.ToArray();
                }
                else _field.Append(ch);
                c = _r.Read();
            }
        }

        private string Field(bool wasQuoted) => wasQuoted ? _field.ToString() : _field.ToString().TrimEnd((char)13);
    }

    /// <summary>Picks the narrowest SQL type that holds every sampled value.</summary>
    public sealed class TypeGuesser
    {
        private static readonly Regex Int = new Regex(@"^[-+]?(0|[1-9]\d{0,2}(,\d{3})+|[1-9]\d*)$", RegexOptions.Compiled);
        private static readonly Regex Dec = new Regex(@"^[-+]?(\d{1,3}(,\d{3})+|\d+)?\.\d+$|^[-+]?\d+\.$", RegexOptions.Compiled);
        private static readonly Regex Sci = new Regex(@"^[-+]?\d+(\.\d+)?[eE][-+]?\d+$", RegexOptions.Compiled);

        private static readonly string[] IsoFormats =
        {
            "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d", "yyyy.MM.dd",
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-dd HH:mm:ss.fff",
            "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy/MM/dd HH:mm:ss", "yyyy/M/d H:mm",
        };
        private static readonly string[] DayFormats =
        {
            "d/M/yyyy", "d-M-yyyy", "d.M.yyyy", "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss", "d-M-yyyy H:mm", "d-M-yyyy H:mm:ss", "d/M/yyyy h:mm tt", "d/M/yyyy h:mm:ss tt",
            "d MMM yyyy", "d-MMM-yyyy", "d MMMM yyyy", "d-MMM-yy",
        };
        private static readonly string[] MonthFormats =
        {
            "M/d/yyyy", "M-d-yyyy", "M.d.yyyy", "M/d/yyyy H:mm", "M/d/yyyy H:mm:ss", "M-d-yyyy H:mm", "M-d-yyyy H:mm:ss", "M/d/yyyy h:mm tt", "M/d/yyyy h:mm:ss tt",
            "MMM d yyyy", "MMM d, yyyy", "MMMM d, yyyy",
        };

        public bool IsNumber(string v) => Int.IsMatch(v) || Dec.IsMatch(v) || Sci.IsMatch(v);

        /// <summary>Day-first (Sri Lanka, UK) unless a value like 3/25/2024 proves month-first.</summary>
        public DateOrder DateOrderFor(IEnumerable<string[]> rows)
        {
            bool dayProof = false, monthProof = false;
            foreach (var row in rows)
                foreach (var cell in row)
                {
                    var m = Regex.Match(cell ?? "", @"^\s*(\d{1,2})[/\-.](\d{1,2})[/\-.]\d{2,4}");
                    if (!m.Success) continue;
                    int a = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), b = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                    if (a > 12 && b <= 12) dayProof = true;
                    if (b > 12 && a <= 12) monthProof = true;
                }
            return monthProof && !dayProof ? DateOrder.MonthFirst : DateOrder.DayFirst;
        }

        public static bool TryDate(string v, DateOrder order, out DateTime d, out bool hasTime)
        {
            v = (v ?? "").Trim();
            hasTime = false;
            d = default;
            if (v.Length < 6 || v.Length > 30 || !char.IsDigit(v[0]) && !char.IsLetter(v[0])) return false;
            var styles = DateTimeStyles.AllowWhiteSpaces;
            var ci = CultureInfo.InvariantCulture;
            if (DateTime.TryParseExact(v, IsoFormats, ci, styles, out d)
                || DateTime.TryParseExact(v, order == DateOrder.DayFirst ? DayFormats : MonthFormats, ci, styles, out d)
                || DateTime.TryParseExact(v, order == DateOrder.DayFirst ? MonthFormats.Where(f => f.StartsWith("MMM", StringComparison.Ordinal)).ToArray() : DayFormats.Where(f => f.Contains("MMM")).ToArray(), ci, styles, out d))
            {
                hasTime = d.TimeOfDay != TimeSpan.Zero || v.Contains(":");
                return true;
            }
            return false;
        }

        public string Guess(IList<string> values, DateOrder order)
        {
            var vs = values.Select(v => (v ?? "").Trim()).Where(v => v.Length > 0 && !string.Equals(v, "NULL", StringComparison.OrdinalIgnoreCase) && v != "\\N").ToList();
            if (vs.Count == 0) return "VARCHAR(255)";

            // numbers (but not codes with leading zeros, like phone numbers 0771234567)
            bool leadingZero = vs.Any(v => v.Length > 1 && v[0] == '0' && char.IsDigit(v[1]) || v.Length > 2 && (v[0] == '-' || v[0] == '+') && v[1] == '0' && char.IsDigit(v[2]));
            bool plus = vs.Any(v => v[0] == '+');
            if (!leadingZero && !plus && vs.All(v => Int.IsMatch(v)))
            {
                bool fits = vs.All(v => long.TryParse(v.Replace(",", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n) && n >= int.MinValue && n <= int.MaxValue);
                if (fits) return "INT";
                bool fitsLong = vs.All(v => long.TryParse(v.Replace(",", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _));
                if (fitsLong) return "BIGINT";
                return Text(vs);
            }
            if (!leadingZero && !plus && vs.All(v => Int.IsMatch(v) || Dec.IsMatch(v)))
            {
                int whole = 1, scale = 0;
                foreach (var v in vs)
                {
                    var n = v.Replace(",", "").TrimStart('-', '+');
                    int dot = n.IndexOf('.');
                    whole = Math.Max(whole, Math.Max(1, (dot < 0 ? n.Length : dot)));
                    scale = Math.Max(scale, dot < 0 ? 0 : n.Length - dot - 1);
                }
                scale = scale == 0 ? 0 : Math.Min(10, Math.Max(2, scale));
                int precision = whole + 2 + scale;   // room to grow
                if (precision > 38) return "DOUBLE";
                return "DECIMAL(" + Math.Max(precision, 10) + "," + scale + ")";
            }
            if (!leadingZero && !plus && vs.All(v => Int.IsMatch(v) || Dec.IsMatch(v) || Sci.IsMatch(v))) return "DOUBLE";

            // dates
            bool allDates = true, anyTime = false;
            foreach (var v in vs)
            {
                if (!TryDate(v, order, out _, out bool t)) { allDates = false; break; }
                anyTime |= t;
            }
            if (allDates) return anyTime ? "DATETIME" : "DATE";
            if (vs.All(v => Regex.IsMatch(v, @"^\d{1,2}:\d{2}(:\d{2})?$"))) return "TIME";

            return Text(vs);
        }

        private static string Text(List<string> vs)
        {
            int max = vs.Max(v => v.Length);
            if (max > 1000) return "TEXT";
            foreach (var n in new[] { 50, 100, 255, 500, 1000 }) if (max * 3 / 2 <= n || n == 1000) return "VARCHAR(" + n + ")";
            return "TEXT";
        }
    }
}
