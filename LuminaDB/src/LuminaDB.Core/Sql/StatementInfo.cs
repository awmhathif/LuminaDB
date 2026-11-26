using System;
using System.Collections.Generic;
using System.Linq;

namespace LuminaDB.Core.Sql
{
    public enum StatementKind { Empty, Read, Write, Schema, Session, Other }

    /// <summary>A table named in a command: FROM shop.orders o.</summary>
    public sealed class TableRef
    {
        /// <summary>Database written in the SQL (null when unqualified).</summary>
        public string Database { get; set; }
        public string Name { get; set; }
        public string Alias { get; set; }
        /// <summary>Database the server will use: written one, else the current one.</summary>
        public string ResolvedDatabase { get; set; }
        /// <summary>Script offset/length of the table-name token.</summary>
        public int NameStart { get; set; }
        public int NameLength { get; set; }
        /// <summary>(SELECT ...) alias.</summary>
        public bool IsDerived { get; set; }
        /// <summary>A reserved word sits where a table name belongs (FROM order).</summary>
        public bool IsReservedWord { get; set; }
        /// <summary>The command defines this name (CREATE TABLE x) rather than reading it.</summary>
        public bool IsDefinition { get; set; }
        /// <summary>Points at a parent table (REFERENCES x).</summary>
        public bool IsReference { get; set; }
        public string Display => Database != null ? Database + "." + Name : Name;
        public override string ToString() => (ResolvedDatabase ?? "?") + "." + Name + (Alias != null ? " " + Alias : "");
    }

    /// <summary>
    /// The shape of one command: what it does, which tables it reads and changes. Built from
    /// tokens (no server needed) and shared by the checker, the safety gate and Undo.
    /// </summary>
    public sealed class StatementInfo
    {
        public SqlStatement Statement { get; private set; }
        /// <summary>Significant tokens, script offsets.</summary>
        public List<SqlToken> Tokens { get; private set; }
        public string Verb { get; private set; } = "";
        /// <summary>TABLE, DATABASE, VIEW, INDEX, PROCEDURE ... for CREATE/DROP/ALTER.</summary>
        public string Object { get; private set; } = "";
        public StatementKind Kind { get; private set; }
        public List<TableRef> Tables { get; } = new List<TableRef>();
        public List<TableRef> Targets { get; } = new List<TableRef>();
        /// <summary>RENAME TABLE a TO b / ALTER TABLE a RENAME TO b: the new names.</summary>
        public List<TableRef> NewNames { get; } = new List<TableRef>();
        public HashSet<string> CteNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool HasWhere { get; private set; }
        public bool HasLimit { get; private set; }
        public bool HasJoin { get; private set; }
        public bool HasGroupBy { get; private set; }
        public bool HasOrderBy { get; private set; }
        public bool HasSubquery { get; private set; }
        public bool IfExists { get; private set; }
        public bool IfNotExists { get; private set; }
        public bool IsDropColumn { get; private set; }
        public bool IsRoutineBody { get; private set; }
        public bool IsTemporary { get; private set; }
        /// <summary>USE x / CREATE DATABASE x / DROP DATABASE x.</summary>
        public string DatabaseName { get; private set; }
        public int DatabaseNameStart { get; private set; } = -1;
        public int DatabaseNameLength { get; private set; }
        public string CurrentDatabase { get; private set; }
        /// <summary>Number of VALUES (...) rows in an INSERT.</summary>
        public int ValuesRows { get; private set; }
        public int LimitCount { get; private set; } = -1;

        public bool IsReadOnly => Kind == StatementKind.Read || Kind == StatementKind.Session && Verb != "SET" || Kind == StatementKind.Empty;
        public bool ChangesData => Kind == StatementKind.Write || Kind == StatementKind.Schema;

