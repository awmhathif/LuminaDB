using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace LuminaDB.Core.Sql
{
    public sealed class ColumnSchema
    {
        public string Name { get; set; }
        /// <summary>Full type as written by the server, e.g. varchar(100), int(10) unsigned.</summary>
        public string Type { get; set; }
        public bool Nullable { get; set; }
        /// <summary>PRI, UNI, MUL or empty.</summary>
        public string Key { get; set; }
        public string Default { get; set; }
        public string Extra { get; set; }
        public bool IsPrimaryKey => Key == "PRI";
        public bool IsAutoIncrement => (Extra ?? "").IndexOf("auto_increment", StringComparison.OrdinalIgnoreCase) >= 0;
        public bool IsGenerated => (Extra ?? "").IndexOf("GENERATED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   (Extra ?? "").IndexOf("PERSISTENT", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                   (Extra ?? "").IndexOf("VIRTUAL", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Character limit for char/varchar columns, else null.</summary>
        public int? MaxLength
        {
            get
            {
                var m = Regex.Match(Type ?? "", @"^(?:var)?char\((\d+)\)", RegexOptions.IgnoreCase);
                return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : (int?)null;
            }
        }

        /// <summary>Allowed values of an ENUM/SET column.</summary>
        public IReadOnlyList<string> EnumValues
        {
            get
            {
                var m = Regex.Match(Type ?? "", @"^(?:enum|set)\((.*)\)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (!m.Success) return Array.Empty<string>();
                var list = new List<string>();
                foreach (Match v in Regex.Matches(m.Groups[1].Value, @"'((?:[^']|'')*)'")) list.Add(v.Groups[1].Value.Replace("''", "'"));
                return list;
            }
        }

        /// <summary>Short type for the schema index: varchar(100) → text 100, int → number.</summary>
        public string FriendlyType
        {
            get
            {
                var t = (Type ?? "").ToLowerInvariant();
                if (t.StartsWith("tinyint(1)", StringComparison.Ordinal)) return "yes/no";
                if (Regex.IsMatch(t, @"^(tiny|small|medium|big)?int")) return "number";
                if (Regex.IsMatch(t, @"^(decimal|numeric|float|double|real)")) return "decimal";
                if (t.StartsWith("varchar", StringComparison.Ordinal) || t.StartsWith("char", StringComparison.Ordinal)) return "text " + MaxLength;
                if (t.Contains("text")) return "long text";
                if (t.StartsWith("datetime", StringComparison.Ordinal) || t.StartsWith("timestamp", StringComparison.Ordinal)) return "date+time";
                if (t.StartsWith("date", StringComparison.Ordinal)) return "date";
                if (t.StartsWith("time", StringComparison.Ordinal)) return "time";
                if (t.StartsWith("enum", StringComparison.Ordinal)) return "one of " + EnumValues.Count;
                if (t.Contains("blob") || t.Contains("binary")) return "binary";
                return t;
            }
        }
    }

    public sealed class TableSchema
    {
        public string Database { get; set; }
        public string Name { get; set; }
        public bool IsView { get; set; }
        /// <summary>Server estimate (InnoDB's TABLE_ROWS is approximate).</summary>
        public long RowEstimate { get; set; }
        public long Bytes { get; set; }
        public List<ColumnSchema> Columns { get; } = new List<ColumnSchema>();

        public ColumnSchema Find(string column) =>
            Columns.FirstOrDefault(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));

        public IEnumerable<ColumnSchema> PrimaryKey => Columns.Where(c => c.IsPrimaryKey);
        /// <summary>CHECK rules by name (MariaDB names a column's own rule "table.column" or just "column").</summary>
        public Dictionary<string, string> Checks { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public string CheckClause(string name)
        {
            if (name == null) return null;
            if (Checks.TryGetValue(name, out var clause)) return clause;
            var bare = name.Contains(".") ? name.Substring(name.LastIndexOf('.') + 1) : name;
            return Checks.TryGetValue(bare, out clause) ? clause : null;
        }
        public override string ToString() => Database + "." + Name;
    }

    public sealed class DbSchema
    {
        public string Name { get; set; }
        public Dictionary<string, TableSchema> Tables { get; } = new Dictionary<string, TableSchema>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>What the server holds right now: databases, tables, columns. Read in one go.</summary>
    public sealed class SchemaSnapshot
    {
        public static readonly SchemaSnapshot Empty = new SchemaSnapshot { IsLoaded = false };

        public Dictionary<string, DbSchema> Databases { get; } = new Dictionary<string, DbSchema>(StringComparer.OrdinalIgnoreCase);
        /// <summary>False when the server was not reachable: checks that need names are skipped.</summary>
        public bool IsLoaded { get; set; } = true;
        public DateTime ReadAt { get; set; } = DateTime.Now;

        public DbSchema Find(string db) => db != null && Databases.TryGetValue(db, out var d) ? d : null;

        public TableSchema FindTable(string db, string table)
        {
            var d = Find(db);
            return d != null && table != null && d.Tables.TryGetValue(table, out var t) ? t : null;
        }

        public static bool IsSystem(string db) =>
            db != null && (DatabaseCatalog.SystemSchemas.Contains(db, StringComparer.OrdinalIgnoreCase) ||
                           string.Equals(db, DatabaseCatalog.UndoSchema, StringComparison.OrdinalIgnoreCase));

        /// <summary>Builder used by tests and the reader.</summary>
        public TableSchema AddTable(string db, string table, params string[] columns)
        {
            if (!Databases.TryGetValue(db, out var d)) Databases[db] = d = new DbSchema { Name = db };
            var t = new TableSchema { Database = db, Name = table };
            foreach (var c in columns)
            {
                var parts = c.Split(new[] { ' ' }, 2);
                t.Columns.Add(new ColumnSchema { Name = parts[0], Type = parts.Length > 1 ? parts[1] : "int", Nullable = true, Key = "" });
            }
            d.Tables[table] = t;
            return t;
        }

        public void AddDatabase(string db)
        {
            if (!Databases.ContainsKey(db)) Databases[db] = new DbSchema { Name = db };
        }
    }

    public static class SchemaReader
    {
        public static async Task<SchemaSnapshot> ReadAsync(MariaDbService server, CancellationToken ct = default)
        {
            using (var c = await server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
                return await ReadAsync(c, ct).ConfigureAwait(false);
        }

        public static async Task<SchemaSnapshot> ReadAsync(MySqlConnection c, CancellationToken ct = default)
        {
            var snap = new SchemaSnapshot();
            string hidden = string.Join(",", DatabaseCatalog.HiddenSchemas.Select(s => "'" + s + "'"));

            using (var cmd = new MySqlCommand("SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME NOT IN (" + hidden + ")", c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await r.ReadAsync(ct).ConfigureAwait(false)) snap.AddDatabase(r.GetString(0));

            using (var cmd = new MySqlCommand(@"SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE, COALESCE(TABLE_ROWS,0), COALESCE(DATA_LENGTH,0) + COALESCE(INDEX_LENGTH,0)
FROM information_schema.TABLES WHERE TABLE_SCHEMA NOT IN (" + hidden + ")", c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var db = snap.Find(r.GetString(0));
                    if (db == null) continue;
                    var t = new TableSchema
                    {
                        Database = db.Name,
                        Name = r.GetString(1),
                        IsView = string.Equals(r.GetString(2), "VIEW", StringComparison.OrdinalIgnoreCase),
                        RowEstimate = Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture),
                        Bytes = Convert.ToInt64(r.GetValue(4), CultureInfo.InvariantCulture),
                    };
                    db.Tables[t.Name] = t;
                }
            }

            using (var cmd = new MySqlCommand(@"SELECT TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME, COLUMN_TYPE, IS_NULLABLE, COLUMN_KEY, COLUMN_DEFAULT, EXTRA
FROM information_schema.COLUMNS WHERE TABLE_SCHEMA NOT IN (" + hidden + ") ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION", c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var t = snap.FindTable(r.GetString(0), r.GetString(1));
                    if (t == null) continue;
                    t.Columns.Add(new ColumnSchema
                    {
                        Name = r.GetString(2),
                        Type = r.GetString(3),
                        Nullable = string.Equals(r.GetString(4), "YES", StringComparison.OrdinalIgnoreCase),
                        Key = r.IsDBNull(5) ? "" : r.GetString(5),
                        Default = r.IsDBNull(6) ? null : r.GetString(6),
                        Extra = r.IsDBNull(7) ? "" : r.GetString(7),
                    });
                }
            }
            // CHECK rules, so a rejected value can say what the rule is (MariaDB 10.2+; older servers simply have none)
            try
            {
                using (var cmd = new MySqlCommand(@"SELECT CONSTRAINT_SCHEMA, TABLE_NAME, CONSTRAINT_NAME, CHECK_CLAUSE
FROM information_schema.CHECK_CONSTRAINTS WHERE CONSTRAINT_SCHEMA NOT IN (" + hidden + ")", c))
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var t = snap.FindTable(r.GetString(0), r.GetString(1));
                        if (t != null && !r.IsDBNull(3)) t.Checks[r.GetString(2)] = r.GetString(3);
                    }
            }
            catch (MySqlException) { }
            snap.ReadAt = DateTime.Now;
            return snap;
        }
    }
}
