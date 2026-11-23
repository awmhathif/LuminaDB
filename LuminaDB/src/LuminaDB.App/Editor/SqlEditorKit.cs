using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using LuminaDB.Core.Sql;

namespace LuminaDB.App.Editor
{
    internal static class Ledger
    {
        public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
        public static Color Color(string key) => ((SolidColorBrush)Brush(key)).Color;

        public static Brush Frozen(Color c, byte alpha)
        {
            var b = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        public static Pen Pen(Brush b, double width)
        {
            var p = new Pen(b, width);
            p.Freeze();
            return p;
        }
    }

    /// <summary>SQL colouring in Ledger ink, generated from the same word lists the checker uses.</summary>
    public static class SqlHighlighting
    {
        private static IHighlightingDefinition _definition;

        public static IHighlightingDefinition Definition => _definition ?? (_definition = Build());

        private static string Words(IEnumerable<string> words) =>
            string.Join("", words.Where(w => w.All(c => char.IsLetterOrDigit(c) || c == '_')).Distinct(StringComparer.OrdinalIgnoreCase)
                                 .Select(w => "<Word>" + w + "</Word>"));

        private static IHighlightingDefinition Build()
        {
            var keywords = SqlKeywords.Reserved.Concat(SqlKeywords.Keywords).Concat(SqlKeywords.StatementStarters).Concat(SqlKeywords.Literals);
            var types = SqlKeywords.Types.Concat(SqlKeywords.DateParts).ToList();
            keywords = keywords.Where(k => !types.Contains(k, StringComparer.OrdinalIgnoreCase));
            var xshd = new StringBuilder();
            xshd.Append(@"<SyntaxDefinition name=""LedgerSQL"" xmlns=""http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008"">
  <Color name=""Comment"" foreground=""#857D6C"" fontStyle=""italic""/>
  <Color name=""String"" foreground=""#7A4E12""/>
  <Color name=""Name"" foreground=""#1C1A16""/>
  <Color name=""Number"" foreground=""#7B2E8E""/>
  <Color name=""Keyword"" foreground=""#1F3FBF"" fontWeight=""bold""/>
  <Color name=""Type"" foreground=""#1F3FBF""/>
  <Color name=""Function"" foreground=""#2B3F8F""/>
  <Color name=""Variable"" foreground=""#B8730A""/>
  <RuleSet ignoreCase=""true"">
    <Span color=""Comment"" begin=""--(?=\s|$)"" />
    <Span color=""Comment"" begin=""\#"" />
    <Span color=""Comment"" multiline=""true"" begin=""/\*"" end=""\*/"" />
    <Span color=""String"" multiline=""true""><Begin>'</Begin><End>'</End><RuleSet><Span begin=""\\"" end="".""/></RuleSet></Span>
    <Span color=""String"" multiline=""true""><Begin>""</Begin><End>""</End><RuleSet><Span begin=""\\"" end="".""/></RuleSet></Span>
    <Span color=""Name"" multiline=""true""><Begin>`</Begin><End>`</End></Span>
    <Rule color=""Variable"">@@?[\w$.]+</Rule>
    <Keywords color=""Keyword"">").Append(Words(keywords)).Append(@"</Keywords>
    <Keywords color=""Type"">").Append(Words(types)).Append(@"</Keywords>
    <Rule color=""Function"">\b[A-Za-z_][A-Za-z0-9_]*(?=\()</Rule>
    <Rule color=""Number"">\b0[xX][0-9a-fA-F]+\b|\b\d+(\.\d+)?([eE][+-]?\d+)?\b</Rule>
  </RuleSet>
</SyntaxDefinition>");
            using (var reader = XmlReader.Create(new System.IO.StringReader(xshd.ToString())))
                return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
    }

    /// <summary>Faint blue ruling under every line, like a notebook page.</summary>
    public sealed class RuledLines : IBackgroundRenderer
    {
        private readonly Pen _rule = Ledger.Pen(Ledger.Frozen(Ledger.Color("Blue"), 0x1E), 1);
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (!textView.VisualLinesValid) return;
            double w = textView.ActualWidth;
            double lineH = textView.DefaultLineHeight;
            double y = 0;
            foreach (var vl in textView.VisualLines)
            {
                y = vl.VisualTop - textView.VerticalOffset + vl.Height;
                double py = Math.Floor(y) - 0.5;
                dc.DrawLine(_rule, new Point(0, py), new Point(w, py));
            }
            // keep ruling the empty page below the last line
            for (y += lineH; y < textView.ActualHeight + lineH; y += lineH)
            {
                double py = Math.Floor(y) - 0.5;
                dc.DrawLine(_rule, new Point(0, py), new Point(w, py));
            }
        }
    }

    /// <summary>The red margin line between line numbers and text.</summary>
    public sealed class RedMarginLine : AbstractMargin
    {
        private readonly Pen _pen = Ledger.Pen(Ledger.Frozen(Ledger.Color("Red"), 0x8C), 1.5);
        protected override Size MeasureOverride(Size availableSize) => new Size(12, 0);
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Ledger.Brush("Paper3"), null, new Rect(0, 0, ActualWidth, ActualHeight));
            dc.DrawLine(_pen, new Point(5.25, 0), new Point(5.25, ActualHeight));
        }
    }

