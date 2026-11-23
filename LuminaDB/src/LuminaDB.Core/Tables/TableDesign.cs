using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LuminaDB.Core.Sql;
using MySqlConnector;

namespace LuminaDB.Core.Tables
{
    /// <summary>One column as the structure editor sees it.</summary>
    public sealed class ColumnDef
    {
        /// <summary>Name in the database now (null for a new column). Lets a rename become CHANGE old new.</summary>
        public string OriginalName { get; set; }
        public string Name { get; set; } = "";
        /// <summary>int, varchar, decimal, enum, json… (lower case).</summary>
        public string BaseType { get; set; } = "int";
        /// <summary>What goes in the brackets: 100, 10,2, or 'a','b' for enum/set. Empty for none.</summary>
        public string Length { get; set; } = "";
        public bool Unsigned { get; set; }
        public bool Zerofill { get; set; }
        public bool Nullable { get; set; } = true;
        /// <summary>The default as the user sees it: 3, abc, CURRENT_TIMESTAMP, NULL. Empty = no default.</summary>
        public string Default { get; set; } = "";
        public bool AutoIncrement { get; set; }
        public bool OnUpdateNow { get; set; }
        public string Comment { get; set; } = "";
        /// <summary>Column collation when it differs from the table's (null = table default).</summary>
        public string Collation { get; set; }
        /// <summary>For generated columns: the expression; null otherwise.</summary>
        public string GenerationExpression { get; set; }
        public bool GeneratedStored { get; set; }
        /// <summary>A column-level CHECK (expression only), kept with the column so MODIFY doesn't lose it.</summary>
        public string Check { get; set; }

        public bool IsNew => OriginalName == null;
        public bool IsGenerated => !string.IsNullOrEmpty(GenerationExpression);

        public string FullType
        {
            get
            {
                var b = (BaseType ?? "").Trim().ToUpperInvariant();
                var len = (Length ?? "").Trim();
                var s = len.Length > 0 ? b + "(" + len + ")" : b;
                if (Unsigned && ColumnTypes.IsNumeric(BaseType)) s += " UNSIGNED";
                if (Zerofill && ColumnTypes.IsNumeric(BaseType)) s += " ZEROFILL";
                return s;
            }
        }

        public ColumnDef Clone() => (ColumnDef)MemberwiseClone();

        /// <summary>The same definition (ignores name and position).</summary>
        public bool SameDefinition(ColumnDef o) =>
            string.Equals(FullType, o.FullType, StringComparison.OrdinalIgnoreCase) && Nullable == o.Nullable &&
            string.Equals(DdlBuilder.DefaultKey(this), DdlBuilder.DefaultKey(o), StringComparison.Ordinal) &&
            AutoIncrement == o.AutoIncrement && OnUpdateNow == o.OnUpdateNow && (Comment ?? "") == (o.Comment ?? "") &&
            string.Equals(Collation ?? "", o.Collation ?? "", StringComparison.OrdinalIgnoreCase) &&
            DdlBuilder.SameExpression(GenerationExpression, o.GenerationExpression) && GeneratedStored == o.GeneratedStored &&
            DdlBuilder.SameExpression(Check, o.Check);
    }

    public enum IndexKind { Primary, Unique, Index, Fulltext, Spatial }

    public sealed class IndexDef
    {
        public string Name { get; set; } = "";
        public IndexKind Kind { get; set; } = IndexKind.Index;
        /// <summary>Column names; a prefix length is written as name(10).</summary>
        public List<string> Columns { get; set; } = new List<string>();
        public IndexDef Clone() => new IndexDef { Name = Name, Kind = Kind, Columns = Columns.ToList() };
        public bool Same(IndexDef o) => Kind == o.Kind && Columns.SequenceEqual(o.Columns, StringComparer.OrdinalIgnoreCase);
        public string KindWord => Kind == IndexKind.Primary ? "PRIMARY KEY" : Kind == IndexKind.Unique ? "UNIQUE" : Kind == IndexKind.Fulltext ? "FULLTEXT" : Kind == IndexKind.Spatial ? "SPATIAL" : "INDEX";
    }

    public sealed class ForeignKeyDef
    {
        public string Name { get; set; } = "";
        public List<string> Columns { get; set; } = new List<string>();
        /// <summary>Null = same database as the table.</summary>
        public string RefDatabase { get; set; }
        public string RefTable { get; set; } = "";
        public List<string> RefColumns { get; set; } = new List<string>();
        public string OnDelete { get; set; } = "RESTRICT";
        public string OnUpdate { get; set; } = "RESTRICT";
        public ForeignKeyDef Clone() => new ForeignKeyDef { Name = Name, Columns = Columns.ToList(), RefDatabase = RefDatabase, RefTable = RefTable, RefColumns = RefColumns.ToList(), OnDelete = OnDelete, OnUpdate = OnUpdate };
        public bool Same(ForeignKeyDef o, string db) =>
            Columns.SequenceEqual(o.Columns, StringComparer.OrdinalIgnoreCase) && RefColumns.SequenceEqual(o.RefColumns, StringComparer.OrdinalIgnoreCase) &&
            string.Equals(RefTable, o.RefTable, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(RefDatabase ?? db, o.RefDatabase ?? db, StringComparison.OrdinalIgnoreCase) &&
            Rule(OnDelete) == Rule(o.OnDelete) && Rule(OnUpdate) == Rule(o.OnUpdate);
        /// <summary>NO ACTION and RESTRICT behave the same in InnoDB.</summary>
        internal static string Rule(string r) { var u = (r ?? "RESTRICT").Trim().ToUpperInvariant(); return u == "NO ACTION" ? "RESTRICT" : u; }
        public static readonly IReadOnlyList<string> Rules = new[] { "RESTRICT", "CASCADE", "SET NULL", "NO ACTION" };
    }

    public sealed class CheckDef
    {
        public string Name { get; set; } = "";
        public string Expression { get; set; } = "";
        public CheckDef Clone() => new CheckDef { Name = Name, Expression = Expression };
    }

    public sealed class TriggerInfo
    {
        public string Name { get; set; }
        public string Timing { get; set; }
        public string Event { get; set; }
        public override string ToString() => Name + " (" + Timing + " " + Event + ")";
    }

