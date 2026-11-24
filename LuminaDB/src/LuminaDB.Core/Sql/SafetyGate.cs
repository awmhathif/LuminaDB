using System.Globalization;
using System.Linq;

namespace LuminaDB.Core.Sql
{
    /// <summary>A "are you sure?" slip shown before a destructive command runs.</summary>
    public sealed class SafetyPrompt
    {
        public string Title { get; set; }
        public string Body { get; set; }
        public string RunLabel { get; set; } = "Run anyway";
        /// <summary>Offer "Add a WHERE for me" (DELETE/UPDATE without WHERE).</summary>
        public TextFix AddWhere { get; set; }
        /// <summary>Table whose exact row count the slip should show (db, table), if any.</summary>
        public TableRef CountTable { get; set; }
        public bool UndoAvailable { get; set; } = true;
    }

    /// <summary>
    /// Decides which commands need a confirmation. With Safe updates on (default): DELETE/UPDATE
    /// with no WHERE, TRUNCATE, DROP TABLE/DATABASE, ALTER ... DROP COLUMN. With it off: only DROP DATABASE.
    /// </summary>
    public static class SafetyGate
    {
        public static SafetyPrompt Check(StatementInfo info, SchemaSnapshot schema, bool safeUpdates)
        {
            if (info == null || info.Kind == StatementKind.Empty) return null;
            schema = schema ?? SchemaSnapshot.Empty;

            if (info.Verb == "DROP" && info.Object == "DATABASE" && info.DatabaseName != null)
            {
                var db = schema.Find(info.DatabaseName);
                if (db == null && info.IfExists) return null;
                long rows = db?.Tables.Values.Sum(t => t.RowEstimate) ?? 0;
                return new SafetyPrompt
                {
                    Title = "Delete the whole database " + info.DatabaseName + "?",
                    Body = "Every table in it goes" + (db != null ? " (" + Plural(db.Tables.Count, "table") + ", about " + Plural(rows, "row") + ")" : "") +
                           ". PHP pages that use it will stop working.",
                    RunLabel = "Delete " + info.DatabaseName,
                };
            }
            if (!safeUpdates) return null;

            if ((info.Verb == "DELETE" || info.Verb == "UPDATE") && !info.HasWhere && !info.HasLimit && info.Targets.Count > 0)
            {
                var t = info.Targets[0];
                var ts = schema.FindTable(t.ResolvedDatabase, t.Name);
                string pk = ts?.PrimaryKey.FirstOrDefault()?.Name ?? "id";
                var last = info.Tokens[info.Tokens.Count - 1];
                return new SafetyPrompt
                {
                    Title = (info.Verb == "DELETE" ? "Delete every row in " : "Change every row in ") + t.Name + "?",
                    Body = "There's no WHERE, so this " + (info.Verb == "DELETE" ? "deletes" : "changes") + " all rows" + "{rows}" +
                           ". Most of the time a WHERE was forgotten.",
                    AddWhere = new TextFix(last.End, 0, " WHERE " + (SqlLexer.IsPlainName(pk) ? pk : SqlLexer.Quote(pk)) + " = 1"),
                    CountTable = t,
                };
            }
            if (info.Verb == "TRUNCATE" && info.Targets.Count > 0)
            {
                var t = info.Targets[0];
                return new SafetyPrompt
                {
                    Title = "Empty " + t.Name + "?",
                    Body = "TRUNCATE removes all rows{rows} and restarts the numbering.",
                    RunLabel = "Empty it",
                    CountTable = t,
                };
            }
            if (info.Verb == "DROP" && (info.Object == "TABLE" || info.Object == "VIEW") && info.Targets.Count > 0)
            {
                var existing = info.Targets.Where(t => schema.FindTable(t.ResolvedDatabase, t.Name) != null).ToList();
                if (existing.Count == 0) return null;
                if (info.Object == "VIEW")
                    return new SafetyPrompt { Title = "Delete the view " + existing[0].Name + "?", Body = "Only the saved query goes; the data in its tables stays.", RunLabel = "Delete view" };
                return new SafetyPrompt
                {
                    Title = existing.Count == 1 ? "Delete the table " + existing[0].Name + "?" : "Delete " + existing.Count + " tables?",
                    Body = existing.Count == 1 ? "The table and all its rows{rows} go." : string.Join(", ", existing.Select(x => x.Name)) + " and all their rows go.",
                    RunLabel = existing.Count == 1 ? "Delete " + existing[0].Name : "Delete them",
                    CountTable = existing.Count == 1 ? existing[0] : null,
                };
            }
            if (info.Verb == "ALTER" && info.IsDropColumn && info.Targets.Count > 0)
            {
                return new SafetyPrompt
                {
                    Title = "Delete a column from " + info.Targets[0].Name + "?",
                    Body = "DROP COLUMN removes that column's value in every row{rows}.",
                    RunLabel = "Drop column",
                    CountTable = info.Targets[0],
                };
            }
            return null;
        }

        /// <summary>
        /// Databases to back up before these commands run (SPEC §5.10: before every DROP, TRUNCATE and delete-all):
        /// DROP DATABASE/TABLE, TRUNCATE, DELETE or UPDATE with no WHERE, ALTER ... DROP COLUMN, whatever Safe updates is
        /// set to. Only databases that exist are listed, each once.
        /// </summary>
        public static System.Collections.Generic.List<string> DatabasesToBackUp(System.Collections.Generic.IEnumerable<StatementInfo> infos, SchemaSnapshot schema)
        {
            schema = schema ?? SchemaSnapshot.Empty;
            var result = new System.Collections.Generic.List<string>();
            void Add(string db)
            {
                if (string.IsNullOrEmpty(db)) return;
                var found = schema.Find(db);
                if (found == null || result.Any(r => string.Equals(r, db, System.StringComparison.OrdinalIgnoreCase))) return;
                result.Add(db);
            }
            foreach (var info in infos ?? Enumerable.Empty<StatementInfo>())
            {
                if (info == null || info.Kind == StatementKind.Empty) continue;
                bool risky =
                    info.Verb == "DROP" && (info.Object == "DATABASE" || info.Object == "TABLE") ||
                    info.Verb == "TRUNCATE" ||
                    (info.Verb == "DELETE" || info.Verb == "UPDATE") && !info.HasWhere && !info.HasLimit ||
                    info.Verb == "ALTER" && info.IsDropColumn;
                if (!risky) continue;
                if (info.Verb == "DROP" && info.Object == "DATABASE") { Add(info.DatabaseName); continue; }
                foreach (var t in info.Targets.Where(t => !t.IsDerived && !t.IsReference)) Add(t.ResolvedDatabase);
            }
            return result;
        }

        /// <summary>Fills the "{rows}" slot once the exact count is known (or estimate/blank).</summary>
        public static string WithRows(string body, long? rows) =>
            body.Replace("{rows}", rows.HasValue ? " (" + Plural(rows.Value, "row") + ")" : "");

        public static string Plural(long n, string word) =>
            n.ToString("N0", CultureInfo.InvariantCulture) + " " + word + (n == 1 ? "" : "s");
    }
}
