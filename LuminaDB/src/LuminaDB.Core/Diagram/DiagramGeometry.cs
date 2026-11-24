using System;
using System.Collections.Generic;
using System.Linq;
using LuminaDB.Core.Tables;

namespace LuminaDB.Core.Diagram
{
    public struct DPoint
    {
        public DPoint(double x, double y) { X = x; Y = y; }
        public double X { get; }
        public double Y { get; }
        public override string ToString() => X.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "," + Y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    public struct DSize
    {
        public DSize(double w, double h) { Width = w; Height = h; }
        public double Width { get; }
        public double Height { get; }
    }

    public struct DLine
    {
        public DLine(DPoint a, DPoint b) { A = a; B = b; }
        public DPoint A { get; }
        public DPoint B { get; }
    }

    public struct DCircle
    {
        public DCircle(DPoint c, double r) { Center = c; Radius = r; }
        public DPoint Center { get; }
        public double Radius { get; }
    }

    /// <summary>One row of a table card.</summary>
    public sealed class DiagramRow
    {
        public string Name { get; set; }
        public string Type { get; set; }
        public bool IsPrimaryKey { get; set; }
        public bool IsForeignKey { get; set; }
        public bool IsUnique { get; set; }
        public bool NotNull { get; set; }
        /// <summary>"PK", "FK", "PK FK", "UQ" or "".</summary>
        public string Badge => IsPrimaryKey && IsForeignKey ? "PK FK" : IsPrimaryKey ? "PK" : IsForeignKey ? "FK" : IsUnique ? "UQ" : "";
    }

    /// <summary>A foreign key drawn between two cards, crow's-foot style.</summary>
    public sealed class DiagramRelation
    {
        public DiagramTable Child { get; set; }
        public DiagramTable Parent { get; set; }
        public ForeignKeyDef ForeignKey { get; set; }
        /// <summary>The FK columns form a unique key: at most one child per parent (1:1).</summary>
        public bool ChildIsOne { get; set; }
        /// <summary>The FK columns may be NULL: a child row can exist without a parent.</summary>
        public bool ParentOptional { get; set; }
        public List<DPoint> Points { get; set; } = new List<DPoint>();
        public List<DLine> Marks { get; set; } = new List<DLine>();
        public List<DCircle> Circles { get; set; } = new List<DCircle>();
        public string Label => ForeignKey?.Name ?? "";
        /// <summary>"1 to many", "0 or 1 to many", "1 to 1"…: the plain-words reading for tooltips.</summary>
        public string Meaning => (ParentOptional ? "zero or one " : "exactly one ") + Parent?.Name + " to " + (ChildIsOne ? "at most one " : "many ") + Child?.Name;
    }

    /// <summary>
    /// Sizes, rows and relation routes, shared by the WPF canvas, the SVG and the PDF so all three look alike.
    /// Text is measured as a monospace font: 12 px rows, 13 px bold header.
    /// </summary>
    public static class DiagramGeometry
    {
        public const double HeaderHeight = 30;
        public const double RowHeight = 20;
        public const double BottomPad = 6;
        public const double CharWidth = 7.2;        // 12 px Courier / Consolas, rounded up
        public const double HeaderCharWidth = 7.9;  // 13 px bold
        public const double SidePad = 10;
        public const double BadgeWidth = 40;        // "PK FK" column
        public const double MinWidth = 170;
        public const double MaxWidth = 460;
        public const double Stub = 26;              // straight run out of a card before the first bend

        public static List<DiagramRow> Rows(TableDesign d)
        {
            var pk = new HashSet<string>((d.PrimaryKey?.Columns ?? new List<string>()).Select(TableDesign.IndexColumnName), StringComparer.OrdinalIgnoreCase);
            var fk = new HashSet<string>(d.ForeignKeys.SelectMany(f => f.Columns), StringComparer.OrdinalIgnoreCase);
            var uq = new HashSet<string>(d.Indexes.Where(i => i.Kind == IndexKind.Unique && i.Columns.Count == 1).Select(i => TableDesign.IndexColumnName(i.Columns[0])), StringComparer.OrdinalIgnoreCase);
            return d.Columns.Select(c => new DiagramRow
            {
                Name = c.Name,
                Type = TypeText(c),
                IsPrimaryKey = pk.Contains(c.Name),
                IsForeignKey = fk.Contains(c.Name),
                IsUnique = uq.Contains(c.Name),
                NotNull = !c.Nullable,
            }).ToList();
        }

