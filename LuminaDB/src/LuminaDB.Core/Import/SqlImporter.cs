using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LuminaDB.Core.Sql;
using MySqlConnector;

namespace LuminaDB.Core.Import
{
    public sealed class ImportOptions
    {
        public string File { get; set; }
        /// <summary>Run the script in this database (made if missing). Null: the script picks its own (CREATE DATABASE / USE).</summary>
        public string Database { get; set; }
        public ImportTarget Target { get; set; } = ImportTarget.Modern;
        /// <summary>Stop after this many failed commands: by then the file is probably not meant for this server.</summary>
        public int MaxErrors { get; set; } = 100;
    }

    public sealed class ImportProblem
    {
        public int Line { get; set; }
        public string Headline { get; set; }
        public int Code { get; set; }
        public string Message { get; set; }
        /// <summary>Plain English: what it means and what to do.</summary>
        public string Explanation { get; set; }
        public string Text => (Explanation ?? Message);
    }

    public sealed class ImportProgress
    {
        public double Fraction { get; set; }
        public long Done { get; set; }
        public long Total { get; set; }
        public int Statements { get; set; }
        public int Errors { get; set; }
        public int Line { get; set; }
        public string Current { get; set; }
    }

    public sealed class ImportResult
    {
        public SqlSource Source { get; set; }
        public int Statements { get; set; }
        public int Succeeded => Statements - Problems.Count;
        public List<ImportProblem> Problems { get; } = new List<ImportProblem>();
        public List<RewriteNote> Rewrites { get; } = new List<RewriteNote>();
        public List<RewriteNote> Skipped { get; } = new List<RewriteNote>();
        /// <summary>Databases the script made or switched to, in order.</summary>
        public List<string> Databases { get; } = new List<string>();
        public long RowsChanged { get; set; }
        public TimeSpan Duration { get; set; }
        public bool Cancelled { get; set; }
        /// <summary>Why it stopped before the end (too many errors, lost connection), or null.</summary>
        public string StoppedBecause { get; set; }
        public int StoppedAtLine { get; set; }
        public bool Complete => !Cancelled && StoppedBecause == null;
        public bool Clean => Complete && Problems.Count == 0;
    }

    /// <summary>What the first part of a file says about it, before anything runs.</summary>
    public sealed class ImportPreview
    {
        public string File { get; set; }
        public long Bytes { get; set; }
        public SqlSource Source { get; set; }
        public string SourceText => ImportRewriter.Describe(Source);
        /// <summary>CREATE DATABASE / USE names seen in the first part.</summary>
        public List<string> Databases { get; } = new List<string>();
        /// <summary>Databases the script deletes (DROP DATABASE/SCHEMA) in the first part.</summary>
        public List<string> Drops { get; } = new List<string>();
        /// <summary>No CREATE DATABASE or USE seen: the script needs a database chosen for it.</summary>
        public bool NeedsDatabase => Databases.Count == 0;
        /// <summary>Rewrites the first part needs (a sample: the whole file may need more).</summary>
        public List<RewriteNote> Rewrites { get; } = new List<RewriteNote>();
        public int StatementsSeen { get; set; }
        public bool SawWholeFile { get; set; }
        public string FirstLines { get; set; }
    }

