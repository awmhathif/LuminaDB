using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LuminaDB.Core.Tables;
using MySqlConnector;

namespace LuminaDB.Core.Diagram
{
    /// <summary>Where one table's card sits on the diagram.</summary>
    [DataContract]
    public sealed class DiagramPosition
    {
        [DataMember] public string Table { get; set; }
        [DataMember] public double X { get; set; }
        [DataMember] public double Y { get; set; }
    }

    /// <summary>
    /// What LuminaDB remembers about one database's diagram (SPEC §5.5): the layout,
    /// tables drawn or edited on the diagram but not yet applied, tables marked to drop,
    /// and tables hidden from the picture. Stored as config\diagrams\&lt;db&gt;.json.
    /// </summary>
    [DataContract]
    public sealed class DiagramDocument
    {
        [DataMember] public int Version { get; set; } = 1;
        [DataMember] public string Database { get; set; }
        [DataMember] public List<DiagramPosition> Positions { get; set; } = new List<DiagramPosition>();
        /// <summary>New tables (OriginalName null) and edited copies of existing ones.</summary>
        [DataMember] public List<TableDesign> Drafts { get; set; } = new List<TableDesign>();
        /// <summary>Existing tables the user asked to drop when the diagram is applied.</summary>
        [DataMember] public List<string> Drops { get; set; } = new List<string>();
        /// <summary>Tables left out of the picture (they stay in the database).</summary>
        [DataMember] public List<string> Hidden { get; set; } = new List<string>();

        public bool HasPendingChanges => Drafts.Count > 0 || Drops.Count > 0;

        public static string PathFor(AppPaths paths, string database) =>
            Path.Combine(paths.Config, "diagrams", SafeFileName(database) + ".json");

        public static DiagramDocument Load(AppPaths paths, string database)
        {
            var d = JsonFile.Read<DiagramDocument>(PathFor(paths, database)) ?? new DiagramDocument();
            d.Database = database;
            d.Normalize();
            return d;
        }

        public void Save(AppPaths paths) => JsonFile.Write(PathFor(paths, Database), this);

        /// <summary>The serializer skips constructors, so a file written by hand or by an older build can leave lists null.</summary>
        public void Normalize()
        {
            if (Positions == null) Positions = new List<DiagramPosition>();
            if (Drafts == null) Drafts = new List<TableDesign>();
            if (Drops == null) Drops = new List<string>();
            if (Hidden == null) Hidden = new List<string>();
            Positions.RemoveAll(p => p == null || string.IsNullOrEmpty(p.Table));
            Drafts.RemoveAll(t => t == null || string.IsNullOrEmpty(t.Name));
            foreach (var t in Drafts)
            {
                t.Database = Database;
                if (t.Columns == null) t.Columns = new List<ColumnDef>();
                if (t.Indexes == null) t.Indexes = new List<IndexDef>();
                if (t.ForeignKeys == null) t.ForeignKeys = new List<ForeignKeyDef>();
                if (t.Checks == null) t.Checks = new List<CheckDef>();
                if (t.Triggers == null) t.Triggers = new List<TriggerInfo>();
                foreach (var c in t.Columns) { if (c.Length == null) c.Length = ""; if (c.Default == null) c.Default = ""; if (c.Comment == null) c.Comment = ""; }
                foreach (var i in t.Indexes) if (i.Columns == null) i.Columns = new List<string>();
                foreach (var f in t.ForeignKeys) { if (f.Columns == null) f.Columns = new List<string>(); if (f.RefColumns == null) f.RefColumns = new List<string>(); }
                if (t.Comment == null) t.Comment = "";
            }
        }

        public DiagramPosition PositionOf(string table) =>
            Positions.FirstOrDefault(p => string.Equals(p.Table, table, StringComparison.OrdinalIgnoreCase));

        public void SetPosition(string table, double x, double y)
        {
            var p = PositionOf(table);
            if (p == null) Positions.Add(p = new DiagramPosition { Table = table });
            p.X = Math.Round(x, 1);
            p.Y = Math.Round(y, 1);
        }

