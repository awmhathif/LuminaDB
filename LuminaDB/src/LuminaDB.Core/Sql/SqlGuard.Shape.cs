using System;
using System.Collections.Generic;
using System.Linq;

namespace LuminaDB.Core.Sql
{
    /// <summary>
    /// The shape of each command: the words a command can't do without (FROM, SET, VALUES, BY, TABLE), clauses in the
    /// wrong order, a ; that cut a command in two, a column used where a table belongs, text without quotes, == and
    /// AND between changes. Each note says what was probably meant and offers the corrected SQL.
    /// </summary>
    public sealed partial class SqlGuard
    {
        // ------------------------------------------------------------------ a clause on its own

        private static readonly HashSet<string> ClauseOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "FROM", "WHERE", "SET", "VALUES", "VALUE", "ORDER", "GROUP", "HAVING", "LIMIT", "AND", "OR", "JOIN", "INNER",
            "LEFT", "RIGHT", "ON", "INTO", "OFFSET",
        };

        /// <summary>
        /// "FROM products;" on its own line: almost always the ; above cut the command short. True when it reported.
        /// </summary>
        private bool CheckOrphanClause(SqlAnalysis a, SqlStatement s, StatementInfo info, KnownObjects known, List<SqlIssue> issues)
        {
            var tok = info.Tokens;
            var first = tok[0];
            if (first.Kind != TokenKind.Word || !ClauseOnly.Contains(first.Text)) return false;
            int idx = a.Statements.IndexOf(s);
            var prev = idx > 0 ? a.Statements[idx - 1] : null;
            var prevInfo = idx > 0 ? a.Infos[idx - 1] : null;

            // SET and VALUES are commands of their own too (SET @x = 1, SET NAMES, VALUES (1),(2))
            if (first.IsWord("SET") && !(prevInfo != null && prevInfo.Verb == "UPDATE" && !prevInfo.Tokens.Any(t => t.IsWord("SET")))) return false;
            if ((first.IsWord("VALUES") || first.IsWord("VALUE")) &&
                !(prevInfo != null && (prevInfo.Verb == "INSERT" || prevInfo.Verb == "REPLACE") &&
                  !prevInfo.Tokens.Any(t => t.IsWord("VALUES") || t.IsWord("VALUE") || t.IsWord("SELECT") || t.IsWord("SET")))) return false;
            // RIGHT(...)/LEFT(...) are functions, but a command can't start with them either: still a clause on its own

            string w = first.Upper;
            int cut = prev == null ? -1 : DelimiterAfter(a.Script, prev, s.Start);
            if (cut >= 0 && prevInfo.Kind != StatementKind.Empty && !prevInfo.IsRoutineBody)
            {
                int line = SqlStatement.LineOf(a.Script, cut);
                issues.Add(new SqlIssue
                {
                    Severity = IssueSeverity.Bad, Code = "cut", Start = first.Start, Length = first.Length,
                    Message = w + " belongs to the command above: the " + prev.Delimiter + " on line " + line + " ended it too early.",
                    FixLabel = "Remove the " + prev.Delimiter,
                    Fixes = { new TextFix(cut, prev.Delimiter.Length, "") },
                });
                return true;
            }

            var issue = new SqlIssue { Severity = IssueSeverity.Bad, Code = "cut", Start = first.Start, Length = first.Length };
            if (first.IsWord("FROM"))
            {
                issue.Message = "FROM can't start a command. To read rows, start with SELECT: SELECT * FROM …";
                issue.FixLabel = "Add SELECT *";
                issue.Fixes.Add(new TextFix(first.Start, 0, "SELECT * "));
            }
            else if (first.IsWord("INTO"))
            {
                issue.Message = "INTO can't start a command. To add rows, start with INSERT: INSERT INTO …";
                issue.FixLabel = "Add INSERT";
                issue.Fixes.Add(new TextFix(first.Start, 0, "INSERT "));
            }
            else issue.Message = w + " can't start a command: it's part of a SELECT, UPDATE or DELETE. Put it after the command it belongs to.";
            issues.Add(issue);
            return true;
        }

        /// <summary>Script offset of the delimiter that ended <paramref name="prev"/>, or -1.</summary>
        private static int DelimiterAfter(string script, SqlStatement prev, int limit)
        {
            if (!prev.Terminated || string.IsNullOrEmpty(prev.Delimiter)) return -1;
            int at = script.IndexOf(prev.Delimiter, prev.End, StringComparison.Ordinal);
            return at >= 0 && at < limit ? at : -1;
        }

        // ------------------------------------------------------------------ per command

        private void CheckShape(SqlStatement s, StatementInfo info, KnownObjects known, List<SqlIssue> issues, bool typingHere)
        {
            var tok = info.Tokens;
            if (info.IsRoutineBody || tok.Count == 0) return;
            int before = issues.Count;

            CheckDoubleEquals(tok, issues);
            CheckMissingBy(tok, issues, typingHere);
            switch (info.Verb)
            {
                case "SELECT": ShapeSelect(info, known, issues, typingHere); break;
                case "DELETE": ShapeDelete(s, info, known, issues, typingHere); break;
                case "UPDATE": ShapeUpdate(info, known, issues, typingHere); break;
                case "INSERT":
                case "REPLACE": ShapeInsert(info, known, issues, typingHere); break;
                case "CREATE": ShapeCreate(info, known, issues, typingHere); break;
                case "DROP":
                case "ALTER": ShapeDropAlter(info, known, issues, typingHere); break;
                case "USE":
                    if (tok.Count == 1 && !typingHere)
                        issues.Add(Bad("shape", tok[0], "USE which database? Name it: USE " + (known.DatabaseNames().FirstOrDefault(d => !SchemaSnapshot.IsSystem(d)) ?? "your_database") + ";"));
                    break;
            }
            if (info.Verb == "CREATE" || info.Verb == "ALTER") CheckColumnTypes(info, issues);
            if (issues.Count > before) return;   // one shape problem at a time: the next one may be a result of it

            if (info.Verb == "SELECT" || info.Verb == "UPDATE" || info.Verb == "DELETE")
            {
                CheckEmptyClauses(info, issues, typingHere);
                if (issues.Count > before) return;
                CheckClauseOrder(s, info, issues);
            }
            // (SET GLOBAL x = ON is a value, not a dangling ON: only the commands that read or change rows)
            if (issues.Count == before && (info.Verb == "SELECT" || info.Verb == "UPDATE" || info.Verb == "DELETE" || info.Verb == "INSERT" || info.Verb == "REPLACE"))
                CheckDanglingEnd(tok, issues, typingHere);
        }

