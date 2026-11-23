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
    /// <summary>One line of the filter builder: column, test, value.</summary>
    public sealed class RowFilter
    {
        public string Column { get; set; }
        public string Op { get; set; } = "=";
        public string Value { get; set; } = "";

        public static readonly IReadOnlyList<string> Ops = new[] { "=", "≠", "<", "≤", ">", "≥", "contains", "starts with", "ends with", "is empty (NULL)", "is not empty" };
        public bool NeedsValue => Op != "is empty (NULL)" && Op != "is not empty";
    }

    /// <summary>What the Rows page asks for: a page of a table, searched, filtered and sorted.</summary>
    public sealed class RowQuery
    {
        public TableDesign Table { get; set; }
        public string Search { get; set; }
        public List<RowFilter> Filters { get; set; } = new List<RowFilter>();
        public string SortColumn { get; set; }
        public bool SortDescending { get; set; }
        public long Offset { get; set; }
        public int Limit { get; set; } = 100;

        public string Where()
        {
            var parts = new List<string>();
            foreach (var f in Filters.Where(f => !string.IsNullOrEmpty(f.Column)))
            {
                var col = Table.Column(f.Column);
                if (col == null) continue;
                var c = DdlBuilder.Q(col.Name);
                var v = f.Value ?? "";
                switch (f.Op)
                {
                    case "=": parts.Add(c + " = " + RowSql.Literal(col, v)); break;
                    case "≠": parts.Add("(" + c + " <> " + RowSql.Literal(col, v) + " OR " + c + " IS NULL)"); break;
                    case "<": parts.Add(c + " < " + RowSql.Literal(col, v)); break;
                    case "≤": parts.Add(c + " <= " + RowSql.Literal(col, v)); break;
                    case ">": parts.Add(c + " > " + RowSql.Literal(col, v)); break;
                    case "≥": parts.Add(c + " >= " + RowSql.Literal(col, v)); break;
                    case "contains": parts.Add(c + " LIKE " + SqlLexer.StringLiteral("%" + LikeEscape(v) + "%")); break;
                    case "starts with": parts.Add(c + " LIKE " + SqlLexer.StringLiteral(LikeEscape(v) + "%")); break;
                    case "ends with": parts.Add(c + " LIKE " + SqlLexer.StringLiteral("%" + LikeEscape(v))); break;
                    case "is empty (NULL)": parts.Add(c + " IS NULL"); break;
                    case "is not empty": parts.Add(c + " IS NOT NULL"); break;
                }
            }
            var s = (Search ?? "").Trim();
            if (s.Length > 0)
            {
                var pattern = SqlLexer.StringLiteral("%" + LikeEscape(s) + "%");
                var searchable = Table.Columns.Where(c => !ColumnTypes.IsBinary(c.BaseType)).ToList();
                if (searchable.Count > 0) parts.Add("(" + string.Join(" OR ", searchable.Select(c => DdlBuilder.Q(c.Name) + " LIKE " + pattern)) + ")");
            }
            return parts.Count == 0 ? "" : " WHERE " + string.Join(" AND ", parts);
        }

        public static string LikeEscape(string s) => (s ?? "").Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

        private string From => DdlBuilder.TableName(Table);

        public string SelectSql()
        {
            var sb = new StringBuilder("SELECT * FROM ").Append(From).Append(Where());
            var sort = Table.Column(SortColumn ?? "");
            if (sort != null) sb.Append(" ORDER BY ").Append(DdlBuilder.Q(sort.Name)).Append(SortDescending ? " DESC" : "");
            else if (Table.RowKey != null) sb.Append(" ORDER BY ").Append(string.Join(", ", Table.RowKey.Select(DdlBuilder.Q)));
            sb.Append(" LIMIT ").Append(Limit.ToString(CultureInfo.InvariantCulture));
            if (Offset > 0) sb.Append(" OFFSET ").Append(Offset.ToString(CultureInfo.InvariantCulture));
            return sb.Append(';').ToString();
        }

        public string CountSql() => "SELECT COUNT(*) FROM " + From + Where() + ";";
    }

    /// <summary>A row value in the form: text, or NULL, or "leave it to the default".</summary>
    public sealed class FieldValue
    {
        public string Text { get; set; } = "";
        public bool IsNull { get; set; }
        public bool UseDefault { get; set; }
        public static FieldValue Of(string text) => text == null ? new FieldValue { IsNull = true } : new FieldValue { Text = text };
    }

    /// <summary>SQL for row changes: INSERT, UPDATE and DELETE by key, with values checked before they're sent.</summary>
    public static class RowSql
    {
        /// <summary>A form value as an SQL literal for this column.</summary>
        public static string Literal(ColumnDef c, string text)
        {
            if (text == null) return "NULL";
            var t = (c.BaseType ?? "").ToLowerInvariant();
            var s = text.Trim();
            if ((ColumnTypes.IsInteger(t) || ColumnTypes.IsDecimal(t) || ColumnTypes.IsFloat(t)) && Regex.IsMatch(s, @"^[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?$"))
                return s;
            if (t == "bit")
            {
                if (Regex.IsMatch(s, @"^b'[01]+'$", RegexOptions.IgnoreCase)) return s;
                if (Regex.IsMatch(s, @"^\d+$")) return s;
            }
            if (ColumnTypes.IsBinary(t) && Regex.IsMatch(s, @"^0x[0-9a-fA-F]*$")) return s.Length == 2 ? "''" : s;
            return SqlLexer.StringLiteral(text);
        }

        public static string Insert(TableDesign t, IDictionary<string, FieldValue> values)
        {
            var cols = new List<string>();
            var vals = new List<string>();
            foreach (var c in t.Columns)
            {
                if (c.IsGenerated || !values.TryGetValue(c.Name, out var v) || v == null || v.UseDefault) continue;
                cols.Add(DdlBuilder.Q(c.Name));
                vals.Add(v.IsNull ? "NULL" : Literal(c, v.Text));
            }
            if (cols.Count == 0) return "INSERT INTO " + DdlBuilder.TableName(t) + " () VALUES ();";
            return "INSERT INTO " + DdlBuilder.TableName(t) + " (" + string.Join(", ", cols) + ")\nVALUES (" + string.Join(", ", vals) + ");";
        }

        public static string KeyWhere(TableDesign t, IDictionary<string, string> key)
        {
            var parts = new List<string>();
            foreach (var k in t.RowKey ?? throw new LuminaException(ErrorKind.InvalidInput, "This table has no primary key, so a row can't be picked out safely. Add a primary key in Structure."))
            {
                var col = t.Column(k);
                key.TryGetValue(k, out var v);
                parts.Add(DdlBuilder.Q(k) + (v == null ? " IS NULL" : " = " + Literal(col, v)));
            }
            return string.Join(" AND ", parts);
        }

        /// <summary>UPDATE of only the changed fields, by key. Null when nothing changed.</summary>
        public static string Update(TableDesign t, IDictionary<string, string> key, IDictionary<string, FieldValue> changes)
        {
            var sets = new List<string>();
            foreach (var c in t.Columns)
            {
                if (c.IsGenerated || !changes.TryGetValue(c.Name, out var v) || v == null) continue;
                sets.Add(DdlBuilder.Q(c.Name) + " = " + (v.UseDefault ? "DEFAULT" : v.IsNull ? "NULL" : Literal(c, v.Text)));
            }
            if (sets.Count == 0) return null;
            return "UPDATE " + DdlBuilder.TableName(t) + "\nSET " + string.Join(", ", sets) + "\nWHERE " + KeyWhere(t, key) + "\nLIMIT 1;";
        }

        public static string Delete(TableDesign t, IReadOnlyList<IDictionary<string, string>> keys)
        {
            if (keys.Count == 0) return null;
            var where = keys.Count == 1 ? KeyWhere(t, keys[0]) : string.Join("\n   OR ", keys.Select(k => "(" + KeyWhere(t, k) + ")"));
            return "DELETE FROM " + DdlBuilder.TableName(t) + "\nWHERE " + where + "\nLIMIT " + keys.Count.ToString(CultureInfo.InvariantCulture) + ";";
        }

        /// <summary>What's wrong with this value for this column, in plain words, or null if it will be accepted.</summary>
        public static string Problem(ColumnDef c, FieldValue v, bool isInsert)
        {
            if (c.IsGenerated) return null;
            var t = (c.BaseType ?? "").ToLowerInvariant();
            if (v.UseDefault)
            {
                if (!c.Nullable && DdlBuilder.DefaultSql(c) == null && !c.AutoIncrement && isInsert && !(t == "timestamp"))
                    return c.Name + " has no default, so it needs a value.";
                return null;
            }
            if (v.IsNull)
            {
                if (!c.Nullable && !(c.AutoIncrement && isInsert)) return c.Name + " can't be empty (NOT NULL). Give it a value.";
                return null;
            }
            var s = v.Text ?? "";
            var trimmed = s.Trim();
            if (ColumnTypes.IsInteger(t))
            {
                if (t == "tinyint" && c.Length == "1" && (trimmed == "true" || trimmed == "false")) return null;
                if (!Regex.IsMatch(trimmed, @"^[+-]?\d+$")) return c.Name + " takes a whole number" + (trimmed.Length == 0 ? "." : "; \"" + Short(s) + "\" isn't one.");
                if (!decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return c.Name + ": that number is far too big.";
                var (min, max) = ColumnTypes.IntRange(t, c.Unsigned);
                if (n < min || n > max) return c.Name + " is " + t + (c.Unsigned ? " unsigned" : "") + ", so it holds " + min.ToString("N0", CultureInfo.InvariantCulture) + " to " + max.ToString("N0", CultureInfo.InvariantCulture) + ".";
                return null;
            }
            if (ColumnTypes.IsDecimal(t))
            {
                if (!Regex.IsMatch(trimmed, @"^[+-]?(\d+\.?\d*|\.\d+)$")) return c.Name + " takes a number like 12.50 (a dot, no commas)" + (trimmed.Length == 0 ? "." : "; \"" + Short(s) + "\" isn't one.");
                var m = Regex.Match(c.Length ?? "", @"^\s*(\d+)\s*(?:,\s*(\d+))?");
                int p = m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : 10, sc = m.Success && m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                var digits = trimmed.TrimStart('+', '-');
                var whole = digits.Split('.')[0].TrimStart('0');
                if (whole.Length > p - sc) return c.Name + " is decimal(" + p + "," + sc + "): at most " + (p - sc) + " digit(s) before the dot.";
                if (c.Unsigned && trimmed.StartsWith("-", StringComparison.Ordinal)) return c.Name + " can't be negative.";
                return null;
            }
            if (ColumnTypes.IsFloat(t))
            {
                if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return c.Name + " takes a number like 3.14" + (trimmed.Length == 0 ? "." : "; \"" + Short(s) + "\" isn't one.");
                return null;
            }
            if (ColumnTypes.IsChar(t) && int.TryParse(c.Length, out var maxLen))
            {
                int len = CodePoints(s);
                if (len > maxLen) return c.Name + " holds at most " + maxLen + " characters; this has " + len + ".";
                return null;
            }
            if (t == "date")
            {
                if (!Regex.IsMatch(trimmed, @"^\d{4}-\d{1,2}-\d{1,2}$") || !ValidDate(trimmed)) return c.Name + " takes a date written year-month-day, like 2025-03-31.";
                return null;
            }
            if (t == "datetime" || t == "timestamp")
            {
                var m = Regex.Match(trimmed, @"^(\d{4}-\d{1,2}-\d{1,2})(?:[ T](\d{1,2}):(\d{2})(?::(\d{2})(?:\.\d{1,6})?)?)?$");
                if (!m.Success || !ValidDate(m.Groups[1].Value) || m.Groups[2].Success && (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) > 23 || int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture) > 59 || m.Groups[4].Success && int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture) > 59))
                    return c.Name + " takes a date and time like 2025-03-31 14:30:00.";
                if (t == "timestamp")
                {
                    var year = int.Parse(m.Groups[1].Value.Substring(0, 4), CultureInfo.InvariantCulture);
                    if (year < 1970 || year > 2106) return c.Name + " is a timestamp, which only holds 1970 to 2106. Use datetime for other years.";
                }
                return null;
            }
            if (t == "time")
            {
                if (!Regex.IsMatch(trimmed, @"^-?\d{1,3}:\d{2}(:\d{2}(\.\d{1,6})?)?$")) return c.Name + " takes a time like 14:30:00.";
                return null;
            }
            if (t == "year")
            {
                if (!Regex.IsMatch(trimmed, @"^\d{4}$") || int.Parse(trimmed, CultureInfo.InvariantCulture) < 1901 || int.Parse(trimmed, CultureInfo.InvariantCulture) > 2155) return c.Name + " takes a year from 1901 to 2155.";
                return null;
            }
            if (t == "enum")
            {
                var values = ColumnTypes.EnumValues(c.Length);
                if (!values.Contains(s)) return c.Name + " must be one of: " + string.Join(", ", values) + ".";
                return null;
            }
            if (t == "set")
            {
                var values = ColumnTypes.EnumValues(c.Length);
                var bad = s.Split(',').Select(x => x).Where(x => x.Length > 0 && !values.Contains(x)).ToList();
                if (bad.Count > 0) return c.Name + ": " + string.Join(", ", bad) + " isn't allowed. Pick from: " + string.Join(", ", values) + ".";
                return null;
            }
            if (t == "json" && trimmed.Length > 0 && !LooksLikeJson(trimmed)) return c.Name + " takes JSON, like {\"key\": \"value\"} or [1, 2].";
            return null;
        }

        private static string Short(string s) => s.Length > 24 ? s.Substring(0, 24) + "…" : s;

        public static int CodePoints(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++) { n++; if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++; }
            return n;
        }

        private static bool ValidDate(string ymd)
        {
            var p = ymd.Split('-');
            int y = int.Parse(p[0], CultureInfo.InvariantCulture), m = int.Parse(p[1], CultureInfo.InvariantCulture), d = int.Parse(p[2], CultureInfo.InvariantCulture);
            if (m < 1 || m > 12 || y < 1 || y > 9999) return false;
            return d >= 1 && d <= DateTime.DaysInMonth(y, m);
        }

        private static bool LooksLikeJson(string s)
        {
            // a light check: balanced brackets and quotes; the server checks the rest
            int depth = 0;
            bool inStr = false;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (inStr) { if (ch == '\\') i++; else if (ch == '"') inStr = false; continue; }
                if (ch == '"') inStr = true;
                else if (ch == '{' || ch == '[') depth++;
                else if (ch == '}' || ch == ']') { depth--; if (depth < 0) return false; }
            }
            if (inStr || depth != 0) return false;
            return s[0] == '{' || s[0] == '[' || s[0] == '"' || Regex.IsMatch(s, @"^(-?\d+(\.\d+)?([eE][+-]?\d+)?|true|false|null)$");
        }
    }

    /// <summary>A choice in a foreign-key lookup: the key value and a readable label from the other table.</summary>
    public sealed class LookupItem
    {
        public string Value { get; set; }
        public string Label { get; set; }
        public override string ToString() => string.IsNullOrEmpty(Label) || Label == Value ? Value : Value + " · " + Label;
    }

    /// <summary>Reads exact row values (no display truncation) and foreign-key choices.</summary>
    public static class RowStore
    {
        /// <summary>The full values of one row, by key. Binary values come back as 0x… hex.</summary>
        public static async Task<Dictionary<string, string>> LoadRowAsync(MySqlConnection c, TableDesign t, IDictionary<string, string> key, CancellationToken ct = default)
        {
            var sql = "SELECT * FROM " + DdlBuilder.TableName(t) + " WHERE " + RowSql.KeyWhere(t, key) + " LIMIT 1";
            using (var cmd = new MySqlCommand(sql, c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
                var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < r.FieldCount; i++)
                {
                    var name = r.GetName(i);
                    var col = t.Column(name);
                    object v;
                    try { v = r.GetValue(i); }
                    catch (InvalidCastException) { v = r.IsDBNull(i) ? null : r.GetString(i); }
                    row[name] = Text(v, col);
                }
                return row;
            }
        }

        /// <summary>A database value as the text the form shows and sends back unchanged.</summary>
        public static string Text(object v, ColumnDef col)
        {
            if (v == null || v is DBNull) return null;
            var t = (col?.BaseType ?? "").ToLowerInvariant();
            if (v is byte[] b)
            {
                if (ColumnTypes.IsStringy(t)) return Encoding.UTF8.GetString(b);
                var sb = new StringBuilder("0x", 2 + b.Length * 2);
                foreach (var x in b) sb.Append(x.ToString("X2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
            if (v is DateTime d)
            {
                if (t == "date") return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                return d.ToString(d.Ticks % TimeSpan.TicksPerSecond != 0 ? "yyyy-MM-dd HH:mm:ss.ffffff" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            if (v is ulong ul && t == "bit") return ul.ToString(CultureInfo.InvariantCulture);
            return NotebookSession.Display(v, t == "date" ? "DATE" : t.ToUpperInvariant(), int.MaxValue);
        }

        /// <summary>The row's key, taken from a full row.</summary>
        public static Dictionary<string, string> KeyOf(TableDesign t, IDictionary<string, string> row)
        {
            var key = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in t.RowKey ?? Array.Empty<string>()) key[k] = row.TryGetValue(k, out var v) ? v : null;
            return key;
        }

        /// <summary>The column of the other table that reads best as a label: name, title, email… else the first text column.</summary>
        public static string LabelColumn(TableSchema other, string keyColumn)
        {
            if (other == null) return null;
            string[] good = { "name", "full_name", "fullname", "title", "label", "username", "email", "description", "code" };
            foreach (var g in good)
            {
                var c = other.Columns.FirstOrDefault(x => string.Equals(x.Name, g, StringComparison.OrdinalIgnoreCase));
                if (c != null) return c.Name;
            }
            var text = other.Columns.FirstOrDefault(x => !string.Equals(x.Name, keyColumn, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(x.Type ?? "", @"^(var)?char|text", RegexOptions.IgnoreCase));
            return text?.Name;
        }

        /// <summary>Up to <paramref name="limit"/> rows of the referenced table, matching the search in the key or label.</summary>
        public static async Task<List<LookupItem>> LookupAsync(MySqlConnection c, string db, string table, string keyColumn, string labelColumn, string search, int limit = 50, CancellationToken ct = default)
        {
            var q = DdlBuilder.Q(db) + "." + DdlBuilder.Q(table);
            var k = DdlBuilder.Q(keyColumn);
            var l = labelColumn != null ? DdlBuilder.Q(labelColumn) : null;
            var sb = new StringBuilder("SELECT ").Append(k).Append(l != null ? ", " + l : ", NULL").Append(" FROM ").Append(q);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var p = SqlLexer.StringLiteral("%" + RowQuery.LikeEscape(search.Trim()) + "%");
                sb.Append(" WHERE ").Append(k).Append(" LIKE ").Append(p);
                if (l != null) sb.Append(" OR ").Append(l).Append(" LIKE ").Append(p);
            }
            sb.Append(" ORDER BY ").Append(l ?? k).Append(" LIMIT ").Append(limit.ToString(CultureInfo.InvariantCulture));
            var list = new List<LookupItem>();
            using (var cmd = new MySqlCommand(sb.ToString(), c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    if (r.IsDBNull(0)) continue;
                    list.Add(new LookupItem { Value = Text(r.GetValue(0), null), Label = r.IsDBNull(1) ? null : Text(r.GetValue(1), null) });
                }
            return list;
        }
    }
}