        public static string TypeText(ColumnDef c)
        {
            var t = c.FullType.ToLowerInvariant();
            if (t.Length > 28) t = t.Substring(0, 27) + "…";
            return t;
        }

        public static DSize MeasureTable(TableDesign d)
        {
            var rows = Rows(d);
            double nameW = rows.Count == 0 ? 0 : rows.Max(r => r.Name.Length) * CharWidth;
            double typeW = rows.Count == 0 ? 0 : rows.Max(r => r.Type.Length) * CharWidth;
            double rowW = SidePad + BadgeWidth + nameW + 18 + typeW + SidePad;
            double headW = SidePad + (d.Name ?? "").Length * HeaderCharWidth + SidePad + 24;
            double w = Math.Max(MinWidth, Math.Max(rowW, headW));
            w = Math.Min(MaxWidth, Math.Ceiling(w / 10) * 10);
            double h = HeaderHeight + Math.Max(1, rows.Count) * RowHeight + BottomPad;
            return new DSize(w, h);
        }

        /// <summary>Vertical centre of a column's row, or of the header when the column isn't on the card.</summary>
        public static double RowCenter(DiagramTable t, string column)
        {
            int i = column == null ? -1 : t.Design.Columns.FindIndex(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? t.Y + HeaderHeight / 2 : t.Y + HeaderHeight + i * RowHeight + RowHeight / 2;
        }

        /// <summary>Relations between the given cards; foreign keys to tables not on the diagram are skipped.</summary>
        public static List<DiagramRelation> Relations(IReadOnlyList<DiagramTable> tables)
        {
            var byName = new Dictionary<string, DiagramTable>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in tables) if (!byName.ContainsKey(t.Name)) byName[t.Name] = t;
            var list = new List<DiagramRelation>();
            foreach (var child in tables)
            {
                foreach (var f in child.Design.ForeignKeys)
                {
                    if (f.RefDatabase != null && !string.Equals(f.RefDatabase, child.Design.Database, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!byName.TryGetValue(f.RefTable ?? "", out var parent)) continue;
                    var rel = new DiagramRelation
                    {
                        Child = child,
                        Parent = parent,
                        ForeignKey = f,
                        ChildIsOne = IsUniqueSet(child.Design, f.Columns),
                        ParentOptional = f.Columns.Any(c => child.Design.Column(c)?.Nullable ?? true),
                    };
                    list.Add(rel);
                }
            }
            RouteAll(list, tables);
            return list;
        }

        /// <summary>Recomputes every route (call after a card moves). Lines steer round the other cards when they can.</summary>
        public static void RouteAll(IReadOnlyList<DiagramRelation> relations, IReadOnlyList<DiagramTable> tables = null)
        {
            var obstacles = tables ?? relations.SelectMany(r => new[] { r.Child, r.Parent }).Distinct().ToList();
            // relations that share the same pair of cards get spread lanes so they don't sit on top of each other
            var lanes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in relations)
            {
                var key = r.Child.Name + "\u0001" + r.Parent.Name;
                lanes.TryGetValue(key, out var n);
                lanes[key] = n + 1;
                Route(r, n, obstacles);
            }
        }

        public static bool IsUniqueSet(TableDesign d, IList<string> columns)
        {
            if (columns == null || columns.Count == 0) return false;
            return d.Indexes.Any(i => (i.Kind == IndexKind.Primary || i.Kind == IndexKind.Unique)
                && i.Columns.Count == columns.Count
                && i.Columns.Select(TableDesign.IndexColumnName).All(c => columns.Contains(c, StringComparer.OrdinalIgnoreCase)));
        }