        private static SqlIssue Bad(string code, SqlToken at, string message) =>
            new SqlIssue { Severity = IssueSeverity.Bad, Code = code, Start = at.Start, Length = Math.Max(1, at.Length), Message = message };

        private static bool IsW(List<SqlToken> tok, int i, string w) => i >= 0 && i < tok.Count && tok[i].IsWord(w);
        private static bool IsS(List<SqlToken> tok, int i, string s) => i >= 0 && i < tok.Count && tok[i].IsSymbol(s);

        /// <summary>A name the user chose (not an SQL word).</summary>
        private static bool IsUserName(SqlToken t) =>
            t.Kind == TokenKind.QuotedIdent || t.Kind == TokenKind.Word && !SqlKeywords.Reserved.Contains(t.Text);

        private static string Q(string name) => SqlLexer.IsPlainName(name) ? name : SqlLexer.Quote(name);
        private static string Text(string value) => "'" + (value ?? "").Replace("'", "''") + "'";

        /// <summary>Index of a word at bracket depth 0, from <paramref name="from"/>; -1 if none.</summary>
        private static int Top(List<SqlToken> tok, string word, int from = 0)
        {
            int depth = 0;
            for (int k = 0; k < tok.Count; k++)
            {
                if (tok[k].IsSymbol("(")) { depth++; continue; }
                if (tok[k].IsSymbol(")")) { depth = Math.Max(0, depth - 1); continue; }
                if (k >= from && depth == 0 && tok[k].IsWord(word)) return k;
            }
            return -1;
        }

        private static int[] Depths(List<SqlToken> tok)
        {
            var d = new int[tok.Count];
            int depth = 0;
            for (int k = 0; k < tok.Count; k++)
            {
                if (tok[k].IsSymbol(")")) depth = Math.Max(0, depth - 1);
                d[k] = depth;
                if (tok[k].IsSymbol("(")) depth++;
            }
            return d;
        }

        private static int SkipParens(List<SqlToken> tok, int k)
        {
            int depth = 0;
            for (; k < tok.Count; k++)
            {
                if (tok[k].IsSymbol("(")) depth++;
                else if (tok[k].IsSymbol(")")) { depth--; if (depth == 0) return k + 1; }
            }
            return k;
        }

        private static List<string> TablesWithColumn(KnownObjects known, string db, string column)
        {
            if (db == null || column == null || !known.IsLoaded) return new List<string>();
            return known.TableNames(db).Where(t => known.Columns(db, t)?.Contains(column, StringComparer.OrdinalIgnoreCase) == true).ToList();
        }

        private static string TableHint(KnownObjects known, string db)
        {
            if (db == null || !known.IsLoaded) return "";
            var names = known.TableNames(db).ToList();
            if (names.Count == 0) return " " + db + " has no tables yet.";
            if (names.Count <= 6) return " Tables here: " + string.Join(", ", names) + ".";
            return "";
        }

        // ------------------------------------------------------------------ == and missing BY

        private static void CheckDoubleEquals(List<SqlToken> tok, List<SqlIssue> issues)
        {
            for (int k = 0; k + 1 < tok.Count; k++)
            {
                if (!tok[k].IsSymbol("=") || !tok[k + 1].IsSymbol("=") || tok[k].End != tok[k + 1].Start) continue;
                int end = k + 1;
                while (end + 1 < tok.Count && tok[end + 1].IsSymbol("=") && tok[end].End == tok[end + 1].Start) end++;
                issues.Add(new SqlIssue
                {
                    Severity = IssueSeverity.Bad, Code = "equals", Start = tok[k].Start, Length = tok[end].End - tok[k].Start,
                    Message = "SQL compares with one =, not " + new string('=', end - k + 1) + ".",
                    FixLabel = "Use =", Fixes = { new TextFix(tok[k].Start, tok[end].End - tok[k].Start, "=") },
                });
                k = end;
            }
        }

        private static void CheckMissingBy(List<SqlToken> tok, List<SqlIssue> issues, bool typingHere)
        {
            for (int k = 0; k < tok.Count; k++)
            {
                var t = tok[k];
                if (!(t.IsWord("ORDER") || t.IsWord("GROUP")) || k > 0 && tok[k - 1].IsSymbol(".")) continue;
                // FROM order: a table called order (the reserved-word check asks for backticks)
                if (k > 0 && (tok[k - 1].IsWord("FROM") || tok[k - 1].IsWord("JOIN") || tok[k - 1].IsWord("INTO") || tok[k - 1].IsWord("TABLE") ||
                              tok[k - 1].IsWord("UPDATE") || tok[k - 1].IsSymbol(","))) continue;
                if (k + 1 >= tok.Count) { if (typingHere) continue; }
                else if (tok[k + 1].IsWord("BY") || !IsUserName(tok[k + 1]) && !tok[k + 1].IsSymbol("(") && tok[k + 1].Kind != TokenKind.Number) continue;
                issues.Add(new SqlIssue
                {
                    Severity = IssueSeverity.Bad, Code = "by", Start = t.Start, Length = t.Length,
                    Message = t.Upper + " needs BY after it: " + t.Upper + " BY " + (k + 1 < tok.Count ? tok[k + 1].Text : "column") + ".",
                    FixLabel = "Add BY", Fixes = { new TextFix(t.End, 0, " BY") },
                });
            }
        }

        // ------------------------------------------------------------------ SELECT