    /// <summary>
    /// Runs a .sql (or .sql.gz / .zip) file into the server one command at a time (SPEC §5.6): MySQL 8, Workbench and
    /// phpMyAdmin syntax rewritten on the way (<see cref="ImportRewriter"/>), any file size, progress as it goes, and
    /// past failing commands with a plain explanation for each at the end.
    /// </summary>
    public static class SqlImporter
    {
        private static readonly Regex DbStatement = new Regex(
            @"^\s*(?:(?<create>CREATE\s+(?:DATABASE|SCHEMA)(?:\s+IF\s+NOT\s+EXISTS)?)|(?<use>USE)|(?<drop>DROP\s+(?:DATABASE|SCHEMA)(?:\s+IF\s+EXISTS)?))\s+(?:`(?<n>(?:[^`]|``)+)`|(?<n>[\w$]+))",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static readonly string[] Extensions = { ".sql", ".gz", ".zip" };

        // ------------------------------------------------------------------ preview

        public static ImportPreview Preview(string file, int sampleChars = 512 * 1024)
        {
            if (!File.Exists(file)) throw new LuminaException(ErrorKind.InvalidInput, "There's no file at " + file + ".");
            var p = new ImportPreview { File = file, Bytes = new FileInfo(file).Length };
            using (var open = Open(file))
            {
                var buf = new char[sampleChars];
                int n = 0, r;
                while (n < buf.Length && (r = open.Reader.Read(buf, n, buf.Length - n)) > 0) n += r;
                p.SawWholeFile = open.Reader.Peek() < 0;
                var head = new string(buf, 0, n);
                p.Source = ImportRewriter.Detect(head.Length > 4000 ? head.Substring(0, 4000) : head);
                p.FirstLines = string.Join("\n", head.Split('\n').Take(12)).TrimEnd();

                var splitter = new SqlStreamSplitter(new StringReader(head));
                var notes = new Dictionary<string, RewriteNote>();
                ScriptStatement s;
                while ((s = splitter.Next()) != null)
                {
                    if (!s.Terminated && !p.SawWholeFile) break;   // cut off by the sample
                    p.StatementsSeen++;
                    var m = DbStatement.Match(s.Text);
                    if (m.Success)
                    {
                        var name = m.Groups["n"].Value.Replace("``", "`");
                        if (m.Groups["drop"].Success) AddOnce(p.Drops, name);
                        else AddOnce(p.Databases, name);
                    }
                    var rw = ImportRewriter.Rewrite(s.Text, ImportTarget.Legacy);   // the longer list, so nothing is missed in the preview
                    foreach (var a in rw.Applied) Count(notes, a.Id, a.Text, s.Line, a.Before + "  →  " + a.After);
                }
                p.Rewrites.AddRange(notes.Values);
            }
            return p;
        }

        // ------------------------------------------------------------------ import

        public static async Task<ImportResult> ImportAsync(MariaDbService server, ImportOptions options, IProgress<ImportProgress> progress = null, CancellationToken ct = default)
        {
            if (server == null || server.State != ServerState.Running) throw new LuminaException(ErrorKind.ServerNotRunning, "Start the database server first.");
            if (options?.File == null || !File.Exists(options.File)) throw new LuminaException(ErrorKind.InvalidInput, "The file to import is missing.");

            var result = new ImportResult();
            var notes = new Dictionary<string, RewriteNote>();
            var skips = new Dictionary<string, RewriteNote>();
            var clock = Stopwatch.StartNew();
            var lastReport = TimeSpan.Zero;

            using (var open = Open(options.File))
            using (var conn = await server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(options.Database))
                {
                    var q = SqlLexer.Quote(options.Database.Trim());
                    await ExecAsync(conn, "CREATE DATABASE IF NOT EXISTS " + q + " CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", ct).ConfigureAwait(false);
                    await ExecAsync(conn, "USE " + q, ct).ConfigureAwait(false);
                    AddOnce(result.Databases, options.Database.Trim());
                }

                var splitter = new SqlStreamSplitter(open.Reader);
                bool sourceKnown = false;
                ScriptStatement s;
                while (true)
                {
                    if (ct.IsCancellationRequested) { result.Cancelled = true; break; }
                    s = splitter.Next();
                    if (s == null) break;
                    if (!sourceKnown)
                    {
                        result.Source = ImportRewriter.Detect(open.Head);
                        sourceKnown = true;
                    }

                    var rw = ImportRewriter.Rewrite(s.Text, options.Target);
                    if (rw.Skip != null) { Count(skips, rw.SkipId, rw.Skip, s.Line, Headline(s.Text)); continue; }
                    foreach (var a in rw.Applied) Count(notes, a.Id, a.Text, s.Line, a.Before + "  →  " + a.After);
                    if (string.IsNullOrWhiteSpace(rw.Text)) continue;

                    result.Statements++;
                    var m = DbStatement.Match(rw.Text);
                    try
                    {
                        using (var cmd = new MySqlCommand(rw.Text, conn) { CommandTimeout = 0 })
                        {
                            int changed = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                            if (changed > 0) result.RowsChanged += changed;
                        }
                        if (m.Success && !m.Groups["drop"].Success) AddOnce(result.Databases, m.Groups["n"].Value.Replace("``", "`"));
                    }
                    catch (OperationCanceledException) { result.Cancelled = true; result.StoppedAtLine = s.Line; break; }
                    catch (MySqlException ex)
                    {
                        if (conn.State != System.Data.ConnectionState.Open || IsConnectionLost(ex))
                        {
                            result.StoppedBecause = "The connection to the server was lost: " + ex.Message;
                            result.StoppedAtLine = s.Line;
                            break;
                        }
                        result.Problems.Add(Problem(s, rw.Text, ex));
                        if (result.Problems.Count >= options.MaxErrors)
                        {
                            result.StoppedBecause = $"{options.MaxErrors} commands failed, so the import stopped. This file may not be SQL for MySQL or MariaDB.";
                            result.StoppedAtLine = s.Line;
                            break;
                        }
                    }

                    if (progress != null && clock.Elapsed - lastReport > TimeSpan.FromMilliseconds(150))
                    {
                        lastReport = clock.Elapsed;
                        progress.Report(Progress(open, result, s));
                    }
                }
                progress?.Report(new ImportProgress { Fraction = result.Complete ? 1 : open.Fraction, Done = open.Done, Total = open.Total, Statements = result.Statements, Errors = result.Problems.Count });
            }
            result.Rewrites.AddRange(notes.Values.OrderBy(n => n.FirstLine));
            result.Skipped.AddRange(skips.Values.OrderBy(n => n.FirstLine));
            result.Duration = clock.Elapsed;
            return result;
        }

