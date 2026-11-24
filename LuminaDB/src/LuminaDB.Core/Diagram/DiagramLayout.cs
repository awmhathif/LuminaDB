using System;
using System.Collections.Generic;
using System.Linq;

namespace LuminaDB.Core.Diagram
{
    /// <summary>
    /// Automatic layout: tables that are referenced (parents) on the left, the tables that point at them to the right,
    /// rank by rank; each column ordered to sit near its parents; tables with no relations parked at the end.
    /// </summary>
    public static class DiagramLayout
    {
        public const double ColumnGap = 110;
        public const double RowGap = 34;
        public const double Origin = 40;
        /// <summary>A rank taller than this wraps into a second column.</summary>
        public const double MaxColumnHeight = 1500;

        /// <summary>Places every table (ignores any saved positions).</summary>
        public static void Arrange(IReadOnlyList<DiagramTable> tables)
        {
            if (tables.Count == 0) return;
            var byName = new Dictionary<string, DiagramTable>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables) if (!byName.ContainsKey(t.Name)) byName[t.Name] = t;

            // parent edges (child -> parents), self references ignored
            var parents = tables.ToDictionary(t => t, t => new List<DiagramTable>());
            var children = tables.ToDictionary(t => t, t => new List<DiagramTable>());
            foreach (var t in tables)
                foreach (var f in t.Design.ForeignKeys)
                {
                    if (f.RefDatabase != null && !string.Equals(f.RefDatabase, t.Design.Database, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!byName.TryGetValue(f.RefTable ?? "", out var p) || ReferenceEquals(p, t)) continue;
                    if (!parents[t].Contains(p)) parents[t].Add(p);
                    if (!children[p].Contains(t)) children[p].Add(t);
                }

            var connected = tables.Where(t => parents[t].Count > 0 || children[t].Count > 0).ToList();
            var loose = tables.Where(t => parents[t].Count == 0 && children[t].Count == 0).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

            // rank = longest chain of parents above a table; edges that close a cycle are ignored
            var rank = new Dictionary<DiagramTable, int>();
            var onStack = new HashSet<DiagramTable>();
            int Rank(DiagramTable t)
            {
                if (rank.TryGetValue(t, out var r)) return r;
                onStack.Add(t);
                int best = 0;
                foreach (var p in parents[t])
                {
                    if (onStack.Contains(p)) continue;
                    best = Math.Max(best, Rank(p) + 1);
                }
                onStack.Remove(t);
                rank[t] = best;
                return best;
            }
            foreach (var t in connected.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)) Rank(t);

            var columns = connected.GroupBy(t => rank[t]).OrderBy(g => g.Key)
                .Select(g => g.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList()).ToList();

            // order inside each rank: a few sweeps of barycentres (average slot of the tables it connects to)
            var slot = new Dictionary<DiagramTable, double>();
            void Number() { foreach (var col in columns) for (int i = 0; i < col.Count; i++) slot[col[i]] = i; }
            Number();
            for (int pass = 0; pass < 6; pass++)
            {
                bool down = pass % 2 == 0;
                var order = down ? columns.Skip(1) : columns.Take(Math.Max(0, columns.Count - 1)).Reverse();
                foreach (var col in order)
                {
                    var keyed = col.Select((t, i) =>
                    {
                        var near = down ? parents[t] : children[t];
                        var known = near.Where(slot.ContainsKey).Select(n => slot[n]).ToList();
                        return (t, key: known.Count > 0 ? known.Average() : i);
                    }).OrderBy(x => x.key).ThenBy(x => x.t.Name, StringComparer.OrdinalIgnoreCase).Select(x => x.t).ToList();
                    col.Clear();
                    col.AddRange(keyed);
                    for (int i = 0; i < col.Count; i++) slot[col[i]] = i;
                }
            }

            // place the ranks left to right; a very tall rank wraps into extra columns
            double x = Origin;
            double tallest = 0;
            foreach (var col in columns)
            {
                foreach (var chunk in Wrap(col))
                {
                    double y = Origin;
                    double w = chunk.Max(t => t.Width);
                    foreach (var t in chunk)
                    {
                        t.X = x; t.Y = y; t.Placed = false;
                        y += t.Height + RowGap;
                    }
                    tallest = Math.Max(tallest, y);
                    x += w + ColumnGap;
                }
            }

            // tables with no relations: packed columns to the right, about as tall as the rest
            if (loose.Count > 0)
            {
                double limit = Math.Max(Math.Min(tallest, MaxColumnHeight), 600);
                double y = Origin, w = 0;
                foreach (var t in loose)
                {
                    if (y > Origin && y + t.Height > limit) { x += w + ColumnGap * 0.6; y = Origin; w = 0; }
                    t.X = x; t.Y = y; t.Placed = false;
                    y += t.Height + RowGap;
                    w = Math.Max(w, t.Width);
                }
            }
        }

        /// <summary>Places only the tables without a saved position, to the right of / below the placed ones.</summary>
        public static void PlaceNew(IReadOnlyList<DiagramTable> tables)
        {
            var placed = tables.Where(t => t.Placed).ToList();
            var fresh = tables.Where(t => !t.Placed).ToList();
            if (fresh.Count == 0) return;
            if (placed.Count == 0) { Arrange(tables); return; }
            double x = placed.Max(t => t.Right) + ColumnGap;
            double y = Math.Max(Origin, placed.Min(t => t.Y));
            double top = y, w = 0;
            foreach (var t in fresh)
            {
                if (y > top && y + t.Height > top + MaxColumnHeight) { x += w + ColumnGap * 0.6; y = top; w = 0; }
                t.X = x; t.Y = y;
                y += t.Height + RowGap;
                w = Math.Max(w, t.Width);
            }
        }

        /// <summary>A free spot for one new card near the visible area's top-left, not overlapping any card.</summary>
        public static (double X, double Y) FreeSpot(IReadOnlyList<DiagramTable> tables, double width, double height, double nearX, double nearY)
        {
            nearX = Math.Max(Origin, nearX);
            nearY = Math.Max(Origin, nearY);
            for (int ring = 0; ring < 40; ring++)
                for (int k = 0; k <= ring; k++)
                {
                    double x = nearX + k * 60, y = nearY + (ring - k) * 60;
                    if (!tables.Any(t => x < t.Right + 30 && x + width + 30 > t.X && y < t.Bottom + 30 && y + height + 30 > t.Y)) return (x, y);
                }
            return (tables.Count == 0 ? Origin : tables.Max(t => t.Right) + ColumnGap, nearY);
        }

        private static IEnumerable<List<DiagramTable>> Wrap(List<DiagramTable> col)
        {
            var chunk = new List<DiagramTable>();
            double h = 0;
            foreach (var t in col)
            {
                if (chunk.Count > 0 && h + t.Height > MaxColumnHeight) { yield return chunk; chunk = new List<DiagramTable>(); h = 0; }
                chunk.Add(t);
                h += t.Height + RowGap;
            }
            if (chunk.Count > 0) yield return chunk;
        }

        /// <summary>True when two cards overlap (used by tests and by the "tidy" check).</summary>
        public static bool Overlap(DiagramTable a, DiagramTable b) =>
            a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;
    }
}