    /// <summary>A whole table: what is in the database (read) and what the user wants (edited copy).</summary>
    public sealed class TableDesign
    {
        public string Database { get; set; }
        public string Name { get; set; } = "";
        /// <summary>Name in the database now; null for a table that doesn't exist yet.</summary>
        public string OriginalName { get; set; }
        public string Engine { get; set; } = "InnoDB";
        public string Collation { get; set; } = "utf8mb4_unicode_ci";
        public string Comment { get; set; } = "";
        public long RowEstimate { get; set; }
        public long Bytes { get; set; }
        public long? AutoIncrementNext { get; set; }
        public List<ColumnDef> Columns { get; set; } = new List<ColumnDef>();
        public List<IndexDef> Indexes { get; set; } = new List<IndexDef>();
        public List<ForeignKeyDef> ForeignKeys { get; set; } = new List<ForeignKeyDef>();
        public List<CheckDef> Checks { get; set; } = new List<CheckDef>();
        /// <summary>Read only: triggers are edited in the object editor.</summary>
        public List<TriggerInfo> Triggers { get; set; } = new List<TriggerInfo>();

        public bool IsNew => OriginalName == null;

        public ColumnDef Column(string name) => Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        public IndexDef PrimaryKey => Indexes.FirstOrDefault(i => i.Kind == IndexKind.Primary);

        /// <summary>Columns that identify one row: the primary key, else a unique key of NOT NULL columns. Null if there is none.</summary>
        public IReadOnlyList<string> RowKey
        {
            get
            {
                var pk = PrimaryKey;
                if (pk != null && pk.Columns.Count > 0) return pk.Columns.Select(IndexColumnName).ToList();
                var uq = Indexes.FirstOrDefault(i => i.Kind == IndexKind.Unique && i.Columns.All(c => { var col = Column(IndexColumnName(c)); return col != null && !col.Nullable && !HasPrefix(c); }));
                return uq?.Columns.Select(IndexColumnName).ToList();
            }
        }

        public static string IndexColumnName(string entry)
        {
            var i = entry.LastIndexOf('(');
            return i > 0 && entry.EndsWith(")", StringComparison.Ordinal) ? entry.Substring(0, i) : entry;
        }

        private static bool HasPrefix(string entry) => IndexColumnName(entry).Length != entry.Length;

        public TableDesign Clone()
        {
            var d = (TableDesign)MemberwiseClone();
            d.Columns = Columns.Select(c => c.Clone()).ToList();
            d.Indexes = Indexes.Select(i => i.Clone()).ToList();
            d.ForeignKeys = ForeignKeys.Select(f => f.Clone()).ToList();
            d.Checks = Checks.Select(c => c.Clone()).ToList();
            d.Triggers = Triggers.ToList();
            return d;
        }

        public static TableDesign NewTable(string db, string name) => new TableDesign
        {
            Database = db,
            Name = name ?? "",
            Columns = { new ColumnDef { Name = "id", BaseType = "int", Nullable = false, AutoIncrement = true } },
            Indexes = { new IndexDef { Name = "PRIMARY", Kind = IndexKind.Primary, Columns = { "id" } } },
        };
    }

    /// <summary>Type families, used by the editor, the checks and the row form.</summary>
    public static class ColumnTypes
    {
        public static readonly IReadOnlyList<string> Common = new[]
        {
            "int", "varchar", "text", "decimal", "date", "datetime", "timestamp", "tinyint", "bigint", "smallint", "mediumint",
            "char", "mediumtext", "longtext", "tinytext", "double", "float", "time", "year", "enum", "set", "json", "boolean",
            "blob", "mediumblob", "longblob", "binary", "varbinary", "bit", "uuid",
        };

        private static readonly Dictionary<string, (decimal min, decimal max, decimal umax)> IntRanges = new Dictionary<string, (decimal, decimal, decimal)>(StringComparer.OrdinalIgnoreCase)
        {
            ["tinyint"] = (-128m, 127m, 255m),
            ["bool"] = (-128m, 127m, 255m),
            ["boolean"] = (-128m, 127m, 255m),
            ["smallint"] = (-32768m, 32767m, 65535m),
            ["mediumint"] = (-8388608m, 8388607m, 16777215m),
            ["int"] = (-2147483648m, 2147483647m, 4294967295m),
            ["integer"] = (-2147483648m, 2147483647m, 4294967295m),
            ["bigint"] = (-9223372036854775808m, 9223372036854775807m, 18446744073709551615m),
        };

        public static bool IsInteger(string t) => t != null && IntRanges.ContainsKey(t);
        public static (decimal min, decimal max) IntRange(string t, bool unsigned)
        {
            var r = IntRanges[t];
            return unsigned ? (0m, r.umax) : (r.min, r.max);
        }
        public static bool IsDecimal(string t) => Is(t, "decimal", "numeric", "dec", "fixed");
        public static bool IsFloat(string t) => Is(t, "float", "double", "real", "double precision");
        public static bool IsNumeric(string t) => IsInteger(t) || IsDecimal(t) || IsFloat(t) || Is(t, "bit", "year");
        public static bool IsChar(string t) => Is(t, "char", "varchar");
        public static bool IsText(string t) => Is(t, "tinytext", "text", "mediumtext", "longtext", "json");
        public static bool IsStringy(string t) => IsChar(t) || IsText(t) || Is(t, "enum", "set", "uuid", "inet4", "inet6");
        public static bool IsBinary(string t) => Is(t, "binary", "varbinary", "tinyblob", "blob", "mediumblob", "longblob", "geometry", "point", "linestring", "polygon");
        public static bool IsDateLike(string t) => Is(t, "date", "datetime", "timestamp", "time", "year");
        public static bool NeedsLength(string t) => Is(t, "varchar", "varbinary", "enum", "set");
        public static bool TakesLength(string t) => NeedsLength(t) || IsInteger(t) || IsDecimal(t) || IsFloat(t) || Is(t, "char", "binary", "bit", "datetime", "timestamp", "time", "text", "blob");
        private static bool Is(string t, params string[] names) => t != null && names.Contains(t.Trim(), StringComparer.OrdinalIgnoreCase);

        /// <summary>Splits COLUMN_TYPE ("int(10) unsigned zerofill", "enum('a','b')") into parts.</summary>
        public static void Parse(string columnType, ColumnDef into)
        {
            var s = (columnType ?? "").Trim();
            var m = Regex.Match(s, @"^([A-Za-z][A-Za-z0-9 ]*?)\s*(?:\((.*)\))?((?:\s+(?:unsigned|zerofill|signed))*)\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) { into.BaseType = s.ToLowerInvariant(); into.Length = ""; return; }
            into.BaseType = m.Groups[1].Value.Trim().ToLowerInvariant();
            into.Length = m.Groups[2].Success ? m.Groups[2].Value : "";
            var rest = m.Groups[3].Value.ToLowerInvariant();
            into.Unsigned = rest.Contains("unsigned");
            into.Zerofill = rest.Contains("zerofill");
            // display widths are noise since MySQL 8 / MariaDB keeps them: int(11) → int
            if (IsInteger(into.BaseType) && !into.Zerofill && !(into.BaseType == "tinyint" && into.Length == "1")) into.Length = "";
        }

        /// <summary>The values of an enum/set length text: 'a','b' → [a, b].</summary>
        public static List<string> EnumValues(string length)
        {
            var list = new List<string>();
            foreach (Match v in Regex.Matches(length ?? "", @"'((?:[^']|'')*)'")) list.Add(v.Groups[1].Value.Replace("''", "'").Replace("\\\\", "\\"));
            return list;
        }
    }

