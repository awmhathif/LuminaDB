using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace LuminaDB.Core.Sql
{
    /// <summary>The reading behind the short messages: which clause, which table, which type. One line each.</summary>
    public static partial class ErrorExplainer
    {
        private static readonly HashSet<string> Aggregates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "COUNT", "SUM", "AVG", "MIN", "MAX", "GROUP_CONCAT", "STDDEV", "VARIANCE" };

        private static List<SqlToken> Sig(SqlStatement s) => s?.SignificantTokens ?? new List<SqlToken>();

        /// <summary>Where a misplaced COUNT/SUM sits: "WHERE" (also ON/SET), "GROUP", or "SELECT" (one inside another).</summary>
        internal static string AggregateClause(SqlStatement s)
        {
            var tok = Sig(s);
            string clause = "";
            int depth = 0;
            var aggDepths = new Stack<int>();
            for (int i = 0; i < tok.Count; i++)
            {
                var t = tok[i];
                if (t.IsSymbol("(")) { depth++; continue; }
                if (t.IsSymbol(")")) { depth--; while (aggDepths.Count > 0 && aggDepths.Peek() > depth) aggDepths.Pop(); continue; }
                if (t.Kind != TokenKind.Word) continue;
                if (depth == 0 && (t.IsWord("SELECT") || t.IsWord("WHERE") || t.IsWord("HAVING") || t.IsWord("ORDER") || t.IsWord("ON") || t.IsWord("SET"))) clause = t.Upper;
                else if (depth == 0 && t.IsWord("GROUP") && i + 1 < tok.Count && tok[i + 1].IsWord("BY")) clause = "GROUP";
                if (Aggregates.Contains(t.Text) && i + 1 < tok.Count && tok[i + 1].IsSymbol("("))
                {
                    if (clause == "WHERE" || clause == "ON" || clause == "SET") return "WHERE";
                    if (clause == "GROUP") return "GROUP";
                    if (aggDepths.Count > 0) return "SELECT";
                    aggDepths.Push(depth + 1);
                }
            }
            return "WHERE";
        }

        /// <summary>The sub-query sits right after =, &lt;, &gt; … (rather than in the SELECT list).</summary>
        internal static bool ComparedSubquery(SqlStatement s)
        {
            var tok = Sig(s);
            for (int i = 1; i + 1 < tok.Count; i++)
                if (tok[i].IsSymbol("(") && tok[i + 1].IsWord("SELECT"))
                {
                    var p = tok[i - 1];
                    if (p.Kind == TokenKind.Symbol && (p.Text == "=" || p.Text == "<" || p.Text == ">" || p.Text == "<=" || p.Text == ">=" || p.Text == "<>" || p.Text == "!=")) return true;
                }
            return false;
        }

        /// <summary>Unknown column, read in context: the table's alias, a sub-query's columns, USING, no FROM, a SELECT-list name in WHERE.</summary>
        private static bool ExplainColumnContext(ErrorExplanation e, StatementInfo info, SqlStatement statement, SchemaSnapshot schema,
                                                 string qual, string col, string full, string where)
        {
            if (info == null || statement == null) return false;
            var tok = Sig(statement);

            if (qual != null)
            {
                // products.name after FROM products p
                var renamed = info.Tables.FirstOrDefault(r => !r.IsDerived && r.Alias != null && string.Equals(r.Name, qual, StringComparison.OrdinalIgnoreCase) &&
                                                              !string.Equals(r.Alias, qual, StringComparison.OrdinalIgnoreCase));
                if (renamed != null)
                {
                    e.Body = qual + " is called " + renamed.Alias + " in this command, so write " + renamed.Alias + "." + col + ".";
                    for (int i = 0; i + 2 < tok.Count; i++)
                        if ((tok[i].Kind == TokenKind.Word || tok[i].Kind == TokenKind.QuotedIdent) && string.Equals(tok[i].IdentName, qual, StringComparison.OrdinalIgnoreCase) &&
                            tok[i + 1].IsSymbol(".") && string.Equals(tok[i + 2].IdentName, col, StringComparison.OrdinalIgnoreCase))
                        {
                            e.FixLabel = "Use " + renamed.Alias + "." + col;
                            e.Fixes.Add(new TextFix(tok[i].Start, tok[i].Length, renamed.Alias));
                            e.Offset = tok[i].Start;
                            break;
                        }
                    return true;
                }
                // q.price where q is (SELECT name …) AS q
                var derived = info.Tables.FirstOrDefault(r => r.IsDerived && string.Equals(r.Alias, qual, StringComparison.OrdinalIgnoreCase));
                if (derived != null)
                {
                    e.Body = "The sub-query " + qual + " doesn't give " + col + ". Add " + col + " to its SELECT list.";
                    return true;
                }
            }

            // JOIN … USING (col): name the side that lacks it
            if (tok.Any(t => t.IsWord("USING")))
            {
                var sides = info.Tables.Where(r => !r.IsDerived && r.Name != null)
                    .Select(r => new { r.Name, Has = schema.FindTable(r.ResolvedDatabase, r.Name)?.Find(col) != null }).ToList();
                var missing = sides.FirstOrDefault(x => !x.Has);
                if (missing != null && sides.Any(x => x.Has))
                {
                    e.Body = missing.Name + " has no " + col + ", so USING (" + col + ") can't join on it. Join with ON and the right columns.";
                    return true;
                }
            }

            // SELECT id, name products: no FROM at all
            if ((info.Verb == "SELECT") && !tok.Any(t => t.IsWord("FROM")))
            {
                var last = tok.LastOrDefault(t => !t.IsSymbol(";"));
                bool lastIsTable = last.Kind == TokenKind.Word && schema.FindTable(info.CurrentDatabase, last.IdentName) != null;
                e.Body = "There's no FROM, so there's no table to find " + col + " in." + (lastIsTable ? " Add FROM before " + last.Text + "." : "");
                if (lastIsTable) { e.FixLabel = "Add FROM"; e.Fixes.Add(new TextFix(last.Start, 0, "FROM ")); e.Offset = last.Start; }
                return true;
            }

            // SELECT price*2 AS doubled … WHERE doubled > 10
            if (where.IndexOf("where", StringComparison.OrdinalIgnoreCase) >= 0)
                for (int i = 0; i + 1 < tok.Count; i++)
                    if (tok[i].IsWord("AS") && string.Equals(tok[i + 1].IdentName, col, StringComparison.OrdinalIgnoreCase))
                    {
                        e.Body = col + " is a name from the SELECT list, and WHERE can't use it. Repeat the expression in WHERE.";
                        return true;
                    }
            return false;
        }

        /// <summary>The column made wider, keeping its kind (CHAR stays CHAR), collation, NOT NULL, DEFAULT and comment.</summary>
        internal static string WiderDefinition(ColumnSchema col)
        {
            var type = Regex.Replace(col.Type ?? "varchar(50)", @"\((\d+)\)", m => "(" + (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 2).ToString(CultureInfo.InvariantCulture) + ")");
            var name = SqlLexer.IsPlainName(col.Name) ? col.Name : SqlLexer.Quote(col.Name);
            var def = name + " " + type.ToUpperInvariant();
            if (!string.IsNullOrEmpty(col.Collation)) def += " COLLATE " + col.Collation;
            if (!col.Nullable) def += " NOT NULL";
            if (col.Default != null && !string.Equals(col.Default, "NULL", StringComparison.OrdinalIgnoreCase)) def += " DEFAULT " + col.Default;
            if (!string.IsNullOrEmpty(col.Comment)) def += " COMMENT '" + col.Comment.Replace("\\", "\\\\").Replace("'", "''") + "'";
            return def;
        }

        /// <summary>"-128 to 127 (TINYINT)", "0 to 255 (TINYINT UNSIGNED)", "up to 2 digits before the point (DECIMAL(4,2))".</summary>
        internal static string RangeOf(string type)
        {
            if (string.IsNullOrEmpty(type)) return null;
            var t = type.ToLowerInvariant();
            bool unsigned = t.Contains("unsigned");
            var d = Regex.Match(t, @"^(?:decimal|numeric)\((\d+),(\d+)\)");
            if (d.Success)
            {
                int p = int.Parse(d.Groups[1].Value, CultureInfo.InvariantCulture), s = int.Parse(d.Groups[2].Value, CultureInfo.InvariantCulture);
                return "numbers with up to " + (p - s) + " digit" + (p - s == 1 ? "" : "s") + " before the point (DECIMAL(" + p + "," + s + "))" + (unsigned ? ", 0 or more" : "");
            }
            string kind = Regex.Match(t, @"^(tinyint|smallint|mediumint|bigint|int)").Value;
            if (kind.Length == 0) return null;
            string lo, hi;
            switch (kind)
            {
                case "tinyint": lo = unsigned ? "0" : "-128"; hi = unsigned ? "255" : "127"; break;
                case "smallint": lo = unsigned ? "0" : "-32,768"; hi = unsigned ? "65,535" : "32,767"; break;
                case "mediumint": lo = unsigned ? "0" : "-8,388,608"; hi = unsigned ? "16,777,215" : "8,388,607"; break;
                case "int": lo = unsigned ? "0" : "-2,147,483,648"; hi = unsigned ? "4,294,967,295" : "2,147,483,647"; break;
                default: lo = unsigned ? "0" : "about -9.2 quintillion"; hi = unsigned ? "about 18.4 quintillion" : "about 9.2 quintillion"; break;
            }
            return lo + " to " + hi + " (" + kind.ToUpperInvariant() + (unsigned ? " UNSIGNED" : "") + ")";
        }

        internal static bool NegativeValueIn(SqlStatement s)
        {
            var tok = Sig(s);
            for (int i = 0; i + 1 < tok.Count; i++)
                if (tok[i].IsSymbol("-") && tok[i + 1].Kind == TokenKind.Number && (i == 0 || tok[i - 1].IsSymbol(",") || tok[i - 1].IsSymbol("(") || tok[i - 1].IsSymbol("="))) return true;
            return false;
        }

        /// <summary>The type written for a column in this CREATE/ALTER: "varchar(9)", "int unsigned".</summary>
        internal static string TypeInDefinition(SqlStatement s, string column)
        {
            var tok = Sig(s);
            for (int i = 0; i + 1 < tok.Count; i++)
            {
                if (!string.Equals(tok[i].IdentName, column, StringComparison.OrdinalIgnoreCase)) continue;
                if (!(tok[i - 1 < 0 ? 0 : i - 1].IsSymbol("(") || i > 0 && tok[i - 1].IsSymbol(",") || i > 0 && (tok[i - 1].IsWord("COLUMN") || tok[i - 1].IsWord("ADD") || tok[i - 1].IsWord("MODIFY")))) continue;
                var ty = tok[i + 1];
                if (ty.Kind != TokenKind.Word || !SqlKeywords.Types.Contains(ty.Text)) continue;
                var text = ty.Text.ToLowerInvariant();
                int k = i + 2;
                if (k < tok.Count && tok[k].IsSymbol("("))
                {
                    var sb = "(";
                    for (k++; k < tok.Count && !tok[k].IsSymbol(")"); k++) sb += tok[k].Text;
                    text += sb + ")";
                    k++;
                }
                if (k < tok.Count && tok[k].IsWord("UNSIGNED")) text += " unsigned";
                return text;
            }
            return null;
        }

        internal static bool IsNumberType(string t) => Regex.IsMatch(t ?? "", @"^(tiny|small|medium|big)?int|^decimal|^numeric|^float|^double|^real|^bit|^bool", RegexOptions.IgnoreCase);
        internal static bool IsDateType(string t) => Regex.IsMatch(t ?? "", @"^(date|datetime|timestamp|time|year)", RegexOptions.IgnoreCase);

        /// <summary>Normalised for comparing a foreign key's two sides: int(11) → int, keeps unsigned and lengths of text.</summary>
        private static string Comparable(string type)
        {
            var t = (type ?? "").ToLowerInvariant().Trim();
            t = Regex.Replace(t, @"^(tinyint|smallint|mediumint|int|integer|bigint)\(\d+\)", "$1");
            t = t.Replace("integer", "int").Replace(" zerofill", "");
            return Regex.Replace(t, @"\s+", " ");
        }

        /// <summary>The exact reason a FOREIGN KEY can't be made, read from the command and the schema; null when unsure.</summary>
        internal static string ForeignKeyProblem(SqlStatement s, SchemaSnapshot schema, string currentDb)
        {
            var tok = Sig(s);
            for (int i = 0; i < tok.Count; i++)
            {
                if (!tok[i].IsWord("REFERENCES") || i + 1 >= tok.Count) continue;
                // parent [db.]table ( column )
                int k = i + 1;
                string db = currentDb, parent = tok[k].IdentName;
                if (k + 2 < tok.Count && tok[k + 1].IsSymbol(".")) { db = parent; parent = tok[k + 2].IdentName; k += 2; }
                string pcol = k + 2 < tok.Count && tok[k + 1].IsSymbol("(") ? tok[k + 2].IdentName : null;

                // child column: FOREIGN KEY (c) just before, or the column this definition belongs to
                string child = null;
                for (int j = i - 1; j >= 0; j--)
                {
                    if (tok[j].IsWord("KEY") && j + 2 < tok.Count && tok[j + 1].IsSymbol("(")) { child = tok[j + 2].IdentName; break; }
                    if (tok[j].IsSymbol(",") || tok[j].IsSymbol("(") && j > 0 && !tok[j - 1].IsWord("KEY") && (j + 1 < tok.Count) && !SqlKeywords.Types.Contains(tok[j - 1].Text))
                    { child = j + 1 < tok.Count ? tok[j + 1].IdentName : null; break; }
                }

                var pt = schema.FindTable(db, parent);
                if (pt == null) return "There's no table " + parent + " to point to. Create it first.";
                if (pcol == null) return null;
                var pc = pt.Find(pcol);
                if (pc == null) return parent + " has no column " + pcol + ". Its columns: " + string.Join(", ", pt.Columns.Select(c => c.Name)) + ".";
                if (string.IsNullOrEmpty(pc.Key)) return parent + "." + pcol + " must be a PRIMARY KEY or UNIQUE to be pointed at.";
                var ct = child != null ? TypeInDefinition(s, child) : null;
                if (ct != null && Comparable(ct) != Comparable(pc.Type))
                    return child + " is " + ct.ToUpperInvariant() + " but " + parent + "." + pcol + " is " + Comparable(pc.Type).ToUpperInvariant() + ": make them the same type.";
                return null;
            }
            return null;
        }

        private static string ColumnsHint(StatementInfo info, SqlStatement s, SchemaSnapshot schema)
        {
            if (info == null) return "";
            List<string> cols;
            if (info.Verb == "CREATE" && info.Object == "TABLE")
                cols = SqlGuard.ColumnDefinitions(info).Where(t => !(t.Kind == TokenKind.Word && SqlKeywords.IsVocabulary(t.Text))).Select(t => t.IdentName).ToList();
            else
            {
                var t = info.Targets.FirstOrDefault() ?? info.Tables.FirstOrDefault();
                cols = t != null ? schema.FindTable(t.ResolvedDatabase, t.Name)?.Columns.Select(c => c.Name).ToList() : null;
            }
            return cols != null && cols.Count > 0 && cols.Count <= 12 ? " Columns here: " + string.Join(", ", cols) + "." : "";
        }

        /// <summary>FROM (SELECT …) with nothing after the closing bracket.</summary>
        internal static bool DerivedWithoutName(SqlStatement s)
        {
            var tok = Sig(s).Where(t => !t.IsSymbol(";")).ToList();
            for (int i = 0; i + 2 < tok.Count; i++)
            {
                if (!(tok[i].IsWord("FROM") || tok[i].IsWord("JOIN")) || !tok[i + 1].IsSymbol("(") || !tok[i + 2].IsWord("SELECT")) continue;
                int depth = 0;
                for (int k = i + 1; k < tok.Count; k++)
                {
                    if (tok[k].IsSymbol("(")) depth++;
                    else if (tok[k].IsSymbol(")") && --depth == 0)
                        return k + 1 >= tok.Count || tok[k + 1].IsWord("WHERE") || tok[k + 1].IsWord("JOIN") || tok[k + 1].IsWord("ORDER") || tok[k + 1].IsWord("GROUP") || tok[k + 1].IsWord("LIMIT");
                }
            }
            return false;
        }

        private static bool PrecededBy(SqlStatement s, int offset, string word)
        {
            if (s == null || offset < 0) return false;
            var prev = s.SignificantTokens.LastOrDefault(t => t.End <= offset);
            return prev.IsWord(word);
        }

        /// <summary>Habits from SQL Server, PostgreSQL and SQLite, in one line each.</summary>
        internal static string DialectHint(string near, SqlStatement s, int offset)
        {
            var n = (near ?? "").TrimStart();
            var tok = Sig(s);
            if (n.StartsWith("::", StringComparison.Ordinal)) return "MariaDB casts with CAST('5' AS SIGNED), not ::.";
            if (n.StartsWith("[", StringComparison.Ordinal)) return "MariaDB quotes names with backticks, like `name`, not [name].";
            if (Regex.IsMatch(n, @"^ILIKE\b", RegexOptions.IgnoreCase)) return "MariaDB has no ILIKE. LIKE already ignores upper and lower case here.";
            if (Regex.IsMatch(n, @"^CONFLICT\b", RegexOptions.IgnoreCase))
                return "MariaDB has no ON CONFLICT. INSERT IGNORE skips duplicates; ON DUPLICATE KEY UPDATE changes them.";
            if (Regex.IsMatch(n, @"^GO\b", RegexOptions.IgnoreCase)) return "GO belongs to SQL Server tools. Remove it: ; ends commands here.";
            if (Regex.IsMatch(n, @"^:\w+")) return n.Split(' ')[0] + " is a placeholder for app code. Type a real value here, like 1.";
            if (Regex.IsMatch(n, @"^MAX\s*\)", RegexOptions.IgnoreCase)) return "MariaDB has no (MAX). Use TEXT for long text, or VARCHAR(255).";
            if (tok.Any(t => t.IsWord("AUTOINCREMENT"))) return "MariaDB spells it AUTO_INCREMENT.";
            if (tok.Count > 2 && tok[0].IsWord("SELECT") && tok[1].IsWord("TOP")) return "MariaDB has no TOP. Put LIMIT at the end instead: … LIMIT " + (tok[2].Kind == TokenKind.Number ? tok[2].Text : "5") + ".";
            return null;
        }

        private static readonly Dictionary<string, string> FunctionShapes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CONCAT"] = "CONCAT(first, ' ', last)", ["CONCAT_WS"] = "CONCAT_WS(', ', a, b)", ["COALESCE"] = "COALESCE(value, fallback)",
            ["IFNULL"] = "IFNULL(value, fallback)", ["NULLIF"] = "NULLIF(a, b)", ["IF"] = "IF(condition, yes, no)", ["ROUND"] = "ROUND(value) or ROUND(value, 2)",
            ["SUBSTRING"] = "SUBSTRING(text, start, length)", ["SUBSTR"] = "SUBSTR(text, start, length)", ["LEFT"] = "LEFT(text, count)", ["RIGHT"] = "RIGHT(text, count)",
            ["DATE_FORMAT"] = "DATE_FORMAT(date, '%Y-%m-%d')", ["DATEDIFF"] = "DATEDIFF(later, earlier)", ["DATE_ADD"] = "DATE_ADD(date, INTERVAL 1 DAY)",
            ["REPLACE"] = "REPLACE(text, 'old', 'new')", ["UPPER"] = "UPPER(text)", ["LOWER"] = "LOWER(text)", ["LENGTH"] = "LENGTH(text)", ["TRIM"] = "TRIM(text)",
            ["ABS"] = "ABS(number)", ["MOD"] = "MOD(a, b)", ["POWER"] = "POWER(base, exponent)", ["NOW"] = "NOW()", ["CURDATE"] = "CURDATE()",
            ["JSON_EXTRACT"] = "JSON_EXTRACT(json, '$.name')", ["STR_TO_DATE"] = "STR_TO_DATE('14/03/2026', '%d/%m/%Y')",
        };

        /// <summary>Errors MariaDB words well enough for itself, said shorter with the next step. False when not one of them.</summary>
        private static bool ExplainMore(ErrorExplanation e, int code, string message, SqlStatement s, StatementInfo info, SchemaSnapshot schema)
        {
            Match m;
            switch (code)
            {
                case 1582:
                    m = M(message, @"native function '([^']+)'");
                    {
                        var f = m.Success ? m.Groups[1].Value.ToUpperInvariant() : "this function";
                        e.Title = "Wrong number of values for " + f;
                        e.Body = FunctionShapes.TryGetValue(f, out var shape) ? "Write it like " + shape + "." : "Check how many values " + f + " takes.";
                    }
                    return true;
                case 1318:
                    m = M(message, @"for (?:PROCEDURE|FUNCTION) (?:[^.;]+\.)?([^;\s]+); expected (\d+), got (\d+)");
                    if (!m.Success) return false;
                    {
                        int want = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                        e.Title = m.Groups[1].Value + " takes " + want + (want == 1 ? " value" : " values") + ", not " + m.Groups[3].Value;
                        e.Body = "Call it with " + want + ": " + m.Groups[1].Value + "(" + string.Join(", ", Enumerable.Repeat("…", want)) + ").";
                    }
                    return true;
                case 1222:
                    e.Title = "The UNION parts don't match";
                    e.Body = "Each SELECT in a UNION must give the same number of columns.";
                    return true;
                case 4078:
                    if (message.IndexOf(" row ", StringComparison.OrdinalIgnoreCase) < 0 && !message.StartsWith("Illegal parameter data types row", StringComparison.OrdinalIgnoreCase)) return false;
                    e.Title = "Different number of values";
                    e.Body = "Both sides need the same number of values, like (a, b) = (1, 2).";
                    return true;
                case 4015:
                    e.Title = "Window functions can't go here";
                    e.Body = "Use them in SELECT or ORDER BY. To filter on one: SELECT * FROM (…) AS t WHERE r = 1.";
                    return true;
                case 4005:
                    m = M(message, @"element '([^']+)'");
                    e.Title = "The recursive WITH has no starting rows";
                    e.Body = "Start with a plain SELECT, then UNION ALL the part that reads " + (m.Success ? m.Groups[1].Value : "it") + ".";
                    return true;
                case 1243:
                    m = M(message, @"handler \(([^)]+)\)");
                    e.Title = "There's no prepared statement called " + (m.Success ? m.Groups[1].Value : "that");
                    e.Body = "Run PREPARE " + (m.Success ? m.Groups[1].Value : "name") + " FROM '…'; first, in this Notebook.";
                    return true;
                case 1330:
                    m = M(message, @"Duplicate parameter: (\S+)");
                    e.Title = "Two parameters called " + (m.Success ? m.Groups[1].Value : "the same");
                    e.Body = "Give each parameter its own name.";
                    return true;
                case 1193:
                    m = M(message, @"Unknown system variable '([^']+)'");
                    e.Title = "There's no setting called " + (m.Success ? m.Groups[1].Value : "that");
                    e.Body = "SHOW VARIABLES LIKE '%" + (m.Success ? m.Groups[1].Value.Split('_')[0] : "name") + "%'; lists the settings.";
                    return true;
                case 1231:
                    m = M(message, @"Variable '([^']+)' can't be set to the value of '([^']*)'");
                    e.Title = (m.Success ? m.Groups[1].Value : "That setting") + " can't be '" + (m.Success ? m.Groups[2].Value : "") + "'";
                    e.Body = m.Success && Regex.IsMatch(m.Groups[1].Value, "autocommit|unique_checks|foreign_key_checks|sql_safe_updates|sql_log_bin", RegexOptions.IgnoreCase)
                        ? "Use ON or OFF (1 or 0)." : "SHOW VARIABLES shows the values it accepts.";
                    return true;
                case 1066:
                    m = M(message, @"alias: '([^']+)'");
                    e.Title = "Two tables called " + (m.Success ? m.Groups[1].Value : "the same");
                    e.Body = "Give each table its own name, like products p JOIN orders o.";
                    return true;
                case 1109:
                    m = M(message, @"Unknown table '([^']+)'");
                    e.Title = (m.Success ? m.Groups[1].Value : "That") + " isn't a table here";
                    e.Body = "DELETE removes whole rows from a table. To change one column, use UPDATE … SET.";
                    return true;
                case 1096:
                    e.Title = "No table named";
                    e.Body = "This command needs FROM and a table.";
                    return true;
                case 0:
                    if (message.IndexOf("Parameter", StringComparison.OrdinalIgnoreCase) < 0) return false;
                    e.Title = "? needs a value";
                    e.Body = "The Notebook can't fill in ?. Type the value in its place, like id = 1.";
                    return true;
                default:
                    if (message.IndexOf("DELIMITER", StringComparison.Ordinal) >= 0)
                    {
                        e.Title = "A command after DELIMITER never ended";
                        e.Body = "After DELIMITER $$, end the command with $$ (END$$), then write DELIMITER ; on its own line.";
                        return true;
                    }
                    return false;
            }
        }

        /// <summary>MariaDB's warnings in one short line: "1 warning: dividing by 0 gives NULL." Null when there are none.</summary>
        public static string DescribeWarnings(IReadOnlyCollection<ServerNote> notes)
        {
            var list = (notes ?? Array.Empty<ServerNote>()).Where(n => !string.Equals(n.Level, "Note", StringComparison.OrdinalIgnoreCase)).ToList();
            if (list.Count == 0) return null;
            string text = WarningText(list[0]);
            return list.Count == 1 ? "1 warning: " + text : list.Count + " warnings. First: " + text;
        }

        private static string WarningText(ServerNote n)
        {
            var msg = n.Message ?? "";
            Match m;
            switch (n.Code)
            {
                case 1365: return "dividing by 0 gives NULL.";
                case 1292:
                    if ((m = M(msg, @"Truncated incorrect (?:DOUBLE|INTEGER|DECIMAL) value: '(.*)'")).Success)
                        return "'" + Shorten(m.Groups[1].Value, 30) + "' isn't a number, so it was read as 0.";
                    if ((m = M(msg, @"Incorrect \w+ value: '(.*)' for function (\w+)")).Success)
                        return "'" + Shorten(m.Groups[1].Value, 30) + "' doesn't match the date format, so " + m.Groups[2].Value.ToUpperInvariant() + " gave NULL.";
                    break;
                case 1411:
                    if ((m = M(msg, @"value: '(.*)' for function (\w+)")).Success)
                        return "'" + Shorten(m.Groups[1].Value, 30) + "' doesn't match the date format, so " + m.Groups[2].Value.ToUpperInvariant() + " gave NULL.";
                    break;
                case 4042: case 4038: case 4037: case 4036:
                    return "the JSON or its path isn't valid (paths look like '$.name'), so the result is NULL.";
                case 1062: return "a row with that key already exists, so it was skipped.";
                case 1048: case 1364: case 1423: return "a missing value was filled with a default.";
                case 1265: return "a value was cut to fit its column.";
                case 1264: return "a number didn't fit its column and was set to the limit.";
                case 1366: return "a value of the wrong kind was changed to fit its column.";
                case 1051: return "that table didn't exist, so nothing was dropped.";
                case 1050: return "that table already exists, so nothing was made.";
            }
            return Shorten(msg, 120);
        }
    }
}