        private void ShapeSelect(StatementInfo info, KnownObjects known, List<SqlIssue> issues, bool typingHere)
        {
            var tok = info.Tokens;
            if (!tok[0].IsWord("SELECT")) return;   // (SELECT ...) UNION ...
            if (tok.Count == 1)
            {
                if (!typingHere) issues.Add(Bad("shape", tok[0], "SELECT what? For example: SELECT * FROM your_table;"));
                return;
            }
            int k = 1;
            while (IsW(tok, k, "DISTINCT") || IsW(tok, k, "ALL") || IsW(tok, k, "DISTINCTROW") || IsW(tok, k, "SQL_CALC_FOUND_ROWS")) k++;
            if (IsW(tok, k, "FROM"))
            {
                var i = Bad("shape", tok[k], "Which columns? Name them, or use * for all of them: SELECT * FROM …");
                i.FixLabel = "Add *";
                i.Fixes.Add(new TextFix(tok[k].Start, 0, "* "));
                issues.Add(i);
                return;
            }

            int from = Top(tok, "FROM");
            string db = info.CurrentDatabase;
            if (from < 0)
            {
                // WHERE / GROUP BY / ORDER BY need a table
                int clause = new[] { Top(tok, "WHERE"), Top(tok, "GROUP"), Top(tok, "HAVING"), Top(tok, "ORDER") }.Where(x => x > 0).DefaultIfEmpty(-1).Min();
                if (clause < 0) return;
                var named = tok.Take(clause).Skip(k).Where(IsUserName).Select(t => t.IdentName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var owners = named.Count == 0 ? new List<string>() :
                    known.TableNames(db ?? "").Where(t => named.All(n => known.Columns(db, t)?.Contains(n, StringComparer.OrdinalIgnoreCase) == true)).ToList();
                var issue = Bad("shape", tok[clause], tok[clause].Upper + " needs a table to look in: add FROM your_table before " + tok[clause].Upper + "." +
                                (owners.Count == 1 ? " These columns are in " + owners[0] + "." : TableHint(known, db)));
                if (owners.Count == 1) { issue.FixLabel = "Add FROM " + owners[0]; issue.Fixes.Add(new TextFix(tok[clause].Start, 0, "FROM " + Q(owners[0]) + " ")); }
                issues.Add(issue);
                return;
            }

            // a missing comma between two columns: SELECT name price FROM ... shows name under the heading "price"
            var refs = info.Tables.Where(r => !r.IsDerived && !r.IsReference && !r.IsReservedWord && r.Name != null && r.ResolvedDatabase != null).ToList();
            if (refs.Count == 0 || info.CteNames.Count > 0) return;
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in refs)
            {
                var c = known.Columns(r.ResolvedDatabase, r.Name);
                if (c == null) return;   // a table we can't see into: don't guess
                foreach (var n in c) cols.Add(n);
            }
            var qualifiers = new HashSet<string>(refs.Select(r => r.Alias ?? r.Name), StringComparer.OrdinalIgnoreCase);
            var depth = Depths(tok);
            for (int j = k; j + 1 < from; j++)
            {
                var x = tok[j];
                var y = tok[j + 1];
                if (depth[j] != 0 || !IsUserName(x) || !IsUserName(y)) continue;
                if (IsS(tok, j + 1 + 1, "(") || IsS(tok, j + 1, "(")) continue;
                if (j > 0 && (tok[j - 1].IsWord("AS") || IsS(tok, j + 2, ".") && !qualifiers.Contains(y.IdentName))) continue;
                bool xIsColumn = cols.Contains(x.IdentName) && !IsS(tok, j + 1, ".");
                bool yIsColumn = IsS(tok, j + 2, ".") ? qualifiers.Contains(y.IdentName) : cols.Contains(y.IdentName);
                if (!xIsColumn || !yIsColumn) continue;
                var issue = Bad("comma", y, "Missing comma between " + x.IdentName + " and " + y.IdentName + ". Without it, MariaDB shows " +
                                x.IdentName + " under the heading \"" + y.IdentName + "\" and leaves " + y.IdentName + " out.");
                issue.FixLabel = "Add ,";
                issue.Fixes.Add(new TextFix(x.End, 0, ","));
                issues.Add(issue);
                return;
            }
        }

        // ------------------------------------------------------------------ DELETE

        private void ShapeDelete(SqlStatement s, StatementInfo info, KnownObjects known, List<SqlIssue> issues, bool typingHere)
        {
            var tok = info.Tokens;
            int k = 1;
            while (IsW(tok, k, "LOW_PRIORITY") || IsW(tok, k, "QUICK") || IsW(tok, k, "IGNORE")) k++;
            string db = info.CurrentDatabase;
            if (k >= tok.Count)
            {
                if (!typingHere) issues.Add(Bad("shape", tok[0], "DELETE what? Write DELETE FROM your_table WHERE …;" + TableHint(known, db)));
                return;
            }
            if (tok[k].IsSymbol("*"))
            {
                var star = Bad("shape", tok[k], "DELETE removes whole rows, so it takes no * (that's for SELECT): DELETE FROM your_table WHERE …");
                int to = k + 1 < tok.Count ? tok[k + 1].Start : tok[k].End;
                star.FixLabel = "Remove *";
                star.Fixes.Add(new TextFix(tok[k].Start, to - tok[k].Start, ""));
                issues.Add(star);
                return;
            }

            int from = Top(tok, "FROM");
            if (from < 0)
            {
                var t = tok[k];
                if (!IsUserName(t))
                {
                    issues.Add(Bad("shape", t, "DELETE needs FROM and the table: DELETE FROM your_table WHERE …"));
                    return;
                }
                string n = t.IdentName;
                if (!known.IsLoaded || known.TableExists(db, n) == true)
                {
                    var i = Bad("from", t, "DELETE needs FROM before the table: DELETE FROM " + n + " …");
                    i.FixLabel = "Add FROM";
                    i.Fixes.Add(new TextFix(t.Start, 0, "FROM "));
                    issues.Add(i);
                    return;
                }
                var owners = TablesWithColumn(known, db, n);
                if (owners.Count > 0) { issues.Add(ColumnNotRows(s, tok, k, n, owners[0], owners.Count)); return; }
                var guess = SqlKeywords.Closest(n, known.TableNames(db ?? ""));
                var issue = Bad("from", t, "DELETE needs FROM and a table, and there's no table called " + n + "." +
                                (guess != null ? " Did you mean " + guess + "?" : TableHint(known, db)));
                if (guess != null) { issue.FixLabel = "Use FROM " + guess; issue.Fixes.Add(new TextFix(t.Start, t.Length, "FROM " + Q(guess))); }
                issues.Add(issue);
                return;
            }

            if (from == tok.Count - 1 || !IsUserName(tok[from + 1]) && !tok[from + 1].IsSymbol("("))
            {
                if (!(typingHere && from == tok.Count - 1))
                    issues.Add(Bad("shape", tok[from], "Which table? DELETE FROM needs the table's name." + TableHint(known, db)));
                return;
            }

            // DELETE price FROM products: names before FROM must be tables, not columns
            if (from > k)
            {
                var main = info.Tables.FirstOrDefault(r => !r.IsDerived && !r.IsReference && !r.IsReservedWord && r.NameStart >= tok[from].End);
                if (main == null || main.ResolvedDatabase == null) return;
                var cols = known.Columns(main.ResolvedDatabase, main.Name);
                if (cols == null) return;
                for (int j = k; j < from; j++)
                {
                    if (!IsUserName(tok[j]) || IsS(tok, j + 1, ".") || IsS(tok, j - 1, ".")) continue;
                    var n = tok[j].IdentName;
                    if (info.Tables.Any(r => string.Equals(r.Alias, n, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Name, n, StringComparison.OrdinalIgnoreCase))) continue;
                    if (!cols.Contains(n, StringComparer.OrdinalIgnoreCase)) continue;
                    issues.Add(ColumnNotRows(s, tok, j, n, main.Name, 1));
                    return;
                }
            }
        }

