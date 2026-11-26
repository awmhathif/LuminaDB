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
    /// <summary>
    /// Palette colours for code that draws. <see cref="Brush"/> gives a brush that follows the theme: one per colour name,
    /// recoloured in place when Settings › Dark theme switches. Don't put one in a Style or freeze it (a frozen brush can't
    /// be recoloured); <see cref="Pen"/> copies it first. Pens and tints made once go through <see cref="Cached{T}"/>,
    /// which is emptied when the theme switches.
    /// </summary>
    internal static class Ledger
    {
        private static readonly Dictionary<string, SolidColorBrush> Live = new Dictionary<string, SolidColorBrush>();
        private static readonly Dictionary<string, object> Made = new Dictionary<string, object>();

        public static Brush Brush(string key)
        {
            if (Live.TryGetValue(key, out var b) && !b.IsFrozen) return b;
            b = new SolidColorBrush(Color(key));
            Live[key] = b;
            return b;
        }

        public static Color Color(string key) => ((SolidColorBrush)Application.Current.FindResource(key)).Color;

        public static T Cached<T>(string id, Func<T> make) where T : class
        {
            if (!Made.TryGetValue(id, out var o)) Made[id] = o = make();
            return (T)o;
        }

        /// <summary>The theme switched: live brushes take the new colours, made pens and tints are made again.</summary>
        internal static void Recolour()
        {
            Made.Clear();
            foreach (var kv in Live.ToList())
            {
                if (kv.Value.IsFrozen) Live.Remove(kv.Key);    // something froze it: its users keep the old colour, new ones get a fresh brush
                else kv.Value.Color = Color(kv.Key);
            }
        }

        public static Brush Frozen(Color c, byte alpha)
        {
            var b = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        public static Pen Pen(Brush b, double width)
        {
            var p = new Pen(b.IsFrozen ? b : b.CloneCurrentValue(), width);
            p.Freeze();
            return p;
        }
    }

    /// <summary>SQL colouring in Ledger ink, generated from the same word lists the checker uses.</summary>
    public static class SqlHighlighting
    {
        private static IHighlightingDefinition _definition;

        public static IHighlightingDefinition Definition => _definition ?? (_definition = Build());

        /// <summary>The theme switched: the next <see cref="Definition"/> is built in the new colours.</summary>
        internal static void Reset() => _definition = null;

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
  <Color name=""Comment"" foreground=""{SyntaxComment}"" fontStyle=""italic""/>
  <Color name=""String"" foreground=""{SyntaxString}""/>
  <Color name=""Name"" foreground=""{SyntaxName}""/>
  <Color name=""Number"" foreground=""{SyntaxNumber}""/>
  <Color name=""Keyword"" foreground=""{SyntaxKeyword}"" fontWeight=""bold""/>
  <Color name=""Type"" foreground=""{SyntaxKeyword}""/>
  <Color name=""Function"" foreground=""{SyntaxFunction}""/>
  <Color name=""Variable"" foreground=""{SyntaxVariable}""/>
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
            // the colours come from the palette (Ledger or Night), so the SQL reads on either paper
            foreach (var key in new[] { "SyntaxComment", "SyntaxString", "SyntaxName", "SyntaxNumber", "SyntaxKeyword", "SyntaxFunction", "SyntaxVariable" })
            {
                var c = Ledger.Color(key);
                xshd.Replace("{" + key + "}", "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"));
            }
            using (var reader = XmlReader.Create(new System.IO.StringReader(xshd.ToString())))
                return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
    }

    /// <summary>Faint blue ruling under every line, like a notebook page.</summary>
    public sealed class RuledLines : IBackgroundRenderer
    {
        private static Pen Rule => Ledger.Cached("ruled", () => Ledger.Pen(Ledger.Frozen(Ledger.Color("Blue"), 0x1E), 1));
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
                dc.DrawLine(Rule, new Point(0, py), new Point(w, py));
            }
            // keep ruling the empty page below the last line
            for (y += lineH; y < textView.ActualHeight + lineH; y += lineH)
            {
                double py = Math.Floor(y) - 0.5;
                dc.DrawLine(Rule, new Point(0, py), new Point(w, py));
            }
        }
    }

    /// <summary>The red margin line between line numbers and text.</summary>
    public sealed class RedMarginLine : AbstractMargin
    {
        private static Pen Line => Ledger.Cached("margin", () => Ledger.Pen(Ledger.Frozen(Ledger.Color("Red"), 0x8C), 1.5));
        protected override Size MeasureOverride(Size availableSize) => new Size(12, 0);
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Ledger.Brush("Paper3"), null, new Rect(0, 0, ActualWidth, ActualHeight));
            dc.DrawLine(Line, new Point(5.25, 0), new Point(5.25, ActualHeight));
        }
    }

    /// <summary>Wavy underlines under the checker's findings (red = will fail, amber = careful).</summary>
    public sealed class IssueUnderlines : IBackgroundRenderer
    {
        private static Pen _bad => Ledger.Cached("issue-bad", () => Ledger.Pen(Ledger.Brush("Red"), 1.3));
        private static Pen _warn => Ledger.Cached("issue-warn", () => Ledger.Pen(Ledger.Brush("Amber"), 1.3));
        private static Brush _badTint => Ledger.Cached("issue-tint", () => Ledger.Frozen(Ledger.Color("Red"), 0x16));
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
                    Text = Kind, FontSize = 11, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
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

    /// <summary>A snippet in the completion list: picking it writes the whole statement with Tab-able blanks.</summary>
    public sealed class SnippetCompletion : ICompletionData
    {
        private readonly SqlSnippet _snippet;

        public SnippetCompletion(SqlSnippet snippet, double priority)
        {
            _snippet = snippet;
            Priority = priority;
        }

        public string Text => _snippet.Trigger;
        public double Priority { get; }
        public ImageSource Image => null;
        public object Description => _snippet.Title + "\n\n" + _snippet.Preview + "\n\nTab or Enter writes it; Tab then moves from blank to blank.";

        public object Content
        {
            get
            {
                var p = new DockPanel { LastChildFill = true, MinWidth = 220 };
                var k = new TextBlock
                {
                    Text = _snippet.IsBuiltIn ? "snippet" : "my snippet", FontSize = 11, Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Ledger.Brush("Red"), FontFamily = (FontFamily)Application.Current.FindResource("FontMono"),
                };
                DockPanel.SetDock(k, Dock.Right);
                p.Children.Add(k);
                var line = new TextBlock();
                line.Inlines.Add(new System.Windows.Documents.Run(_snippet.Trigger) { Foreground = Ledger.Brush("Ink") });
                line.Inlines.Add(new System.Windows.Documents.Run("  " + _snippet.Title) { Foreground = Ledger.Brush("Ink2"), FontStyle = FontStyles.Italic });
                p.Children.Add(line);
                return p;
            }
        }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs) =>
            SnippetWriter.Write(textArea, _snippet, completionSegment.Offset, completionSegment.Length);
    }

    /// <summary>
    /// Writes a snippet into the editor: blanks are boxed, Tab and Shift+Tab move between them in number order
    /// (${1} first, whatever its place in the text), Enter or Esc finishes, Tab after the last blank goes to ${0}.
    /// </summary>
    public static class SnippetWriter
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TextArea, SnippetSession> Sessions =
            new System.Runtime.CompilerServices.ConditionalWeakTable<TextArea, SnippetSession>();

        public static void Write(TextArea area, SqlSnippet snippet, int offset, int length)
        {
            if (area?.Document == null || snippet == null) return;
            var doc = area.Document;
            offset = Math.Max(0, Math.Min(offset, doc.TextLength));
            length = Math.Max(0, Math.Min(length, doc.TextLength - offset));
            Current(area)?.Finish();
            var (s, fields) = Build(snippet.Body);
            var context = new ICSharpCode.AvalonEdit.Snippets.InsertionContext(area, offset);
            using (doc.RunUpdate())
            {
                if (length > 0) doc.Remove(offset, length);
                area.ClearSelection();
                context.InsertionPosition = offset;
                s.Insert(context);
                context.RaiseInsertionCompleted(EventArgs.Empty);
            }
            var session = new SnippetSession(area, context, fields
                .OrderBy(f => f.Key)
                .Select(f => context.GetActiveElement(f.Value) as ICSharpCode.AvalonEdit.Snippets.IReplaceableActiveElement)
                .Where(a => a != null).ToList());
            Sessions.Remove(area);
            if (session.Fields.Count == 0) return;   // plain SQL: AvalonEdit already finished it and put the cursor at ${0} or the end
            // AvalonEdit paints blanks bright green; ours are ruled fill-in blanks in Ledger blue
            foreach (var r in area.TextView.BackgroundRenderers.Where(r => r.GetType().DeclaringType?.Name == "ReplaceableActiveElement").ToList())
                area.TextView.BackgroundRenderers.Remove(r);
            var blanks = new SnippetBlanks(context, session);
            area.TextView.BackgroundRenderers.Add(blanks);
            EventHandler follow = (o, e) => area.TextView.InvalidateLayer(KnownLayer.Selection);   // a click into another blank moves the solid rule
            area.Caret.PositionChanged += follow;
            Sessions.Add(area, session);
            context.Deactivated += (o, e) =>
            {
                area.Caret.PositionChanged -= follow;
                area.TextView.BackgroundRenderers.Remove(blanks);
                area.TextView.InvalidateLayer(KnownLayer.Selection);
                if (Sessions.TryGetValue(area, out var cur) && cur == session) Sessions.Remove(area);
            };
            session.Select(0);
        }

        /// <summary>The snippet whose blanks are still active in this editor, if any.</summary>
        public static SnippetSession Current(TextArea area) =>
            area != null && Sessions.TryGetValue(area, out var s) ? s : null;

        /// <summary>Tab / Shift+Tab inside a snippet. Returns false when no snippet is active (Tab then means what it always does).</summary>
        public static bool HandleTab(TextArea area, bool backwards)
        {
            var s = Current(area);
            if (s == null) return false;
            s.Move(backwards);
            return true;
        }

        private static (ICSharpCode.AvalonEdit.Snippets.Snippet, Dictionary<int, ICSharpCode.AvalonEdit.Snippets.SnippetReplaceableTextElement>) Build(string body)
        {
            var s = new ICSharpCode.AvalonEdit.Snippets.Snippet();
            var fields = new Dictionary<int, ICSharpCode.AvalonEdit.Snippets.SnippetReplaceableTextElement>();
            foreach (var part in SnippetTemplate.Parse(body))
            {
                switch (part.Kind)
                {
                    case SnippetPartKind.Text:
                        s.Elements.Add(new ICSharpCode.AvalonEdit.Snippets.SnippetTextElement { Text = part.Text });
                        break;
                    case SnippetPartKind.Caret:
                        s.Elements.Add(new ICSharpCode.AvalonEdit.Snippets.SnippetCaretElement());
                        break;
                    default:
                        if (fields.TryGetValue(part.Index, out var target))
                            s.Elements.Add(new ICSharpCode.AvalonEdit.Snippets.SnippetBoundElement { TargetElement = target });
                        else
                        {
                            var f = new ICSharpCode.AvalonEdit.Snippets.SnippetReplaceableTextElement { Text = part.Text };
                            fields[part.Index] = f;
                            s.Elements.Add(f);
                        }
                        break;
                }
            }
            return (s, fields);
        }
    }

    /// <summary>
    /// A snippet's blanks drawn like a form to fill in: a blue box with a dotted rule under it; the blank you're in gets
    /// a solid rule; copies of a blank get the dotted rule only.
    /// </summary>
    internal sealed class SnippetBlanks : IBackgroundRenderer
    {
        private static Brush Fill => Ledger.Cached("blank-fill", () => Ledger.Frozen(Ledger.Color("Blue"), 0x26));
        private static Brush HereFill => Ledger.Cached("blank-here", () => Ledger.Frozen(Ledger.Color("Blue"), 0x30));
        private static Pen Rule => Ledger.Cached("blank-rule", () => DottedPen(0xFF));
        private static Pen CopyRule => Ledger.Cached("blank-copy", () => DottedPen(0xA0));
        private static Pen HereRule => Ledger.Cached("blank-solid", SolidPen);
        private readonly ICSharpCode.AvalonEdit.Snippets.InsertionContext _context;
        private readonly SnippetSession _session;

        public SnippetBlanks(ICSharpCode.AvalonEdit.Snippets.InsertionContext context, SnippetSession session)
        {
            _context = context;
            _session = session;
        }

        // Above the current-line tint (which would hide the blank being typed in), under the selection.
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (!textView.VisualLinesValid) return;
            var here = _session?.CurrentField;
            foreach (var el in _context.ActiveElements)
            {
                var seg = el.Segment;
                if (seg == null) continue;
                bool blank = el is ICSharpCode.AvalonEdit.Snippets.IReplaceableActiveElement;
                bool isHere = blank && ReferenceEquals(el, here);
                var s = new TextSegment { StartOffset = seg.Offset, Length = Math.Max(0, seg.Length) };
                var rects = BackgroundGeometryBuilder.GetRectsForSegment(textView, s).ToList();
                if (rects.Count == 0 && seg.Length == 0)
                {
                    // an emptied blank still shows where to type
                    var pos = textView.GetVisualPosition(new TextViewPosition(textView.Document.GetLocation(seg.Offset)), VisualYPosition.LineTop) - textView.ScrollOffset;
                    rects.Add(new Rect(pos.X, pos.Y, 8, textView.DefaultLineHeight));
                }
                foreach (var r in rects)
                {
                    if (blank) dc.DrawRectangle(isHere ? HereFill : Fill, null, r);
                    double y = Math.Floor(r.Bottom) - 1.5;
                    dc.DrawLine(isHere ? HereRule : blank ? Rule : CopyRule, new Point(r.Left, y), new Point(r.Right, y));
                }
            }
        }

        private static Pen SolidPen()
        {
            return Ledger.Pen(Ledger.Brush("Blue"), 2);
        }

        private static Pen DottedPen(byte alpha)
        {
            var p = new Pen(Ledger.Frozen(Ledger.Color("Blue"), alpha), 1.5) { DashStyle = new DashStyle(new double[] { 1.5, 1.5 }, 0) };
            p.Freeze();
            return p;
        }
    }

    /// <summary>One written snippet with its blanks, in Tab order.</summary>
    public sealed class SnippetSession
    {
        private readonly TextArea _area;
        private readonly ICSharpCode.AvalonEdit.Snippets.InsertionContext _context;
        private int _current;

        internal SnippetSession(TextArea area, ICSharpCode.AvalonEdit.Snippets.InsertionContext context, List<ICSharpCode.AvalonEdit.Snippets.IReplaceableActiveElement> fields)
        {
            _area = area;
            _context = context;
            Fields = fields;
        }

        public IReadOnlyList<ICSharpCode.AvalonEdit.Snippets.IReplaceableActiveElement> Fields { get; }

        internal void Select(int index)
        {
            _current = index;
            var seg = Fields[index].Segment;
            if (seg == null) return;
            _area.Selection = Selection.Create(_area, seg.Offset, seg.EndOffset);
            _area.Caret.Offset = seg.EndOffset;
            _area.Caret.BringCaretToView();
            _area.TextView.InvalidateLayer(KnownLayer.Selection);   // move the solid "you are here" rule
        }

        /// <summary>The blank the cursor is in (the one Tab left you in, or the one you clicked into).</summary>
        internal ICSharpCode.AvalonEdit.Snippets.IActiveElement CurrentField => Fields.Count == 0 ? null : Fields[CurrentIndex()];

        internal void Move(bool backwards)
        {
            int at = CurrentIndex();
            if (!backwards && at >= Fields.Count - 1) { Finish(); return; }   // past the last blank: go to the end mark
            Select(backwards ? (at <= 0 ? 0 : at - 1) : at + 1);
        }

        /// <summary>Ends the snippet; the cursor goes to ${0} (or stays put if there isn't one).</summary>
        public void Finish() =>
            _context.Deactivate(new ICSharpCode.AvalonEdit.Snippets.SnippetEventArgs(ICSharpCode.AvalonEdit.Snippets.DeactivateReason.ReturnPressed));

        private int CurrentIndex()
        {
            int caret = _area.Caret.Offset;
            for (int i = 0; i < Fields.Count; i++)
            {
                var seg = Fields[i].Segment;
                if (seg != null && caret >= seg.Offset && caret <= seg.EndOffset) return i;
            }
            return _current;
        }
    }

    /// <summary>
    /// Enter keeps the line's indent, and one more step after an open "(" (like phpMyAdmin's editor). A line that starts
    /// with ")" lines up with its "(".
    /// </summary>
    internal sealed class SqlIndentation : ICSharpCode.AvalonEdit.Indentation.IIndentationStrategy
    {
        private readonly TextEditorOptions _options;

        public SqlIndentation(TextEditorOptions options) { _options = options; }

        public void IndentLine(TextDocument document, DocumentLine line)
        {
            var prev = line.PreviousLine;
            if (prev == null) return;
            string indent = Lead(document, prev);
            int open = SqlBrackets.InnermostOpen(document.Text, line.Offset);
            if (open >= 0)
            {
                var openLine = document.GetLineByOffset(open);
                string rest = document.GetText(line.Offset, line.Length).TrimStart();
                if (rest.StartsWith(")", StringComparison.Ordinal)) indent = Lead(document, openLine);
                else if (openLine.LineNumber == prev.LineNumber) indent = Lead(document, openLine) + _options.IndentationString;
                // deeper lines keep the line above's indent, so your own alignment stays
            }
            var ws = TextUtilities.GetWhitespaceAfter(document, line.Offset);
            document.Replace(ws.Offset, ws.Length, indent, OffsetChangeMappingType.RemoveAndInsert);
        }

        public void IndentLines(TextDocument document, int beginLine, int endLine)
        {
            for (int i = beginLine; i <= endLine; i++) IndentLine(document, document.GetLineByNumber(i));
        }

        internal static string Lead(TextDocument d, DocumentLine l) => d.GetText(TextUtilities.GetWhitespaceAfter(d, l.Offset));
    }

    /// <summary>Typing helpers around brackets: Enter between "(" and ")" opens an indented line; a typed ")" lines up with its "(".</summary>
    internal static class SqlTyping
    {
        /// <summary>"(|)" + Enter becomes "(", an indented empty line with the cursor, then ")" on its own line.</summary>
        public static bool SplitBrackets(TextArea area, TextEditorOptions options)
        {
            var doc = area.Document;
            if (doc == null || !area.Selection.IsEmpty) return false;
            int caret = area.Caret.Offset;
            var line = doc.GetLineByOffset(caret);
            int p = caret - 1;
            while (p >= line.Offset && IsBlank(doc.GetCharAt(p))) p--;
            int q = caret;
            while (q < line.EndOffset && IsBlank(doc.GetCharAt(q))) q++;
            if (p < line.Offset || q >= line.EndOffset || doc.GetCharAt(p) != '(' || doc.GetCharAt(q) != ')') return false;
            if (SqlBrackets.Partner(doc.Text, p) != q) return false;   // a bracket in a string, or not a pair
            string outer = SqlIndentation.Lead(doc, line), inner = outer + options.IndentationString;
            string nl = TextUtilities.GetNewLineFromDocument(doc, line.LineNumber);
            doc.Replace(p + 1, q - p - 1, nl + inner + nl + outer);
            area.Caret.Offset = p + 1 + nl.Length + inner.Length;
            area.Caret.BringCaretToView();
            return true;
        }

        /// <summary>After typing ")" at the start of a line: move it under the line its "(" is on.</summary>
        public static void LineUpClosing(TextArea area)
        {
            var doc = area.Document;
            int close = area.Caret.Offset - 1;
            if (doc == null || close < 0 || close >= doc.TextLength || doc.GetCharAt(close) != ')') return;
            var line = doc.GetLineByOffset(close);
            string before = doc.GetText(line.Offset, close - line.Offset);
            if (before.Trim().Length != 0) return;
            int? open = SqlBrackets.Partner(doc.Text, close);
            if (open == null || open < 0) return;
            var openLine = doc.GetLineByOffset(open.Value);
            if (openLine.LineNumber == line.LineNumber) return;
            string want = SqlIndentation.Lead(doc, openLine);
            if (before != want) doc.Replace(line.Offset, before.Length, want);
        }

        private static bool IsBlank(char c) => c == ' ' || c == '\t';
    }

    /// <summary>
    /// With the cursor next to a bracket, it and its partner get a soft amber box. A ")" with no "(" gets a red one;
    /// a "(" with no ")" yet gets nothing (you're probably still typing it).
    /// </summary>
    internal sealed class BracketMatch : IBackgroundRenderer
    {
        private static Brush Fill => Ledger.Cached("bracket-fill", () => Ledger.Frozen(Ledger.Color("Amber"), 0x33));
        private static Pen Edge => Ledger.Cached("bracket-edge", () => Ledger.Pen(Ledger.Frozen(Ledger.Color("Amber"), 0xCC), 1));
        private static Brush BadFill => Ledger.Cached("bracket-bad", () => Ledger.Frozen(Ledger.Color("Red"), 0x26));
        private static Pen BadEdge => Ledger.Cached("bracket-bad-edge", () => Ledger.Pen(Ledger.Brush("Red"), 1));
        private readonly TextArea _area;
        private int _a = -1, _b = -1;

        public BracketMatch(TextArea area)
        {
            _area = area;
            area.Caret.PositionChanged += (s, e) => Update();
            area.DocumentChanged += (s, e) => Update();
        }

        public KnownLayer Layer => KnownLayer.Selection;

        private void Update()
        {
            int a = -1, b = -1;
            var doc = _area.Document;
            if (doc != null && doc.TextLength > 0)
            {
                int c = _area.Caret.Offset;
                string text = doc.Text;
                foreach (var at in new[] { c - 1, c })
                {
                    var partner = SqlBrackets.Partner(text, at);
                    if (partner == null) continue;
                    if (partner < 0 && text[at] == '(') continue;   // still being typed
                    a = at;
                    b = partner.Value;
                    break;
                }
            }
            if (a == _a && b == _b) return;
            _a = a;
            _b = b;
            _area.TextView.InvalidateLayer(KnownLayer.Selection);
        }

        public void Draw(TextView textView, DrawingContext dc)
        {
            if (_a < 0 || textView.Document == null || !textView.VisualLinesValid) return;
            bool bad = _b < 0;
            Box(textView, dc, _a, bad);
            if (!bad) Box(textView, dc, _b, false);
        }

        private static void Box(TextView textView, DrawingContext dc, int offset, bool bad)
        {
            if (offset >= textView.Document.TextLength) return;
            foreach (var r in BackgroundGeometryBuilder.GetRectsForSegment(textView, new TextSegment { StartOffset = offset, Length = 1 }))
            {
                var box = new Rect(Math.Floor(r.X) + 0.5, Math.Floor(r.Y) + 1.5, Math.Max(1, Math.Round(r.Width) - 1), Math.Max(1, Math.Round(r.Height) - 3));
                dc.DrawRectangle(bad ? BadFill : Fill, bad ? BadEdge : Edge, box);
            }
        }
    }

    /// <summary>The exercise-book SQL editor: ruled lines, red margin, issue underlines, Ledger colours, search, auto-indent, bracket matching.</summary>
    public static class SqlEditorSetup
    {
        /// <summary>The colours the editor takes from the palette: SQL colouring, line numbers, current line, selection, caret.</summary>
        private static void Recolour(TextEditor editor)
        {
            if (editor.SyntaxHighlighting != SqlHighlighting.Definition) editor.SyntaxHighlighting = SqlHighlighting.Definition;
            editor.LineNumbersForeground = Ledger.Frozen(Ledger.Color("RuleStrong"), 0xFF);
            var area = editor.TextArea;
            area.TextView.CurrentLineBackground = Ledger.Frozen(Ledger.Color("Blue"), 0x0A);
            area.SelectionBrush = Ledger.Frozen(Ledger.Color("Blue"), 0x40);   // BlueSoft was too faint to see on paper
            area.Caret.CaretBrush = Ledger.Frozen(Ledger.Color("Ink"), 0xFF);
            foreach (var m in area.LeftMargins) (m as UIElement)?.InvalidateVisual();
            area.TextView.Redraw();
        }

        /// <param name="popupOpen">True while a suggestion list is open: Enter then belongs to the list.</param>
        public static void Apply(TextEditor editor, IssueUnderlines underlines, Func<bool> popupOpen = null)
        {
            editor.LineNumbersForeground = Ledger.Frozen(Ledger.Color("RuleStrong"), 0xFF);
            editor.Options.ConvertTabsToSpaces = true;
            editor.Options.IndentationSize = 2;
            editor.Options.EnableHyperlinks = false;
            editor.Options.EnableEmailHyperlinks = false;
            editor.Options.HighlightCurrentLine = true;
            editor.Options.AllowScrollBelowDocument = false;
            editor.Options.CutCopyWholeLine = true;
            editor.Options.EnableRectangularSelection = true;

            var area = editor.TextArea;
            area.TextView.CurrentLineBorder = null;
            area.SelectionBorder = null;
            area.SelectionForeground = null;
            area.SelectionCornerRadius = 0;
            Recolour(editor);
            // Settings › Dark theme switches while the editor is open: colour it again (only while it is on screen)
            Action recolour = () => Recolour(editor);
            editor.Loaded += (s, e) => { Palette.Changed -= recolour; Palette.Changed += recolour; Recolour(editor); };
            editor.Unloaded += (s, e) => Palette.Changed -= recolour;
            if (editor.IsLoaded) Palette.Changed += recolour;
            area.TextView.BackgroundRenderers.Add(new RuledLines());
            if (underlines != null) area.TextView.BackgroundRenderers.Add(underlines);

            area.IndentationStrategy = new SqlIndentation(editor.Options);
            area.TextView.BackgroundRenderers.Add(new BracketMatch(area));
            area.PreviewKeyDown += (s, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter || System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;
                if ((popupOpen?.Invoke() ?? false) || SnippetWriter.Current(area) != null) return;
                if (SqlTyping.SplitBrackets(area, editor.Options)) e.Handled = true;
            };
            area.TextEntered += (s, e) => { if (e.Text == ")") SqlTyping.LineUpClosing(area); };

            // the dotted separator becomes the red margin line of a school exercise book
            for (int i = 0; i < area.LeftMargins.Count; i++)
                if (DottedLineMargin.IsDottedLineMargin(area.LeftMargins[i])) { area.LeftMargins[i] = new RedMarginLine(); break; }
            foreach (var m in area.LeftMargins.OfType<LineNumberMargin>()) m.Margin = new Thickness(6, 0, 4, 0);
            SearchPanel.Install(editor);
        }
    }
}
