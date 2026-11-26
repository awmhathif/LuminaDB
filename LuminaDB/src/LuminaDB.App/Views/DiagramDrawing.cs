using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LuminaDB.Core.Diagram;

namespace LuminaDB.App.Views
{
    /// <summary>Draws relation lines (crow's feet included) under the table cards. Repaints when Revision changes.</summary>
    public sealed class RelationLayer : FrameworkElement
    {
        public static readonly DependencyProperty RelationsProperty = DependencyProperty.Register(nameof(Relations), typeof(IReadOnlyList<DiagramRelation>), typeof(RelationLayer),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty RevisionProperty = DependencyProperty.Register(nameof(Revision), typeof(int), typeof(RelationLayer),
            new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<DiagramRelation> Relations { get => (IReadOnlyList<DiagramRelation>)GetValue(RelationsProperty); set => SetValue(RelationsProperty, value); }
        public int Revision { get => (int)GetValue(RevisionProperty); set => SetValue(RevisionProperty, value); }

        public RelationLayer()
        {
            IsHitTestVisible = false;
            SnapsToDevicePixels = true;
            // the lines are drawn in Blue on Paper3: draw them again when Settings › Dark theme switches
            Action redraw = InvalidateVisual;
            Loaded += (s, e) => { LuminaDB.App.Palette.Changed -= redraw; LuminaDB.App.Palette.Changed += redraw; };
            Unloaded += (s, e) => LuminaDB.App.Palette.Changed -= redraw;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var rels = Relations;
            if (rels == null) return;
            var line = (Brush)TryFindResource("Blue") ?? Brushes.SteelBlue;
            var paper = (Brush)TryFindResource("Paper3") ?? Brushes.White;
            DiagramDrawing.DrawRelations(dc, rels, line, paper, 0, 0);
        }
    }

    /// <summary>WPF drawing of a diagram scene, shared by the relation layer and the PNG export.</summary>
    public static class DiagramDrawing
    {
        private static Pen Freeze(Pen p) { p.Freeze(); return p; }

        public static void DrawRelations(DrawingContext dc, IEnumerable<DiagramRelation> rels, Brush line, Brush paper, double ox, double oy)
        {
            var solid = Freeze(new Pen(line, 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
            var dashed = Freeze(new Pen(line, 1.4) { DashStyle = new DashStyle(new double[] { 4, 2.5 }, 0), LineJoin = PenLineJoin.Round });
            foreach (var r in rels)
            {
                if (r.Points.Count < 2) continue;
                var g = new StreamGeometry();
                using (var ctx = g.Open())
                {
                    ctx.BeginFigure(P(r.Points[0], ox, oy), false, false);
                    ctx.PolyLineTo(r.Points.Skip(1).Select(p => P(p, ox, oy)).ToList(), true, true);
                }
                g.Freeze();
                dc.DrawGeometry(null, r.ParentOptional ? dashed : solid, g);
                foreach (var m in r.Marks) dc.DrawLine(solid, P(m.A, ox, oy), P(m.B, ox, oy));
                foreach (var c in r.Circles) dc.DrawEllipse(paper, solid, P(c.Center, ox, oy), c.Radius, c.Radius);
            }
        }

        private static Point P(DPoint p, double ox, double oy) => new Point(p.X + ox, p.Y + oy);

        /// <summary>Writes the scene as a PNG at <paramref name="scale"/>× (2 = sharp enough to print on A4).</summary>
        public static void SavePng(DiagramScene s, string file, double scale = 2)
        {
            var mono = (Application.Current?.TryFindResource("FontMono") as FontFamily) ?? new FontFamily("Consolas");
            var monoBold = (Application.Current?.TryFindResource("FontMonoMedium") as FontFamily) ?? mono;
            var regular = new Typeface(mono, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var bold = new Typeface(monoBold, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            Brush B(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
            var white = Brushes.White;
            var ink = B("#1F1D1A"); var muted = B("#66625A"); var rule = B("#D8D4CA"); var header = B("#F3F1EC");
            var line = B("#3D5A80"); var key = B("#9A6A00");
            double dpi = 96 * scale;
            double ox = -s.X, oy = -s.Y;

            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(white, null, new Rect(0, 0, s.Width, s.Height));
                Text(dc, s.Title, bold, 15, ink, 24, 12, false);
                DrawRelations(dc, s.Relations, line, white, ox, oy);
                foreach (var t in s.Tables)
                {
                    double x = t.X + ox, y = t.Y + oy;
                    var edge = Freeze(new Pen(B(EdgeHex(t.State)), t.State == DiagramTableState.Live ? 1 : 2) { DashStyle = t.State == DiagramTableState.Dropped ? new DashStyle(new double[] { 3, 1.5 }, 0) : DashStyles.Solid });
                    var box = new Rect(x, y, t.Width, t.Height);
                    dc.DrawRoundedRectangle(white, null, box, 3, 3);
                    dc.PushClip(new RectangleGeometry(box, 3, 3));
                    dc.DrawRectangle(header, null, new Rect(x, y, t.Width, DiagramGeometry.HeaderHeight));
                    dc.Pop();
                    dc.DrawLine(Freeze(new Pen(rule, 1)), new Point(x, y + DiagramGeometry.HeaderHeight), new Point(x + t.Width, y + DiagramGeometry.HeaderHeight));
                    dc.DrawRoundedRectangle(null, edge, box, 3, 3);
                    Text(dc, Clip(t.Name, (t.Width - 2 * DiagramGeometry.SidePad) / DiagramGeometry.HeaderCharWidth), bold, 13, ink, x + DiagramGeometry.SidePad, y + 7, false);
                    var rows = DiagramGeometry.Rows(t.Design);
                    for (int i = 0; i < rows.Count; i++)
                    {
                        var row = rows[i];
                        double ry = y + DiagramGeometry.HeaderHeight + i * DiagramGeometry.RowHeight + 3;
                        if (row.Badge.Length > 0) Text(dc, row.Badge, regular, 10, row.IsPrimaryKey ? key : line, x + DiagramGeometry.SidePad, ry + 1.5, false);
                        Text(dc, row.Name, row.IsPrimaryKey ? bold : regular, 12, ink, x + DiagramGeometry.SidePad + DiagramGeometry.BadgeWidth, ry, false, underline: row.IsPrimaryKey);
                        Text(dc, row.Type + (row.NotNull ? "" : " ?"), regular, 12, muted, x + t.Width - DiagramGeometry.SidePad, ry, true);
                    }
                }
            }

            var bmp = new RenderTargetBitmap((int)Math.Ceiling(s.Width * scale), (int)Math.Ceiling(s.Height * scale), dpi, dpi, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            var tmp = file + ".tmp";
            using (var fs = File.Create(tmp)) enc.Save(fs);
            if (File.Exists(file)) File.Delete(file);
            File.Move(tmp, file);
        }

        private static void Text(DrawingContext dc, string text, Typeface face, double size, Brush brush, double x, double y, bool alignRight, bool underline = false)
        {
            var ft = new FormattedText(text ?? "", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush, 1.0);
            if (underline) ft.SetTextDecorations(TextDecorations.Underline);
            dc.DrawText(ft, new Point(alignRight ? x - ft.Width : x, y));
        }

        private static string Clip(string s, double chars)
        {
            int n = Math.Max(3, (int)Math.Floor(chars));
            return s.Length <= n ? s : s.Substring(0, n - 1) + "…";
        }

        private static string EdgeHex(DiagramTableState s) =>
            s == DiagramTableState.New ? "#2E7D4F" : s == DiagramTableState.Changed ? "#3D5A80" : s == DiagramTableState.Dropped ? "#B3261E" : "#D8D4CA";
    }
}
