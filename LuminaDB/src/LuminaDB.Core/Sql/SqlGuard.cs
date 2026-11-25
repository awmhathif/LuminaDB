using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LuminaDB.Core.Sql
{
    public enum IssueSeverity { Info, Warn, Bad }

    /// <summary>A text replacement in the script (script offsets).</summary>
    public sealed class TextFix
    {
        public TextFix(int start, int length, string text) { Start = start; Length = length; Text = text ?? ""; }
        public int Start { get; }
        public int Length { get; }
        public string Text { get; }

        /// <summary>Applies fixes from the back so earlier offsets stay valid.</summary>
        public static string Apply(string script, IEnumerable<TextFix> fixes)
        {
            var s = script;
            foreach (var f in fixes.OrderByDescending(f => f.Start))
            {
                if (f.Start < 0 || f.Start + f.Length > s.Length) continue;
                s = s.Substring(0, f.Start) + f.Text + s.Substring(f.Start + f.Length);
            }
            return s;
        }
    }

    /// <summary>One margin correction.</summary>
    public sealed class SqlIssue
    {
        public IssueSeverity Severity { get; set; }
        /// <summary>Script offset of the underlined text.</summary>
        public int Start { get; set; }
        public int Length { get; set; }
        public int End => Start + Length;
        public string Message { get; set; }
        /// <summary>Button text, e.g. "Use FROM". Null when there is no automatic fix.</summary>
        public string FixLabel { get; set; }
        public List<TextFix> Fixes { get; } = new List<TextFix>();
        /// <summary>Short id for tests and telemetry-free counting: semicolon, table, column...</summary>
        public string Code { get; set; }
        /// <summary>Set for a word the user may still be typing: hidden while the caret touches it.</summary>
        internal bool CaretSensitive { get; set; }
        public bool HasFix => Fixes.Count > 0;
        public override string ToString() => $"{Severity} {Code} @{Start}+{Length}: {Message}";
    }

    public sealed class SqlAnalysis
    {
        public string Script { get; set; }
        public List<SqlStatement> Statements { get; set; } = new List<SqlStatement>();
        public List<StatementInfo> Infos { get; set; } = new List<StatementInfo>();
        public List<SqlIssue> Issues { get; } = new List<SqlIssue>();
        /// <summary>Command under the caret.</summary>
        public StatementInfo Current { get; set; }
        /// <summary>Plain sentence about the command under the caret.</summary>
        public string Summary { get; set; } = "";
        /// <summary>One entry per statement (same order as <see cref="Statements"/>); null when it stands on its own.</summary>
        public List<ScriptNeed> Needs { get; } = new List<ScriptNeed>();
        /// <summary>What the command under the caret needs from earlier lines that haven't run yet.</summary>
        public ScriptNeed CurrentNeed { get; set; }
        public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Bad);

        /// <summary>The need of the statement that contains <paramref name="offset"/> (null if none).</summary>
        public ScriptNeed NeedAt(int offset)
        {
            for (int i = 0; i < Statements.Count; i++)
                if (offset >= Statements[i].Start && offset <= Statements[i].End) return i < Needs.Count ? Needs[i] : null;
            return null;
        }
        public IEnumerable<SqlIssue> IssuesIn(SqlStatement s) => Issues.Where(i => s != null && i.Start >= s.Start && i.Start <= s.End);
    }

    /// <summary>
    /// Live checks while typing: catches the mistakes that make MariaDB reply with a cryptic
    /// #1064/#1146/#1054 before the user presses Run, each with a one-click correction.
    /// </summary>
    /// <summary>
    /// A command that only works after earlier lines of the same script have run: a table or database created above,
    /// or a USE above. Run on its own (Ctrl+Enter) it fails on the server even though the script reads fine.
    /// </summary>
    public sealed class ScriptNeed
    {
        public List<string> Tables { get; } = new List<string>();
        /// <summary>A database created above that the server doesn't have yet.</summary>
        public string Database { get; set; }
        /// <summary>A USE above that hasn't been run: on its own the command would go to another database.</summary>
        public string UseDatabase { get; set; }
        /// <summary>The database the Notebook is in now (for the USE sentence).</summary>
        public string SessionDatabase { get; set; }
        /// <summary>Start of the earliest command this one needs; "run from here".</summary>
        public int FromOffset { get; set; } = int.MaxValue;

        public bool IsEmpty => Tables.Count == 0 && Database == null && UseDatabase == null;

        public string Message
        {
            get
            {
                var parts = new List<string>();
                if (Tables.Count > 0)
                    parts.Add(Join(Tables) + (Tables.Count == 1 ? " is" : " are") + " created above but not run yet.");
                if (Database != null) parts.Add("The database " + Database + " is created above but not run yet.");
                if (UseDatabase != null)
                    parts.Add("USE " + UseDatabase + " above hasn't run, so alone this runs in " + (SessionDatabase ?? "no database") + ".");
                return string.Join(" ", parts);
            }
        }

        private static string Join(List<string> names) =>
            names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
    }

    public sealed class SqlGuard
    {
        private readonly SchemaSnapshot _schema;

        public SqlGuard(SchemaSnapshot schema) { _schema = schema ?? SchemaSnapshot.Empty; }

        public SqlAnalysis Analyze(string script, string currentDb, int caret = -1)
        {
            var a = new SqlAnalysis { Script = script ?? "" };
            var statements = SqlSplitter.Split(a.Script);
            var known = new KnownObjects(_schema);
            string db = currentDb;
            if (db != null && !known.DbExists(db)) db = currentDb;   // still report against it
            int useAt = -1;   // the last USE in the script so far

            foreach (var original in statements)
            {
                foreach (var stmt in SplitMissingSemicolons(a.Script, original, a.Issues))
                {
                    var info = StatementInfo.Analyze(stmt, db);
                    a.Statements.Add(stmt);
                    a.Infos.Add(info);
                    CheckStatement(a, stmt, info, known, caret);
                    a.Needs.Add(NeedOf(info, known, currentDb, useAt));
                    db = Apply(info, known, db);
                    if (info.Verb == "USE" && info.DatabaseName != null) useAt = stmt.Start;
                }
            }

            // hide complaints about the word being typed right now
            if (caret >= 0)
                a.Issues.RemoveAll(i => i.CaretSensitive && caret >= i.Start && caret <= i.End);

            a.Issues.Sort((x, y) => x.Start.CompareTo(y.Start));
            var cur = SqlSplitter.StatementAt(a.Statements, caret < 0 ? 0 : caret);
            a.Current = cur == null ? null : a.Infos[a.Statements.IndexOf(cur)];
            a.CurrentNeed = cur == null ? null : a.Needs[a.Statements.IndexOf(cur)];
            a.Summary = a.Current == null ? "" : Describe(a.Current, known);
            return a;
        }

        // ------------------------------------------------------------------ missing ;

        private static readonly HashSet<string> NoSplitAfter = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "UNION", "ALL", "DISTINCT", "AS", "ON", "FOR", "KEY", "THEN", "ELSE", "DO", "BEGIN", "OR", "EXPLAIN", "DESCRIBE",
            "DESC", "ANALYZE", "EXISTS", "IN", "NOT", "IGNORE", "INTO", "FROM", "JOIN", "EXCEPT", "INTERSECT", "RETURN", "TABLE",
        };

        private IEnumerable<SqlStatement> SplitMissingSemicolons(string script, SqlStatement s, List<SqlIssue> issues)
        {
            var info = StatementInfo.Analyze(s, null);
            if (info.IsRoutineBody) { yield return s; yield break; }
            var tok = info.Tokens;
            int depth = 0;
            int pieceStart = s.Start;
            for (int k = 0; k < tok.Count; k++)
            {
                var t = tok[k];
                if (t.IsSymbol("(")) { depth++; continue; }
                if (t.IsSymbol(")")) { depth = Math.Max(0, depth - 1); continue; }
                if (k == 0 || depth != 0 || t.Kind != TokenKind.Word || !SqlKeywords.LineStarters.Contains(t.Text)) continue;
                var prev = tok[k - 1];
                if (!HasNewlineBetween(script, prev.End, t.Start)) continue;
                if (prev.Kind == TokenKind.Symbol && !prev.IsSymbol(")")) continue;
                if (prev.Kind == TokenKind.Word && NoSplitAfter.Contains(prev.Text)) continue;
                var u = t.Upper;
                var verb = PieceVerb(tok, script, pieceStart);
                if (u == "SELECT" && (verb == "INSERT" || verb == "REPLACE" || verb == "CREATE" || verb == "WITH")) continue;
                if (verb == "WITH" && (u == "UPDATE" || u == "DELETE" || u == "INSERT")) continue;
                if (verb == "ALTER" && (u == "DROP" || u == "CREATE")) continue;
                if ((u == "REPLACE" || u == "INSERT") && k + 1 < tok.Count && tok[k + 1].IsSymbol("(")) continue;
                if (u == "USE" && k + 1 < tok.Count && (tok[k + 1].IsWord("INDEX") || tok[k + 1].IsWord("KEY"))) continue;

                issues.Add(new SqlIssue
                {
                    Severity = IssueSeverity.Bad,
                    Code = "semicolon",
                    Start = prev.Start,
                    Length = prev.Length,
                    Message = "Missing ; at the end of line " + SqlStatement.LineOf(script, prev.Start) +
                              ". Without it, MariaDB reads this line and the next as one command.",
                    FixLabel = "Add ;",
                    Fixes = { new TextFix(prev.End, 0, ";") },
                });
                yield return Piece(script, pieceStart, prev.End, s.Delimiter);
                pieceStart = t.Start;
            }
            if (pieceStart == s.Start) { yield return s; yield break; }
            yield return Piece(script, pieceStart, s.End, s.Delimiter, s.Terminated);
        }

        private static string PieceVerb(List<SqlToken> tok, string script, int pieceStart)
        {
            foreach (var t in tok) if (t.Start >= pieceStart) return t.Kind == TokenKind.Word ? t.Upper : "";
            return "";
        }

        private static SqlStatement Piece(string script, int a, int b, string delim, bool terminated = true) =>
            new SqlStatement(script, a, b - a, delim, terminated);

        private static bool HasNewlineBetween(string s, int a, int b)
        {
            for (int i = a; i < b && i < s.Length; i++) if (s[i] == '\n') return true;
            return false;
        }

        // ------------------------------------------------------------------ per statement

        private void CheckStatement(SqlAnalysis a, SqlStatement s, StatementInfo info, KnownObjects known, int caret)
        {
            var issues = a.Issues;
            bool typingHere = caret >= s.Start && caret >= s.End - 1 && caret <= s.End + 2 && !s.Terminated;

            // A. quotes and comments that never close
            foreach (var t in s.Tokens)
            {
                if (!t.Unterminated) continue;
                if (typingHere && t.Kind != TokenKind.Comment) return;
                issues.Add(Unterminated(t));
                return;   // everything after is swallowed; other checks would only add noise
            }
            var tok = info.Tokens;
            if (tok.Count == 0) return;

            // B. first word
            var first = tok[0];
            if (first.Kind == TokenKind.Word && !SqlKeywords.StatementStarters.Contains(first.Text) && !(tok.Count > 1 && tok[1].IsSymbol(":")))
            {
                var guess = SqlKeywords.Closest(first.Text, SqlKeywords.CommonStarters);
                var issue = new SqlIssue
                {
                    Severity = IssueSeverity.Bad,
                    Code = "command",
                    Start = first.Start,
                    Length = first.Length,
                    CaretSensitive = true,
                    Message = guess != null
                        ? first.Text + " isn't a command. Did you mean " + guess + "?"
                        : first.Text + " isn't a command MariaDB knows. Commands start with SELECT, INSERT, UPDATE, DELETE, CREATE...",
                };
                if (guess != null) { issue.FixLabel = "Use " + guess; issue.Fixes.Add(new TextFix(first.Start, first.Length, guess)); }
                issues.Add(issue);
                return;
            }
            else if (first.Kind != TokenKind.Word && !first.IsSymbol("("))
            {
                issues.Add(new SqlIssue
                {
                    Severity = IssueSeverity.Bad, Code = "command", Start = first.Start, Length = first.Length,
                    Message = "A command must start with a word like SELECT or INSERT, not \"" + first.Text + "\".",
                });
                return;
            }

            // C. brackets
            CheckBrackets(s, tok, issues, typingHere);
            if (info.IsRoutineBody) return;

            // D. commas
            CheckCommas(tok, issues, typingHere);

            // E. double-quoted names, reserved words used as names
            CheckNames(s, info, known, issues);

            // F. clause typos (FORM, WHER, VALUSE)
            CheckClauseTypos(info, known, issues);

            // G. tables
            CheckTables(info, known, issues);

            // H. columns
            CheckColumns(info, known, issues);

            // I. = NULL
            CheckNullCompare(info, issues);

            // J. risky commands
            CheckRisk(info, known, issues);
        }

        private static SqlIssue Unterminated(SqlToken t)
        {
            string what;
            if (t.Kind == TokenKind.Comment) what = "This /* comment never closes, so everything after it is ignored. Add */ where the comment ends.";
            else if (t.Kind == TokenKind.QuotedIdent) what = "This ` never closes. Add the closing ` after the name.";
            else if (t.Text.TrimStart('x', 'X', 'b', 'B', 'n', 'N', '_').StartsWith("\"", StringComparison.Ordinal))
                what = "This \" never closes, so the rest of the script is read as text. Add the closing \".";
            else
                what = "This ' never closes, so the rest of the script is read as text. An apostrophe inside text must be doubled: 'O''Brien'.";
            return new SqlIssue { Severity = IssueSeverity.Bad, Code = "quote", Start = t.Start, Length = 1, Message = what };
        }

        private static void CheckBrackets(SqlStatement s, List<SqlToken> tok, List<SqlIssue> issues, bool typingHere)
        {
            var open = new Stack<SqlToken>();
            foreach (var t in tok)
            {
                if (t.IsSymbol("(")) open.Push(t);
                else if (t.IsSymbol(")"))
                {
                    if (open.Count == 0)
                    {
                        issues.Add(new SqlIssue
                        {
                            Severity = IssueSeverity.Bad, Code = "bracket", Start = t.Start, Length = 1,
                            Message = "This ) has no matching (.",
                            FixLabel = "Remove )", Fixes = { new TextFix(t.Start, 1, "") },
                        });
                        return;
                    }
                    open.Pop();
                }
            }
            if (open.Count > 0 && !typingHere)
            {
                var t = open.Peek();
                var last = tok[tok.Count - 1];
                issues.Add(new SqlIssue
                {
                    Severity = IssueSeverity.Bad, Code = "bracket", Start = t.Start, Length = 1,
                    Message = open.Count == 1 ? "This ( is never closed." : open.Count + " brackets are never closed; this is the last one.",
                    FixLabel = "Add )", Fixes = { new TextFix(last.End, 0, new string(')', open.Count)) },
                });
            }
        }

        private static readonly HashSet<string> NoCommaBefore = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "FROM", "WHERE", "GROUP", "ORDER", "LIMIT", "HAVING", "VALUES", "SET", "JOIN", "ON", "UNION" };

        private static void CheckCommas(List<SqlToken> tok, List<SqlIssue> issues, bool typingHere)
        {
            for (int k = 0; k < tok.Count; k++)
            {
                var t = tok[k];
                if (!t.IsSymbol(",")) continue;
                if (k + 1 >= tok.Count)
                {
                    if (typingHere) continue;
                    issues.Add(Comma(t, "Extra comma at the end of the command."));
                    continue;
                }
                var n = tok[k + 1];
                if (n.IsSymbol(")")) issues.Add(Comma(t, "Extra comma before ). Remove it, or add the missing item."));
                else if (n.IsSymbol(",")) issues.Add(Comma(t, "Two commas in a row: something is missing between them."));
                else if (n.Kind == TokenKind.Word && NoCommaBefore.Contains(n.Text) && !(n.IsWord("SET") || n.IsWord("ON")) &&
                         !((n.IsWord("ORDER") || n.IsWord("GROUP")) && !(k + 2 < tok.Count && tok[k + 2].IsWord("BY"))))
                    issues.Add(Comma(t, "Extra comma before " + n.Upper + ". The last item in a list has no comma after it."));
                else if (k > 0 && tok[k - 1].Kind == TokenKind.Word && (tok[k - 1].IsWord("SELECT") || tok[k - 1].IsWord("BY")))
                    issues.Add(Comma(t, "Comma right after " + tok[k - 1].Upper + ": the first item is missing."));
            }
        }

        private static SqlIssue Comma(SqlToken t, string msg) => new SqlIssue
        {
            Severity = IssueSeverity.Bad, Code = "comma", Start = t.Start, Length = 1, Message = msg,
            FixLabel = "Remove ,", Fixes = { new TextFix(t.Start, 1, "") },
        };

        private static readonly HashSet<string> ColumnDefLead = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "PRIMARY", "KEY", "INDEX", "UNIQUE", "CONSTRAINT", "FOREIGN", "CHECK", "FULLTEXT", "SPATIAL", "PERIOD", "LIKE" };

        private void CheckNames(SqlStatement s, StatementInfo info, KnownObjects known, List<SqlIssue> issues)
        {
            var tok = info.Tokens;
            // "students" where a name belongs
            for (int k = 0; k + 1 < tok.Count; k++)
            {
                var t = tok[k];
                if (t.Kind != TokenKind.Word) continue;
                bool namePos = t.IsWord("FROM") || t.IsWord("JOIN") || t.IsWord("INTO") || t.IsWord("TABLE") && (info.Verb == "CREATE" || info.Verb == "DROP" || info.Verb == "ALTER") || t.IsWord("UPDATE") && k == 0;
                if (!namePos) continue;
                var n = tok[k + 1];
                if (n.Kind == TokenKind.String && n.Text.StartsWith("\"", StringComparison.Ordinal) && !n.Unterminated && n.Text.Length > 2)
                    issues.Add(BacktickFix(n, n.Text.Substring(1, n.Text.Length - 2).Replace("\"\"", "\""), "Names use backticks, not double quotes: "));
            }

            // reserved words in table positions
            foreach (var r in info.Tables.Where(r => r.IsReservedWord))
            {
                bool tableExists = r.Name != null && known.TableExists(info.CurrentDatabase, r.Name) == true;
                if (tableExists || info.Verb == "CREATE" && r.NameStart > 0 && !NoCommaBefore.Contains(r.Name))
                {
                    var issue = new SqlIssue
                    {
                        Severity = IssueSeverity.Bad, Code = "reserved", Start = r.NameStart, Length = r.NameLength,
                        Message = r.Name.ToUpperInvariant() + " is a reserved SQL word, so it can't be a plain table name. Wrap it in backticks: " + SqlLexer.Quote(r.Name.ToLowerInvariant()) + ".",
                        FixLabel = "Use " + SqlLexer.Quote(r.Name),
                    };
                    issue.Fixes.Add(new TextFix(r.NameStart, r.NameLength, SqlLexer.Quote(r.Name)));
                    issues.Add(issue);
                }
                else
                {
                    issues.Add(new SqlIssue
                    {
                        Severity = IssueSeverity.Bad, Code = "table-missing", Start = r.NameStart, Length = r.NameLength,
                        Message = "A table name is missing before " + r.Name.ToUpperInvariant() + ".",
                    });
                }
            }

            // column definitions in CREATE TABLE
            if (info.Verb == "CREATE" && info.Object == "TABLE")
            {
                foreach (var col in ColumnDefinitions(info))
                {
                    if (col.Kind == TokenKind.Word && SqlKeywords.Reserved.Contains(col.Text) && !ColumnDefLead.Contains(col.Text))
                        issues.Add(BacktickFix(col, col.Text, col.Upper + " is a reserved SQL word, so it can't be a plain column name. Wrap it in backticks: "));
                    else if (col.Kind == TokenKind.String && col.Text.StartsWith("\"", StringComparison.Ordinal) && col.Text.Length > 2)
                        issues.Add(BacktickFix(col, col.Text.Substring(1, col.Text.Length - 2), "Names use backticks, not double quotes: "));
                    else if (col.Kind == TokenKind.String && col.Text.StartsWith("'", StringComparison.Ordinal) && col.Text.Length > 2)
                        issues.Add(BacktickFix(col, col.Text.Substring(1, col.Text.Length - 2), "Column names use backticks, not quotes: "));
                }
            }
        }

        private static SqlIssue BacktickFix(SqlToken t, string name, string message)
        {
            var q = SqlLexer.Quote(name);
            return new SqlIssue
            {
                Severity = IssueSeverity.Bad, Code = "reserved", Start = t.Start, Length = t.Length,
                Message = message + q + ".", FixLabel = "Use " + q, Fixes = { new TextFix(t.Start, t.Length, q) },
            };
        }

        /// <summary>First token of each column definition in CREATE TABLE t ( ... ).</summary>
        internal static List<SqlToken> ColumnDefinitions(StatementInfo info)
        {
            var list = new List<SqlToken>();
            var tok = info.Tokens;
            var def = info.Tables.FirstOrDefault(r => r.IsDefinition);
            if (def == null) return list;
            int k = tok.FindIndex(t => t.Start == def.NameStart);
            if (k < 0) return list;
            k++;
            while (k < tok.Count && !tok[k].IsSymbol("(")) { if (tok[k].IsWord("AS") || tok[k].IsWord("SELECT") || tok[k].IsWord("LIKE")) return list; k++; }
            if (k >= tok.Count) return list;
            int depth = 0;
            bool expectName = true;
            for (; k < tok.Count; k++)
            {
                var t = tok[k];
                if (t.IsSymbol("(")) { depth++; if (depth == 1) { expectName = true; continue; } }
                else if (t.IsSymbol(")")) { depth--; if (depth == 0) break; }
                else if (depth == 1 && t.IsSymbol(",")) { expectName = true; continue; }
                if (depth == 1 && expectName)
                {
                    list.Add(t);
                    expectName = false;
                }
            }
            return list;
        }

        private void CheckClauseTypos(StatementInfo info, KnownObjects known, List<SqlIssue> issues)
        {
            if (!_schema.IsLoaded) return;
            if (!(info.Verb == "SELECT" || info.Verb == "INSERT" || info.Verb == "UPDATE" || info.Verb == "DELETE" || info.Verb == "REPLACE")) return;
            var names = known.AllNames(info.CurrentDatabase);
            foreach (var r in info.Tables) { if (r.Alias != null) names.Add(r.Alias); if (r.Name != null) names.Add(r.Name); }
            foreach (var c in info.CteNames) names.Add(c);
            var tok = info.Tokens;
            for (int k = 1; k < tok.Count; k++)
            {
                var t = tok[k];
                if (t.Kind != TokenKind.Word || t.Length < 4 || SqlKeywords.IsVocabulary(t.Text) || names.Contains(t.Text)) continue;
                if (k + 1 < tok.Count && (tok[k + 1].IsSymbol("(") || tok[k + 1].IsSymbol("."))) continue;
                if (tok[k - 1].IsSymbol(".") || tok[k - 1].IsWord("AS")) continue;
                var guess = SqlKeywords.Closest(t.Text, SqlKeywords.ClauseWords, 1);
                if (guess == null) continue;
                issues.Add(new SqlIssue
                {
                    Severity = IssueSeverity.Bad, Code = "typo", Start = t.Start, Length = t.Length, CaretSensitive = true,
                    Message = t.Text + " isn't a SQL word or one of your names. Did you mean " + guess + "?",
                    FixLabel = "Use " + guess, Fixes = { new TextFix(t.Start, t.Length, guess) },
                });
            }
        }

        private void CheckTables(StatementInfo info, KnownObjects known, List<SqlIssue> issues)
        {
            if (!_schema.IsLoaded) return;

            if (info.Verb == "USE" && info.DatabaseName != null)
            {
                if (!known.DbExists(info.DatabaseName))
                    issues.Add(UnknownDatabase(info.DatabaseName, info.DatabaseNameStart, info.DatabaseNameLength, known));
                return;
            }
            if (info.Object == "DATABASE" && info.DatabaseName != null)
            {
                bool exists = known.DbExists(info.DatabaseName);
                if (info.Verb == "CREATE" && exists && !info.IfNotExists)
                    issues.Add(new SqlIssue
                    {
                        Severity = IssueSeverity.Warn, Code = "exists", Start = info.DatabaseNameStart, Length = info.DatabaseNameLength,
                        Message = "A database called " + info.DatabaseName + " already exists, so this will fail. Add IF NOT EXISTS to skip it.",
                        FixLabel = "Add IF NOT EXISTS", Fixes = { new TextFix(info.DatabaseNameStart, 0, "IF NOT EXISTS ") },
                    });
                else if (info.Verb == "DROP" && !exists && !info.IfExists)
                    issues.Add(UnknownDatabase(info.DatabaseName, info.DatabaseNameStart, info.DatabaseNameLength, known));
                return;
            }

            bool reportedNoDb = false;
            foreach (var r in info.Tables)
            {
                if (r.IsDerived || r.IsReservedWord || r.Name == null) continue;
                if (r.Database == null && info.CteNames.Contains(r.Name)) continue;
                var db = r.ResolvedDatabase;
                if (db == null)
                {
                    if (reportedNoDb) continue;
                    reportedNoDb = true;
                    issues.Add(new SqlIssue
                    {
                        Severity = IssueSeverity.Bad, Code = "no-database", Start = r.NameStart, Length = r.NameLength,
                        Message = "No database is selected, so MariaDB can't find " + r.Name + ". Pick one in the toolbar, or start with USE your_database;",
                    });
                    continue;
                }
                if (SchemaSnapshot.IsSystem(db)) continue;
                if (!known.DbExists(db))
                {
                    if (r.Database != null)
                    {
                        var dbTok = info.Tokens.LastOrDefault(t => t.End <= r.NameStart && t.IsSignificant && t.Kind != TokenKind.Symbol);
                        issues.Add(UnknownDatabase(r.Database, dbTok.Start, dbTok.Length, known));
                    }
                    else if (!reportedNoDb)
                    {
                        reportedNoDb = true;
                        issues.Add(new SqlIssue
                        {
                            Severity = IssueSeverity.Bad, Code = "no-database", Start = r.NameStart, Length = r.NameLength,
                            Message = "The selected database " + db + " doesn't exist any more. Pick another one in the toolbar.",
                        });
                    }
                    continue;
                }

                bool? exists = known.TableExists(db, r.Name);
                if (exists == null) continue;
                if (r.IsDefinition)
                {
                    if (exists == true && !info.IfNotExists && !OrReplace(info))
                    {
                        var tableTok = info.Tokens.FirstOrDefault(t => (t.IsWord("TABLE") || t.IsWord("VIEW")) && t.End <= r.NameStart);
                        var issue = new SqlIssue
                        {
                            Severity = IssueSeverity.Warn, Code = "exists", Start = r.NameStart, Length = r.NameLength,
                            Message = (info.Object == "VIEW" ? "A view" : "A table") + " called " + r.Name + " already exists in " + db +
                                      ", so this will fail. Add IF NOT EXISTS to skip it, or DROP it first.",
                        };
                        if (tableTok.Length > 0) { issue.FixLabel = "Add IF NOT EXISTS"; issue.Fixes.Add(new TextFix(tableTok.End, 0, " IF NOT EXISTS")); }
                        issues.Add(issue);
                    }
                    continue;
                }
                if (exists == false)
                {
                    if (info.Verb == "DROP" && info.IfExists) continue;
                    var guess = SqlKeywords.Closest(r.Name, known.TableNames(db));
                    var issue = new SqlIssue
                    {
                        Severity = IssueSeverity.Bad, Code = "table", Start = r.NameStart, Length = r.NameLength, CaretSensitive = true,
                        Message = "There's no table called " + r.Name + " in " + db + "." + (guess != null ? " Did you mean " + guess + "?" : NoTablesHint(known, db)),
                    };
                    if (guess != null) { issue.FixLabel = "Use " + guess; issue.Fixes.Add(new TextFix(r.NameStart, r.NameLength, RequoteLike(info, r.NameStart, guess))); }
                    issues.Add(issue);
                }
            }
        }

        private static string NoTablesHint(KnownObjects known, string db)
        {
            var names = known.TableNames(db).ToList();
            if (names.Count == 0) return " " + db + " has no tables yet. Create one first.";
            if (names.Count <= 6) return " Tables here: " + string.Join(", ", names) + ".";
            return "";
        }

        private static bool OrReplace(StatementInfo info) => info.Tokens.Count > 2 && info.Tokens[1].IsWord("OR") && info.Tokens[2].IsWord("REPLACE");

        private static string RequoteLike(StatementInfo info, int start, string name)
        {
            var t = info.Tokens.FirstOrDefault(x => x.Start == start);
            return t.Kind == TokenKind.QuotedIdent || !SqlLexer.IsPlainName(name) ? SqlLexer.Quote(name) : name;
        }

        private SqlIssue UnknownDatabase(string name, int start, int length, KnownObjects known)
        {
            var guess = SqlKeywords.Closest(name, known.DatabaseNames());
            var issue = new SqlIssue
            {
                Severity = IssueSeverity.Bad, Code = "database", Start = start, Length = length, CaretSensitive = true,
                Message = "There's no database called " + name + "." + (guess != null ? " Did you mean " + guess + "?" : " Create it first with CREATE DATABASE " + name + ";"),
            };
            if (guess != null) { issue.FixLabel = "Use " + guess; issue.Fixes.Add(new TextFix(start, length, guess)); }
            return issue;
        }

        // ------------------------------------------------------------------ columns

        private static readonly HashSet<string> ClauseStarts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SELECT", "FROM", "WHERE", "GROUP", "HAVING", "ORDER", "LIMIT", "SET", "VALUES", "VALUE", "INTO", "ON", "USING", "UNION", "WINDOW", "RETURNING" };

        private void CheckColumns(StatementInfo info, KnownObjects known, List<SqlIssue> issues)
        {
            if (!_schema.IsLoaded) return;
            var tok = info.Tokens;
            var refs = info.Tables.Where(r => !r.IsReservedWord && !r.IsDefinition && !r.IsReference).ToList();

            // qualified names: s.name, students.name
            var refStarts = new HashSet<int>(refs.Select(r => r.NameStart));
            for (int k = 0; k + 2 < tok.Count; k++)
            {
                var q = tok[k];
                if (!(q.Kind == TokenKind.Word || q.Kind == TokenKind.QuotedIdent) || !tok[k + 1].IsSymbol(".")) continue;
                var c = tok[k + 2];
                if (refStarts.Contains(c.Start) || refStarts.Contains(q.Start)) continue;
                if (k > 0 && tok[k - 1].IsSymbol(".")) continue;
                if (k + 3 < tok.Count && (tok[k + 3].IsSymbol(".") || tok[k + 3].IsSymbol("("))) continue;
                if (!(c.Kind == TokenKind.Word || c.Kind == TokenKind.QuotedIdent)) continue;
                if (refs.Count == 0 || info.Verb == "CREATE" || info.Verb == "ALTER") continue;

                var qn = q.IdentName;
                var r = refs.FirstOrDefault(x => string.Equals(x.Alias, qn, StringComparison.OrdinalIgnoreCase)) ??
                        refs.FirstOrDefault(x => x.Alias == null && string.Equals(x.Name, qn, StringComparison.OrdinalIgnoreCase));
                if (r == null)
                {
                    var aliased = refs.FirstOrDefault(x => x.Alias != null && string.Equals(x.Name, qn, StringComparison.OrdinalIgnoreCase));
                    if (aliased != null)
                    {
                        issues.Add(new SqlIssue
                        {
                            Severity = IssueSeverity.Bad, Code = "alias", Start = q.Start, Length = q.Length,
                            Message = "You named " + aliased.Name + " \"" + aliased.Alias + "\" in FROM, so write " + aliased.Alias + "." + c.IdentName + " here.",
                            FixLabel = "Use " + aliased.Alias, Fixes = { new TextFix(q.Start, q.Length, aliased.Alias) },
                        });
                        continue;
                    }
                    if (info.CteNames.Contains(qn) || known.DbExists(qn)) continue;
                    var names = refs.Select(x => x.Alias ?? x.Name).Where(x => x != null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var guess = SqlKeywords.Closest(qn, names);
                    var issue = new SqlIssue
                    {
                        Severity = IssueSeverity.Bad, Code = "alias", Start = q.Start, Length = q.Length, CaretSensitive = true,
                        Message = qn + " isn't a table or alias in this command." + (names.Count > 0 ? " Here you can use: " + string.Join(", ", names) + "." : ""),
                    };
                    if (guess != null) { issue.FixLabel = "Use " + guess; issue.Fixes.Add(new TextFix(q.Start, q.Length, guess)); }
                    issues.Add(issue);
                    continue;
                }
                if (r.IsDerived || r.ResolvedDatabase == null || info.CteNames.Contains(r.Name ?? "")) continue;
                var cols = known.Columns(r.ResolvedDatabase, r.Name);
                if (cols == null) continue;
                if (!cols.Contains(c.IdentName, StringComparer.OrdinalIgnoreCase))
                    issues.Add(UnknownColumn(c, r.Name, cols));
            }

            // unqualified names when only one table is involved
            if (!(info.Verb == "SELECT" || info.Verb == "UPDATE" || info.Verb == "DELETE")) { CheckInsertColumns(info, known, issues); return; }
            if (info.HasSubquery || info.HasJoin || info.CteNames.Count > 0) return;
            var single = refs.Where(r => !r.IsDerived).ToList();
            if (single.Count != 1 || refs.Count != 1) return;
            var table = single[0];
            if (table.ResolvedDatabase == null) return;
            var columns = known.Columns(table.ResolvedDatabase, table.Name);
            if (columns == null) return;

            var selectAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string clause = info.Verb == "SELECT" ? "SELECT" : info.Verb;
            int depth = 0;
            var candidates = new List<(SqlToken tok, string clause)>();
            for (int k = 0; k < tok.Count; k++)
            {
                var t = tok[k];
                if (t.IsSymbol("(")) { depth++; continue; }
                if (t.IsSymbol(")")) { depth--; continue; }
                if (t.Kind == TokenKind.Word && ClauseStarts.Contains(t.Text) && depth == 0) { clause = t.Upper; continue; }
                if (t.Start == table.NameStart || table.Alias != null && t.Kind != TokenKind.Symbol && k > 0 && tok[k - 1].Start == table.NameStart) continue;
                if (!(t.Kind == TokenKind.QuotedIdent || t.Kind == TokenKind.Word && !SqlKeywords.IsVocabulary(t.Text))) continue;
                if (k + 1 < tok.Count && (tok[k + 1].IsSymbol("(") || tok[k + 1].IsSymbol("."))) continue;
                if (k > 0 && (tok[k - 1].IsSymbol(".") || tok[k - 1].IsWord("COLLATE") || tok[k - 1].IsWord("CHARSET") || tok[k - 1].IsWord("USING"))) continue;
                if (k > 0 && tok[k - 1].IsWord("AS")) { if (clause == "SELECT") selectAliases.Add(t.IdentName); continue; }
                if (clause == "SELECT" && k > 0 && depth == 0 && IsAliasPosition(tok[k - 1])) { selectAliases.Add(t.IdentName); continue; }
                if (clause == "FROM" || clause == "LIMIT" || clause == "INTO" || clause == "UPDATE" || clause == "DELETE") continue;
                candidates.Add((t, clause));
            }
            foreach (var (t, cl) in candidates)
            {
                var name = t.IdentName;
                if (columns.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if ((cl == "ORDER" || cl == "GROUP" || cl == "HAVING") && selectAliases.Contains(name)) continue;
                if (string.Equals(name, table.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(name, table.Alias, StringComparison.OrdinalIgnoreCase)) continue;
                issues.Add(UnknownColumn(t, table.Name, columns));
            }
        }

        private static bool IsAliasPosition(SqlToken prev) =>
            prev.Kind == TokenKind.QuotedIdent || prev.Kind == TokenKind.Number || prev.Kind == TokenKind.String || prev.IsSymbol(")") ||
            prev.IsSymbol("*") && false ||
            prev.Kind == TokenKind.Word && !SqlKeywords.IsVocabulary(prev.Text);

        private static SqlIssue UnknownColumn(SqlToken t, string table, IList<string> columns)
        {
            var guess = SqlKeywords.Closest(t.IdentName, columns);
            var issue = new SqlIssue
            {
                Severity = IssueSeverity.Bad, Code = "column", Start = t.Start, Length = t.Length, CaretSensitive = true,
                Message = table + " has no column " + t.IdentName + "." + (guess != null ? " Did you mean " + guess + "?" :
                          columns.Count <= 8 ? " Its columns: " + string.Join(", ", columns) + "." : ""),
            };
            if (t.Kind == TokenKind.String && t.Text.StartsWith("\"", StringComparison.Ordinal)) return issue;
            if (guess != null)
            {
                issue.FixLabel = "Use " + guess;
                issue.Fixes.Add(new TextFix(t.Start, t.Length, t.Kind == TokenKind.QuotedIdent || !SqlLexer.IsPlainName(guess) ? SqlLexer.Quote(guess) : guess));
            }
            return issue;
        }

        private static void CheckInsertColumns(StatementInfo info, KnownObjects known, List<SqlIssue> issues)
        {
            if (!(info.Verb == "INSERT" || info.Verb == "REPLACE") || info.Targets.Count == 0) return;
            var target = info.Targets[0];
            if (target.ResolvedDatabase == null) return;
            var columns = known.Columns(target.ResolvedDatabase, target.Name);
            var tok = info.Tokens;
            int k = tok.FindIndex(t => t.Start == target.NameStart);
            if (k < 0) return;
            k++;
            if (target.Alias != null) k++;
            var listed = new List<SqlToken>();
            if (k < tok.Count && tok[k].IsSymbol("(") && !(k + 1 < tok.Count && tok[k + 1].IsWord("SELECT")))
            {
                k++;
                for (; k < tok.Count && !tok[k].IsSymbol(")"); k++)
                    if (tok[k].Kind == TokenKind.Word || tok[k].Kind == TokenKind.QuotedIdent) listed.Add(tok[k]);
                    else if (tok[k].Kind == TokenKind.String)
                    {
                        issues.Add(BacktickFix(tok[k], tok[k].Text.Substring(1, Math.Max(0, tok[k].Text.Length - 2)), "Column names use backticks, not quotes: "));
                        return;
                    }
                k++;
                if (columns != null)
                    foreach (var c in listed)
                        if (!columns.Contains(c.IdentName, StringComparer.OrdinalIgnoreCase)) issues.Add(UnknownColumn(c, target.Name, columns));
            }

            // VALUES (...) rows must match
            int expected = listed.Count > 0 ? listed.Count : columns?.Count ?? -1;
            if (expected < 0) return;
            while (k < tok.Count && !(tok[k].IsWord("VALUES") || tok[k].IsWord("VALUE"))) { if (tok[k].IsWord("SELECT") || tok[k].IsWord("SET")) return; k++; }
            k++;
            int row = 0;
            while (k < tok.Count && tok[k].IsSymbol("("))
            {
                row++;
                var open = tok[k];
                int depth = 0, values = 0;
                bool any = false;
                int closeEnd = open.End;
                for (; k < tok.Count; k++)
                {
                    var t = tok[k];
                    if (t.IsSymbol("(")) { depth++; if (depth == 1) continue; }
                    if (t.IsSymbol(")")) { depth--; if (depth == 0) { closeEnd = t.End; k++; break; } }
                    if (depth == 1 && t.IsSymbol(",")) { values++; continue; }
                    any = true;
                }
                if (any) values++;
                if (values != expected)
                {
                    string what = listed.Count > 0
                        ? expected + (expected == 1 ? " column is" : " columns are") + " listed (" + string.Join(", ", listed.Select(x => x.IdentName)) + ")"
                        : target.Name + " has " + expected + " columns (" + string.Join(", ", columns) + ")";
                    issues.Add(new SqlIssue
                    {
                        Severity = IssueSeverity.Bad, Code = "count", Start = open.Start, Length = closeEnd - open.Start,
                        Message = (row > 1 ? "Row " + row + " has " : "This row has ") + values + (values == 1 ? " value" : " values") + ", but " + what + ". They must match one-to-one.",
                    });
                }
                if (k < tok.Count && tok[k].IsSymbol(",")) k++;
                else break;
            }
        }

        // ------------------------------------------------------------------ = NULL, risk

        private static void CheckNullCompare(StatementInfo info, List<SqlIssue> issues)
        {
            var tok = info.Tokens;
            bool inSet = info.Verb == "SET";
            for (int k = 0; k + 1 < tok.Count; k++)
            {
                var t = tok[k];
                if (t.IsWord("SET")) inSet = true;
                else if (t.IsWord("WHERE") || t.IsWord("ON") || t.IsWord("HAVING") || t.IsWord("WHEN") || t.IsWord("IF")) inSet = false;
                if (inSet) continue;
                bool eq = t.IsSymbol("="), ne = t.IsSymbol("<>") || t.IsSymbol("!=");
                if (!(eq || ne) || !tok[k + 1].IsWord("NULL")) continue;
                var n = tok[k + 1];
                string repl = eq ? "IS NULL" : "IS NOT NULL";
                issues.Add(new SqlIssue
                {
                    Severity = IssueSeverity.Warn, Code = "null", Start = t.Start, Length = n.End - t.Start,
                    Message = t.Text + " NULL is never true, so this matches no rows. Use " + repl + ".",
                    FixLabel = "Use " + repl, Fixes = { new TextFix(t.Start, n.End - t.Start, repl) },
                });
            }
        }

        private static void CheckRisk(StatementInfo info, KnownObjects known, List<SqlIssue> issues)
        {
            var tok = info.Tokens;
            if ((info.Verb == "DELETE" || info.Verb == "UPDATE") && !info.HasWhere && !info.HasLimit && info.Targets.Count > 0)
            {
                var target = info.Targets[0];
                var rows = known.RowEstimate(target.ResolvedDatabase, target.Name);
                var pk = known.PrimaryKey(target.ResolvedDatabase, target.Name) ?? "id";
                var last = tok[tok.Count - 1];
                issues.Add(new SqlIssue
                {
                    Severity = IssueSeverity.Warn, Code = "no-where", Start = tok[0].Start, Length = tok[0].Length,
                    Message = "No WHERE: this " + (info.Verb == "DELETE" ? "deletes" : "changes") + " every row in " + target.Name +
                              (rows > 0 ? " (about " + rows.ToString("N0", CultureInfo.InvariantCulture) + ")" : "") + ". Add a WHERE to pick the rows.",
                    FixLabel = "Add WHERE " + pk + " = …",
                    Fixes = { new TextFix(last.End, 0, " WHERE " + pk + " = 1") },
                });
            }
            else if (info.Verb == "TRUNCATE" || info.Verb == "DROP" && (info.Object == "TABLE" || info.Object == "DATABASE"))
            {
                string what;
                if (info.Object == "DATABASE") what = "This deletes the database " + info.DatabaseName + " and every table in it.";
                else
                {
                    var t = info.Targets.FirstOrDefault();
                    if (t == null) return;
                    var rows = known.RowEstimate(t.ResolvedDatabase, t.Name);
                    string count = rows > 0 ? " and its " + rows.ToString("N0", CultureInfo.InvariantCulture) + " rows" : "";
                    what = info.Verb == "TRUNCATE" ? "This empties " + t.Name + count.Replace(" and its", ": all") + "." : "This deletes the table " + t.Name + count + ".";
                    if (info.Targets.Count > 1) what = "This deletes " + info.Targets.Count + " tables: " + string.Join(", ", info.Targets.Select(x => x.Name)) + ".";
                }
                issues.Add(new SqlIssue
                {
                    Severity = IssueSeverity.Warn, Code = "destroy", Start = tok[0].Start, Length = tok[0].Length,
                    Message = what + " Undo keeps a copy unless it's very large.",
                });
            }
            else if (info.Verb == "ALTER" && info.IsDropColumn)
            {
                issues.Add(new SqlIssue
                {
                    Severity = IssueSeverity.Warn, Code = "destroy", Start = tok[0].Start, Length = tok[0].Length,
                    Message = "DROP COLUMN deletes that column's data in every row. Undo keeps a copy.",
                });
            }
        }

        // ------------------------------------------------------------------ state across the script

        /// <summary>
        /// What this command needs from earlier lines that the server hasn't seen: tables or a database created above
        /// (and not on the server), or a USE above when the Notebook is in another database. Null when it stands alone.
        /// </summary>
        private static ScriptNeed NeedOf(StatementInfo info, KnownObjects known, string sessionDb, int useAt)
        {
            if (info.Kind == StatementKind.Empty || !known.IsLoaded) return null;
            var need = new ScriptNeed { SessionDatabase = sessionDb };
            void From(int at) { if (at >= 0 && at < need.FromOffset) need.FromOffset = at; }

            if (info.Verb == "USE")
            {
                int at = known.ScriptOnlyDatabase(info.DatabaseName);
                if (at >= 0) { need.Database = info.DatabaseName; From(at); }
                return need.IsEmpty ? null : need;
            }

            var refs = info.Tables.Concat(info.Targets).Where(r => !r.IsDerived && !r.IsDefinition && r.Name != null && r.ResolvedDatabase != null).ToList();
            foreach (var r in refs)
            {
                int at = known.ScriptOnlyTable(r.ResolvedDatabase, r.Name);
                if (at >= 0 && !need.Tables.Contains(r.Name, StringComparer.OrdinalIgnoreCase)) { need.Tables.Add(r.Name); From(at); }
            }
            // FOREIGN KEY ... REFERENCES parent: inside CREATE TABLE ( ... ) the parent isn't in Tables, so read it here
            if (info.Verb == "CREATE" || info.Verb == "ALTER")
            {
                var tok = info.Tokens;
                for (int k = 0; k + 1 < tok.Count; k++)
                {
                    if (!tok[k].IsWord("REFERENCES") || tok[k + 1].IdentName == null) continue;
                    string parentDb = info.CurrentDatabase, parent = tok[k + 1].IdentName;
                    if (k + 3 < tok.Count && tok[k + 2].IsSymbol(".") && tok[k + 3].IdentName != null) { parentDb = parent; parent = tok[k + 3].IdentName; }
                    int at = known.ScriptOnlyTable(parentDb, parent);
                    if (at >= 0 && !need.Tables.Contains(parent, StringComparer.OrdinalIgnoreCase)) { need.Tables.Add(parent); From(at); }
                }
            }

            // a USE above sends the script to another database than the one the Notebook is in right now
            bool usesCurrentDb = refs.Any(r => r.Database == null) || info.Targets.Any(r => r.Database == null) || info.Tables.Any(r => r.Database == null && !r.IsDerived);
            if (useAt >= 0 && usesCurrentDb && info.CurrentDatabase != null && !string.Equals(info.CurrentDatabase, sessionDb, StringComparison.OrdinalIgnoreCase))
            {
                need.UseDatabase = info.CurrentDatabase;
                From(useAt);
            }
            if (usesCurrentDb && info.CurrentDatabase != null)
            {
                int at = known.ScriptOnlyDatabase(info.CurrentDatabase);
                if (at >= 0) { need.Database = info.CurrentDatabase; From(at); }
            }
            return need.IsEmpty ? null : need;
        }

        private static string Apply(StatementInfo info, KnownObjects known, string db)
        {
            switch (info.Verb)
            {
                case "USE":
                    return info.DatabaseName ?? db;
                case "CREATE":
                    if (info.Object == "DATABASE" && info.DatabaseName != null) known.CreateDatabase(info.DatabaseName, info.Statement.Start);
                    else if ((info.Object == "TABLE" || info.Object == "VIEW") && info.Targets.Count > 0)
                    {
                        var t = info.Targets[0];
                        List<string> cols = null;
                        if (info.Object == "TABLE" && !info.Tokens.Any(x => x.IsWord("SELECT") || x.IsWord("LIKE")))
                        {
                            cols = ColumnDefinitions(info).Where(x => !(x.Kind == TokenKind.Word && ColumnDefLead.Contains(x.Text)))
                                .Select(x => x.Kind == TokenKind.String ? x.Text.Trim('"', '\'') : x.IdentName).ToList();
                        }
                        if (t.ResolvedDatabase != null) known.CreateTable(t.ResolvedDatabase, t.Name, cols, info.Statement.Start);
                    }
                    break;
                case "DROP":
                    if (info.Object == "DATABASE" && info.DatabaseName != null)
                    {
                        known.DropDatabase(info.DatabaseName);
                        if (string.Equals(db, info.DatabaseName, StringComparison.OrdinalIgnoreCase)) return null;
                    }
                    else foreach (var t in info.Targets) if (t.ResolvedDatabase != null) known.DropTable(t.ResolvedDatabase, t.Name);
                    break;
                case "ALTER":
                    foreach (var t in info.Targets) if (t.ResolvedDatabase != null) known.ForgetColumns(t.ResolvedDatabase, t.Name);
                    foreach (var n in info.NewNames)
                        if (n.ResolvedDatabase != null && info.Targets.Count > 0)
                        {
                            known.DropTable(info.Targets[0].ResolvedDatabase, info.Targets[0].Name);
                            known.CreateTable(n.ResolvedDatabase, n.Name, null, info.Statement.Start);
                        }
                    break;
                case "RENAME":
                    foreach (var t in info.Targets) if (t.ResolvedDatabase != null) known.DropTable(t.ResolvedDatabase, t.Name);
                    foreach (var n in info.NewNames) if (n.ResolvedDatabase != null) known.CreateTable(n.ResolvedDatabase, n.Name, null, info.Statement.Start);
                    break;
            }
            return db;
        }

        // ------------------------------------------------------------------ plain-English summary

        public static string Describe(StatementInfo info, KnownObjects known = null)
        {
            if (info == null || info.Kind == StatementKind.Empty) return "";
            string T(TableRef r) => r?.Name ?? "a sub-query";
            var main = info.Tables.Where(r => !r.IsReference).ToList();
            switch (info.Verb)
            {
                case "SELECT":
                case "WITH":
                    {
                        if (main.Count == 0) return "Works out a value. Read-only.";
                        var parts = new List<string> { "Reads rows from " + T(main[0]) };
                        var joins = main.Skip(1).Select(T).Distinct().ToList();
                        if (joins.Count > 0) parts.Add((info.HasJoin ? "joins " : "combines with ") + string.Join(", ", joins));
                        if (info.HasWhere) parts.Add("keeps the matching ones");
                        if (info.HasGroupBy) parts.Add("groups them");
                        if (info.HasOrderBy) parts.Add("sorts them");
                        if (info.LimitCount >= 0) parts.Add("shows the first " + info.LimitCount);
                        return Join(parts) + ". Read-only.";
                    }
                case "INSERT":
                case "REPLACE":
                    {
                        var t = info.Targets.FirstOrDefault();
                        string rows = info.ValuesRows > 0 ? info.ValuesRows + (info.ValuesRows == 1 ? " row" : " rows") : "rows";
                        return (info.Verb == "REPLACE" ? "Adds or replaces " : "Adds ") + rows + " to " + T(t) + ". Undo can take them out again.";
                    }
                case "UPDATE":
                    return "Changes " + (info.HasWhere ? "the matching rows" : "EVERY row") + " in " + string.Join(", ", info.Targets.Select(T)) + ". Undo keeps a copy.";
                case "DELETE":
                    return "Deletes " + (info.HasWhere ? "the matching rows" : "EVERY row") + " from " + string.Join(", ", info.Targets.Select(T)) + ". Undo keeps a copy.";
                case "TRUNCATE":
                    return "Empties " + T(info.Targets.FirstOrDefault()) + " (all rows). Undo keeps a copy.";
                case "CREATE":
                    if (info.Object == "DATABASE") return "Creates the database " + info.DatabaseName + ".";
                    if (info.Object == "TABLE")
                    {
                        var cols = ColumnDefinitions(info).Count(x => !(x.Kind == TokenKind.Word && ColumnDefLead.Contains(x.Text)));
                        return "Creates the table " + T(info.Targets.FirstOrDefault()) + (cols > 0 ? " with " + cols + (cols == 1 ? " column" : " columns") : "") + ".";
                    }
                    if (info.Object == "VIEW") return "Creates the view " + T(info.Targets.FirstOrDefault()) + ": a saved SELECT you can query like a table.";
                    if (info.Object == "INDEX") return "Adds an index to " + T(info.Targets.FirstOrDefault()) + " so searches on it are faster.";
                    if (info.IsRoutineBody) return "Creates a stored " + info.Object.ToLowerInvariant() + ".";
                    return "Creates " + info.Object.ToLowerInvariant() + ".";
                case "DROP":
                    if (info.Object == "DATABASE") return "Deletes the database " + info.DatabaseName + " and every table in it.";
                    if (info.Object == "TABLE") return "Deletes " + string.Join(", ", info.Targets.Select(T)) + " and all " + (info.Targets.Count > 1 ? "their" : "its") + " rows. Undo keeps a copy.";
                    if (info.Object == "VIEW") return "Deletes the view " + T(info.Targets.FirstOrDefault()) + " (the data stays).";
                    return "Deletes " + info.Object.ToLowerInvariant() + ".";
                case "ALTER":
                    return "Changes the structure of " + T(info.Targets.FirstOrDefault()) + (info.IsDropColumn ? " and deletes a column's data" : "") + ". Undo keeps a copy.";
                case "RENAME":
                    return "Renames " + string.Join(", ", info.Targets.Select(T)) + " to " + string.Join(", ", info.NewNames.Select(T)) + ".";
                case "USE":
                    return "Switches to the database " + info.DatabaseName + ".";
                case "SHOW":
                    return "Shows information from the server. Read-only.";
                case "DESCRIBE":
                    return "Lists the columns of " + T(main.FirstOrDefault()) + ". Read-only.";
                case "EXPLAIN":
                    return "Shows how MariaDB will run the query. Read-only.";
                case "BEGIN":
                case "START":
                    return "Starts a transaction: changes stay private until COMMIT.";
                case "COMMIT":
                    return "Saves the transaction's changes for good.";
                case "ROLLBACK":
                    return "Throws away the transaction's changes.";
                case "SET":
                    return "Sets a variable or option for this session.";
                case "CALL":
                    return "Runs a stored procedure.";
                case "LOAD":
                    return "Loads rows from a file into " + T(info.Targets.FirstOrDefault()) + ".";
                case "GRANT":
                case "REVOKE":
                    return "Changes what a user is allowed to do.";
                default:
                    return "";
            }
        }

        private static string Join(List<string> parts)
        {
            if (parts.Count <= 1) return parts.FirstOrDefault() ?? "";
            return string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[parts.Count - 1];
        }
    }

    /// <summary>The schema as the script would leave it: tables made or dropped earlier count.</summary>
    public sealed class KnownObjects
    {
        private readonly SchemaSnapshot _schema;
        private readonly HashSet<string> _createdDbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _droppedDbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _createdTables = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _droppedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _forgottenColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _tableMadeAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _dbMadeAt = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public KnownObjects(SchemaSnapshot schema) { _schema = schema ?? SchemaSnapshot.Empty; }

        public bool IsLoaded => _schema.IsLoaded;

        /// <summary>Where the script creates this table, when the server doesn't have it (yet); otherwise -1.</summary>
        public int ScriptOnlyTable(string db, string table)
        {
            if (!_schema.IsLoaded || db == null || table == null) return -1;
            var k = Key(db, table);
            if (!_createdTables.ContainsKey(k) || !_tableMadeAt.TryGetValue(k, out var at)) return -1;
            return _schema.FindTable(db, table) == null ? at : -1;
        }

        /// <summary>Where the script creates this database, when the server doesn't have it (yet); otherwise -1.</summary>
        public int ScriptOnlyDatabase(string db)
        {
            if (!_schema.IsLoaded || db == null || !_createdDbs.Contains(db) || !_dbMadeAt.TryGetValue(db, out var at)) return -1;
            return _schema.Find(db) == null ? at : -1;
        }

        private static string Key(string db, string t) => db + "\u0001" + t;

        public bool DbExists(string db)
        {
            if (db == null) return false;
            if (SchemaSnapshot.IsSystem(db)) return true;
            if (_createdDbs.Contains(db)) return true;
            return !_droppedDbs.Contains(db) && _schema.Find(db) != null;
        }

        /// <summary>True/false, or null when we can't tell (schema not loaded).</summary>
        public bool? TableExists(string db, string table)
        {
            if (!_schema.IsLoaded || db == null || table == null) return null;
            var k = Key(db, table);
            if (_createdTables.ContainsKey(k)) return true;
            if (_droppedTables.Contains(k) || _droppedDbs.Contains(db) && !_createdDbs.Contains(db)) return false;
            return _schema.FindTable(db, table) != null;
        }

        public IList<string> Columns(string db, string table)
        {
            if (db == null || table == null) return null;
            var k = Key(db, table);
            if (_forgottenColumns.Contains(k)) return null;
            if (_createdTables.TryGetValue(k, out var cols)) return cols;
            if (_droppedTables.Contains(k)) return null;
            var t = _schema.FindTable(db, table);
            return t?.Columns.Select(c => c.Name).ToList();
        }

        public long RowEstimate(string db, string table) => _schema.FindTable(db, table)?.RowEstimate ?? 0;

        public string PrimaryKey(string db, string table)
        {
            var t = _schema.FindTable(db, table);
            var pk = t?.PrimaryKey.FirstOrDefault();
            return pk == null ? null : SqlLexer.IsPlainName(pk.Name) ? pk.Name : SqlLexer.Quote(pk.Name);
        }

        public IEnumerable<string> TableNames(string db)
        {
            var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var d = _schema.Find(db);
            if (d != null && !(_droppedDbs.Contains(db) && !_createdDbs.Contains(db)))
                foreach (var t in d.Tables.Keys) if (!_droppedTables.Contains(Key(db, t))) set.Add(t);
            foreach (var k in _createdTables.Keys)
            {
                var parts = k.Split('\u0001');
                if (string.Equals(parts[0], db, StringComparison.OrdinalIgnoreCase)) set.Add(parts[1]);
            }
            return set;
        }

        public IEnumerable<string> DatabaseNames() => _schema.Databases.Keys.Where(d => !_droppedDbs.Contains(d)).Concat(_createdDbs).Distinct(StringComparer.OrdinalIgnoreCase);

        /// <summary>Every table and column name in a database (for the clause typo check).</summary>
        public HashSet<string> AllNames(string db)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (db == null) return set;
            foreach (var t in TableNames(db))
            {
                set.Add(t);
                var cols = Columns(db, t);
                if (cols != null) foreach (var c in cols) set.Add(c);
            }
            foreach (var d in DatabaseNames()) set.Add(d);
            return set;
        }

        public void CreateDatabase(string db, int at = -1) { _createdDbs.Add(db); _droppedDbs.Remove(db); if (at >= 0) _dbMadeAt[db] = at; else _dbMadeAt.Remove(db); }
        public void DropDatabase(string db) { _droppedDbs.Add(db); _createdDbs.Remove(db); }

        public void CreateTable(string db, string table, List<string> columns, int at = -1)
        {
            var k = Key(db, table);
            _createdTables[k] = columns;
            if (at >= 0) _tableMadeAt[k] = at; else _tableMadeAt.Remove(k);
            _droppedTables.Remove(k);
            if (columns == null) _forgottenColumns.Add(k); else _forgottenColumns.Remove(k);
        }

        public void DropTable(string db, string table)
        {
            var k = Key(db, table);
            _createdTables.Remove(k);
            _droppedTables.Add(k);
        }

        public void ForgetColumns(string db, string table) => _forgottenColumns.Add(Key(db, table));
    }
}