        /// <summary>How many cards a route passes through (the line may touch its own two cards where it leaves and enters them).</summary>
        public static int Crossings(IList<DPoint> pts, IEnumerable<DiagramTable> cards, DiagramTable child, DiagramTable parent)
        {
            int n = 0;
            foreach (var t in cards)
            {
                for (int i = 0; i + 1 < pts.Count; i++)
                {
                    if (i == 0 && ReferenceEquals(t, child) || i == pts.Count - 2 && ReferenceEquals(t, parent)) continue;
                    if (SegmentHits(pts[i], pts[i + 1], t, 3)) { n++; break; }
                }
            }
            return n;
        }

        private static bool SegmentHits(DPoint a, DPoint b, DiagramTable t, double pad)
        {
            double x0 = Math.Min(a.X, b.X), x1 = Math.Max(a.X, b.X), y0 = Math.Min(a.Y, b.Y), y1 = Math.Max(a.Y, b.Y);
            return x1 > t.X - pad && x0 < t.Right + pad && y1 > t.Y - pad && y0 < t.Bottom + pad;
        }

        private static double Length(IList<DPoint> pts)
        {
            double l = 0;
            for (int i = 0; i + 1 < pts.Count; i++) l += Math.Abs(pts[i + 1].X - pts[i].X) + Math.Abs(pts[i + 1].Y - pts[i].Y);
            return l;
        }

        /// <summary>The candidate with the fewest cards in the way, then the fewest bends, then the shortest.</summary>
        private static List<DPoint> Best(IEnumerable<List<DPoint>> candidates, IReadOnlyList<DiagramTable> cards, DiagramTable child, DiagramTable parent)
        {
            List<DPoint> best = null;
            double bestScore = double.MaxValue;
            foreach (var c in candidates)
            {
                double score = Crossings(c, cards, child, parent) * 100000 + (c.Count - 4) * 400 + Length(c);
                if (score < bestScore) { bestScore = score; best = c; }
            }
            return best;
        }