    /// <summary>Wavy underlines under the checker's findings (red = will fail, amber = careful).</summary>
    public sealed class IssueUnderlines : IBackgroundRenderer
    {
        private readonly Pen _bad = Ledger.Pen(Ledger.Brush("Red"), 1.3);
        private readonly Pen _warn = Ledger.Pen(Ledger.Brush("Amber"), 1.3);
        private readonly Brush _badTint = Ledger.Frozen(Ledger.Color("Red"), 0x16);
        private IReadOnlyList<SqlIssue> _issues = Array.Empty<SqlIssue>();

        public KnownLayer Layer => KnownLayer.Selection;

        public void SetIssues(IReadOnlyList<SqlIssue> issues) => _issues = issues ?? Array.Empty<SqlIssue>();

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (!textView.VisualLinesValid || _issues.Count == 0 || textView.Document == null) return;
            int docLen = textView.Document.TextLength;
            foreach (var issue in _issues)
            {
                if (issue.Severity == IssueSeverity.Info || issue.Start >= docLen) continue;
                int len = Math.Max(1, Math.Min(issue.Length, docLen - issue.Start));
                var seg = new TextSegment { StartOffset = issue.Start, Length = len };
                foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, seg))
                {
                    if (issue.Severity == IssueSeverity.Bad) dc.DrawRectangle(_badTint, null, r);
                    dc.DrawGeometry(null, issue.Severity == IssueSeverity.Bad ? _bad : _warn, Wave(r));
                }
            }
        }

        private static Geometry Wave(Rect r)
        {
            var g = new StreamGeometry();
            using (var c = g.Open())
            {
                double y = r.Bottom - 1.5, x = r.Left;
                c.BeginFigure(new Point(x, y), false, false);
                bool up = true;
                for (x += 2; x <= r.Right + 0.1; x += 2, up = !up) c.LineTo(new Point(x, up ? y - 2 : y), true, true);
            }
            g.Freeze();
            return g;
        }
    }

    /// <summary>A line in the completion list: table, column or SQL word.</summary>
    public sealed class SqlCompletion : ICompletionData
    {
        public SqlCompletion(string text, string kind, string detail, double priority)
        {
            Text = text;
            Kind = kind;
            Detail = detail;
            Priority = priority;
        }

        public string Text { get; }
        public string Kind { get; }
        public string Detail { get; }
        public double Priority { get; }
        public ImageSource Image => null;
        public object Description => Detail;

        public object Content
        {
            get
            {
                var p = new DockPanel { LastChildFill = true, MinWidth = 220 };
                var k = new TextBlock
                {
                    Text = Kind, FontSize = 10.5, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Ledger.Brush("Ink3"), FontFamily = (FontFamily)Application.Current.FindResource("FontMono"),
                };
                DockPanel.SetDock(k, Dock.Right);
                p.Children.Add(k);
                p.Children.Add(new TextBlock { Text = Text, Foreground = Ledger.Brush(Kind == "keyword" || Kind == "function" ? "Blue" : "Ink") });
                return p;
            }
        }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            var text = Text;
            if (Kind == "function") text += "(";
            textArea.Document.Replace(completionSegment, text);
        }
    }

    /// <summary>The exercise-book SQL editor: ruled lines, red margin, issue underlines, Ledger colours, search.</summary>
    public static class SqlEditorSetup
    {
        public static void Apply(TextEditor editor, IssueUnderlines underlines)
        {
            editor.SyntaxHighlighting = SqlHighlighting.Definition;
            editor.LineNumbersForeground = Ledger.Brush("RuleStrong");
            editor.Options.ConvertTabsToSpaces = true;
            editor.Options.IndentationSize = 2;
            editor.Options.EnableHyperlinks = false;
            editor.Options.EnableEmailHyperlinks = false;
            editor.Options.HighlightCurrentLine = true;
            editor.Options.AllowScrollBelowDocument = false;
            editor.Options.CutCopyWholeLine = true;
            editor.Options.EnableRectangularSelection = true;

            var area = editor.TextArea;
            area.TextView.CurrentLineBackground = Ledger.Frozen(Ledger.Color("Blue"), 0x0A);
            area.TextView.CurrentLineBorder = null;
            area.SelectionBrush = Ledger.Brush("BlueSoft");
            area.SelectionBorder = null;
            area.SelectionForeground = null;
            area.SelectionCornerRadius = 0;
            area.Caret.CaretBrush = Ledger.Brush("Ink");
            area.TextView.BackgroundRenderers.Add(new RuledLines());
            if (underlines != null) area.TextView.BackgroundRenderers.Add(underlines);

            // the dotted separator becomes the red margin line of a school exercise book
            for (int i = 0; i < area.LeftMargins.Count; i++)
                if (DottedLineMargin.IsDottedLineMargin(area.LeftMargins[i])) { area.LeftMargins[i] = new RedMarginLine(); break; }
            foreach (var m in area.LeftMargins.OfType<LineNumberMargin>()) m.Margin = new Thickness(6, 0, 4, 0);
            SearchPanel.Install(editor);
        }
    }
}
