using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LuminaDB.Core.Sql;
using MySqlConnector;

namespace LuminaDB.Core.Tables
{
    /// <summary>Whole-table operations. Each returns the SQL to show and run; running goes through the Notebook session (Undo included).</summary>
    public static class TableOps
    {
        private static string Full(string db, string t) => DdlBuilder.Q(db) + "." + DdlBuilder.Q(t);

        public static string Rename(TableDesign d, string newName) =>
            "RENAME TABLE " + Full(d.Database, d.Name) + " TO " + Full(d.Database, newName) + ";";

        public static string Move(TableDesign d, string toDb) =>
            "RENAME TABLE " + Full(d.Database, d.Name) + " TO " + Full(toDb, d.Name) + ";";

        /// <summary>A copy with the same columns, keys and indexes (CREATE … LIKE), and optionally its rows.</summary>
        public static List<string> Copy(TableDesign d, string toDb, string newName, bool withData)
        {
            var list = new List<string> { "CREATE TABLE " + Full(toDb, newName) + " LIKE " + Full(d.Database, d.Name) + ";" };
            if (withData)
            {
                var cols = string.Join(", ", d.Columns.Where(c => !c.IsGenerated).Select(c => DdlBuilder.Q(c.Name)));
                list.Add("INSERT INTO " + Full(toDb, newName) + " (" + cols + ")\nSELECT " + cols + " FROM " + Full(d.Database, d.Name) + ";");
            }
            return list;
        }

        /// <summary>Removes every row but keeps the table. DELETE (not TRUNCATE) works with foreign keys and can be undone.</summary>
        public static string Empty(TableDesign d) => "DELETE FROM " + Full(d.Database, d.Name) + ";";

        public static string Drop(TableDesign d, bool isView) => (isView ? "DROP VIEW " : "DROP TABLE ") + Full(d.Database, d.Name) + ";";

        public static string ConvertCollation(TableDesign d, string collation) =>
            "ALTER TABLE " + Full(d.Database, d.Name) + " CONVERT TO CHARACTER SET " + DdlBuilder.CharsetOf(collation) + " COLLATE " + collation + ";";

        public static readonly IReadOnlyList<string> MaintenanceVerbs = new[] { "CHECK", "ANALYZE", "OPTIMIZE", "REPAIR" };

        public static string Maintenance(TableDesign d, string verb)
        {
            if (!MaintenanceVerbs.Contains(verb)) throw new ArgumentException("Unknown maintenance command " + verb, nameof(verb));
            return verb + " TABLE " + Full(d.Database, d.Name) + ";";
        }

        /// <summary>Plain-English problem with a new table name in a database, or null.</summary>
        public static string NewNameProblem(SchemaSnapshot schema, string db, string name)
        {
            var p = DesignChecker.NameProblem(name, "The table");
            if (p != null) return p;
            if (schema?.FindTable(db, name) != null) return "There's already a table called " + name + " in " + db + ".";
            return null;
        }