        public TableDesign DraftOf(string table) =>
            Drafts.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));

        /// <summary>A draft that edits an existing table, found by the table's name in the database.</summary>
        public TableDesign DraftForExisting(string originalName) =>
            Drafts.FirstOrDefault(t => t.OriginalName != null && string.Equals(t.OriginalName, originalName, StringComparison.OrdinalIgnoreCase));

        public bool IsDropped(string table) => Drops.Contains(table, StringComparer.OrdinalIgnoreCase);
        public bool IsHidden(string table) => Hidden.Contains(table, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The tables as the diagram shows them: the database's tables with drafts laid over them,
        /// plus new drafts. Hidden tables are left out.
        /// </summary>
        public List<DiagramTable> Compose(IReadOnlyList<TableDesign> live)
        {
            var list = new List<DiagramTable>();
            // a table renamed on the diagram: the server will move the foreign keys that point at it, so show them moved already
            var renamed = Drafts.Where(d => d.OriginalName != null && !string.Equals(d.OriginalName, d.Name, StringComparison.Ordinal))
                                .GroupBy(d => d.OriginalName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
            foreach (var t in live)
            {
                var draft = DraftForExisting(t.Name);
                var shown = draft ?? t;
                if (draft == null && renamed.Count > 0 && t.ForeignKeys.Any(f => renamed.ContainsKey(f.RefTable ?? "")))
                {
                    shown = t.Clone();
                    foreach (var f in shown.ForeignKeys) if (renamed.TryGetValue(f.RefTable ?? "", out var to)) f.RefTable = to;
                }
                if (IsHidden(shown.Name) || IsHidden(t.Name)) continue;
                list.Add(new DiagramTable(shown) { State = draft != null ? DiagramTableState.Changed : IsDropped(t.Name) ? DiagramTableState.Dropped : DiagramTableState.Live });
            }
            foreach (var d in Drafts)
            {
                if (d.OriginalName != null && live.Any(t => string.Equals(t.Name, d.OriginalName, StringComparison.OrdinalIgnoreCase))) continue;
                if (IsHidden(d.Name)) continue;
                list.Add(new DiagramTable(d) { State = DiagramTableState.New });
            }
            foreach (var t in list)
            {
                var p = PositionOf(t.Name) ?? (t.Design.OriginalName != null ? PositionOf(t.Design.OriginalName) : null);
                if (p != null) { t.X = p.X; t.Y = p.Y; t.Placed = true; }
            }
            return list;
        }

        /// <summary>Forgets everything not applied yet, keeping the layout.</summary>
        public void DiscardChanges()
        {
            Drafts.Clear();
            Drops.Clear();
        }

        private static string SafeFileName(string s)
        {
            var sb = new StringBuilder();
            foreach (var ch in s ?? "")
                sb.Append(Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch);
            return sb.Length == 0 ? "_" : sb.ToString();
        }
    }

    public enum DiagramTableState { Live, New, Changed, Dropped }

    /// <summary>One table card: its design, where it sits and how big it is.</summary>
    public sealed class DiagramTable
    {
        public DiagramTable(TableDesign design)
        {
            Design = design ?? throw new ArgumentNullException(nameof(design));
            var size = DiagramGeometry.MeasureTable(design);
            Width = size.Width;
            Height = size.Height;
        }

        public TableDesign Design { get; }
        public string Name => Design.Name;
        public DiagramTableState State { get; set; }
        public double X { get; set; }
        public double Y { get; set; }
        public double Width { get; private set; }
        public double Height { get; private set; }
        /// <summary>True when the position came from the saved layout (or a drag), not from auto-layout.</summary>
        public bool Placed { get; set; }

        public double Right => X + Width;
        public double Bottom => Y + Height;
        public double CenterX => X + Width / 2;
        public double CenterY => Y + Height / 2;

        public void Remeasure()
        {
            var size = DiagramGeometry.MeasureTable(Design);
            Width = size.Width;
            Height = size.Height;
        }

        public override string ToString() => Name + " @ " + X + "," + Y;
    }

    /// <summary>Reads every base table of a database as a design (columns, keys, foreign keys).</summary>
    public static class DiagramReader
    {
        public static async Task<List<TableDesign>> ReadAsync(MySqlConnection c, string db, CancellationToken ct = default)
        {
            var names = new List<string>();
            using (var cmd = new MySqlCommand("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA=@d AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME", c))
            {
                cmd.Parameters.AddWithValue("@d", db);
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    while (await r.ReadAsync(ct).ConfigureAwait(false)) names.Add(r.GetString(0));
            }
            var list = new List<TableDesign>();
            foreach (var n in names)
            {
                ct.ThrowIfCancellationRequested();
                try { list.Add(await TableDesignReader.ReadAsync(c, db, n, ct).ConfigureAwait(false)); }
                catch (LuminaException) { /* dropped between the two queries */ }
            }
            return list;
        }
    }
}