        private static readonly HashSet<string> ReadVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "SELECT", "SHOW", "DESCRIBE", "DESC", "EXPLAIN", "WITH", "VALUES", "TABLE", "HELP", "CHECKSUM", "CHECK", "ANALYZE" };
        private static readonly HashSet<string> WriteVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "INSERT", "REPLACE", "UPDATE", "DELETE", "LOAD", "CALL", "DO", "HANDLER", "EXECUTE" };
        private static readonly HashSet<string> SchemaVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME" };
        private static readonly HashSet<string> SessionVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "USE", "SET", "BEGIN", "START", "COMMIT", "ROLLBACK", "SAVEPOINT", "RELEASE", "LOCK", "UNLOCK", "PREPARE", "DEALLOCATE", "XA" };

        private static readonly HashSet<string> AliasStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "WHERE", "JOIN", "INNER", "LEFT", "RIGHT", "CROSS", "NATURAL", "FULL", "OUTER", "STRAIGHT_JOIN", "ON", "USING", "SET",
            "ORDER", "GROUP", "HAVING", "LIMIT", "VALUES", "VALUE", "SELECT", "PARTITION", "USE", "FORCE", "IGNORE", "UNION",
            "EXCEPT", "INTERSECT", "WINDOW", "FOR", "LOCK", "INTO", "READ", "WRITE", "LOW_PRIORITY", "TO", "AS", "ADD", "DROP",
            "MODIFY", "CHANGE", "ALTER", "RENAME", "ENGINE", "CHARACTER", "CHARSET", "COLLATE", "DEFAULT", "COMMENT", "RETURNING",
            "LIKE", "WITH", "ROW", "SYSTEM_TIME", "OFFSET", "FETCH", "PROCEDURE", "CASCADE", "RESTRICT", "WAIT", "NOWAIT",
        };

        public static StatementInfo Analyze(SqlStatement statement, string currentDb)
        {
            var info = new StatementInfo { Statement = statement, CurrentDatabase = currentDb };
            info.Tokens = statement?.SignificantTokens ?? new List<SqlToken>();
            if (info.Tokens.Count == 0) { info.Kind = StatementKind.Empty; return info; }
            info.Parse();
            foreach (var r in info.Tables) r.ResolvedDatabase = r.Database ?? currentDb;
            foreach (var r in info.NewNames) r.ResolvedDatabase = r.Database ?? currentDb;
            return info;
        }

        private SqlToken T(int i) => i >= 0 && i < Tokens.Count ? Tokens[i] : default(SqlToken);
        private bool IsW(int i, string w) => i >= 0 && i < Tokens.Count && Tokens[i].IsWord(w);
        private bool IsS(int i, string s) => i >= 0 && i < Tokens.Count && Tokens[i].IsSymbol(s);
        private static bool IsName(SqlToken t) => t.Kind == TokenKind.QuotedIdent || t.Kind == TokenKind.Word;

        private void Parse()
        {
            var first = Tokens[0];
            Verb = first.Kind == TokenKind.Word ? first.Upper : (first.IsSymbol("(") ? "SELECT" : "");
            if (Verb == "DESC") Verb = "DESCRIBE";

            if (ReadVerbs.Contains(Verb)) Kind = StatementKind.Read;
            else if (WriteVerbs.Contains(Verb)) Kind = StatementKind.Write;
            else if (SchemaVerbs.Contains(Verb)) Kind = StatementKind.Schema;
            else if (SessionVerbs.Contains(Verb)) Kind = StatementKind.Session;
            else Kind = StatementKind.Other;

            int i = 1;
            if (Verb == "CREATE" || Verb == "DROP" || Verb == "ALTER")
            {
                // CREATE [OR REPLACE] [TEMPORARY] [DEFINER=..] [ALGORITHM=..] [SQL SECURITY ..] [UNIQUE|FULLTEXT] TABLE|VIEW|...
                for (; i < Tokens.Count && i < 14; i++)
                {
                    var t = Tokens[i];
                    if (t.Kind != TokenKind.Word) continue;
                    var u = t.Upper;
                    if (u == "TEMPORARY") { IsTemporary = true; continue; }
                    if (u == "TABLE" || u == "DATABASE" || u == "SCHEMA" || u == "VIEW" || u == "INDEX" || u == "TRIGGER" ||
                        u == "PROCEDURE" || u == "FUNCTION" || u == "EVENT" || u == "USER" || u == "ROLE" || u == "SEQUENCE" ||
                        u == "SERVER" || u == "PACKAGE" || u == "TABLESPACE")
                    {
                        Object = u == "SCHEMA" ? "DATABASE" : u;
                        break;
                    }
                }
                if (Object == "TRIGGER" || Object == "PROCEDURE" || Object == "FUNCTION" || Object == "EVENT" || Object == "PACKAGE")
                {
                    IsRoutineBody = Verb == "CREATE";
                    if (Verb == "CREATE" && Object == "TRIGGER")
                    {
                        // CREATE TRIGGER name BEFORE INSERT ON table
                        for (int k = i; k < Tokens.Count - 1; k++)
                            if (IsW(k, "ON")) { var r = ReadRef(k + 1, out _); if (r != null) { Tables.Add(r); } break; }
                    }
                    return;
                }
            }
            else if (Verb == "BEGIN" && (IsW(1, "NOT") || Tokens.Count > 2 && !IsW(1, "WORK"))) { IsRoutineBody = true; Kind = StatementKind.Write; return; }
            else if (Verb == "IF" || Verb == "WHILE" || Verb == "LOOP" || Verb == "REPEAT" || Verb == "CASE") { IsRoutineBody = true; Kind = StatementKind.Write; return; }

            // Object-level commands
            if (Object == "DATABASE")
            {
                int k = i + 1;
                if (IsW(k, "IF")) { if (IsW(k + 1, "NOT")) { IfNotExists = true; k += 3; } else { IfExists = true; k += 2; } }
                if (k < Tokens.Count && IsName(T(k))) SetDatabaseName(k);
                return;
            }
            if (Verb == "USE")
            {
                if (Tokens.Count > 1 && IsName(T(1))) SetDatabaseName(1);
                return;
            }

            ScanBody();
            ResolveTargets();
            ReadWhere();
        }

        private int _whereAt = -1;

        /// <summary>The WHERE condition is always true (WHERE 1=1, WHERE TRUE, … OR 1=1): it picks every row.</summary>
        public bool WhereAlwaysTrue { get; private set; }
        /// <summary>The WHERE condition's text span (script offsets), when there is one.</summary>
        public int WhereConditionStart { get; private set; } = -1;
        public int WhereConditionLength { get; private set; }
        /// <summary>No WHERE, or one that's always true: every row is touched.</summary>
        public bool TouchesEveryRow => !HasLimit && (!HasWhere || WhereAlwaysTrue);

        private static readonly HashSet<string> WhereEnd = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "ORDER", "GROUP", "HAVING", "LIMIT", "RETURNING", "WINDOW", "UNION", "EXCEPT", "INTERSECT", "FOR", "LOCK" };

        private void ReadWhere()
        {
            if (_whereAt < 0) return;
            int depth = 0, end = Tokens.Count;
            for (int k = _whereAt + 1; k < Tokens.Count; k++)
            {
                var t = Tokens[k];
                if (t.IsSymbol("(")) depth++;
                else if (t.IsSymbol(")")) depth--;
                else if (depth == 0 && (t.IsSymbol(";") || t.Kind == TokenKind.Word && WhereEnd.Contains(t.Text))) { end = k; break; }
            }
            var cond = Tokens.GetRange(_whereAt + 1, Math.Max(0, end - _whereAt - 1));
            if (cond.Count == 0) return;
            WhereConditionStart = cond[0].Start;
            WhereConditionLength = cond[cond.Count - 1].End - cond[0].Start;
            WhereAlwaysTrue = AlwaysTrue(cond);
        }

        /// <summary>Constant conditions only: TRUE, 1, 1=1, 'a'='a', id=id, NOT FALSE, and any of those joined by OR / AND.</summary>
        internal static bool AlwaysTrue(List<SqlToken> c)
        {
            c = Unwrap(c);
            if (c.Count == 0) return false;
            var ors = SplitTop(c, "OR");
            if (ors.Count > 1) return ors.Any(AlwaysTrue);
            var ands = SplitTop(c, "AND");
            if (ands.Count > 1) return ands.All(AlwaysTrue);
            if (c.Count == 1) return c[0].IsWord("TRUE") || c[0].Kind == TokenKind.Number && double.TryParse(c[0].Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v != 0;
            if (c.Count == 2 && c[0].IsWord("NOT")) return c[1].IsWord("FALSE") || c[1].Kind == TokenKind.Number && c[1].Text.Trim('0', '.').Length == 0;
            if (c.Count == 3 && c[1].Kind == TokenKind.Symbol)
            {
                string op = c[1].Text;
                bool same = string.Equals(c[0].Text, c[2].Text, StringComparison.OrdinalIgnoreCase) && c[0].Kind == c[2].Kind &&
                            (c[0].Kind == TokenKind.Number || c[0].Kind == TokenKind.String || c[0].Kind == TokenKind.Word || c[0].Kind == TokenKind.QuotedIdent);
                if (same && (op == "=" || op == ">=" || op == "<=" || op == "<=>")) return true;
                if (c[0].Kind == TokenKind.Number && c[2].Kind == TokenKind.Number &&
                    double.TryParse(c[0].Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a) &&
                    double.TryParse(c[2].Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b))
                {
                    switch (op)
                    {
                        case "=": return a == b;
                        case "<>": case "!=": return a != b;
                        case "<": return a < b;
                        case ">": return a > b;
                        case "<=": return a <= b;
                        case ">=": return a >= b;
                    }
                }
            }
            return false;
        }

        private static List<SqlToken> Unwrap(List<SqlToken> c)
        {
            while (c.Count >= 2 && c[0].IsSymbol("(") && c[c.Count - 1].IsSymbol(")"))
            {
                int depth = 0; bool wraps = true;
                for (int k = 0; k < c.Count - 1; k++)
                {
                    if (c[k].IsSymbol("(")) depth++;
                    else if (c[k].IsSymbol(")")) depth--;
                    if (depth == 0) { wraps = false; break; }
                }
                if (!wraps) break;
                c = c.GetRange(1, c.Count - 2);
            }
            return c;
        }

        private static List<List<SqlToken>> SplitTop(List<SqlToken> c, string word)
        {
            var parts = new List<List<SqlToken>> { new List<SqlToken>() };
            int depth = 0;
            bool between = false;
            foreach (var t in c)
            {
                if (t.IsSymbol("(")) depth++;
                else if (t.IsSymbol(")")) depth--;
                if (t.IsWord("BETWEEN")) between = true;
                bool split = depth == 0 && (t.IsWord(word) || word == "OR" && t.IsSymbol("||") || word == "AND" && t.IsSymbol("&&"));
                if (split && word == "AND" && between) { between = false; parts[parts.Count - 1].Add(t); continue; }
                if (split) { parts.Add(new List<SqlToken>()); continue; }
                parts[parts.Count - 1].Add(t);
            }
            return parts;
        }

        private void SetDatabaseName(int k)
        {
            DatabaseName = T(k).IdentName;
            DatabaseNameStart = T(k).Start;
            DatabaseNameLength = T(k).Length;
        }

        private void ScanBody()
        {
            var parens = new Stack<bool>();   // true = sub-query
            bool clauseLevel() => parens.Count == 0 || parens.Peek();
            bool sawSelectAfterInsert = false;

            for (int i = 0; i < Tokens.Count; i++)
            {
                var t = Tokens[i];
                if (t.IsSymbol("("))
                {
                    bool sub = IsW(i + 1, "SELECT") || IsW(i + 1, "WITH");
                    if (sub) HasSubquery = true;
                    parens.Push(sub);
                    continue;
                }
                if (t.IsSymbol(")")) { if (parens.Count > 0) parens.Pop(); continue; }
                if (t.Kind != TokenKind.Word) continue;
                var u = t.Upper;
                bool top = parens.Count == 0;

                if (u == "WITH" && i == 0 || u == "WITH" && IsW(i + 1, "RECURSIVE"))
                {
                    CollectCtes(i + 1);
                    continue;
                }
                if (!clauseLevel()) continue;

                switch (u)
                {
                    case "FROM":
                        if (Verb == "SHOW" && !IsW(i - 1, "COLUMNS") && !IsW(i - 1, "FIELDS") && !IsW(i - 1, "INDEX") && !IsW(i - 1, "INDEXES") && !IsW(i - 1, "KEYS") && !IsW(i - 2, "COLUMNS")) break;
                        ReadRefList(i + 1, allowComma: true);
                        break;
                    case "JOIN":
                    case "STRAIGHT_JOIN":
                        if (top) HasJoin = true;
                        ReadRefList(i + 1, allowComma: false);
                        break;
                    case "INTO":
                        if (T(i + 1).Kind == TokenKind.Variable || IsW(i + 1, "OUTFILE") || IsW(i + 1, "DUMPFILE")) break;
                        ReadRefList(IsW(i + 1, "TABLE") ? i + 2 : i + 1, allowComma: false);
                        break;
                    case "UPDATE":
                        if (i == 0 || Verb == "WITH" && !IsW(i - 1, "KEY") && !IsW(i - 1, "FOR") && !IsW(i - 1, "ON"))
                        {
                            int k = i + 1;
                            while (IsW(k, "LOW_PRIORITY") || IsW(k, "IGNORE")) k++;
                            ReadRefList(k, allowComma: true);
                        }
                        break;
                    case "REFERENCES":
                        { var r = ReadRef(i + 1, out _); if (r != null) { r.IsReference = true; Tables.Add(r); } }
                        break;
                    case "TABLE":
                    case "TABLES":
                        if (i > 0 && (IsW(i - 1, "SHOW") || IsW(i - 1, "OPEN") || IsW(i - 1, "FULL") || IsW(i - 1, "LIKE") || IsW(i - 1, "IN"))) break;
                        if (Verb == "SHOW" && !IsW(i - 1, "CREATE")) break;
                        if (Verb == "CREATE" && i > 0 && !(Object == "TABLE")) break;
                        if (Verb == "SELECT" || Verb == "INSERT" || Verb == "UPDATE" || Verb == "DELETE") break;
                        if (Verb == "LOCK" || Verb == "UNLOCK" || Verb == "FLUSH") { if (Verb == "LOCK") ReadRefList(i + 1, allowComma: true); break; }
                        {
                            int k = i + 1;
                            if (IsW(k, "IF")) { if (IsW(k + 1, "NOT")) { IfNotExists = true; k += 3; } else { IfExists = true; k += 2; } }
                            var created = ReadRefList(k, allowComma: Verb != "CREATE");
                            if (Verb == "CREATE") foreach (var r in created) r.IsDefinition = true;
                            if (Verb == "RENAME") CollectRenames(k);
                        }
                        break;
                    case "VIEW":
                        if (Verb == "CREATE" || Verb == "DROP" || Verb == "ALTER")
                        {
                            int k = i + 1;
                            if (IsW(k, "IF")) { if (IsW(k + 1, "NOT")) { IfNotExists = true; k += 3; } else { IfExists = true; k += 2; } }
                            var refs = ReadRefList(k, allowComma: Verb == "DROP");
                            if (Verb == "CREATE") foreach (var r in refs) r.IsDefinition = true;
                        }
                        break;
                    case "ON":
                        if ((Verb == "CREATE" || Verb == "DROP") && Object == "INDEX" && top) ReadRefList(i + 1, allowComma: false);
                        break;
                    case "WHERE":
                        if (top && !HasWhere) { HasWhere = true; _whereAt = i; }
                        break;
                    case "LIMIT":
                        if (top)
                        {
                            HasLimit = true;
                            if (T(i + 1).Kind == TokenKind.Number && int.TryParse(T(i + 1).Text, out var n)) LimitCount = n;
                        }
                        break;
                    case "GROUP":
                        if (top && IsW(i + 1, "BY")) HasGroupBy = true;
                        break;
                    case "ORDER":
                        if (top && IsW(i + 1, "BY")) HasOrderBy = true;
                        break;
                    case "DROP":
                        if (Verb == "ALTER" && i > 0 && !IsW(i + 1, "INDEX") && !IsW(i + 1, "KEY") && !IsW(i + 1, "PRIMARY") && !IsW(i + 1, "FOREIGN") &&
                            !IsW(i + 1, "CONSTRAINT") && !IsW(i + 1, "CHECK") && !IsW(i + 1, "PARTITION") && !IsW(i + 1, "DEFAULT") && !IsW(i - 1, "ALTER") && !IsW(i - 1, "SET"))
                            IsDropColumn = true;
                        break;
                    case "RENAME":
                        if (Verb == "ALTER" && i > 0)
                        {
                            int k = i + 1;
                            if (IsW(k, "TO") || IsW(k, "AS")) k++;
                            if (!IsW(i + 1, "COLUMN") && !IsW(i + 1, "INDEX") && !IsW(i + 1, "KEY"))
                            {
                                var r = ReadRef(k, out _);
                                if (r != null) NewNames.Add(r);
                            }
                        }
                        break;
                    case "SELECT":
                        if (i > 0 && (Verb == "INSERT" || Verb == "REPLACE") && top) sawSelectAfterInsert = true;
                        break;
                    case "VALUES":
                    case "VALUE":
                        if ((Verb == "INSERT" || Verb == "REPLACE") && top && !sawSelectAfterInsert) ValuesRows = CountTuples(i + 1);
                        break;
                }
            }

            if (Verb == "TRUNCATE" && !IsW(1, "TABLE")) ReadRefList(1, allowComma: false);
            if (Verb == "DESCRIBE" || Verb == "EXPLAIN" && Tokens.Count > 1 && !IsW(1, "SELECT") && !IsW(1, "UPDATE") && !IsW(1, "DELETE") &&
                !IsW(1, "INSERT") && !IsW(1, "REPLACE") && !IsW(1, "EXTENDED") && !IsW(1, "PARTITIONS") && !IsW(1, "FORMAT") && !IsW(1, "ANALYZE"))
                ReadRefList(1, allowComma: false);
            if ((Verb == "INSERT" || Verb == "REPLACE") && !Tables.Any())
            {
                int k = 1;
                while (IsW(k, "LOW_PRIORITY") || IsW(k, "DELAYED") || IsW(k, "HIGH_PRIORITY") || IsW(k, "IGNORE")) k++;
                if (!IsW(k, "INTO")) ReadRefList(k, allowComma: false);
            }
        }

        private int CountTuples(int k)
        {
            int rows = 0;
            while (k < Tokens.Count && IsS(k, "("))
            {
                rows++;
                int depth = 0;
                for (; k < Tokens.Count; k++)
                {
                    if (IsS(k, "(")) depth++;
                    else if (IsS(k, ")")) { depth--; if (depth == 0) { k++; break; } }
                }
                if (IsS(k, ",")) k++;
                else break;
            }
            return rows;
        }

        private void CollectCtes(int k)
        {
            if (IsW(k, "RECURSIVE")) k++;
            while (k < Tokens.Count)
            {
                if (!IsName(T(k))) return;
                CteNames.Add(T(k).IdentName);
                k++;
                if (IsS(k, "(")) k = SkipParens(k);
                if (!IsW(k, "AS")) return;
                k++;
                if (!IsS(k, "(")) return;
                k = SkipParens(k);
                if (!IsS(k, ",")) return;
                k++;
            }
        }

        private void CollectRenames(int k)
        {
            // RENAME TABLE a TO b, c TO d
            for (; k < Tokens.Count; k++)
            {
                if (IsW(k, "TO"))
                {
                    var r = ReadRef(k + 1, out _);
                    if (r != null)
                    {
                        NewNames.Add(r);
                        Tables.Remove(Tables.FirstOrDefault(x => x.NameStart == r.NameStart));
                    }
                }
            }
        }

        private int SkipParens(int k)
        {
            int depth = 0;
            for (; k < Tokens.Count; k++)
            {
                if (IsS(k, "(")) depth++;
                else if (IsS(k, ")")) { depth--; if (depth == 0) return k + 1; }
            }
            return k;
        }

        private List<TableRef> ReadRefList(int k, bool allowComma)
        {
            var list = new List<TableRef>();
            while (k < Tokens.Count)
            {
                var r = ReadRef(k, out int next);
                if (r == null) break;
                Tables.Add(r);
                list.Add(r);
                k = next;
                if (allowComma && IsS(k, ",")) { k++; continue; }
                break;
            }
            return list;
        }

        /// <summary>Reads "name", "db.name", "(subquery) alias" plus an optional alias.</summary>
        private TableRef ReadRef(int k, out int next)
        {
            next = k;
            if (k >= Tokens.Count) return null;
            var t = T(k);
            TableRef r;
            if (t.IsSymbol("("))
            {
                if (!(IsW(k + 1, "SELECT") || IsW(k + 1, "WITH") || IsS(k + 1, "("))) return null;
                k = SkipParens(k);
                r = new TableRef { IsDerived = true, Name = null, NameStart = t.Start, NameLength = 1 };
            }
            else if (t.Kind == TokenKind.QuotedIdent || t.Kind == TokenKind.Word && !SqlKeywords.Reserved.Contains(t.Text) && !IsSoftStop(t))
            {
                r = new TableRef { Name = t.IdentName, NameStart = t.Start, NameLength = t.Length };
                k++;
                if (IsS(k, ".") && k + 1 < Tokens.Count && IsName(T(k + 1)))
                {
                    r.Database = r.Name;
                    r.Name = T(k + 1).IdentName;
                    r.NameStart = T(k + 1).Start;
                    r.NameLength = T(k + 1).Length;
                    k += 2;
                }
            }
            else if (t.Kind == TokenKind.Word && SqlKeywords.Reserved.Contains(t.Text) && !t.IsWord("DUAL") && !IsSoftStop(t) && !t.IsWord("SELECT") && !t.IsWord("IF"))
            {
                next = k + 1;
                return new TableRef { Name = t.Text, NameStart = t.Start, NameLength = t.Length, IsReservedWord = true };
            }
            else return null;

            // alias
            if (IsW(k, "AS") && k + 1 < Tokens.Count && IsName(T(k + 1)))
            {
                r.Alias = T(k + 1).IdentName;
                k += 2;
            }
            else if (k < Tokens.Count && (T(k).Kind == TokenKind.QuotedIdent || T(k).Kind == TokenKind.Word && !AliasStop.Contains(T(k).Text) && !SqlKeywords.Reserved.Contains(T(k).Text)))
            {
                r.Alias = T(k).IdentName;
                k++;
            }
            if (IsS(k, "(") && (IsW(k - 1, "PARTITION"))) k = SkipParens(k);
            next = k;
            return r;
        }

        private static bool IsSoftStop(SqlToken t) => t.IsWord("SET") || t.IsWord("VALUES") || t.IsWord("WHERE") || t.IsWord("LIKE") || t.IsWord("AS");

        private void ResolveTargets()
        {
            var nonDerived = Tables.Where(r => !r.IsDerived && !r.IsReference && !r.IsReservedWord).ToList();
            switch (Verb)
            {
                case "INSERT":
                case "REPLACE":
                case "LOAD":
                    if (nonDerived.Count > 0) Targets.Add(nonDerived[0]);
                    break;
                case "UPDATE":
                    Targets.AddRange(nonDerived.Where(r => !CteNames.Contains(r.Name)));
                    break;
                case "DELETE":
                    {
                        // DELETE t1, t2 FROM ... : the names before FROM are the targets (may be aliases)
                        int from = Tokens.FindIndex(t => t.IsWord("FROM"));
                        var named = new List<string>();
                        for (int k = 1; k < from; k++)
                        {
                            if (IsName(T(k)) && !IsW(k, "LOW_PRIORITY") && !IsW(k, "QUICK") && !IsW(k, "IGNORE")) named.Add(T(k).IdentName);
                        }
                        if (named.Count == 0) { if (nonDerived.Count > 0) Targets.Add(nonDerived[0]); }
                        else
                        {
                            foreach (var n in named)
                            {
                                var r = nonDerived.FirstOrDefault(x => string.Equals(x.Alias, n, StringComparison.OrdinalIgnoreCase)) ??
                                        nonDerived.FirstOrDefault(x => string.Equals(x.Name, n, StringComparison.OrdinalIgnoreCase));
                                if (r != null && !Targets.Contains(r)) Targets.Add(r);
                            }
                        }
                        break;
                    }
                case "TRUNCATE":
                case "DROP":
                case "ALTER":
                case "RENAME":
                    if (Object == "TABLE" || Object == "VIEW" || Verb == "TRUNCATE" || Verb == "RENAME" || Object == "INDEX")
                        Targets.AddRange(nonDerived);
                    break;
                case "CREATE":
                    if (Object == "TABLE" || Object == "VIEW") { var d = nonDerived.FirstOrDefault(r => r.IsDefinition); if (d != null) Targets.Add(d); }
                    else if (Object == "INDEX") Targets.AddRange(nonDerived);
                    break;
            }
        }
    }
}