        private static ImportProgress Progress(Source open, ImportResult r, ScriptStatement s) => new ImportProgress
        {
            Fraction = open.Fraction,
            Done = open.Done,
            Total = open.Total,
            Statements = r.Statements,
            Errors = r.Problems.Count,
            Line = s.Line,
            Current = Headline(s.Text),
        };

        private static ImportProblem Problem(ScriptStatement s, string ran, MySqlException ex)
        {
            int code = (int)ex.ErrorCode;
            string why = ImportRewriter.ExplainUnsupported(ran);
            if (why == null && ran.Length < 200_000)
            {
                try
                {
                    var e = ErrorExplainer.Explain(code, ex.Message, new SqlStatement(ran, 0, ran.Length, ";", true), SchemaSnapshot.Empty, null);
                    if (e != null && !string.IsNullOrEmpty(e.Body)) why = (e.Title != null ? e.Title.TrimEnd('.') + ". " : "") + e.Body;
                }
                catch (Exception) { /* an explanation is a bonus */ }
            }
            return new ImportProblem { Line = s.Line, Headline = Headline(s.Text), Code = code, Message = ex.Message, Explanation = why };
        }

        private static bool IsConnectionLost(MySqlException ex) =>
            ex.ErrorCode == MySqlErrorCode.UnableToConnectToHost || (int)ex.ErrorCode == 2006 || (int)ex.ErrorCode == 2013;