        /// <summary>"DELETE price": a column where DELETE wants rows. Offers the three things it could have meant.</summary>
        private static SqlIssue ColumnNotRows(SqlStatement s, List<SqlToken> tok, int at, string column, string table, int owners)
        {
            int start = tok[0].Start, end = tok[tok.Count - 1].End;
            int where = Top(tok, "WHERE");
            string whereText = where >= 0 ? " " + s.Text.Substring(tok[where].Start - s.Start, end - tok[where].Start) : "";
            string t = Q(table), c = Q(column);
            var issue = Bad("delete-column", tok[at],
                column + " is a column of " + table + (owners > 1 ? " (and other tables)" : "") + ", not a table: DELETE removes whole rows. What did you mean?");
            issue.FixLabel = "Empty " + column;
            issue.Fixes.Add(new TextFix(start, end - start, "UPDATE " + t + " SET " + c + " = NULL" + whereText));
            issue.MoreFixes.Add(new SqlFixOption("Delete whole rows", new TextFix(start, end - start, "DELETE FROM " + t + whereText)));
            issue.MoreFixes.Add(new SqlFixOption("Remove the column", new TextFix(start, end - start, "ALTER TABLE " + t + " DROP COLUMN " + c)));
            return issue;
        }

        // ------------------------------------------------------------------ UPDATE

        private void ShapeUpdate(StatementInfo info, KnownObjects known, List<SqlIssue> issues, bool typingHere)
        {
            var tok = info.Tokens;
            int k = 1;
            while (IsW(tok, k, "LOW_PRIORITY") || IsW(tok, k, "IGNORE")) k++;
            string db = info.CurrentDatabase;
            if (k >= tok.Count)
            {
                if (!typingHere) issues.Add(Bad("shape", tok[0], "UPDATE which table? UPDATE your_table SET column = value WHERE …;" + TableHint(known, db)));
                return;
            }
            int set = Top(tok, "SET");
            if (set == k)
            {
                // UPDATE SET price = 5: guess the table from the columns being set
                var named = new List<string>();
                for (int j = set + 1; j + 1 < tok.Count; j++) if (IsUserName(tok[j]) && tok[j + 1].IsSymbol("=")) named.Add(tok[j].IdentName);
                var owners = named.Count == 0 ? new List<string>() :
                    known.TableNames(db ?? "").Where(t => named.All(n => known.Columns(db, t)?.Contains(n, StringComparer.OrdinalIgnoreCase) == true)).ToList();
                var issue = Bad("shape", tok[set], "UPDATE needs the table's name before SET: UPDATE your_table SET …" +
                                (owners.Count == 1 ? " These columns are in " + owners[0] + "." : TableHint(known, db)));
                if (owners.Count == 1) { issue.FixLabel = "Add " + owners[0]; issue.Fixes.Add(new TextFix(tok[set].Start, 0, Q(owners[0]) + " ")); }
                issues.Add(issue);
                return;
            }
            var depth = Depths(tok);
            if (set < 0)
            {
                for (int j = k + 1; j + 1 < tok.Count; j++)
                {
                    if (depth[j] != 0 || !IsUserName(tok[j])) continue;
                    bool plain = tok[j + 1].IsSymbol("=");
                    bool qualified = IsS(tok, j + 1, ".") && IsS(tok, j + 3, "=");
                    if (!plain && !qualified) continue;
                    var issue = Bad("set", tok[j], "UPDATE needs SET before the changes: UPDATE " + tok[k].Text + " SET " + tok[j].Text + " = …");
                    issue.FixLabel = "Add SET";
                    issue.Fixes.Add(new TextFix(tok[j].Start, 0, "SET "));
                    issues.Add(issue);
                    return;
                }
                if (!typingHere) issues.Add(Bad("set", tok[0], "UPDATE needs SET and the change to make: UPDATE " + tok[k].Text + " SET column = value WHERE …"));
                return;
            }
            if (set == tok.Count - 1 || IsW(tok, set + 1, "WHERE"))
            {
                if (!(typingHere && set == tok.Count - 1)) issues.Add(Bad("set", tok[set], "SET needs a change after it, like SET price = 5."));
                return;
            }

            int end = new[] { Top(tok, "WHERE", set), Top(tok, "ORDER", set), Top(tok, "LIMIT", set) }.Where(x => x > 0).DefaultIfEmpty(tok.Count).Min();
            bool between = false;
            int inCase = 0;   // CASE WHEN a AND b THEN ... END: that AND is a condition
            for (int j = set + 1; j < end; j++)
            {
                if (depth[j] != 0) continue;
                var t = tok[j];
                if (t.IsWord("CASE")) { inCase++; continue; }
                if (t.IsWord("END") && inCase > 0) { inCase--; continue; }
                if (inCase > 0) continue;
                if (t.IsWord("BETWEEN")) { between = true; continue; }
                if (t.IsWord("AND"))
                {
                    if (between) { between = false; continue; }
                    if (j + 2 < end && IsUserName(tok[j + 1]) && tok[j + 2].IsSymbol("="))
                    {
                        var issue = Bad("set-and", t, "Use a comma between changes, not AND. With AND, MariaDB reads it as one true/false sum and stores 1 or 0 in " +
                                        (IsUserName(tok[set + 1]) ? tok[set + 1].IdentName : "the first column") + ".");
                        issue.FixLabel = "Use ,";
                        issue.Fixes.Add(new TextFix(tok[j - 1].End, t.End - tok[j - 1].End, ","));
                        issues.Add(issue);
                        return;
                    }
                }
                if (IsUserName(t) && (j == set + 1 || IsS(tok, j - 1, ",")) && j + 1 < end &&
                    (tok[j + 1].Kind == TokenKind.Number || tok[j + 1].Kind == TokenKind.String))
                {
                    var issue = Bad("set", tok[j + 1], "Missing = between " + t.IdentName + " and the new value: SET " + t.IdentName + " = " + tok[j + 1].Text + ".");
                    issue.FixLabel = "Add =";
                    issue.Fixes.Add(new TextFix(tok[j + 1].Start, 0, "= "));
                    issues.Add(issue);
                    return;
                }
            }
        }