    public static class TableDesignReader
    {
        public static async Task<TableDesign> ReadAsync(MySqlConnection c, string db, string table, CancellationToken ct = default)
        {
            var d = new TableDesign { Database = db, Name = table, OriginalName = table };
            using (var cmd = Cmd(c, @"SELECT ENGINE, TABLE_COLLATION, TABLE_COMMENT, COALESCE(TABLE_ROWS,0), COALESCE(DATA_LENGTH,0)+COALESCE(INDEX_LENGTH,0), AUTO_INCREMENT, TABLE_TYPE
FROM information_schema.TABLES WHERE TABLE_SCHEMA=@d AND TABLE_NAME=@t", db, table))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await r.ReadAsync(ct).ConfigureAwait(false)) throw new LuminaException(ErrorKind.InvalidInput, "There's no table called " + table + " in " + db + ".");
                d.Engine = r.IsDBNull(0) ? "InnoDB" : r.GetString(0);
                d.Collation = r.IsDBNull(1) ? null : r.GetString(1);
                d.Comment = r.IsDBNull(2) ? "" : r.GetString(2);
                d.RowEstimate = Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture);
                d.Bytes = Convert.ToInt64(r.GetValue(4), CultureInfo.InvariantCulture);
                d.AutoIncrementNext = r.IsDBNull(5) ? (long?)null : Convert.ToInt64(r.GetValue(5), CultureInfo.InvariantCulture);
            }

            using (var cmd = Cmd(c, @"SELECT COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA, COLUMN_COMMENT, COLLATION_NAME, GENERATION_EXPRESSION, DATA_TYPE
FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@d AND TABLE_NAME=@t ORDER BY ORDINAL_POSITION", db, table))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var col = new ColumnDef { Name = r.GetString(0), OriginalName = r.GetString(0) };
                    ColumnTypes.Parse(r.GetString(1), col);
                    col.Nullable = string.Equals(r.GetString(2), "YES", StringComparison.OrdinalIgnoreCase);
                    string extra = r.IsDBNull(4) ? "" : r.GetString(4);
                    col.AutoIncrement = extra.IndexOf("auto_increment", StringComparison.OrdinalIgnoreCase) >= 0;
                    col.OnUpdateNow = extra.IndexOf("on update", StringComparison.OrdinalIgnoreCase) >= 0;
                    col.Comment = r.IsDBNull(5) ? "" : r.GetString(5);
                    col.Collation = r.IsDBNull(6) ? null : r.GetString(6);
                    var gen = r.IsDBNull(7) ? null : r.GetString(7);
                    if (!string.IsNullOrEmpty(gen) && (extra.IndexOf("GENERATED", StringComparison.OrdinalIgnoreCase) >= 0 || extra.IndexOf("PERSISTENT", StringComparison.OrdinalIgnoreCase) >= 0 || extra.IndexOf("VIRTUAL", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        col.GenerationExpression = gen;
                        col.GeneratedStored = extra.IndexOf("VIRTUAL", StringComparison.OrdinalIgnoreCase) < 0;
                    }
                    col.Default = r.IsDBNull(3) || col.IsGenerated ? "" : DdlBuilder.DefaultForDisplay(r.GetString(3), col);
                    d.Columns.Add(col);
                }
            }
            if (d.Columns.Count == 0) return d;   // a view
            // a column that just follows the table's collation is shown (and compared) as "table default"
            foreach (var col in d.Columns)
                if (col.Collation != null && string.Equals(col.Collation, d.Collation, StringComparison.OrdinalIgnoreCase)) col.Collation = null;

            using (var cmd = Cmd(c, @"SELECT INDEX_NAME, NON_UNIQUE, COLUMN_NAME, SUB_PART, INDEX_TYPE FROM information_schema.STATISTICS
WHERE TABLE_SCHEMA=@d AND TABLE_NAME=@t ORDER BY INDEX_NAME='PRIMARY' DESC, INDEX_NAME, SEQ_IN_INDEX", db, table))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                IndexDef cur = null;
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var name = r.GetString(0);
                    if (cur == null || cur.Name != name)
                    {
                        var type = r.IsDBNull(4) ? "" : r.GetString(4);
                        bool unique = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture) == 0;
                        cur = new IndexDef
                        {
                            Name = name,
                            Kind = name == "PRIMARY" ? IndexKind.Primary : type == "FULLTEXT" ? IndexKind.Fulltext : type == "SPATIAL" ? IndexKind.Spatial : unique ? IndexKind.Unique : IndexKind.Index,
                        };
                        d.Indexes.Add(cur);
                    }
                    var colName = r.IsDBNull(2) ? "" : r.GetString(2);
                    if (!r.IsDBNull(3)) colName += "(" + Convert.ToString(r.GetValue(3), CultureInfo.InvariantCulture) + ")";
                    cur.Columns.Add(colName);
                }
            }

            using (var cmd = Cmd(c, @"SELECT k.CONSTRAINT_NAME, k.COLUMN_NAME, k.REFERENCED_TABLE_SCHEMA, k.REFERENCED_TABLE_NAME, k.REFERENCED_COLUMN_NAME, rc.DELETE_RULE, rc.UPDATE_RULE
FROM information_schema.KEY_COLUMN_USAGE k
JOIN information_schema.REFERENTIAL_CONSTRAINTS rc ON rc.CONSTRAINT_SCHEMA = k.CONSTRAINT_SCHEMA AND rc.CONSTRAINT_NAME = k.CONSTRAINT_NAME AND rc.TABLE_NAME = k.TABLE_NAME
WHERE k.TABLE_SCHEMA=@d AND k.TABLE_NAME=@t AND k.REFERENCED_TABLE_NAME IS NOT NULL ORDER BY k.CONSTRAINT_NAME, k.ORDINAL_POSITION", db, table))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                ForeignKeyDef cur = null;
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var name = r.GetString(0);
                    if (cur == null || cur.Name != name)
                    {
                        var refDb = r.GetString(2);
                        cur = new ForeignKeyDef
                        {
                            Name = name,
                            RefDatabase = string.Equals(refDb, db, StringComparison.OrdinalIgnoreCase) ? null : refDb,
                            RefTable = r.GetString(3),
                            OnDelete = r.IsDBNull(5) ? "RESTRICT" : r.GetString(5),
                            OnUpdate = r.IsDBNull(6) ? "RESTRICT" : r.GetString(6),
                        };
                        d.ForeignKeys.Add(cur);
                    }
                    cur.Columns.Add(r.GetString(1));
                    cur.RefColumns.Add(r.GetString(4));
                }
            }

            // CHECK constraints. A JSON column is stored as LONGTEXT + CHECK(json_valid(col)): show it as json.
            try
            {
                using (var cmd = Cmd(c, "SELECT CONSTRAINT_NAME, CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@d AND TABLE_NAME=@t ORDER BY CONSTRAINT_NAME", db, table))
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var name = r.GetString(0);
                        var clause = r.GetString(1);
                        var col = d.Column(name);
                        if (col != null)
                        {
                            if (col.BaseType == "longtext" && Regex.IsMatch(clause, @"^\s*json_valid\(\s*`" + Regex.Escape(col.Name.Replace("`", "``")) + @"`\s*\)\s*$", RegexOptions.IgnoreCase))
                            {
                                col.BaseType = "json";
                                col.Length = "";
                                col.Collation = null;
                            }
                            else col.Check = clause;
                        }
                        else d.Checks.Add(new CheckDef { Name = name, Expression = clause });
                    }
                }
            }
            catch (MySqlException) { /* very old servers have no CHECK_CONSTRAINTS */ }