        public static void Route(DiagramRelation r, int lane = 0, IReadOnlyList<DiagramTable> cards = null)
        {
            var c = r.Child;
            var p = r.Parent;
            var f = r.ForeignKey;
            cards = cards ?? new[] { c, p };
            double cy = RowCenter(c, f.Columns.FirstOrDefault());
            double py = RowCenter(p, f.RefColumns.FirstOrDefault());
            double laneShift = lane * 10;
            r.Points.Clear();
            r.Marks.Clear();
            r.Circles.Clear();

            DPoint s, e;
            double outC, outP;   // +1 = leaves to the right, -1 = to the left
            if (ReferenceEquals(c, p))
            {
                double x = c.Right + Stub + 16 + laneShift;
                if (Math.Abs(cy - py) < 1) py = cy + RowHeight / 2;   // FK to its own key column
                s = new DPoint(c.Right, cy); e = new DPoint(p.Right, py);
                r.Points.AddRange(new[] { s, new DPoint(x, cy), new DPoint(x, py), e });
                outC = 1; outP = 1;
            }
            else if (c.Right + 2 * Stub <= p.X || p.Right + 2 * Stub <= c.X)
            {
                bool right = c.Right + 2 * Stub <= p.X;          // child on the left: the line leaves to the right
                double dir = right ? 1 : -1;
                s = right ? new DPoint(c.Right, cy) : new DPoint(c.X, cy);
                e = right ? new DPoint(p.X, py) : new DPoint(p.Right, py);
                outC = dir; outP = -dir;
                double lo = Math.Min(s.X, e.X) + Stub, hi = Math.Max(s.X, e.X) - Stub;
                double Clamp(double x) => Math.Max(lo, Math.Min(hi, Math.Round(x)));
                var between = cards.Where(t => !ReferenceEquals(t, c) && !ReferenceEquals(t, p) && t.Right > lo - Stub && t.X < hi + Stub).ToList();

                // three segments: one vertical run, in the middle or in a gap beside a card that sits between
                var mids = new List<double> { (s.X + e.X) / 2 + dir * laneShift, s.X + dir * (Stub + laneShift), e.X - dir * (Stub + laneShift) };
                foreach (var t in between) { mids.Add(t.Right + 18 + laneShift); mids.Add(t.X - 18 - laneShift); }
                var candidates = mids.Select(Clamp).Distinct()
                    .Select(m => new List<DPoint> { s, new DPoint(m, cy), new DPoint(m, py), e }).ToList();

                // five segments: out of the child, along a free channel above or below the cards in the way, into the parent
                if (between.Count > 0)
                {
                    double x1 = Clamp(s.X + dir * (Stub + laneShift)), x2 = Clamp(e.X - dir * (Stub + laneShift));
                    var ys = new List<double>();
                    foreach (var t in between) { ys.Add(t.Y - 16 - lane * 6); ys.Add(t.Bottom + 16 + lane * 6); }
                    ys.Add(between.Min(t => t.Y) - 16 - lane * 6);
                    ys.Add(between.Max(t => t.Bottom) + 16 + lane * 6);
                    foreach (var yy in ys.Where(y => y > 0).Distinct())
                        candidates.Add(new List<DPoint> { s, new DPoint(x1, cy), new DPoint(x1, yy), new DPoint(x2, yy), new DPoint(x2, py), e });
                }
                r.Points.AddRange(Best(candidates, cards, c, p));
            }
            else
            {
                // the cards overlap side to side (one above the other): round the right-hand or the left-hand side
                double xr = Math.Max(c.Right, p.Right) + Stub + laneShift;
                double xl = Math.Min(c.X, p.X) - Stub - laneShift;
                var viaRight = new List<DPoint> { new DPoint(c.Right, cy), new DPoint(xr, cy), new DPoint(xr, py), new DPoint(p.Right, py) };
                var viaLeft = new List<DPoint> { new DPoint(c.X, cy), new DPoint(xl, cy), new DPoint(xl, py), new DPoint(p.X, py) };
                var candidates = new List<List<DPoint>> { viaRight };
                if (xl > 0) candidates.Add(viaLeft);
                var best = Best(candidates, cards, c, p);
                r.Points.AddRange(best);
                s = best[0]; e = best[best.Count - 1];
                outC = outP = ReferenceEquals(best, viaRight) ? 1 : -1;
            }

            // child end: crow's foot (many) or a bar (one); then a bar (mandatory) or a circle (optional) — read from the parent's side
            if (r.ChildIsOne)
            {
                Bar(r, s, outC, 7);
            }
            else
            {
                var toe = new DPoint(s.X + outC * 14, s.Y);
                r.Marks.Add(new DLine(toe, new DPoint(s.X, s.Y - 7)));
                r.Marks.Add(new DLine(toe, new DPoint(s.X, s.Y + 7)));
                r.Marks.Add(new DLine(toe, s));
            }
            Bar(r, s, outC, 19);

            // parent end: exactly one (two bars) or zero-or-one (bar and circle)
            Bar(r, e, outP, 7);
            if (r.ParentOptional) r.Circles.Add(new DCircle(new DPoint(e.X + outP * 17, e.Y), 4));
            else Bar(r, e, outP, 13);
        }

        private static void Bar(DiagramRelation r, DPoint at, double dir, double offset)
        {
            double x = at.X + dir * offset;
            r.Marks.Add(new DLine(new DPoint(x, at.Y - 6), new DPoint(x, at.Y + 6)));
        }

        /// <summary>The smallest box holding every card and route, with a margin.</summary>
        public static (double X, double Y, double Width, double Height) Bounds(IReadOnlyList<DiagramTable> tables, IReadOnlyList<DiagramRelation> relations, double margin = 24)
        {
            if (tables.Count == 0) return (0, 0, 2 * margin + 200, 2 * margin + 100);
            double x0 = tables.Min(t => t.X), y0 = tables.Min(t => t.Y), x1 = tables.Max(t => t.Right), y1 = tables.Max(t => t.Bottom);
            foreach (var r in relations)
                foreach (var pt in r.Points) { x0 = Math.Min(x0, pt.X); x1 = Math.Max(x1, pt.X + 8); y0 = Math.Min(y0, pt.Y); y1 = Math.Max(y1, pt.Y); }
            return (x0 - margin, y0 - margin, x1 - x0 + 2 * margin, y1 - y0 + 2 * margin);
        }
    }
}
