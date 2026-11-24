using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LuminaDB.Core.Tables;

namespace LuminaDB.Core.Diagram
{
    /// <summary>The script that makes the database match the diagram, plus the plain-words notes shown above it.</summary>
    public sealed class SyncPlan
    {
        public List<string> Statements { get; } = new List<string>();
        public List<string> Notes { get; } = new List<string>();
        /// <summary>Problems that stop the script from being run (bad names, missing keys…).</summary>
        public List<string> Blockers { get; } = new List<string>();
        public int Creates { get; set; }
        public int Alters { get; set; }
        public int Drops { get; set; }
        public bool IsEmpty => Statements.Count == 0;
        public bool CanRun => !IsEmpty && Blockers.Count == 0;

        public string Summary
        {
            get
            {
                if (IsEmpty) return "The database already matches the diagram.";
                var parts = new List<string>();
                if (Creates > 0) parts.Add(Creates + (Creates == 1 ? " new table" : " new tables"));
                if (Alters > 0) parts.Add(Alters + (Alters == 1 ? " changed table" : " changed tables"));
                if (Drops > 0) parts.Add(Drops + (Drops == 1 ? " table to drop" : " tables to drop"));
                return string.Join(", ", parts) + " — " + Statements.Count + (Statements.Count == 1 ? " statement." : " statements.");
            }
        }