            using (var cmd = Cmd(c, "SELECT TRIGGER_NAME, ACTION_TIMING, EVENT_MANIPULATION FROM information_schema.TRIGGERS WHERE EVENT_OBJECT_SCHEMA=@d AND EVENT_OBJECT_TABLE=@t ORDER BY ACTION_TIMING, EVENT_MANIPULATION, ACTION_ORDER", db, table))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                    d.Triggers.Add(new TriggerInfo { Name = r.GetString(0), Timing = r.GetString(1), Event = r.GetString(2) });
            return d;
        }

        private static MySqlCommand Cmd(MySqlConnection c, string sql, string db, string table)
        {
            var cmd = new MySqlCommand(sql, c);
            cmd.Parameters.AddWithValue("@d", db);
            cmd.Parameters.AddWithValue("@t", table);
            return cmd;
        }
    }

    /// <summary>Writes the SQL for a design: CREATE TABLE for a new one, the smallest ALTERs for a changed one.</summary>
    public static class DdlBuilder
    {
        private static readonly Regex Expression = new Regex(@"^(current_timestamp|now|localtime|localtimestamp|curdate|current_date|curtime|current_time|utc_timestamp|uuid|sysdate|unix_timestamp)\s*(\(\s*\d*\s*\))?$|^\(.*\)$|^[a-z_]+\s*\(.*\)$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        public static string Q(string name) => SqlLexer.Quote(name);

        /// <summary>The DEFAULT clause value for a column, or null for "no default".</summary>
        public static string DefaultSql(ColumnDef c)
        {
            var d = (c.Default ?? "").Trim();
            if (d.Length == 0) return null;
            if (string.Equals(d, "NULL", StringComparison.OrdinalIgnoreCase)) return "NULL";
            if (d.Length >= 2 && d[0] == '\'' && d[d.Length - 1] == '\'') return d;   // already quoted by the user
            if (Expression.IsMatch(d))
            {
                // CURRENT_TIMESTAMP → current_timestamp(); other functions need brackets around them
                var m = Regex.Match(d, @"^(current_timestamp|now|localtime|localtimestamp)\s*(\(\s*(\d*)\s*\))?$", RegexOptions.IgnoreCase);
                if (m.Success) return "current_timestamp(" + m.Groups[3].Value + ")";
                if (d.StartsWith("(", StringComparison.Ordinal)) return d;
                return "(" + d + ")";
            }
            if (ColumnTypes.IsNumeric(c.BaseType) && Regex.IsMatch(d, @"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$")) return d;
            if (string.Equals(c.BaseType, "bit", StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(d, @"^b'[01]*'$", RegexOptions.IgnoreCase)) return d;
            return SqlLexer.StringLiteral(d);
        }

        /// <summary>The default in a form where 0 and 0.00 compare equal.</summary>
        public static string DefaultKey(ColumnDef c)
        {
            var sql = DefaultSql(c);
            if (sql == null && c.Nullable && !c.IsGenerated && !c.AutoIncrement) return "NULL";   // no default on a nullable column = DEFAULT NULL
            if (sql != null && ColumnTypes.IsNumeric(c.BaseType) &&
                decimal.TryParse(sql.Trim('\''), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
                return n.ToString("0.############################", CultureInfo.InvariantCulture);
            return sql;
        }

        /// <summary>Compares two SQL expressions the way the server stores them: case, backticks, spacing and
        /// outer brackets don't count (inside quoted strings they do).</summary>
        public static bool SameExpression(string a, string b) => NormalizeExpression(a) == NormalizeExpression(b);

        public static string NormalizeExpression(string e)
        {
            if (string.IsNullOrWhiteSpace(e)) return "";
            var sb = new StringBuilder();
            char quote = '\0';
            bool pendingSpace = false;
            void Flush()
            {
                if (pendingSpace && sb.Length > 0)
                {
                    char last = sb[sb.Length - 1];
                    if (char.IsLetterOrDigit(last) || last == '_' || last == '$' || last == '\'' || last == '"') sb.Append(' ');
                }
                pendingSpace = false;
            }
            for (int i = 0; i < e.Length; i++)
            {
                char ch = e[i];
                if (quote != '\0')
                {
                    sb.Append(ch);
                    if (ch == '\\' && i + 1 < e.Length) { sb.Append(e[++i]); continue; }
                    if (ch == quote) quote = '\0';
                    continue;
                }
                if (ch == '\'' || ch == '"') { Flush(); sb.Append(ch); quote = ch; continue; }
                if (ch == '`') continue;
                if (char.IsWhiteSpace(ch)) { pendingSpace = true; continue; }
                if (!char.IsLetterOrDigit(ch) && ch != '_' && ch != '$') { pendingSpace = false; sb.Append(ch); continue; }
                Flush();
                sb.Append(char.ToLowerInvariant(ch));
            }
            var s = sb.ToString();
            while (s.Length > 1 && s[0] == '(' && s[s.Length - 1] == ')' && Balanced(s.Substring(1, s.Length - 2))) s = s.Substring(1, s.Length - 2);
            return s;
        }

        private static bool Balanced(string s)
        {
            int depth = 0; char quote = '\0';
            foreach (var ch in s)
            {
                if (quote != '\0') { if (ch == quote) quote = '\0'; continue; }
                if (ch == '\'' || ch == '"') quote = ch;
                else if (ch == '(') depth++;
                else if (ch == ')' && --depth < 0) return false;
            }
            return depth == 0;
        }

        /// <summary>information_schema COLUMN_DEFAULT ('abc', 3, NULL, current_timestamp()) → what the editor shows.</summary>
        public static string DefaultForDisplay(string serverDefault, ColumnDef c)
        {
            if (serverDefault == null) return "";
            var d = serverDefault;
            if (d == "NULL") return "NULL";
            if (d.Length >= 2 && d[0] == '\'' && d[d.Length - 1] == '\'')
            {
                var inner = d.Substring(1, d.Length - 2).Replace("''", "'").Replace("\\\\", "\\");
                if (inner.Length == 0) return "''";   // an empty-string default must not read as "no default"
                // a string default that looks like a keyword or a number keeps its quotes, so it stays a string
                if (string.Equals(inner, "NULL", StringComparison.OrdinalIgnoreCase) || Expression.IsMatch(inner) ||
                    !ColumnTypes.IsStringy(c.BaseType) && !ColumnTypes.IsDateLike(c.BaseType)) return d;
                return inner;
            }
            if (Regex.IsMatch(d, @"^current_timestamp\(\)$", RegexOptions.IgnoreCase)) return "CURRENT_TIMESTAMP";
            return d;
        }

        public static string ColumnSql(ColumnDef c)
        {
            var sb = new StringBuilder();
            sb.Append(Q(c.Name)).Append(' ');
            if (string.Equals(c.BaseType, "boolean", StringComparison.OrdinalIgnoreCase) || string.Equals(c.BaseType, "bool", StringComparison.OrdinalIgnoreCase))
                sb.Append("TINYINT(1)");
            else sb.Append(c.FullType);
            if (!string.IsNullOrEmpty(c.Collation) && ColumnTypes.IsStringy(c.BaseType) && !string.Equals(c.BaseType, "json", StringComparison.OrdinalIgnoreCase))
                sb.Append(" COLLATE ").Append(c.Collation);
            if (c.IsGenerated)
            {
                sb.Append(" AS (").Append(c.GenerationExpression).Append(c.GeneratedStored ? ") STORED" : ") VIRTUAL");
            }
            else
            {
                sb.Append(c.Nullable ? " NULL" : " NOT NULL");
                var def = DefaultSql(c);
                if (def != null && !c.AutoIncrement) sb.Append(" DEFAULT ").Append(def);
                if (c.AutoIncrement) sb.Append(" AUTO_INCREMENT");
                if (c.OnUpdateNow) sb.Append(" ON UPDATE current_timestamp()");
            }
            if (!string.IsNullOrEmpty(c.Comment)) sb.Append(" COMMENT ").Append(SqlLexer.StringLiteral(c.Comment));
            if (!string.IsNullOrEmpty(c.Check)) sb.Append(" CHECK (").Append(c.Check).Append(')');
            return sb.ToString();
        }

        private static string Cols(IEnumerable<string> entries) =>
            string.Join(", ", entries.Select(e =>
            {
                var name = TableDesign.IndexColumnName(e);
                return name.Length == e.Length ? Q(e) : Q(name) + e.Substring(name.Length);
            }));

        public static string IndexSql(IndexDef i) =>
            i.Kind == IndexKind.Primary ? "PRIMARY KEY (" + Cols(i.Columns) + ")"
            : (i.Kind == IndexKind.Unique ? "UNIQUE INDEX " : i.Kind == IndexKind.Fulltext ? "FULLTEXT INDEX " : i.Kind == IndexKind.Spatial ? "SPATIAL INDEX " : "INDEX ") + Q(i.Name) + " (" + Cols(i.Columns) + ")";

        public static string ForeignKeySql(ForeignKeyDef f, string db) =>
            "CONSTRAINT " + Q(f.Name) + " FOREIGN KEY (" + Cols(f.Columns) + ") REFERENCES " +
            (f.RefDatabase != null && !string.Equals(f.RefDatabase, db, StringComparison.OrdinalIgnoreCase) ? Q(f.RefDatabase) + "." : "") + Q(f.RefTable) +
            " (" + Cols(f.RefColumns) + ") ON DELETE " + ForeignKeyDef.Rule(f.OnDelete) + " ON UPDATE " + ForeignKeyDef.Rule(f.OnUpdate);

        public static string CheckSql(CheckDef c) => "CONSTRAINT " + Q(c.Name) + " CHECK (" + c.Expression + ")";

        public static string TableName(TableDesign d) => Q(d.Database) + "." + Q(d.Name);

        public static string Create(TableDesign d)
        {
            var lines = new List<string>();
            lines.AddRange(d.Columns.Select(ColumnSql));
            lines.AddRange(d.Indexes.Where(i => i.Columns.Count > 0).Select(IndexSql));
            lines.AddRange(d.ForeignKeys.Select(f => ForeignKeySql(f, d.Database)));
            lines.AddRange(d.Checks.Select(CheckSql));
            var sb = new StringBuilder();
            sb.Append("CREATE TABLE ").Append(TableName(d)).Append(" (\n  ").Append(string.Join(",\n  ", lines)).Append("\n)");
            sb.Append(" ENGINE=").Append(string.IsNullOrEmpty(d.Engine) ? "InnoDB" : d.Engine);
            var coll = string.IsNullOrEmpty(d.Collation) ? "utf8mb4_unicode_ci" : d.Collation;
            sb.Append(" DEFAULT CHARSET=").Append(CharsetOf(coll)).Append(" COLLATE=").Append(coll);
            if (!string.IsNullOrEmpty(d.Comment)) sb.Append(" COMMENT=").Append(SqlLexer.StringLiteral(d.Comment));
            return sb.Append(';').ToString();
        }

        public static string CharsetOf(string collation)
        {
            var c = (collation ?? "").ToLowerInvariant();
            var i = c.IndexOf('_');
            return i > 0 ? c.Substring(0, i) : "utf8mb4";
        }

        /// <summary>
        /// The statements that turn <paramref name="before"/> into <paramref name="after"/>.
        /// Foreign keys (and checks) that change are dropped in a first ALTER and added back in a last one:
        /// InnoDB refuses to drop and re-add a constraint of the same name in one statement.
        /// </summary>
        public static List<string> Alter(TableDesign before, TableDesign after)
        {
            var result = new List<string>();
            if (after.IsNew) { result.Add(Create(after)); return result; }
            var table = Q(after.Database) + "." + Q(before.OriginalName ?? before.Name);
            string db = after.Database;

            var dropFirst = new List<string>();
            var main = new List<string>();
            var addLast = new List<string>();

            // ---- constraints that go away or change: drop first
            foreach (var f in before.ForeignKeys)
            {
                var now = after.ForeignKeys.FirstOrDefault(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase));
                if (now == null || !now.Same(f, db) || RenamedColumns(before, after).Any(r => f.Columns.Contains(r, StringComparer.OrdinalIgnoreCase)))
                    dropFirst.Add("DROP FOREIGN KEY " + Q(f.Name));
            }
            foreach (var ch in before.Checks)
            {
                var now = after.Checks.FirstOrDefault(x => string.Equals(x.Name, ch.Name, StringComparison.OrdinalIgnoreCase));
                if (now == null || !SameExpression(now.Expression, ch.Expression)) dropFirst.Add("DROP CONSTRAINT " + Q(ch.Name));
            }

            // ---- columns
            var keptOriginal = new HashSet<string>(after.Columns.Where(c => !c.IsNew && before.Column(c.OriginalName) != null).Select(c => c.OriginalName), StringComparer.OrdinalIgnoreCase);
            foreach (var c in before.Columns.Where(c => !keptOriginal.Contains(c.Name)))
                main.Add("DROP COLUMN " + Q(c.Name));

            // simulate the column order as each clause is applied, so AFTER/FIRST is written only where needed
            var order = before.Columns.Where(c => keptOriginal.Contains(c.Name)).Select(c => c.Name).ToList();
            var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in after.Columns.Where(c => !c.IsNew && keptOriginal.Contains(c.OriginalName))) renamed[c.OriginalName] = c.Name;
            order = order.Select(n => renamed[n]).ToList();

            for (int i = 0; i < after.Columns.Count; i++)
            {
                var c = after.Columns[i];
                string wantBefore = i == 0 ? null : after.Columns[i - 1].Name;
                string position = wantBefore == null ? " FIRST" : " AFTER " + Q(wantBefore);
                var old = c.IsNew ? null : before.Columns.FirstOrDefault(o => string.Equals(o.Name, c.OriginalName, StringComparison.OrdinalIgnoreCase));
                if (old == null)
                {
                    main.Add("ADD COLUMN " + ColumnSql(c) + position);
                    order.Insert(wantBefore == null ? 0 : order.FindIndex(n => string.Equals(n, wantBefore, StringComparison.OrdinalIgnoreCase)) + 1, c.Name);
                    continue;
                }
                int at = order.FindIndex(n => string.Equals(n, c.Name, StringComparison.OrdinalIgnoreCase));
                string hasBefore = at <= 0 ? null : order[at - 1];
                bool moved = !string.Equals(hasBefore, wantBefore, StringComparison.OrdinalIgnoreCase);
                bool rename = !string.Equals(c.Name, c.OriginalName, StringComparison.Ordinal);
                bool changed = !c.SameDefinition(old);
                if (!moved && !rename && !changed) continue;
                main.Add((rename ? "CHANGE COLUMN " + Q(c.OriginalName) + " " : "MODIFY COLUMN ") + ColumnSql(c) + (moved ? position : ""));
                if (moved)
                {
                    order.RemoveAt(at);
                    order.Insert(wantBefore == null ? 0 : order.FindIndex(n => string.Equals(n, wantBefore, StringComparison.OrdinalIgnoreCase)) + 1, c.Name);
                }
            }

            // ---- indexes (column renames are followed by the server, so compare with names mapped)
            var beforeIdx = before.Indexes.Select(i => Map(i, renamed)).ToList();
            foreach (var i in beforeIdx)
            {
                var now = after.Indexes.FirstOrDefault(x => x.Kind == IndexKind.Primary && i.Kind == IndexKind.Primary || string.Equals(x.Name, i.Name, StringComparison.OrdinalIgnoreCase) && x.Kind != IndexKind.Primary && i.Kind != IndexKind.Primary);
                bool allColumnsDropped = i.Columns.All(col => after.Column(TableDesign.IndexColumnName(col)) == null);
                if (allColumnsDropped) continue;   // went with its columns
                if (now == null || !now.Same(i))
                    main.Add(i.Kind == IndexKind.Primary ? "DROP PRIMARY KEY" : "DROP INDEX " + Q(i.Name));
            }
            foreach (var i in after.Indexes.Where(x => x.Columns.Count > 0))
            {
                var was = beforeIdx.FirstOrDefault(x => x.Kind == IndexKind.Primary && i.Kind == IndexKind.Primary || string.Equals(x.Name, i.Name, StringComparison.OrdinalIgnoreCase) && x.Kind != IndexKind.Primary && i.Kind != IndexKind.Primary);
                if (was == null || !was.Same(i) || was.Columns.All(col => after.Column(TableDesign.IndexColumnName(col)) == null))
                    main.Add("ADD " + IndexSql(i));
            }

            // ---- table options
            if (!string.Equals(before.Engine ?? "", after.Engine ?? "", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(after.Engine))
                main.Add("ENGINE=" + after.Engine);
            if ((before.Comment ?? "") != (after.Comment ?? ""))
                main.Add("COMMENT=" + SqlLexer.StringLiteral(after.Comment ?? ""));

            // ---- constraints that are new or changed: add last
            foreach (var f in after.ForeignKeys)
            {
                var was = before.ForeignKeys.FirstOrDefault(x => string.Equals(x.Name, f.Name, StringComparison.OrdinalIgnoreCase));
                if (was == null || dropFirst.Contains("DROP FOREIGN KEY " + Q(was.Name)))
                    addLast.Add("ADD " + ForeignKeySql(f, db));
            }
            foreach (var ch in after.Checks)
            {
                var was = before.Checks.FirstOrDefault(x => string.Equals(x.Name, ch.Name, StringComparison.OrdinalIgnoreCase));
                if (was == null || !SameExpression(was.Expression, ch.Expression)) addLast.Add("ADD " + CheckSql(ch));
            }

            if (dropFirst.Count > 0) result.Add("ALTER TABLE " + table + "\n  " + string.Join(",\n  ", dropFirst) + ";");
            if (main.Count > 0) result.Add("ALTER TABLE " + table + "\n  " + string.Join(",\n  ", main) + ";");
            if (addLast.Count > 0) result.Add("ALTER TABLE " + table + "\n  " + string.Join(",\n  ", addLast) + ";");
            return result;
        }

        private static IEnumerable<string> RenamedColumns(TableDesign before, TableDesign after) =>
            after.Columns.Where(c => !c.IsNew && !string.Equals(c.Name, c.OriginalName, StringComparison.Ordinal)).Select(c => c.OriginalName);

        private static IndexDef Map(IndexDef i, Dictionary<string, string> renamed)
        {
            var m = i.Clone();
            m.Columns = i.Columns.Select(e =>
            {
                var n = TableDesign.IndexColumnName(e);
                return renamed.TryGetValue(n, out var to) ? to + e.Substring(n.Length) : e;
            }).ToList();
            return m;
        }
    }

    public enum DesignSeverity { Bad, Warn }

    public sealed class DesignIssue
    {
        public DesignIssue(DesignSeverity s, string message, string column = null) { Severity = s; Message = message; Column = column; }
        public DesignSeverity Severity { get; }
        public string Message { get; }
        /// <summary>The column the issue is about (to highlight its row), or null.</summary>
        public string Column { get; }
        public override string ToString() => Severity + ": " + Message;
    }

    /// <summary>Catches the mistakes the server would reject (or accept and regret) before any SQL is sent.</summary>
    public static class DesignChecker
    {
        public static List<DesignIssue> Check(TableDesign d, SchemaSnapshot schema = null)
        {
            var list = new List<DesignIssue>();
            void Bad(string m, string col = null) => list.Add(new DesignIssue(DesignSeverity.Bad, m, col));
            void Warn(string m, string col = null) => list.Add(new DesignIssue(DesignSeverity.Warn, m, col));

            var tableProblem = NameProblem(d.Name, "The table");
            if (tableProblem != null) Bad(tableProblem);
            if (d.IsNew && schema?.FindTable(d.Database, d.Name) != null) Bad("There's already a table called " + d.Name + " in " + d.Database + ". Pick another name.");
            if (d.Columns.Count == 0) Bad("A table needs at least one column.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int autoCount = 0;
            foreach (var c in d.Columns)
            {
                var label = string.IsNullOrWhiteSpace(c.Name) ? "A column" : c.Name;
                var np = NameProblem(c.Name, "A column");
                if (np != null) Bad(np, c.Name);
                else if (!seen.Add(c.Name)) Bad("Two columns are called " + c.Name + ". Each column needs its own name.", c.Name);

                var t = (c.BaseType ?? "").Trim().ToLowerInvariant();
                var len = (c.Length ?? "").Trim();
                if (t.Length == 0) { Bad(label + " needs a type.", c.Name); continue; }
                if (!ColumnTypes.Common.Contains(t) && !ColumnTypes.IsNumeric(t) && !ColumnTypes.IsBinary(t) && !ColumnTypes.IsText(t) && !ColumnTypes.IsDateLike(t) &&
                    !new[] { "inet4", "inet6", "geometry", "point", "linestring", "polygon", "integer", "numeric", "real", "bool", "tinyblob" }.Contains(t))
                {
                    var near = SqlKeywords.Closest(t, ColumnTypes.Common, 2);
                    Bad(label + ": " + t + " isn't a MariaDB type." + (near != null ? " Did you mean " + near + "?" : ""), c.Name);
                    continue;
                }
                if (ColumnTypes.NeedsLength(t) && len.Length == 0)
                    Bad(t == "enum" || t == "set" ? label + " needs its list of values, like 'small','medium','large'." : label + " needs a length, like " + t + "(100).", c.Name);
                if (ColumnTypes.IsChar(t) || t == "varbinary" || t == "binary")
                {
                    if (len.Length > 0 && !int.TryParse(len, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) Bad(label + ": the length must be a whole number.", c.Name);
                    else if (len.Length > 0 && t == "varchar" && int.Parse(len, CultureInfo.InvariantCulture) > 16383) Bad(label + ": varchar can hold at most 16,383 characters in utf8mb4. Use TEXT for longer text.", c.Name);
                    else if (len.Length > 0 && t == "char" && int.Parse(len, CultureInfo.InvariantCulture) > 255) Bad(label + ": char can hold at most 255 characters. Use varchar.", c.Name);
                }
                if (ColumnTypes.IsDecimal(t) && len.Length > 0)
                {
                    var m = Regex.Match(len, @"^\s*(\d+)\s*(?:,\s*(\d+)\s*)?$");
                    if (!m.Success) Bad(label + ": write the size as digits,decimals, like 10,2.", c.Name);
                    else
                    {
                        int p = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), s = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                        if (p < 1 || p > 65) Bad(label + ": a decimal can have 1 to 65 digits.", c.Name);
                        else if (s > 38 || s > p) Bad(label + ": the decimals (" + s + ") can't be more than the digits (" + p + ") or 38.", c.Name);
                    }
                }
                if ((t == "enum" || t == "set") && len.Length > 0 && ColumnTypes.EnumValues(len).Count == 0)
                    Bad(label + ": write the values in quotes, like 'yes','no'.", c.Name);
                if (c.AutoIncrement)
                {
                    autoCount++;
                    if (!ColumnTypes.IsInteger(t)) Bad(label + ": only whole-number columns can count up by themselves (AUTO_INCREMENT).", c.Name);
                    if (!d.Indexes.Any(i => i.Columns.Count > 0 && string.Equals(TableDesign.IndexColumnName(i.Columns[0]), c.Name, StringComparison.OrdinalIgnoreCase) && i.Kind != IndexKind.Fulltext))
                        Bad(label + " counts up by itself, so it must be a key. Tick PK (or add an index that starts with it).", c.Name);
                }
                if (c.OnUpdateNow && !(t == "timestamp" || t == "datetime")) Bad(label + ": \"on update, now\" only works for datetime and timestamp.", c.Name);
                var def = DdlBuilder.DefaultSql(c);
                if (def == "NULL" && !c.Nullable) Bad(label + " can't be empty (NOT NULL), so its default can't be NULL.", c.Name);
                if (def != null && c.AutoIncrement) Warn(label + " counts up by itself; its default is ignored.", c.Name);
                if (def != null && def.StartsWith("'", StringComparison.Ordinal) && ColumnTypes.IsInteger(t)) Bad(label + ": the default " + c.Default + " isn't a whole number.", c.Name);
                if (def != null && (t == "enum" || t == "set") && def.StartsWith("'", StringComparison.Ordinal))
                {
                    var values = ColumnTypes.EnumValues(len);
                    var given = c.Default.Trim().Trim('\'');
                    if (t == "enum" && !values.Contains(given)) Bad(label + ": the default " + given + " isn't one of its values.", c.Name);
                }
                if (def != null && def.StartsWith("current_timestamp", StringComparison.Ordinal) && !(t == "timestamp" || t == "datetime" || t == "date"))
                    Bad(label + ": CURRENT_TIMESTAMP can only be the default of a date or time column.", c.Name);
                if (t == "float" || t == "double") Warn(label + " is " + t + ": it stores approximate values. For money use decimal(10,2).", c.Name);
                if (t == "varchar" && int.TryParse(len, out var vl) && vl > 0 && d.Indexes.Any(i => i.Kind == IndexKind.Unique && i.Columns.Contains(c.Name, StringComparer.OrdinalIgnoreCase)) && vl > 768)
                    Warn(label + " is too long for a unique key (InnoDB keys hold 3,072 bytes = 768 utf8mb4 characters).", c.Name);
            }
            if (autoCount > 1) Bad("Only one column per table can count up by itself (AUTO_INCREMENT).");
            if (d.PrimaryKey == null && d.Columns.Count > 0)
                Warn("This table has no primary key. Rows can't be edited or deleted one by one, and duplicates can creep in. Tick PK on an id column.");

            var idxNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var i in d.Indexes)
            {
                if (i.Kind != IndexKind.Primary)
                {
                    var np = NameProblem(i.Name, "An index");
                    if (np != null) Bad(np);
                    else if (!idxNames.Add(i.Name)) Bad("Two indexes are called " + i.Name + ".");
                }
                if (i.Columns.Count == 0) Bad((i.Kind == IndexKind.Primary ? "The primary key" : "The index " + i.Name) + " has no columns.");
                foreach (var e in i.Columns)
                {
                    var col = d.Column(TableDesign.IndexColumnName(e));
                    if (col == null) Bad((i.Kind == IndexKind.Primary ? "The primary key" : "The index " + i.Name) + " uses " + TableDesign.IndexColumnName(e) + ", which isn't a column any more.");
                    else if (i.Kind == IndexKind.Primary && col.Nullable) Warn(col.Name + " is in the primary key, so it will become NOT NULL.", col.Name);
                    else if (col != null && ColumnTypes.IsText(col.BaseType) && i.Kind != IndexKind.Fulltext && TableDesign.IndexColumnName(e).Length == e.Length)
                        Bad(col.Name + " is " + col.BaseType + ", so a normal index needs a prefix length, like " + col.Name + "(100). A FULLTEXT index doesn't.", col.Name);
                }
            }

            var fkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in d.ForeignKeys)
            {
                var np = NameProblem(f.Name, "A foreign key");
                if (np != null) Bad(np);
                else if (!fkNames.Add(f.Name)) Bad("Two foreign keys are called " + f.Name + ".");
                if (f.Columns.Count == 0 || f.RefColumns.Count == 0 || string.IsNullOrWhiteSpace(f.RefTable)) { Bad("The foreign key " + f.Name + " needs a column, a table and the column it points to."); continue; }
                if (f.Columns.Count != f.RefColumns.Count) Bad("The foreign key " + f.Name + " has " + f.Columns.Count + " column(s) but points to " + f.RefColumns.Count + ".");
                var refDb = f.RefDatabase ?? d.Database;
                var refTable = string.Equals(f.RefTable, d.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(refDb, d.Database, StringComparison.OrdinalIgnoreCase) ? null : schema?.FindTable(refDb, f.RefTable);
                bool selfRef = string.Equals(f.RefTable, d.Name, StringComparison.OrdinalIgnoreCase) && string.Equals(refDb, d.Database, StringComparison.OrdinalIgnoreCase);
                if (!selfRef && schema != null && schema.IsLoaded && refTable == null) { Bad("The foreign key " + f.Name + " points to " + f.RefTable + ", but there's no such table in " + refDb + "."); continue; }
                for (int k = 0; k < Math.Min(f.Columns.Count, f.RefColumns.Count); k++)
                {
                    var mine = d.Column(f.Columns[k]);
                    if (mine == null) { Bad("The foreign key " + f.Name + " uses " + f.Columns[k] + ", which isn't a column."); continue; }
                    string theirType = null;
                    if (selfRef) { var rc = d.Column(f.RefColumns[k]); theirType = rc?.FullType; if (rc == null) Bad(f.RefTable + " has no column " + f.RefColumns[k] + "."); }
                    else if (refTable != null)
                    {
                        var rc = refTable.Find(f.RefColumns[k]);
                        if (rc == null) { Bad(f.RefTable + " has no column " + f.RefColumns[k] + "."); continue; }
                        var tmp = new ColumnDef();
                        ColumnTypes.Parse(rc.Type, tmp);
                        theirType = tmp.FullType;
                    }
                    if (theirType != null && !SameKeyType(mine.FullType, theirType))
                        Bad(mine.Name + " is " + mine.FullType.ToLowerInvariant() + " but " + f.RefTable + "." + f.RefColumns[k] + " is " + theirType.ToLowerInvariant() + ". A foreign key needs the same type on both sides (error 150 otherwise).", mine.Name);
                    if (ForeignKeyDef.Rule(f.OnDelete) == "SET NULL" && !mine.Nullable || ForeignKeyDef.Rule(f.OnUpdate) == "SET NULL" && !mine.Nullable)
                        Bad(mine.Name + " is NOT NULL, so the foreign key can't SET NULL. Allow empty (untick NN) or pick CASCADE/RESTRICT.", mine.Name);
                }
            }

            var checkNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var ch in d.Checks)
            {
                var np = NameProblem(ch.Name, "A check");
                if (np != null) Bad(np);
                else if (!checkNames.Add(ch.Name)) Bad("Two checks are called " + ch.Name + ".");
                if (string.IsNullOrWhiteSpace(ch.Expression)) Bad("The check " + ch.Name + " needs a rule, like price >= 0.");
            }
            return list;
        }

        private static bool SameKeyType(string a, string b)
        {
            string N(string t) => Regex.Replace(t.ToLowerInvariant(), @"^(tinyint|smallint|mediumint|int|integer|bigint)\(\d+\)", "$1").Replace("integer", "int").Trim();
            var x = N(a);
            var y = N(b);
            if (x == y) return true;
            // varchar lengths may differ; the character set must match, which we can't see here
            return Regex.IsMatch(x, @"^varchar\(") && Regex.IsMatch(y, @"^varchar\(") || Regex.IsMatch(x, @"^char\(") && Regex.IsMatch(y, @"^char\(");
        }

        /// <summary>Plain-English problem with a table/column/index name, or null if it's fine.</summary>
        public static string NameProblem(string name, string what)
        {
            if (string.IsNullOrWhiteSpace(name)) return what + " needs a name.";
            if (name.Length > 64) return what + " name can be at most 64 characters (" + name + " has " + name.Length + ").";
            if (name != name.Trim()) return what + " name \"" + name + "\" starts or ends with a space. Remove it: it's easy to mistype later.";
            if (name.EndsWith(" ", StringComparison.Ordinal) || name.Contains("`")) return what + " name can't contain a backtick (`).";
            return null;
        }
    }
}
