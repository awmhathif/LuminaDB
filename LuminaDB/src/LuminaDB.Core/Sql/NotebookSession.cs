using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace LuminaDB.Core.Sql
{
    public sealed class ResultColumn
    {
        public string Name { get; set; }
        /// <summary>Server type name: VARCHAR, INT, DATETIME...</summary>
        public string TypeName { get; set; }
        /// <summary>DataTable column holding the values: c0, c1... (names can repeat or contain dots).</summary>
        public string Key { get; set; }
        public bool IsNumeric { get; set; }
        /// <summary>Hidden numeric column used for sorting (null for text columns).</summary>
        public string SortKey { get; set; }
    }

    /// <summary>One result grid. Values are display strings; NULL stays DBNull.</summary>
    public sealed class ResultTable
    {
        public List<ResultColumn> Columns { get; } = new List<ResultColumn>();
        public DataTable Rows { get; set; }
        public int RowCount => Rows?.Rows.Count ?? 0;
        /// <summary>Stopped at the row cap; more rows exist.</summary>
        public bool Truncated { get; set; }
    }

    public sealed class ServerNote
    {
        public string Level { get; set; }
        public int Code { get; set; }
        public string Message { get; set; }
        public override string ToString() => Level + " " + Code + ": " + Message;
    }

    public sealed class StatementResult
    {
        public SqlStatement Statement { get; set; }
        public StatementInfo Info { get; set; }
        public bool Ok { get; set; }
        public bool Cancelled { get; set; }
        public long Affected { get; set; } = -1;
        public long LastInsertId { get; set; }
        public List<ResultTable> Tables { get; } = new List<ResultTable>();
        public List<ServerNote> Warnings { get; } = new List<ServerNote>();
        public TimeSpan Elapsed { get; set; }
        public int ErrorCode { get; set; }
        public string ErrorMessage { get; set; }
        public ErrorExplanation Explanation { get; set; }
        public UndoPlan Undo { get; set; }
        public string DatabaseAfter { get; set; }
        public DateTime Finished { get; set; } = DateTime.Now;

        /// <summary>"4 rows · 3 ms", "1 row added · 2 ms", "Failed".</summary>
        public string Outcome
        {
            get
            {
                if (Cancelled) return "Stopped";
                if (!Ok) return "Failed";
                string time = FormatElapsed(Elapsed);
                if (Tables.Count > 0)
                {
                    var t = Tables[Tables.Count - 1];
                    string rows = t.RowCount.ToString("N0", CultureInfo.InvariantCulture) + (t.Truncated ? "+" : "") + (t.RowCount == 1 ? " row" : " rows");
                    if (Tables.Count > 1) rows = Tables.Count + " results, last has " + rows;
                    return rows + " · " + time;
                }
                if (Affected >= 0 && Info != null && (Info.Verb == "INSERT" || Info.Verb == "REPLACE" || Info.Verb == "UPDATE" || Info.Verb == "DELETE" || Info.Verb == "LOAD"))
                {
                    string verb = Info.Verb == "UPDATE" ? "changed" : Info.Verb == "DELETE" ? "deleted" : "added";
                    return Affected.ToString("N0", CultureInfo.InvariantCulture) + (Affected == 1 ? " row " : " rows ") + verb + " · " + time;
                }
                return "Done · " + time;
            }
        }

        public static string FormatElapsed(TimeSpan t)
        {
            if (t.TotalMilliseconds < 1) return "<1 ms";
            if (t.TotalSeconds < 1) return ((int)t.TotalMilliseconds) + " ms";
            if (t.TotalMinutes < 1) return t.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
            return ((int)t.TotalMinutes) + " min " + t.Seconds + " s";
        }
    }

    /// <summary>
    /// The Notebook's own connection: keeps the chosen database, variables and transactions
    /// between commands (like a mysql console), reconnects after a server restart, and runs
    /// each command with an Undo copy first.
    /// </summary>
    /// <summary>The outcome of a generated script: every statement's result and the one Undo copy for all of them.</summary>
    public sealed class ScriptResult
    {
        public List<StatementResult> Results { get; } = new List<StatementResult>();
        public UndoPlan Undo { get; set; }
        public bool Ok => Results.Count > 0 && Results.All(r => r.Ok);
        public StatementResult Failed => Results.FirstOrDefault(r => !r.Ok);
        public bool Changed => Results.Any(r => r.Ok && r.Info != null && r.Info.ChangesData);
    }

    public sealed class NotebookSession : IDisposable
    {
        /// <summary>Rows kept for a grid at first: plenty to look through, and light on memory.</summary>
        public const int DefaultRowCap = 10_000;
        /// <summary>"Show all": the most a grid ever holds (Export saves whole tables).</summary>
        public const int MaxRowCap = 100_000;
        private const int MaxCellChars = 2000;

        private readonly MariaDbService _server;
        private readonly UndoService _undo;
        private MySqlConnection _conn;
        private MySqlCommand _running;
        private int _serverThreadId;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly List<ServerNote> _notes = new List<ServerNote>();

        public NotebookSession(MariaDbService server, UndoService undo = null)
        {
            _server = server;
            _undo = undo ?? new UndoService(server);
        }

        public string CurrentDatabase { get; private set; }
        public bool IsBusy => _running != null;
        public UndoService Undo => _undo;
        public int RowCap { get; set; } = DefaultRowCap;
        /// <summary>When false, commands run without an Undo copy (faster for big scripts).</summary>
        public bool MakeUndoCopies { get; set; } = true;

        public event Action<string> DatabaseChanged;

        private MySqlConnectionStringBuilder Builder(string db)
        {
            var b = _server.ConnectionString(db);
            b.Pooling = false;
            b.TreatTinyAsBoolean = false;      // show 0/1 like the server does
            b.GuidFormat = MySqlGuidFormat.None; // CHAR(36) stays text
            b.AllowZeroDateTime = true;
            b.ConvertZeroDateTime = false;
            b.AllowUserVariables = true;
            b.Keepalive = 30;
            return b;
        }

        private async Task<MySqlConnection> EnsureOpenAsync(CancellationToken ct)
        {
            if (_conn != null && _conn.State == ConnectionState.Open)
            {
                try { if (await _conn.PingAsync(ct).ConfigureAwait(false)) return _conn; }
                catch (MySqlException) { }
                catch (InvalidOperationException) { }
            }
            _conn?.Dispose();
            _conn = null;
            var c = new MySqlConnection(Builder(null).ConnectionString);
            c.InfoMessage += (s, e) => { lock (_notes) foreach (var err in e.Errors) _notes.Add(new ServerNote { Level = err.Level, Code = (int)err.ErrorCode, Message = err.Message }); };
            await c.OpenAsync(ct).ConfigureAwait(false);
            _serverThreadId = c.ServerThread;
            if (!string.IsNullOrEmpty(CurrentDatabase))
            {
                try { using (var cmd = new MySqlCommand("USE " + SqlLexer.Quote(CurrentDatabase), c)) await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
                catch (MySqlException) { SetDatabase(null); }   // it was dropped meanwhile
            }
            _conn = c;
            return c;
        }

        private void SetDatabase(string db)
        {
            if (string.Equals(db, CurrentDatabase, StringComparison.Ordinal)) return;
            CurrentDatabase = string.IsNullOrEmpty(db) ? null : db;
            DatabaseChanged?.Invoke(CurrentDatabase);
        }

        /// <summary>Switch database (toolbar). Throws a LuminaException with a plain message if it fails.</summary>
        public async Task UseAsync(string db, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (string.IsNullOrEmpty(db)) { SetDatabase(null); _conn?.Dispose(); _conn = null; return; }
                var c = await EnsureOpenAsync(ct).ConfigureAwait(false);
                try
                {
                    using (var cmd = new MySqlCommand("USE " + SqlLexer.Quote(db), c)) await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                catch (MySqlException ex)
                {
                    throw new LuminaException(ErrorKind.InvalidInput, ErrorExplainer.Explain((int)ex.ErrorCode, ex.Message, null, null, null).Title, ex);
                }
                SetDatabase(db);
            }
            finally { _gate.Release(); }
        }

        /// <summary>Exact row count for the safety slip (falls back to null if it takes too long).</summary>
        public async Task<long?> CountRowsAsync(string db, string table, CancellationToken ct = default)
        {
            try
            {
                using (var c = await _server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
                using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM " + SqlLexer.Quote(db) + "." + SqlLexer.Quote(table), c) { CommandTimeout = 5 })
                    return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }
            catch (MySqlException) { return null; }
            catch (TimeoutException) { return null; }
        }

        /// <summary>Asks the server to stop the running command (KILL QUERY). Safe to call any time.</summary>
        public async Task CancelAsync()
        {
            var id = _serverThreadId;
            if (_running == null || id == 0) return;
            try
            {
                using (var c = await _server.OpenConnectionAsync(null).ConfigureAwait(false))
                using (var cmd = new MySqlCommand("KILL QUERY " + id.ToString(CultureInfo.InvariantCulture), c))
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            catch (MySqlException) { }
            catch (LuminaException) { }
        }

        /// <summary>Runs one command. Never throws for SQL errors: they come back explained in the result.</summary>
        public async Task<StatementResult> ExecuteAsync(SqlStatement statement, SchemaSnapshot schema, CancellationToken ct = default) =>
            await ExecuteAsync(statement, schema, MakeUndoCopies, ct).ConfigureAwait(false);

        /// <param name="makeUndo">False when the caller already took one copy for a whole run.</param>
        public async Task<StatementResult> ExecuteAsync(SqlStatement statement, SchemaSnapshot schema, bool makeUndo, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            var result = new StatementResult { Statement = statement };
            var sw = Stopwatch.StartNew();
            try
            {
                var info = StatementInfo.Analyze(statement, CurrentDatabase);
                result.Info = info;
                MySqlConnection c;
                try { c = await EnsureOpenAsync(ct).ConfigureAwait(false); }
                catch (MySqlException ex) { return Fail(result, ex, schema, sw); }

                // Undo copy first (on another connection)
                if (makeUndo && info.ChangesData)
                {
                    try { result.Undo = await _undo.SnapshotAsync(info, ct).ConfigureAwait(false); }
                    catch (MySqlException ex) { result.Undo = new UndoPlan { SkippedReason = "Couldn't make the Undo copy: " + ex.Message }; }
                }

                lock (_notes) _notes.Clear();
                sw.Restart();
                using (var cmd = new MySqlCommand(statement.Text, c) { CommandTimeout = 0 })
                {
                    _running = cmd;
                    try
                    {
                        using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                        {
                            do
                            {
                                if (r.FieldCount == 0) continue;
                                var table = await ReadTableAsync(r, cmd, ct).ConfigureAwait(false);
                                result.Tables.Add(table);
                                if (table.Truncated) break;
                            } while (await r.NextResultAsync(ct).ConfigureAwait(false));
                            result.Affected = r.RecordsAffected;
                        }
                        result.Ok = true;
                        result.LastInsertId = cmd.LastInsertedId;
                    }
                    catch (MySqlException ex)
                    {
                        if (result.Undo?.Batch != null) await _undo.DiscardAsync(result.Undo.Batch.Id).ConfigureAwait(false);
                        result.Undo = null;
                        // a cancelled big result is still a result
                        if (ex.ErrorCode == MySqlErrorCode.QueryInterrupted && result.Tables.Count > 0 && result.Tables.Last().Truncated)
                            result.Ok = true;
                        else
                            return Fail(result, ex, schema, sw);
                    }
                    finally { _running = null; }
                }
                result.Elapsed = sw.Elapsed;
                lock (_notes) result.Warnings.AddRange(_notes.Where(n => !string.Equals(n.Level, "Note", StringComparison.OrdinalIgnoreCase) || n.Code != 1051));
                // the driver doesn't always pass on warnings after a read (SELECT 1/0): ask the server, it keeps them until the next command
                if (result.Warnings.Count == 0 && info.Verb != "SHOW")
                    await ReadWarningsAsync(c, result, ct).ConfigureAwait(false);

                if (info.Verb == "USE" || info.Verb == "DROP" && info.Object == "DATABASE" || info.Verb == "CREATE" && info.Object == "DATABASE" && CurrentDatabase == null)
                    await RefreshDatabaseAsync(c, ct).ConfigureAwait(false);
                result.DatabaseAfter = CurrentDatabase;
                return result;
            }
            finally
            {
                result.Finished = DateTime.Now;
                _gate.Release();
            }
        }

        /// <summary>
        /// Runs a script of generated statements (a table change, a copy, a saved procedure) as one unit:
        /// one Undo copy before the first statement, then each statement in order, stopping at the first error.
        /// If the very first statement fails nothing changed, so the copy is thrown away.
        /// </summary>
        public async Task<ScriptResult> RunScriptAsync(IReadOnlyList<SqlStatement> statements, SchemaSnapshot schema, string summary, CancellationToken ct = default)
        {
            var run = new ScriptResult();
            var infos = statements.Select(s => StatementInfo.Analyze(s, CurrentDatabase)).ToList();
            if (MakeUndoCopies && infos.Any(i => i.ChangesData))
            {
                var text = string.Join("\n", statements.Select(s => s.Text));
                try { run.Undo = await _undo.SnapshotAsync(infos, text.Length > 60000 ? text.Substring(0, 60000) : text, summary, ct).ConfigureAwait(false); }
                catch (MySqlException ex) { run.Undo = new UndoPlan { SkippedReason = "Couldn't make the Undo copy: " + ex.Message }; }
            }
            foreach (var s in statements)
            {
                var r = await ExecuteAsync(s, schema, makeUndo: false, ct).ConfigureAwait(false);
                run.Results.Add(r);
                if (!r.Ok) break;
            }
            if (!run.Ok && run.Results.Count == 1 && run.Undo?.Batch != null)
            {
                try { await _undo.DiscardAsync(run.Undo.Batch.Id).ConfigureAwait(false); }
                catch (MySqlException) { }
                run.Undo = null;
            }
            var firstChange = run.Results.FirstOrDefault(x => x.Info != null && x.Info.ChangesData);
            if (firstChange != null) firstChange.Undo = run.Undo;
            return run;
        }

        /// <summary>The script as statements (splits on ; and honours DELIMITER).</summary>
        public Task<ScriptResult> RunScriptAsync(string script, SchemaSnapshot schema, string summary, CancellationToken ct = default) =>
            RunScriptAsync(SqlSplitter.Split(script), schema, summary, ct);

        private async Task RefreshDatabaseAsync(MySqlConnection c, CancellationToken ct)
        {
            try
            {
                using (var cmd = new MySqlCommand("SELECT DATABASE()", c))
                {
                    var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                    SetDatabase(v == null || v is DBNull ? null : Convert.ToString(v, CultureInfo.InvariantCulture));
                }
            }
            catch (MySqlException) { }
        }

        private static async Task ReadWarningsAsync(MySqlConnection c, StatementResult result, CancellationToken ct)
        {
            try
            {
                using (var cmd = new MySqlCommand("SHOW WARNINGS LIMIT 10", c))
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var n = new ServerNote { Level = r.GetString(0), Code = Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture), Message = r.GetString(2) };
                        if (!string.Equals(n.Level, "Note", StringComparison.OrdinalIgnoreCase) || n.Code != 1051) result.Warnings.Add(n);
                    }
            }
            catch (MySqlException) { }
        }

        private StatementResult Fail(StatementResult result, MySqlException ex, SchemaSnapshot schema, Stopwatch sw)
        {
            result.Ok = false;
            result.Elapsed = sw.Elapsed;
            result.ErrorCode = ex.Number;
            result.ErrorMessage = ex.Message;
            result.Cancelled = ex.ErrorCode == MySqlErrorCode.QueryInterrupted;
            result.Explanation = ErrorExplainer.Explain(ex.Number, ex.Message, result.Statement, schema, CurrentDatabase);
            if (result.Explanation.IsConnectionProblem || _conn == null || _conn.State != ConnectionState.Open)
            {
                _conn?.Dispose();
                _conn = null;
            }
            result.DatabaseAfter = CurrentDatabase;
            return result;
        }

        private async Task<ResultTable> ReadTableAsync(MySqlDataReader r, MySqlCommand cmd, CancellationToken ct)
        {
            var t = new ResultTable { Rows = new DataTable { Locale = CultureInfo.InvariantCulture } };
            int n = r.FieldCount;
            var types = new string[n];
            for (int i = 0; i < n; i++)
            {
                types[i] = SafeTypeName(r, i);
                var col = new ResultColumn { Name = r.GetName(i), TypeName = types[i], Key = "c" + i.ToString(CultureInfo.InvariantCulture), IsNumeric = IsNumericType(types[i]) };
                t.Columns.Add(col);
                t.Rows.Columns.Add(col.Key, typeof(string));
            }
            // numbers also get a hidden numeric twin so sorting puts 9 before 10
            var numeric = Enumerable.Range(0, n).Where(i => t.Columns[i].IsNumeric).ToArray();
            foreach (var i in numeric)
            {
                t.Columns[i].SortKey = "n" + i.ToString(CultureInfo.InvariantCulture);
                t.Rows.Columns.Add(t.Columns[i].SortKey, typeof(double));
            }
            t.Rows.BeginLoadData();
            var values = new object[n + numeric.Length];
            int count = 0;
            while (await r.ReadAsync(ct).ConfigureAwait(false))
            {
                if (count >= RowCap)
                {
                    t.Truncated = true;
                    try { cmd.Cancel(); } catch (InvalidOperationException) { }
                    break;
                }
                for (int i = 0; i < n; i++)
                {
                    object v;
                    try { v = r.GetValue(i); }
                    catch (InvalidCastException) { v = r.IsDBNull(i) ? (object)DBNull.Value : r.GetString(i); }
                    catch (FormatException) { v = r.IsDBNull(i) ? (object)DBNull.Value : "(unreadable)"; }
                    values[i] = v is DBNull ? (object)DBNull.Value : Display(v, types[i]);
                }
                for (int k = 0; k < numeric.Length; k++)
                {
                    var s = values[numeric[k]] as string;
                    values[n + k] = s != null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (object)d : DBNull.Value;
                }
                t.Rows.LoadDataRow(values, true);
                count++;
            }
            t.Rows.EndLoadData();
            return t;
        }

        private static string SafeTypeName(MySqlDataReader r, int i)
        {
            try { return r.GetDataTypeName(i); } catch (Exception) { return ""; }
        }

        public static bool IsNumericType(string t)
        {
            t = (t ?? "").ToUpperInvariant();
            return t.Contains("INT") || t.Contains("DECIMAL") || t.Contains("FLOAT") || t.Contains("DOUBLE") || t == "BIT" || t == "YEAR" || t.Contains("NUMERIC");
        }

        /// <summary>Formats a value the way the server would print it.</summary>
        public static string Display(object v, string typeName) => Display(v, typeName, MaxCellChars);

        /// <summary>Formats a value the way the server would print it, cutting text longer than <paramref name="maxChars"/>.</summary>
        public static string Display(object v, string typeName, int maxChars)
        {
            var type = (typeName ?? "").ToUpperInvariant();
            switch (v)
            {
                case null: return null;
                case string s: return s.Length > maxChars ? s.Substring(0, maxChars) + "…" : s;
                case DateTime d:
                    if (type == "DATE") return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    return d.ToString(d.Millisecond != 0 || d.Ticks % TimeSpan.TicksPerSecond != 0 ? "yyyy-MM-dd HH:mm:ss.ffffff" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                case MySqlDateTime md:
                    if (md.IsValidDateTime) return Display(md.GetDateTime(), typeName, maxChars);
                    return type == "DATE"
                        ? string.Format(CultureInfo.InvariantCulture, "{0:0000}-{1:00}-{2:00}", md.Year, md.Month, md.Day)
                        : string.Format(CultureInfo.InvariantCulture, "{0:0000}-{1:00}-{2:00} {3:00}:{4:00}:{5:00}", md.Year, md.Month, md.Day, md.Hour, md.Minute, md.Second);
                case TimeSpan ts:
                    {
                        var neg = ts < TimeSpan.Zero;
                        var a = neg ? ts.Negate() : ts;
                        return (neg ? "-" : "") + string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00}", (int)a.TotalHours, a.Minutes, a.Seconds);
                    }
                case byte[] b:
                    {
                        if (type.Contains("TEXT") || type.Contains("CHAR") || type == "JSON")
                        {
                            var s = Encoding.UTF8.GetString(b);
                            return s.Length > maxChars ? s.Substring(0, maxChars) + "…" : s;
                        }
                        if (b.Length <= 32)
                        {
                            var sb = new StringBuilder("0x", 2 + b.Length * 2);
                            foreach (var x in b) sb.Append(x.ToString("X2", CultureInfo.InvariantCulture));
                            return sb.ToString();
                        }
                        return "(binary, " + b.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes)";
                    }
                case bool bo: return bo ? "1" : "0";
                case double db: return db.ToString("R", CultureInfo.InvariantCulture);
                case float f: return f.ToString("R", CultureInfo.InvariantCulture);
                case decimal m: return m.ToString(CultureInfo.InvariantCulture);
                case Guid g: return g.ToString();
                case IFormattable fm: return fm.ToString(null, CultureInfo.InvariantCulture);
                default: return Convert.ToString(v, CultureInfo.InvariantCulture);
            }
        }

        /// <summary>Drops the connection (server stopped, or the Notebook was reset). Next run reconnects.</summary>
        public void Reset()
        {
            try { _conn?.Dispose(); } catch (Exception) { }
            _conn = null;
            _serverThreadId = 0;
        }

        public void Dispose()
        {
            Reset();
            _gate.Dispose();
        }
    }
}