        /// <summary>Tables whose foreign keys point at this one: "marks.fk_marks_student".</summary>
        public static async Task<List<string>> ReferencedByAsync(MySqlConnection c, string db, string table, CancellationToken ct = default)
        {
            var list = new List<string>();
            using (var cmd = new MySqlCommand(@"SELECT DISTINCT TABLE_SCHEMA, TABLE_NAME, CONSTRAINT_NAME FROM information_schema.KEY_COLUMN_USAGE
WHERE REFERENCED_TABLE_SCHEMA=@d AND REFERENCED_TABLE_NAME=@t AND NOT (TABLE_SCHEMA=@d AND TABLE_NAME=@t) ORDER BY TABLE_NAME", c))
            {
                cmd.Parameters.AddWithValue("@d", db);
                cmd.Parameters.AddWithValue("@t", table);
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                        list.Add((string.Equals(r.GetString(0), db, StringComparison.OrdinalIgnoreCase) ? "" : r.GetString(0) + ".") + r.GetString(1) + " (" + r.GetString(2) + ")");
            }
            return list;
        }

        /// <summary>Collations worth offering, utf8mb4 first.</summary>
        public static async Task<List<string>> CollationsAsync(MySqlConnection c, CancellationToken ct = default)
        {
            var list = new List<string>();
            using (var cmd = new MySqlCommand("SELECT COLLATION_NAME FROM information_schema.COLLATIONS WHERE CHARACTER_SET_NAME IN ('utf8mb4','utf8mb3','utf8','latin1','ascii') ORDER BY CHARACTER_SET_NAME='utf8mb4' DESC, COLLATION_NAME", c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(r.GetString(0));
            // the recommended ones at the top
            foreach (var best in new[] { "utf8mb4_general_ci", "utf8mb4_bin", "utf8mb4_unicode_ci" })
                if (list.Remove(best)) list.Insert(0, best);
            return list;
        }

        public static async Task<List<string>> EnginesAsync(MySqlConnection c, CancellationToken ct = default)
        {
            var list = new List<string>();
            using (var cmd = new MySqlCommand("SELECT ENGINE FROM information_schema.ENGINES WHERE SUPPORT IN ('YES','DEFAULT') AND ENGINE IN ('InnoDB','Aria','MyISAM','MEMORY','CSV','ARCHIVE') ORDER BY ENGINE='InnoDB' DESC, ENGINE", c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(r.GetString(0));
            return list;
        }
    }

    public enum DbObjectKind { Table, View, Procedure, Function, Trigger, Event }

    public sealed class DbObjectInfo
    {
        public DbObjectKind Kind { get; set; }
        public string Database { get; set; }
        public string Name { get; set; }
        /// <summary>Triggers: the table they belong to.</summary>
        public string Table { get; set; }
        /// <summary>Short description for the index: "BEFORE INSERT on marks", "every 1 DAY".</summary>
        public string Detail { get; set; }
        public string KindWord => Kind.ToString().ToUpperInvariant();
        public override string ToString() => KindWord + " " + Database + "." + Name;
    }

    /// <summary>Views, procedures, functions, triggers and events: list, read as an editable script, templates.</summary>
    public static class DbObjects
    {
        public static async Task<List<DbObjectInfo>> ListAsync(MySqlConnection c, string db, CancellationToken ct = default)
        {
            var list = new List<DbObjectInfo>();
            async Task Read(string sql, Func<MySqlDataReader, DbObjectInfo> make)
            {
                using (var cmd = new MySqlCommand(sql, c))
                {
                    cmd.Parameters.AddWithValue("@d", db);
                    using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                        while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(make(r));
                }
            }
            await Read("SELECT ROUTINE_NAME, ROUTINE_TYPE, COALESCE(DTD_IDENTIFIER,'') FROM information_schema.ROUTINES WHERE ROUTINE_SCHEMA=@d ORDER BY ROUTINE_TYPE, ROUTINE_NAME",
                r => new DbObjectInfo
                {
                    Kind = string.Equals(r.GetString(1), "FUNCTION", StringComparison.OrdinalIgnoreCase) ? DbObjectKind.Function : DbObjectKind.Procedure,
                    Database = db, Name = r.GetString(0), Detail = r.GetString(2).Length > 0 ? "returns " + r.GetString(2).ToLowerInvariant() : null,
                }).ConfigureAwait(false);
            await Read("SELECT TRIGGER_NAME, ACTION_TIMING, EVENT_MANIPULATION, EVENT_OBJECT_TABLE FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=@d ORDER BY EVENT_OBJECT_TABLE, TRIGGER_NAME",
                r => new DbObjectInfo { Kind = DbObjectKind.Trigger, Database = db, Name = r.GetString(0), Table = r.GetString(3), Detail = r.GetString(1).ToLowerInvariant() + " " + r.GetString(2).ToLowerInvariant() + " on " + r.GetString(3) }).ConfigureAwait(false);
            await Read("SELECT EVENT_NAME, COALESCE(CONCAT('every ', INTERVAL_VALUE, ' ', INTERVAL_FIELD), CONCAT('once at ', EXECUTE_AT), ''), STATUS FROM information_schema.EVENTS WHERE EVENT_SCHEMA=@d ORDER BY EVENT_NAME",
                r => new DbObjectInfo { Kind = DbObjectKind.Event, Database = db, Name = r.GetString(0), Detail = (r.GetString(1) + (string.Equals(r.GetString(2), "ENABLED", StringComparison.OrdinalIgnoreCase) ? "" : " · off")).ToLowerInvariant() }).ConfigureAwait(false);
            return list;
        }

        private static readonly Regex Definer = new Regex(@"\s+DEFINER\s*=\s*(`[^`]*`|'[^']*'|\S+?)@(`[^`]*`|'[^']*'|\S+)", RegexOptions.IgnoreCase);

        /// <summary>The object as a script you can edit and run again (CREATE OR REPLACE, DEFINER removed so it works on any PC).</summary>
        public static async Task<string> DefinitionAsync(MySqlConnection c, DbObjectInfo o, CancellationToken ct = default)
        {
            var name = DdlBuilder.Q(o.Database) + "." + DdlBuilder.Q(o.Name);
            string sql = "SHOW CREATE " + (o.Kind == DbObjectKind.View ? "VIEW" : o.KindWord) + " " + name;
            string text = null;
            using (var cmd = new MySqlCommand(sql, c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
                for (int i = 0; i < r.FieldCount; i++)
                {
                    var col = r.GetName(i);
                    if ((col.StartsWith("Create ", StringComparison.OrdinalIgnoreCase) && !col.Equals("Create Table", StringComparison.OrdinalIgnoreCase) || col.Equals("SQL Original Statement", StringComparison.OrdinalIgnoreCase)) && !r.IsDBNull(i))
                    {
                        text = r.GetString(i);
                        break;
                    }
                }
            }
            if (text == null)
                throw new LuminaException(ErrorKind.InvalidInput, "MariaDB didn't show the definition of " + o.Name + ". The account may lack the privilege to see it.");
            return ToScript(o.Kind, text);
        }

        /// <summary>CREATE DEFINER=… VIEW → CREATE OR REPLACE VIEW, formatted; routines wrapped in DELIMITER.</summary>
        public static string ToScript(DbObjectKind kind, string createText)
        {
            var t = Definer.Replace(createText.Trim(), "");
            if (kind == DbObjectKind.View)
            {
                t = Regex.Replace(t, @"\s+ALGORITHM\s*=\s*\w+", "", RegexOptions.IgnoreCase);
                t = Regex.Replace(t, @"\s+SQL\s+SECURITY\s+(DEFINER|INVOKER)", "", RegexOptions.IgnoreCase);
                var m = Regex.Match(t, @"^CREATE\s+(?:OR\s+REPLACE\s+)?VIEW\s+(.+?)\s+AS\s+(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                if (m.Success)
                {
                    var body = SqlFormatter.Format(m.Groups[2].Value.Trim().TrimEnd(';') + ";").Trim();
                    return "CREATE OR REPLACE VIEW " + m.Groups[1].Value + " AS\n" + body + "\n";
                }
                return t.TrimEnd(';') + ";\n";
            }
            t = Regex.Replace(t, @"^CREATE\s+(?!OR\s+REPLACE)", "CREATE OR REPLACE ", RegexOptions.IgnoreCase);
            var delim = t.Contains("$$") ? (t.Contains("//") ? ";;" : "//") : "$$";
            return "DELIMITER " + delim + "\n" + t.TrimEnd().TrimEnd(';') + delim + "\nDELIMITER ;\n";
        }

        public static string DropSql(DbObjectInfo o) =>
            "DROP " + (o.Kind == DbObjectKind.View ? "VIEW" : o.KindWord) + " IF EXISTS " + DdlBuilder.Q(o.Database) + "." + DdlBuilder.Q(o.Name) + ";";

        /// <summary>A starting script for a new object. <paramref name="table"/> is a table to use in the example (may be null).</summary>
        public static string Template(DbObjectKind kind, TableSchema table)
        {
            var t = table == null ? "your_table" : table.Name;
            var tq = DdlBuilder.Q(t);
            var key = table?.Columns.FirstOrDefault(c => c.IsPrimaryKey)?.Name ?? "id";
            var text = table?.Columns.FirstOrDefault(c => !c.IsPrimaryKey && Regex.IsMatch(c.Type ?? "", "char|text", RegexOptions.IgnoreCase))?.Name;
            switch (kind)
            {
                case DbObjectKind.View:
                    return "-- A view is a saved SELECT you can use like a table.\nCREATE OR REPLACE VIEW `" + t + "_list` AS\nSELECT *\nFROM " + tq + ";\n";
                case DbObjectKind.Procedure:
                    return "-- A procedure is a named block of SQL. Run it with CALL find_" + t + "(1);\nDELIMITER $$\nCREATE OR REPLACE PROCEDURE `find_" + t + "`(IN p_id INT)\nBEGIN\n  SELECT *\n  FROM " + tq + "\n  WHERE " + DdlBuilder.Q(key) + " = p_id;\nEND$$\nDELIMITER ;\n";
                case DbObjectKind.Function:
                    return "-- A function returns one value. Use it like SELECT add_tax(100);\nDELIMITER $$\nCREATE OR REPLACE FUNCTION `add_tax`(amount DECIMAL(10,2))\nRETURNS DECIMAL(10,2)\nDETERMINISTIC\nBEGIN\n  RETURN ROUND(amount * 1.18, 2);\nEND$$\nDELIMITER ;\n";
                case DbObjectKind.Trigger:
                    return "-- A trigger runs by itself when rows change. NEW is the row being added.\nDELIMITER $$\nCREATE OR REPLACE TRIGGER `" + t + "_before_insert`\nBEFORE INSERT ON " + tq + "\nFOR EACH ROW\nBEGIN\n" +
                           (text != null ? "  SET NEW." + DdlBuilder.Q(text) + " = TRIM(NEW." + DdlBuilder.Q(text) + ");\n" : "  -- for example: SET NEW.name = TRIM(NEW.name);\n  SET @last_insert_at = NOW();\n") + "END$$\nDELIMITER ;\n";
                case DbObjectKind.Event:
                    return "-- An event runs on a schedule (the event scheduler is on in LuminaDB).\nCREATE OR REPLACE EVENT `daily_" + t + "_tidy`\nON SCHEDULE EVERY 1 DAY\nSTARTS CURRENT_TIMESTAMP + INTERVAL 1 DAY\nDO\n  -- what to run each day, for example:\n  -- DELETE FROM " + tq + " WHERE created < NOW() - INTERVAL 30 DAY;\n  SET @tidied_at = NOW();\n";
                default:
                    throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }
}