        // ------------------------------------------------------------------ INSERT

        private void ShapeInsert(StatementInfo info, KnownObjects known, List<SqlIssue> issues, bool typingHere)
        {
            var tok = info.Tokens;
            int k = 1;
            while (IsW(tok, k, "LOW_PRIORITY") || IsW(tok, k, "DELAYED") || IsW(tok, k, "HIGH_PRIORITY") || IsW(tok, k, "IGNORE")) k++;
            if (IsW(tok, k, "INTO")) k++;
            string db = info.CurrentDatabase;
            string verb = tok[0].Upper;
            if (k >= tok.Count)
            {
                if (!typingHere) issues.Add(Bad("shape", tok[k - 1], verb + " INTO which table? " + verb + " INTO your_table (columns) VALUES (…);" + TableHint(known, db)));
                return;
            }
            if (!IsUserName(tok[k]))
            {
                if (tok[k].IsWord("VALUES") || tok[k].IsWord("VALUE") || tok[k].IsSymbol("(") || tok[k].IsWord("SET") || tok[k].IsWord("SELECT"))
                    issues.Add(Bad("shape", tok[k], "The table's name is missing: " + verb + " INTO your_table " + tok[k].Upper + " …" + TableHint(known, db)));
                return;
            }
            int j = k + 1;
            if (IsS(tok, j, ".") && j + 1 < tok.Count) j += 2;   // db.table
            int values = new[] { Top(tok, "VALUES"), Top(tok, "VALUE") }.Where(x => x > 0).DefaultIfEmpty(-1).Min();
            bool other = Top(tok, "SELECT") > 0 || Top(tok, "SET") > 0 || Top(tok, "WITH") > 0 || Top(tok, "TABLE") > 0 ||
                         tok.Skip(j).Any(t => t.IsWord("SELECT"));
            var target = info.Targets.FirstOrDefault();
            var cols = target?.ResolvedDatabase == null ? null : known.Columns(target.ResolvedDatabase, target.Name);

            if (values < 0 && !other)
            {
                if (j >= tok.Count)
                {
                    if (!typingHere) issues.Add(Bad("values", tok[0], "Now the values to add: " + verb + " INTO " + tok[k].Text + " VALUES (…);"));
                    return;
                }
                if (tok[j].IsSymbol("("))
                {
                    int close = SkipParens(tok, j);
                    if (close < tok.Count && tok[close].IsSymbol("("))
                    {
                        var i = Bad("values", tok[close], "Missing VALUES before the row: … (columns) VALUES (values).");
                        i.FixLabel = "Add VALUES";
                        i.Fixes.Add(new TextFix(tok[close].Start, 0, "VALUES "));
                        issues.Add(i);
                        return;
                    }
                    if (close >= tok.Count)
                    {
                        bool looksLikeValues = tok.Skip(j + 1).Take(close - j - 2).Any(t => t.Kind == TokenKind.Number || t.Kind == TokenKind.String || t.IsWord("NULL"));
                        if (looksLikeValues)
                        {
                            var i = Bad("values", tok[j], "Missing VALUES before the brackets: " + verb + " INTO " + tok[k].Text + " VALUES (…).");
                            i.FixLabel = "Add VALUES";
                            i.Fixes.Add(new TextFix(tok[j].Start, 0, "VALUES "));
                            issues.Add(i);
                        }
                        else if (!typingHere)
                            issues.Add(Bad("values", tok[j], "Those are the columns. Now add the values: … (" + string.Join(", ", tok.Skip(j + 1).Take(close - j - 2).Where(IsUserName).Select(t => t.IdentName)) + ") VALUES (…);"));
                        return;
                    }
                }
                var bare = Bad("values", tok[j], "Missing VALUES and brackets: " + verb + " INTO " + tok[k].Text + " VALUES (" + tok[j].Text + ", …);");
                if (tok[j].Kind == TokenKind.Number || tok[j].Kind == TokenKind.String)
                {
                    bare.FixLabel = "Add VALUES ( )";
                    bare.Fixes.Add(new TextFix(tok[j].Start, 0, "VALUES ("));
                    bare.Fixes.Add(new TextFix(tok[tok.Count - 1].End, 0, ")"));
                }
                issues.Add(bare);
                return;
            }
            if (values < 0) return;

            if (values == tok.Count - 1)
            {
                if (!typingHere) issues.Add(Bad("values", tok[values], "VALUES needs the row in brackets, like VALUES ('Tea', 450)."));
                return;
            }
            if (!tok[values + 1].IsSymbol("("))
            {
                var i = Bad("values", tok[values + 1], "Each row goes in brackets: VALUES (" + tok[values + 1].Text + ", …).");
                if (!tok.Skip(values + 1).Any(t => t.IsSymbol("(")))
                {
                    i.FixLabel = "Add ( )";
                    i.Fixes.Add(new TextFix(tok[values + 1].Start, 0, "("));
                    i.Fixes.Add(new TextFix(tok[tok.Count - 1].End, 0, ")"));
                }
                issues.Add(i);
                return;
            }

            // text without quotes inside the rows: VALUES (Tea, 450)
            var depth = Depths(tok);
            for (int v = values + 1; v < tok.Count; v++)
            {
                var t = tok[v];
                if (depth[v] == 0 && t.Kind == TokenKind.Word) break;   // AS new(a, b) / ON DUPLICATE KEY UPDATE: past the rows
                if (depth[v] != 1 || t.Kind != TokenKind.Word || SqlKeywords.IsVocabulary(t.Text)) continue;
                if (IsS(tok, v + 1, "(") || IsS(tok, v + 1, ".") || IsS(tok, v - 1, ".")) continue;
                if (cols != null && cols.Contains(t.Text, StringComparer.OrdinalIgnoreCase)) continue;
                if (IsW(tok, v - 1, "ON")) break;   // ON DUPLICATE KEY UPDATE ...
                var i = Bad("text-quote", t, t.Text + " is text, so it needs quotes: " + Text(t.Text) + ". Without them MariaDB looks for a column called " + t.Text + ".");
                i.FixLabel = "Use " + Text(t.Text);
                i.Fixes.Add(new TextFix(t.Start, t.Length, Text(t.Text)));
                issues.Add(i);
                return;
            }
        }