        /// <summary>The whole script, notes as comments at the top.</summary>
        public string ToScript(string database)
        {
            var sb = new StringBuilder();
            sb.Append("-- LuminaDB diagram → ").Append(database).Append('\n');
            sb.Append("-- ").Append(Summary).Append('\n');
            foreach (var n in Blockers) sb.Append("-- PROBLEM: ").Append(n).Append('\n');
            foreach (var n in Notes) sb.Append("-- ").Append(n).Append('\n');
            sb.Append('\n');
            foreach (var s in Statements) sb.Append(s.TrimEnd().TrimEnd(';')).Append(";\n\n");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Forward engineering and synchronisation (SPEC §5.5): compares the diagram's tables with the live ones
    /// and writes CREATE / ALTER / DROP statements in an order the server accepts.
    /// </summary>
    public static class DiagramSync
    {
        /// <param name="live">The tables as they are in the database now.</param>
        /// <param name="drafts">New tables (OriginalName null) and edited copies of live ones (OriginalName = live name).</param>
        /// <param name="drops">Live tables to drop.</param>
        public static SyncPlan Plan(string database, IReadOnlyList<TableDesign> live, IReadOnlyList<TableDesign> drafts, IEnumerable<string> drops = null)
        {
            var plan = new SyncPlan();
            var liveByName = new Dictionary<string, TableDesign>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in live) liveByName[t.Name] = t;
            var dropSet = new HashSet<string>((drops ?? Enumerable.Empty<string>()).Where(liveByName.ContainsKey), StringComparer.OrdinalIgnoreCase);

            var creates = new List<TableDesign>();
            var alters = new List<(TableDesign before, TableDesign after)>();
            foreach (var d0 in drafts)
            {
                var d = d0.Clone();
                d.Database = database;
                if (d.OriginalName != null && !liveByName.ContainsKey(d.OriginalName))
                {
                    plan.Notes.Add(d.OriginalName + " is no longer in the database, so " + d.Name + " will be created from the diagram.");
                    d.OriginalName = null;
                    foreach (var c in d.Columns) c.OriginalName = null;
                }
                if (d.OriginalName == null)
                {
                    if (liveByName.ContainsKey(d.Name) && !dropSet.Contains(d.Name))
                    {
                        plan.Blockers.Add("A table called " + d.Name + " already exists. Rename the new one on the diagram.");
                        continue;
                    }
                    creates.Add(d);
                }
                else
                {
                    if (dropSet.Contains(d.OriginalName)) continue;   // dropping wins over editing
                    alters.Add((liveByName[d.OriginalName], d));
                }
            }

            // names must stay unique after renames and creates
            var finalNames = live.Where(t => !dropSet.Contains(t.Name)).Select(t => t.Name).ToList();
            foreach (var (before, after) in alters)
                if (!string.Equals(before.Name, after.Name, StringComparison.OrdinalIgnoreCase)) { finalNames.Remove(before.Name); finalNames.Add(after.Name); }
            finalNames.AddRange(creates.Select(c => c.Name));
            foreach (var dup in finalNames.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                plan.Blockers.Add("Two tables would be called " + dup.Key + ".");

            // design problems that would make the server refuse the statements
            var all = new List<TableDesign>();
            all.AddRange(creates);
            all.AddRange(alters.Select(a => a.after));
            foreach (var d in all)
            {
                foreach (var issue in DesignChecker.Check(d).Where(i => i.Severity == DesignSeverity.Bad))
                    plan.Blockers.Add(d.Name + ": " + issue.Message);
                foreach (var f in d.ForeignKeys)
                {
                    if (f.RefDatabase != null && !string.Equals(f.RefDatabase, database, StringComparison.OrdinalIgnoreCase)) continue;
                    var target = finalNames.FirstOrDefault(n => string.Equals(n, f.RefTable, StringComparison.OrdinalIgnoreCase));
                    if (target == null) plan.Blockers.Add(d.Name + ": the relation " + f.Name + " points at " + f.RefTable + ", which won't exist.");
                }
            }

            // ---- 1. new tables, parents first. FKs inside a cycle of new tables are added afterwards.
            var deferred = new List<string>();
            var createNames = new HashSet<string>(creates.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = creates.ToList();
            while (pending.Count > 0)
            {
                var ready = pending.Where(c => c.ForeignKeys.All(f => !RefersToNew(f, c, createNames) || done.Contains(f.RefTable) || string.Equals(f.RefTable, c.Name, StringComparison.OrdinalIgnoreCase)))
                                   .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
                if (ready.Count == 0)
                {
                    // a cycle: create the first one without the FKs that point at tables not created yet
                    var c = pending.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).First();
                    var cut = c.Clone();
                    var later = cut.ForeignKeys.Where(f => RefersToNew(f, c, createNames) && !done.Contains(f.RefTable) && !string.Equals(f.RefTable, c.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    foreach (var f in later)
                    {
                        cut.ForeignKeys.Remove(f);
                        deferred.Add("ALTER TABLE " + DdlBuilder.TableName(c) + " ADD " + DdlBuilder.ForeignKeySql(f, database));
                    }
                    plan.Statements.Add(DdlBuilder.Create(cut));
                    done.Add(c.Name);
                    pending.Remove(c);
                    plan.Creates++;
                    continue;
                }
                foreach (var c in ready)
                {
                    plan.Statements.Add(DdlBuilder.Create(c));
                    done.Add(c.Name);
                    pending.Remove(c);
                    plan.Creates++;
                }
            }
            if (deferred.Count > 0)
                plan.Notes.Add("Some new tables point at each other, so " + deferred.Count + (deferred.Count == 1 ? " relation is" : " relations are") + " added after the tables exist.");

            // ---- 2. changed tables. Renames go first: the server moves the foreign keys that point at a renamed table,
            //      so every "before" is rewritten to the new names and only real changes remain.
            var renames = alters.Where(a => !string.Equals(a.before.Name, a.after.Name, StringComparison.Ordinal)).ToList();
            foreach (var (before, after) in renames.OrderBy(a => a.before.Name, StringComparer.OrdinalIgnoreCase))
                plan.Statements.Add("RENAME TABLE " + DdlBuilder.Q(database) + "." + DdlBuilder.Q(before.Name) + " TO " + DdlBuilder.Q(database) + "." + DdlBuilder.Q(after.Name));
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var renamedTo = renames.ToDictionary(a => a.before.Name, a => a.after.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var (before0, after) in alters.OrderBy(a => a.after.Name, StringComparer.OrdinalIgnoreCase))
            {
                var before = before0.Clone();
                if (renamedTo.TryGetValue(before.Name, out var nn)) { before.Name = nn; before.OriginalName = nn; }
                foreach (var f in before.ForeignKeys)
                    if ((f.RefDatabase == null || string.Equals(f.RefDatabase, database, StringComparison.OrdinalIgnoreCase)) && renamedTo.TryGetValue(f.RefTable ?? "", out var to))
                        f.RefTable = to;
                bool renamedOnly = renamedTo.ContainsKey(before0.Name);
                var stmts = DdlBuilder.Alter(before, after);
                if (renamedOnly && stmts.Count == 0) { plan.Alters++; changed.Add(before0.Name); continue; }
                if (stmts.Count == 0) continue;
                plan.Statements.AddRange(stmts);
                plan.Alters++;
                changed.Add(before0.Name);
            }

            plan.Statements.AddRange(deferred);

            // ---- 3. drops, children before parents
            if (dropSet.Count > 0)
            {
                var order = DropOrder(live.Where(t => dropSet.Contains(t.Name)).ToList());
                foreach (var t in order)
                {
                    plan.Statements.Add("DROP TABLE " + DdlBuilder.Q(database) + "." + DdlBuilder.Q(t.Name));
                    plan.Drops++;
                }
                var keptChildren = live.Where(t => !dropSet.Contains(t.Name))
                    .Select(t => alters.FirstOrDefault(a => string.Equals(a.before.Name, t.Name, StringComparison.OrdinalIgnoreCase)).after ?? t)
                    .Where(t => t.ForeignKeys.Any(f => dropSet.Contains(f.RefTable) && (f.RefDatabase == null || string.Equals(f.RefDatabase, database, StringComparison.OrdinalIgnoreCase))))
                    .Select(t => t.Name).ToList();
                foreach (var k in keptChildren)
                    plan.Blockers.Add(k + " still points at a table you're dropping. Remove that relation first.");
                plan.Notes.Add("Dropping a table deletes its rows. A backup is taken before the script runs.");
            }

            var untouched = live.Where(t => !dropSet.Contains(t.Name) && !changed.Contains(t.Name)).Count();
            if (untouched > 0 && !plan.IsEmpty)
                plan.Notes.Add(untouched + (untouched == 1 ? " other table is" : " other tables are") + " left as they are.");
            return plan;
        }

        private static bool RefersToNew(ForeignKeyDef f, TableDesign owner, HashSet<string> createNames) =>
            (f.RefDatabase == null || string.Equals(f.RefDatabase, owner.Database, StringComparison.OrdinalIgnoreCase)) && createNames.Contains(f.RefTable ?? "");

        /// <summary>Children (tables that point at others in the set) come first.</summary>
        private static List<TableDesign> DropOrder(List<TableDesign> set)
        {
            var result = new List<TableDesign>();
            var left = set.ToList();
            while (left.Count > 0)
            {
                var leaf = left.Where(t => !left.Any(o => !ReferenceEquals(o, t) && o.ForeignKeys.Any(f => string.Equals(f.RefTable, t.Name, StringComparison.OrdinalIgnoreCase))))
                               .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault()
                           ?? left.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).First();
                result.Add(leaf);
                left.Remove(leaf);
            }
            return result;
        }

        /// <summary>
        /// The "forward engineer" script for the whole diagram as if the database were empty:
        /// every table's CREATE in dependency order (for handing in, or building the database elsewhere).
        /// </summary>
        public static string CreateScript(string database, IReadOnlyList<TableDesign> tables, bool includeCreateDatabase)
        {
            var asNew = tables.Select(t =>
            {
                var c = t.Clone();
                c.Database = database;
                c.OriginalName = null;
                foreach (var col in c.Columns) col.OriginalName = null;
                return c;
            }).ToList();
            var plan = Plan(database, new List<TableDesign>(), asNew);
            var sb = new StringBuilder();
            sb.Append("-- ").Append(database).Append(": ").Append(asNew.Count).Append(asNew.Count == 1 ? " table" : " tables").Append(", written by LuminaDB from the diagram\n\n");
            if (includeCreateDatabase)
            {
                sb.Append("CREATE DATABASE IF NOT EXISTS ").Append(DdlBuilder.Q(database)).Append(" DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;\n");
                sb.Append("USE ").Append(DdlBuilder.Q(database)).Append(";\n\n");
            }
            foreach (var s in plan.Statements) sb.Append(s.TrimEnd().TrimEnd(';')).Append(";\n\n");
            return sb.ToString();
        }

        /// <summary>
        /// Adds a relation from <paramref name="child"/> to <paramref name="parent"/>'s primary key the way a student would by hand:
        /// a new column parent_id (same type as the key, NOT NULL), an index and the FK. Returns null on success, else why not.
        /// </summary>
        public static string AddRelation(TableDesign child, TableDesign parent, string columnName = null, bool required = true)
        {
            var pk = parent.PrimaryKey;
            if (pk == null || pk.Columns.Count == 0) return parent.Name + " has no primary key to point at. Give it one first.";
            if (pk.Columns.Count > 1) return parent.Name + "'s primary key has more than one column; add this relation in the table editor.";
            var keyName = TableDesign.IndexColumnName(pk.Columns[0]);
            var key = parent.Column(keyName);
            if (key == null) return parent.Name + "'s key column is missing.";
            var name = string.IsNullOrWhiteSpace(columnName) ? Singular(parent.Name) + "_" + keyName : columnName.Trim();
            var col = child.Column(name);
            if (col == null)
            {
                col = new ColumnDef
                {
                    Name = name,
                    BaseType = key.BaseType,
                    Length = ColumnTypes.IsInteger(key.BaseType) ? "" : key.Length,
                    Unsigned = key.Unsigned,
                    Nullable = !required,
                    Collation = key.Collation,
                };
                child.Columns.Add(col);
            }
            else if (!string.Equals(col.BaseType, key.BaseType, StringComparison.OrdinalIgnoreCase) || col.Unsigned != key.Unsigned)
            {
                return child.Name + "." + name + " is " + col.FullType.ToLowerInvariant() + " but " + parent.Name + "." + keyName + " is " + key.FullType.ToLowerInvariant() + ". They must match.";
            }
            var fkName = UniqueName(child, "fk_" + child.Name + "_" + parent.Name);
            child.ForeignKeys.Add(new ForeignKeyDef
            {
                Name = fkName,
                Columns = { name },
                RefTable = parent.Name,
                RefColumns = { keyName },
                OnDelete = "RESTRICT",
                OnUpdate = "CASCADE",
            });
            if (!child.Indexes.Any(i => i.Columns.Count > 0 && string.Equals(TableDesign.IndexColumnName(i.Columns[0]), name, StringComparison.OrdinalIgnoreCase)))
                child.Indexes.Add(new IndexDef { Name = UniqueName(child, "idx_" + name), Kind = IndexKind.Index, Columns = { name } });
            return null;
        }

        /// <summary>Renames a table in a set of designs, following the foreign keys that point at it.</summary>
        public static void RenameReferences(IEnumerable<TableDesign> tables, string oldName, string newName)
        {
            foreach (var t in tables)
                foreach (var f in t.ForeignKeys)
                    if (string.Equals(f.RefTable, oldName, StringComparison.OrdinalIgnoreCase) && (f.RefDatabase == null || string.Equals(f.RefDatabase, t.Database, StringComparison.OrdinalIgnoreCase)))
                        f.RefTable = newName;
        }

        internal static string Singular(string name)
        {
            var n = name ?? "";
            if (n.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && n.Length > 4) return n.Substring(0, n.Length - 3) + "y";
            if (n.EndsWith("sses", StringComparison.OrdinalIgnoreCase)) return n.Substring(0, n.Length - 2);
            if (n.EndsWith("s", StringComparison.OrdinalIgnoreCase) && !n.EndsWith("ss", StringComparison.OrdinalIgnoreCase) && n.Length > 3) return n.Substring(0, n.Length - 1);
            return n;
        }

        private static string UniqueName(TableDesign t, string baseName)
        {
            var b = baseName.Length > 60 ? baseName.Substring(0, 60) : baseName;
            bool Taken(string n) => t.ForeignKeys.Any(f => string.Equals(f.Name, n, StringComparison.OrdinalIgnoreCase))
                                 || t.Indexes.Any(i => string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase));
            if (!Taken(b)) return b;
            for (int i = 2; ; i++) if (!Taken(b + "_" + i)) return b + "_" + i;
        }
    }
}