        private static async Task ExecAsync(MySqlConnection c, string sql, CancellationToken ct)
        {
            using (var cmd = new MySqlCommand(sql, c)) await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        /// <summary>First line of a command without leading comments, shortened.</summary>
        public static string Headline(string text, int max = 90)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var t = text.Length > 2000 ? text.Substring(0, 2000) : text;
            t = Regex.Replace(t, @"^(\s*(--[^\n]*\n|#[^\n]*\n|/\*(?![!M])[\s\S]*?\*/))*", "").TrimStart();
            int nl = t.IndexOf('\n');
            var first = (nl >= 0 ? t.Substring(0, nl) : t).Trim();
            if (nl >= 0 || text.Length > t.Length) first += " …";
            return first.Length > max ? first.Substring(0, max - 1).TrimEnd() + "…" : first;
        }

        private static void Count(Dictionary<string, RewriteNote> notes, string id, string text, int line, string example)
        {
            var key = id + "|" + text;
            if (notes.TryGetValue(key, out var n)) { n.Count++; return; }
            notes[key] = new RewriteNote { Id = id, Text = text, Count = 1, FirstLine = line, Example = example };
        }

        private static void AddOnce(List<string> list, string name)
        {
            if (!string.IsNullOrEmpty(name) && !list.Contains(name, StringComparer.OrdinalIgnoreCase)) list.Add(name);
        }

        // ------------------------------------------------------------------ files: .sql, .sql.gz, .zip

        /// <summary>A readable script with a sense of how far through it we are.</summary>
        private sealed class Source : IDisposable
        {
            private readonly FileStream _file;
            private readonly CountingStream _counter;
            private readonly IDisposable _extra;
            private string _head;

            public Source(FileStream file, Stream content, long total, CountingStream counter, IDisposable extra)
            {
                _file = file; _counter = counter; _extra = extra; Total = total;
                Reader = new StreamReader(content, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, bufferSize: 1 << 16);
            }

            public TextReader Reader { get; }
            public long Total { get; }
            /// <summary>Bytes read so far: of the file (.sql, .gz) or of the unpacked entry (.zip).</summary>
            public long Done => _counter != null ? _counter.Count : _file.Position;
            public double Fraction => Total <= 0 ? 0 : Math.Min(1, (double)Done / Total);

            /// <summary>The first 4 KB, read separately (detection must not disturb the import's own reading).</summary>
            public string Head
            {
                get
                {
                    if (_head != null) return _head;
                    try
                    {
                        using (var again = Open(_file.Name)) { var buf = new char[4096]; int n = again.Reader.Read(buf, 0, buf.Length); _head = new string(buf, 0, Math.Max(0, n)); }
                    }
                    catch (IOException) { _head = ""; }
                    return _head;
                }
            }

            public void Dispose()
            {
                Reader.Dispose();
                _extra?.Dispose();
                _file.Dispose();
            }
        }

        private static Source Open(string file)
        {
            var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            try
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".gz")
                {
                    var gz = new GZipStream(fs, CompressionMode.Decompress, leaveOpen: true);
                    return new Source(fs, gz, fs.Length, null, gz);
                }
                if (ext == ".zip")
                {
                    var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
                    var entry = zip.Entries.Where(e => e.FullName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)).OrderByDescending(e => e.Length).FirstOrDefault();
                    if (entry == null) { zip.Dispose(); throw new LuminaException(ErrorKind.InvalidInput, Path.GetFileName(file) + " has no .sql file inside."); }
                    var counter = new CountingStream(entry.Open());
                    return new Source(fs, counter, entry.Length, counter, zip);
                }
                return new Source(fs, fs, fs.Length, null, null);
            }
            catch (InvalidDataException ex)
            {
                fs.Dispose();
                throw new LuminaException(ErrorKind.InvalidInput, Path.GetFileName(file) + " isn't a readable archive: " + ex.Message);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        private sealed class CountingStream : Stream
        {
            private readonly Stream _inner;
            public CountingStream(Stream inner) { _inner = inner; }
            public long Count { get; private set; }
            public override int Read(byte[] buffer, int offset, int count) { int n = _inner.Read(buffer, offset, count); Count += n; return n; }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => Count; set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
        }
    }
}