        // ------------------------------------------------------------------ CREATE

        private static readonly HashSet<string> CreateWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "TABLE", "TABLES", "DATABASE", "SCHEMA", "VIEW", "INDEX", "TRIGGER", "PROCEDURE", "FUNCTION", "EVENT", "USER", "ROLE",
            "SEQUENCE", "SERVER", "PACKAGE", "TABLESPACE", "TEMPORARY", "OR", "DEFINER", "ALGORITHM", "SQL", "UNIQUE", "FULLTEXT",
            "SPATIAL", "ONLINE", "IGNORE", "AGGREGATE", "PREPARE", "COLUMN", "IF", "LOGFILE", "SYNONYM",
        };

        private void ShapeCreate(StatementInfo info, KnownObjects known, List<SqlIssue> issues, bool typingHere)
        {
            var tok = info.Tokens;
            if (tok.Count < 2)
            {
                if (!typingHere) issues.Add(Bad("shape", tok[0], "CREATE what? CREATE DATABASE name; or CREATE TABLE name (columns);"));
                return;
            }
            if (tok[1].Kind == TokenKind.QuotedIdent || tok[1].Kind == TokenKind.Word && !CreateWords.Contains(tok[1].Text) && !SqlKeywords.IsVocabulary(tok[1].Text))
            {
                var name = tok[1];
                if (IsS(tok, 2, "("))
                {
                    var i = Bad("object", name, "CREATE what? Add TABLE: CREATE TABLE " + name.Text + " (…).");
                    i.FixLabel = "Add TABLE";
                    i.Fixes.Add(new TextFix(name.Start, 0, "TABLE "));
                    issues.Add(i);
                }
                else if (!typingHere || tok.Count > 2)
                {
                    var i = Bad("object", name, "CREATE what? A database: CREATE DATABASE " + name.Text + "; or a table: CREATE TABLE " + name.Text + " (…).");
                    i.FixLabel = "Add DATABASE";
                    i.Fixes.Add(new TextFix(name.Start, 0, "DATABASE "));
                    i.MoreFixes.Add(new SqlFixOption("Add TABLE", new TextFix(name.Start, 0, "TABLE ")));
                    issues.Add(i);
                }
                return;
            }
            if (info.Object == "DATABASE")
            {
                if (info.DatabaseName == null && !typingHere) issues.Add(Bad("shape", tok[tok.Count - 1], "The database needs a name: CREATE DATABASE shop;"));
                return;
            }
            if (info.Object != "TABLE") return;
            var def = info.Targets.FirstOrDefault();
            if (def == null)
            {
                if (!typingHere) issues.Add(Bad("shape", tok[tok.Count - 1], "The table needs a name: CREATE TABLE products (id INT PRIMARY KEY, name VARCHAR(100));"));
                return;
            }
            int n = tok.FindIndex(t => t.Start == def.NameStart);
            int p = n + 1;
            if (p >= tok.Count)
            {
                if (!typingHere) issues.Add(Bad("shape", tok[n], "A new table needs its columns in brackets: CREATE TABLE " + def.Name + " (id INT PRIMARY KEY, name VARCHAR(100));"));
                return;
            }
            if (IsUserName(tok[p]) && !tok[p].IsWord("LIKE") && !tok[p].IsWord("AS") && !tok[p].IsWord("SELECT") && !tok[p].IsWord("ENGINE"))
            {
                issues.Add(Bad("shape", tok[p], "The columns go in brackets: CREATE TABLE " + def.Name + " (" + tok[p].Text + " …);"));
                return;
            }

            // each column needs a type, and a known one. Columns with no type are fixed together, so one click gives a table that can be made.
            var untyped = new List<SqlToken>();
            foreach (var c in ColumnDefinitions(info))
            {
                if (c.Kind == TokenKind.Word && ColumnDefLead.Contains(c.Text)) continue;
                int at = tok.FindIndex(t => t.Start == c.Start);
                if (at >= 0 && at + 1 < tok.Count && (tok[at + 1].IsSymbol(",") || tok[at + 1].IsSymbol(")"))) untyped.Add(c);
            }
            if (untyped.Count > 0)
            {
                var c = untyped[0];
                var i = Bad("type", c, untyped.Count == 1
                    ? c.Text + " needs a type, like " + c.Text + " " + TypeFor(c.IdentName ?? c.Text) + "."
                    : "Every column needs a type: " + string.Join(", ", untyped.Select(u => u.Text + " " + TypeFor(u.IdentName ?? u.Text))) + ".");
                i.FixLabel = untyped.Count == 1 ? "Add " + TypeFor(c.IdentName ?? c.Text) : "Add the types";
                foreach (var u in untyped) i.Fixes.Add(new TextFix(u.End, 0, " " + TypeFor(u.IdentName ?? u.Text)));
                issues.Add(i);
                return;
            }
            foreach (var c in ColumnDefinitions(info))
            {
                if (c.Kind == TokenKind.Word && ColumnDefLead.Contains(c.Text)) continue;
                int at = tok.FindIndex(t => t.Start == c.Start);
                if (at < 0 || at + 1 >= tok.Count) continue;
                var next = tok[at + 1];
                if (next.Kind == TokenKind.Word && !SqlKeywords.Types.Contains(next.Text) && !SqlKeywords.IsVocabulary(next.Text))
                {
                    var guess = SqlKeywords.Closest(next.Text, SqlKeywords.Types);
                    var i = Bad("type", next, next.Text + " isn't a data type." + (guess != null ? " Did you mean " + guess.ToUpperInvariant() + "?" :
                                " Common ones: INT, VARCHAR(100), DECIMAL(10,2), DATE, TEXT."));
                    if (guess != null) { i.FixLabel = "Use " + guess.ToUpperInvariant(); i.Fixes.Add(new TextFix(next.Start, next.Length, guess.ToUpperInvariant())); }
                    issues.Add(i);
                    return;
                }
            }
        }

        /// <summary>A sensible type for a column by its name (id → INT, price → DECIMAL...).</summary>
        internal static string TypeFor(string column)
        {
            var c = (column ?? "").ToLowerInvariant();
            if (c == "id" || c.EndsWith("_id") || c.EndsWith("id") && c.Length <= 4 || c.EndsWith("_no") || c == "age" || c == "qty" || c == "quantity" ||
                c == "stock" || c == "count" || c == "year" || c == "marks" || c == "mark" || c == "credits") return "INT";
            if (c.Contains("price") || c.Contains("amount") || c.Contains("total") || c.Contains("salary") || c.Contains("cost") || c.Contains("fee") ||
                c.Contains("balance")) return "DECIMAL(10,2)";
            if (c.EndsWith("_at") || c.Contains("time")) return "DATETIME";
            if (c.Contains("date") || c == "dob" || c.Contains("birth")) return "DATE";
            if (c.Contains("description") || c.Contains("notes") || c.Contains("address") || c == "body" || c == "content") return "TEXT";
            return "VARCHAR(100)";
        }

        /// <summary>VARCHAR without a length; AUTO_INCREMENT that isn't a key.</summary>
        private static void CheckColumnTypes(StatementInfo info, List<SqlIssue> issues)
        {
            var tok = info.Tokens;
            if (!(info.Object == "TABLE" || info.Verb == "ALTER")) return;
            for (int k = 1; k < tok.Count; k++)
            {
                var t = tok[k];
                if ((t.IsWord("VARCHAR") || t.IsWord("VARBINARY") || t.IsWord("NVARCHAR")) && !IsS(tok, k + 1, "(") && !IsW(tok, k - 1, "AS"))
                {
                    var i = Bad("type", t, t.Upper + " needs a length: the most characters it can hold, like " + t.Upper + "(100).");
                    i.FixLabel = "Add (100)";
                    i.Fixes.Add(new TextFix(t.End, 0, "(100)"));
                    issues.Add(i);
                    return;
                }
            }
            if (info.Verb != "CREATE") return;
            int ai = tok.FindIndex(t => t.IsWord("AUTO_INCREMENT"));
            if (ai < 0 || IsS(tok, ai + 1, "=")) return;
            if (tok.Any(t => t.IsWord("PRIMARY") || t.IsWord("KEY") || t.IsWord("UNIQUE") || t.IsWord("INDEX"))) return;
            var a = Bad("type", tok[ai], "An AUTO_INCREMENT column must be a key, or the table can't be made. Add PRIMARY KEY.");
            a.FixLabel = "Add PRIMARY KEY";
            a.Fixes.Add(new TextFix(tok[ai].End, 0, " PRIMARY KEY"));
            issues.Add(a);
        }

        // ------------------------------------------------------------------ DROP / ALTER without TABLE

        private void ShapeDropAlter(StatementInfo info, KnownObjects known, List<SqlIssue> issues, bool typingHere)
        {
            var tok = info.Tokens;
            string verb = tok[0].Upper;
            string db = info.CurrentDatabase;
            if (tok.Count < 2)
            {
                if (!typingHere) issues.Add(Bad("shape", tok[0], verb + " what? " + verb + " TABLE name;"));
                return;
            }
            var t1 = tok[1];
            if (verb == "DROP" && t1.IsWord("COLUMN") && tok.Count > 2 && IsUserName(tok[2]))
            {
                var col = tok[2].IdentName;
                var owners = TablesWithColumn(known, db, col);
                var i = Bad("object", t1, "A column is removed through its table: ALTER TABLE " + (owners.Count == 1 ? owners[0] : "your_table") + " DROP COLUMN " + col + "." +
                            (owners.Count > 1 ? " " + col + " is in " + string.Join(", ", owners) + "." : ""));
                if (owners.Count == 1)
                {
                    i.FixLabel = "Use ALTER TABLE " + owners[0];
                    i.Fixes.Add(new TextFix(tok[0].Start, tok[tok.Count - 1].End - tok[0].Start, "ALTER TABLE " + Q(owners[0]) + " DROP COLUMN " + Q(col)));
                }
                issues.Add(i);
                return;
            }
            if (!(t1.Kind == TokenKind.QuotedIdent || t1.Kind == TokenKind.Word && !CreateWords.Contains(t1.Text) && !SqlKeywords.IsVocabulary(t1.Text))) return;
            string n = t1.IdentName;
            bool isTable = known.TableExists(db, n) == true;
            string obj = verb == "DROP" && !isTable && known.DbExists(n) ? "DATABASE" : "TABLE";
            var issue = Bad("object", t1, verb + " what? Add " + obj + ": " + verb + " " + obj + " " + t1.Text + " …");
            issue.FixLabel = "Add " + obj;
            issue.Fixes.Add(new TextFix(t1.Start, 0, obj + " "));
            issues.Add(issue);
        }

        // ------------------------------------------------------------------ clauses: empty, order, dangling end

        private static readonly HashSet<string> ClauseWordsTop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "FROM", "WHERE", "GROUP", "HAVING", "ORDER", "LIMIT", "SET", "WINDOW", "UNION", "EXCEPT", "INTERSECT", "INTO", "FOR", "LOCK", "RETURNING" };

        private static void CheckEmptyClauses(StatementInfo info, List<SqlIssue> issues, bool typingHere)
        {
            var tok = info.Tokens;
            var depth = Depths(tok);
            for (int k = 1; k < tok.Count; k++)
            {
                if (depth[k] != 0) continue;
                var t = tok[k];
                string what;
                if (t.IsWord("WHERE")) what = "WHERE needs a condition after it, like WHERE id = 1.";
                else if (t.IsWord("HAVING")) what = "HAVING needs a condition after it, like HAVING COUNT(*) > 1.";
                else if (t.IsWord("LIMIT")) what = "LIMIT needs a number after it, like LIMIT 10.";
                else if (t.IsWord("BY") && (IsW(tok, k - 1, "ORDER") || IsW(tok, k - 1, "GROUP"))) what = tok[k - 1].Upper + " BY needs a column after it, like " + tok[k - 1].Upper + " BY name.";
                else if (t.IsWord("FROM") && info.Verb == "SELECT") what = "FROM needs a table's name after it.";
                else continue;
                bool atEnd = k == tok.Count - 1;
                if (atEnd && typingHere) continue;
                if (!atEnd && !(tok[k + 1].Kind == TokenKind.Word && ClauseWordsTop.Contains(tok[k + 1].Text) && !tok[k + 1].IsWord("FROM"))) continue;
                // FROM order / FROM group: a table with a reserved name, not an empty FROM
                if (!atEnd && (tok[k + 1].IsWord("ORDER") || tok[k + 1].IsWord("GROUP")) && !IsW(tok, k + 2, "BY")) continue;
                issues.Add(Bad("empty", t, what));
                return;
            }
        }

        private static int ClauseRank(string verb, string word)
        {
            switch (word)
            {
                case "FROM": return verb == "SELECT" || verb == "DELETE" ? 1 : -1;
                case "SET": return verb == "UPDATE" ? 1 : -1;
                case "WHERE": return 2;
                case "GROUP": return verb == "SELECT" ? 3 : -1;
                case "HAVING": return verb == "SELECT" ? 4 : -1;
                case "ORDER": return 5;
                case "LIMIT": return 6;
                default: return 0;
            }
        }

        private static readonly string[] ClauseNames = { "", "FROM", "WHERE", "GROUP BY", "HAVING", "ORDER BY", "LIMIT" };

        private static void CheckClauseOrder(SqlStatement s, StatementInfo info, List<SqlIssue> issues)
        {
            var tok = info.Tokens;
            var depth = Depths(tok);
            var clauses = new List<(int at, int rank)>();
            for (int k = 1; k < tok.Count; k++)
            {
                if (depth[k] != 0 || tok[k].Kind != TokenKind.Word) continue;
                var u = tok[k].Upper;
                if (u == "UNION" || u == "EXCEPT" || u == "INTERSECT" || u == "WINDOW" || u == "INTO" || u == "FOR" || u == "LOCK" || u == "RETURNING") return;
                if (u == "FROM" && info.Verb == "DELETE" && clauses.Count > 0) return;
                int rank = ClauseRank(info.Verb, u);
                if (rank == 0) continue;
                if (rank < 0) return;
                if ((u == "GROUP" || u == "ORDER") && !IsW(tok, k + 1, "BY")) continue;
                clauses.Add((k, rank));
            }
            for (int i = 1; i < clauses.Count; i++)
            {
                if (clauses[i].rank >= clauses[i - 1].rank) continue;
                var moved = tok[clauses[i].at];
                var issue = Bad("order", moved, ClauseNames[clauses[i].rank] + " must come before " + ClauseNames[clauses[i - 1].rank] + ". The order is: " +
                                (info.Verb == "UPDATE" ? "SET, WHERE, ORDER BY, LIMIT." : info.Verb == "DELETE" ? "FROM, WHERE, ORDER BY, LIMIT." :
                                 "FROM, WHERE, GROUP BY, HAVING, ORDER BY, LIMIT."));
                if (clauses.Select(c => c.rank).Distinct().Count() == clauses.Count)
                {
                    int start = tok[clauses[0].at].Start, end = tok[tok.Count - 1].End;
                    var parts = new List<(int rank, string text)>();
                    for (int c = 0; c < clauses.Count; c++)
                    {
                        int a = tok[clauses[c].at].Start;
                        int b = c + 1 < clauses.Count ? tok[clauses[c + 1].at].Start : end;
                        parts.Add((clauses[c].rank, s.Text.Substring(a - s.Start, b - a).Trim()));
                    }
                    issue.FixLabel = "Put them in order";
                    issue.Fixes.Add(new TextFix(start, end - start, string.Join(" ", parts.OrderBy(p => p.rank).Select(p => p.text))));
                }
                issues.Add(issue);
                return;
            }
        }

        private static readonly HashSet<string> NeedsMoreAfter = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "AND", "OR", "NOT", "LIKE", "IN", "BETWEEN", "ON", "JOIN", "AS", "IS", "SET" };

        private static void CheckDanglingEnd(List<SqlToken> tok, List<SqlIssue> issues, bool typingHere)
        {
            var depth = Depths(tok);
            for (int k = 1; k < tok.Count; k++)
            {
                var t = tok[k];
                bool op = t.Kind == TokenKind.Word && NeedsMoreAfter.Contains(t.Text) ||
                          t.Kind == TokenKind.Symbol && (t.Text == "=" || t.Text == "<" || t.Text == ">" || t.Text == "<=" || t.Text == ">=" || t.Text == "<>" || t.Text == "!=" || t.Text == "+" || t.Text == "-" || t.Text == "/");
                if (!op || depth[k] != 0) continue;
                bool atEnd = k == tok.Count - 1;
                if (atEnd && typingHere) continue;
                bool beforeClause = !atEnd && tok[k + 1].Kind == TokenKind.Word && ClauseWordsTop.Contains(tok[k + 1].Text) && !tok[k + 1].IsWord("SET") && !tok[k + 1].IsWord("FROM") ||
                                    !atEnd && (tok[k + 1].IsWord("ORDER") || tok[k + 1].IsWord("GROUP") || tok[k + 1].IsWord("LIMIT"));
                if (t.IsWord("IS") && !atEnd) continue;
                if (!atEnd && !beforeClause) continue;
                issues.Add(Bad("dangling", t, atEnd
                    ? "The command stops right after " + t.Text + ": something is missing after it."
                    : "Something is missing after " + t.Text + ", before " + tok[k + 1].Upper + "."));
                return;
            }
        }

        // ------------------------------------------------------------------ text without quotes (WHERE name = Tea)

        /// <summary>WHERE name = Tea: Tea isn't a column, so it's text missing its quotes.</summary>
        private static SqlIssue UnquotedText(List<SqlToken> tok, int at, IList<string> columns, string table)
        {
            if (at < 2 || tok[at].Kind != TokenKind.Word) return null;
            var op = tok[at - 1];
            bool compare = op.Kind == TokenKind.Symbol && (op.Text == "=" || op.Text == "<>" || op.Text == "!=" || op.Text == "<" || op.Text == ">" || op.Text == "<=" || op.Text == ">=") ||
                           op.IsWord("LIKE");
            if (!compare) return null;
            var left = tok[at - 2];
            if (!(left.Kind == TokenKind.Word || left.Kind == TokenKind.QuotedIdent) || !columns.Contains(left.IdentName, StringComparer.OrdinalIgnoreCase)) return null;
            var t = tok[at];
            var issue = new SqlIssue
            {
                Severity = IssueSeverity.Bad, Code = "text-quote", Start = t.Start, Length = t.Length, CaretSensitive = true,
                Message = t.Text + " isn't a column of " + table + ". If it's text, put it in quotes: " + Text(t.Text) + ".",
                FixLabel = "Use " + Text(t.Text),
                Fixes = { new TextFix(t.Start, t.Length, Text(t.Text)) },
            };
            var guess = SqlKeywords.Closest(t.Text, columns);
            if (guess != null) issue.MoreFixes.Add(new SqlFixOption("Use column " + guess, new TextFix(t.Start, t.Length, Q(guess))));
            return issue;
        }
    }
}
