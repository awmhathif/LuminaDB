using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
using LuminaDB.App.Controls;
using LuminaDB.App.Editor;
using LuminaDB.App.ViewModels;
using LuminaDB.Core.Sql;

namespace LuminaDB.App.Views
{
    public partial class NotebookView : UserControl
    {
        private const double NoteGap = 6;

        private readonly IssueUnderlines _underlines = new IssueUnderlines();
        private readonly List<(MarginNote note, FrameworkElement view)> _notes = new List<(MarginNote, FrameworkElement)>();
        private NotebookViewModel _vm;
        private CompletionWindow _completion;
        private bool _syncingCaret;

        public NotebookView()
        {
            InitializeComponent();
            SetUpEditor();
            DataContextChanged += (s, e) => Attach(e.NewValue as NotebookViewModel);
            Unloaded += (s, e) => Attach(null);
            Loaded += (s, e) =>
            {
                if (_vm == null) Attach(DataContext as NotebookViewModel);
                Dispatcher.BeginInvoke(new Action(() => { Editor.Focus(); PlaceNotes(); }), System.Windows.Threading.DispatcherPriority.Input);
            };
            PreviewKeyDown += OnKeys;
        }

        // ------------------------------------------------------------------ editor

        private void SetUpEditor()
        {
            SqlEditorSetup.Apply(Editor, _underlines, () => _completion != null);
            var area = Editor.TextArea;

            area.Caret.PositionChanged += (s, e) =>
            {
                if (_vm == null || _syncingCaret) return;
                _vm.CaretOffset = area.Caret.Offset;
            };
            area.SelectionChanged += (s, e) =>
            {
                if (_vm == null) return;
                var sel = area.Selection;
                if (sel.IsEmpty || sel.Segments.Count() != 1) _vm.SetSelection(0, 0);
                else { var seg = sel.SurroundingSegment; _vm.SetSelection(seg.Offset, seg.Length); }
            };
            area.TextEntered += OnTextEntered;
            area.TextEntering += OnTextEntering;
            area.TextView.ScrollOffsetChanged += (s, e) => PlaceNotes();
            area.TextView.VisualLinesChanged += (s, e) => PlaceNotes();
            area.SizeChanged += (s, e) => PlaceNotes();
            NotesCanvas.SizeChanged += (s, e) => PlaceNotes();
        }

        private void Attach(NotebookViewModel vm)
        {
            if (_vm == vm) return;
            if (_vm != null)
            {
                _vm.SelectRequested -= OnSelectRequested;
                _vm.InsertRequested -= OnInsertRequested;
                _vm.AnalysisChanged -= OnAnalysisChanged;
                _vm.PropertyChanged -= OnVmChanged;
                _vm.SnippetRequested -= OnSnippetRequested;
                _completion?.Close();
                SnippetWriter.Current(Editor.TextArea)?.Finish();
            }
            _vm = vm;
            if (vm == null) return;
            Editor.Document = vm.Document;
            vm.SelectRequested += OnSelectRequested;
            vm.InsertRequested += OnInsertRequested;
            vm.AnalysisChanged += OnAnalysisChanged;
            vm.PropertyChanged += OnVmChanged;
            vm.SnippetRequested += OnSnippetRequested;
            int caret = Math.Min(vm.CaretOffset, vm.Document.TextLength);
            _syncingCaret = true;
            try { Editor.TextArea.Caret.Offset = caret; }
            finally { _syncingCaret = false; }
            Editor.ScrollTo(Editor.TextArea.Caret.Line, 1);
            OnAnalysisChanged();
            ShowResult(vm.SelectedResult);
            _indexOn = _receiptOn = null;
            _appliedLayout = null;
            ApplyPanels(animate: false);
            ApplyLayout(animate: false);
        }

        private void OnSelectRequested(int offset, int length)
        {
            var doc = Editor.Document;
            offset = Math.Max(0, Math.Min(offset, doc.TextLength));
            length = Math.Max(0, Math.Min(length, doc.TextLength - offset));
            Editor.Select(offset, length);
            Editor.TextArea.Caret.Offset = offset + length;
            var loc = doc.GetLocation(offset);
            Editor.ScrollTo(loc.Line, loc.Column);
            Editor.Focus();
        }

        private void OnInsertRequested(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var area = Editor.TextArea;
            var doc = Editor.Document;
            if (!area.Selection.IsEmpty) area.Selection.ReplaceSelectionWithText(text);
            else
            {
                int at = area.Caret.Offset;
                // keep words apart: "FROM" + name → "FROM name"
                string before = at > 0 ? doc.GetText(at - 1, 1) : " ";
                string pad = char.IsLetterOrDigit(before[0]) || before[0] == '_' || before[0] == '`' ? " " : "";
                doc.Insert(at, pad + text);
            }
            Editor.Focus();
        }

        /// <summary>From the Snippets menu: written at the caret, replacing any selection.</summary>
        private void OnSnippetRequested(SqlSnippet s)
        {
            var area = Editor.TextArea;
            _completion?.Close();
            Editor.Focus();
            var doc = Editor.Document;
            var sel = area.Selection;
            int offset = area.Caret.Offset, length = 0;
            if (!sel.IsEmpty && sel.Segments.Count() == 1) { offset = sel.SurroundingSegment.Offset; length = sel.SurroundingSegment.Length; }
            using (doc.RunUpdate())
            {
                // a whole statement gets its own line: break before and after if other SQL shares the line
                var line = doc.GetLineByOffset(offset);
                int endOffset = offset + length;
                var lineEnd = doc.GetLineByOffset(endOffset);
                bool textAfter = doc.GetText(endOffset, lineEnd.EndOffset - endOffset).Trim().Length > 0;
                bool textBefore = doc.GetText(line.Offset, offset - line.Offset).Trim().Length > 0;
                if (textAfter) doc.Insert(endOffset, "\n");
                if (textBefore) { doc.Insert(offset, "\n"); offset++; }
                SnippetWriter.Write(area, s, offset, length);
            }
        }

        /// <summary>Tab right after a snippet word ("sel|") writes the snippet. Returns false if the word isn't one.</summary>
        private bool TryExpandSnippet()
        {
            var area = Editor.TextArea;
            if (!area.Selection.IsEmpty) return false;
            int caret = area.Caret.Offset;
            var doc = Editor.Document;
            if (caret < doc.TextLength && IsWord(doc.GetCharAt(caret))) return false;   // in the middle of a word
            int start = WordStart(caret);
            if (caret - start < 2 || (start > 0 && (doc.GetCharAt(start - 1) == '.' || doc.GetCharAt(start - 1) == '`' || doc.GetCharAt(start - 1) == '@'))) return false;
            if (InsideTextOrComment(start)) return false;
            var s = _vm.FindSnippet(doc.GetText(start, caret - start));
            if (s == null) return false;
            SnippetWriter.Write(area, s, start, caret - start);
            return true;
        }

        private void OnKeys(object sender, KeyEventArgs e)
        {
            if (_vm == null) return;
            var mods = Keyboard.Modifiers;
            if (e.Key == Key.Tab && (mods == ModifierKeys.None || mods == ModifierKeys.Shift) && _completion == null && Editor.TextArea.IsKeyboardFocusWithin)
            {
                if (SnippetWriter.HandleTab(Editor.TextArea, mods == ModifierKeys.Shift)) { e.Handled = true; return; }
                if (mods == ModifierKeys.None && TryExpandSnippet()) { e.Handled = true; return; }
            }
            if (e.Key == Key.Enter && mods == ModifierKeys.Control)
            {
                _completion?.Close();
                Execute(_vm.RunCommand);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && mods == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                _completion?.Close();
                Execute(_vm.RunAllCommand);
                e.Handled = true;
            }
            else if (e.Key == Key.Space && mods == ModifierKeys.Control && Editor.IsKeyboardFocusWithin)
            {
                ShowCompletion(force: true);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _vm.IsExecuting && _completion == null)
            {
                Execute(_vm.StopCommand);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && mods == ModifierKeys.None && _vm.IsFocus && _completion == null
                     && SnippetWriter.Current(Editor.TextArea) == null && !(Keyboard.FocusedElement is TextBox))
            {
                _vm.IsFocus = false;   // the search box (a TextBox) and snippet fields keep Esc for themselves
                e.Handled = true;
            }
        }

        private void Execute(ICommand c)
        {
            if (_vm == null) return;
            _vm.CaretOffset = Editor.TextArea.Caret.Offset;
            if (c.CanExecute(null)) c.Execute(null);
        }

        // ------------------------------------------------------------------ completion

        private static bool IsWord(char ch) => char.IsLetterOrDigit(ch) || ch == '_' || ch == '$';

        private void OnTextEntering(object sender, TextCompositionEventArgs e)
        {
            if (_completion == null || e.Text.Length == 0) return;
            if (!IsWord(e.Text[0])) _completion.Close();   // typing on (space, comma, bracket) keeps exactly what was typed
        }

        private void OnTextEntered(object sender, TextCompositionEventArgs e)
        {
            if (_completion != null || e.Text.Length != 1) return;
            char ch = e.Text[0];
            if (ch == '.') ShowCompletion(force: false);
            else if (IsWord(ch))
            {
                int caret = Editor.TextArea.Caret.Offset;
                int start = WordStart(caret);
                if (caret - start == 1 && !char.IsDigit(Editor.Document.GetCharAt(start)) && !InsideTextOrComment(start)) ShowCompletion(force: false);
            }
        }

        private int WordStart(int caret)
        {
            var doc = Editor.Document;
            int i = caret;
            while (i > 0 && IsWord(doc.GetCharAt(i - 1))) i--;
            return i;
        }

        private bool InsideTextOrComment(int offset)
        {
            var line = Editor.Document.GetLineByOffset(offset);
            var text = Editor.Document.GetText(line.Offset, offset - line.Offset);
            if (text.Contains("--") || text.Contains("#")) return true;
            int single = text.Count(c => c == '\''), dbl = text.Count(c => c == '"');
            return single % 2 == 1 || dbl % 2 == 1;
        }

        private void ShowCompletion(bool force)
        {
            if (_vm == null) return;
            var doc = Editor.Document;
            int caret = Editor.TextArea.Caret.Offset;
            int start = WordStart(caret);
            string qualifier = null;
            if (start > 0 && doc.GetCharAt(start - 1) == '.')
            {
                int q = start - 1, qs = q;
                if (qs > 0 && doc.GetCharAt(qs - 1) == '`')
                {
                    int open = doc.Text.LastIndexOf('`', qs - 2);
                    if (open >= 0) qualifier = doc.GetText(open + 1, qs - 2 - open);
                }
                else
                {
                    while (qs > 0 && IsWord(doc.GetCharAt(qs - 1))) qs--;
                    qualifier = doc.GetText(qs, q - qs);
                }
                if (string.IsNullOrEmpty(qualifier)) return;
            }
            else if (!force && caret - start < 1) return;

            _vm.CaretOffset = caret;
            _vm.Analyse();
            var items = _vm.Completions(qualifier).OrderByDescending(x => x.priority).ThenBy(x => x.text, StringComparer.OrdinalIgnoreCase).ToList();
            if (items.Count == 0) return;

            _completion?.Close();
            var w = new CompletionWindow(Editor.TextArea)
            {
                StartOffset = start,
                EndOffset = caret,
                Background = Ledger.Brush("Paper3"),
                BorderBrush = Ledger.Brush("Ink"),
                BorderThickness = new Thickness(1.5),
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight,
                MaxHeight = 300,
                CloseWhenCaretAtBeginning = true,
                CloseAutomatically = true,
            };
            w.CompletionList.ListBox.Background = Ledger.Brush("Paper3");
            w.CompletionList.ListBox.BorderThickness = new Thickness(0);
            w.CompletionList.ListBox.FontFamily = (FontFamily)FindResource("FontMono");
            w.CompletionList.ListBox.FontSize = 12.5;
            w.CompletionList.ListBox.Foreground = Ledger.Brush("Ink");
            w.CompletionList.ListBox.ItemContainerStyle = CompletionRowStyle();
            // AvalonEdit's own filter also lists words that merely contain the letters (TO_BASE64 for "se") and prefers
            // lowercase snippet words over keywords; we list only words that start with what was typed, in our order.
            w.CompletionList.IsFiltering = false;
            var all = new List<ICompletionData>();
            foreach (var (text, kind, detail, priority) in items)
            {
                var snippet = kind == "snippet" ? _vm.FindSnippet(text) : null;
                if (snippet != null) all.Add(new SnippetCompletion(snippet, priority));
                else if (kind != "snippet") all.Add(new SqlCompletion(text, kind, detail, priority));
            }
            EventHandler refilter = (s, e) => Refilter(w, all);
            Editor.TextArea.Caret.PositionChanged += refilter;   // after the window's own handler, so ours has the last word
            w.Closed += (s, e) =>
            {
                Editor.TextArea.Caret.PositionChanged -= refilter;
                if (_completion == w) _completion = null;
            };
            _completion = w;
            w.Show();
            Refilter(w, all);
        }

        /// <summary>Shows only the words starting with what's typed; picks an exact match, else the first. Nothing left: closes.</summary>
        private void Refilter(CompletionWindow w, List<ICompletionData> all)
        {
            if (_completion != w) return;
            int caret = Editor.TextArea.Caret.Offset;
            if (caret < w.StartOffset || caret > w.EndOffset) return;
            string typed = Editor.Document.GetText(w.StartOffset, caret - w.StartOffset);
            var hits = all.Where(d => d.Text.StartsWith(typed, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 0) { w.Close(); return; }
            var data = w.CompletionList.CompletionData;
            if (!data.SequenceEqual(hits))
            {
                data.Clear();
                foreach (var h in hits) data.Add(h);
            }
            var pick = hits.FirstOrDefault(d => string.Equals(d.Text, typed, StringComparison.OrdinalIgnoreCase)) ?? hits[0];
            w.CompletionList.SelectedItem = pick;
            w.CompletionList.ListBox.ScrollIntoView(pick);
        }

        private static Style _rowStyle;

        /// <summary>List rows in Ledger colours: the chosen row is marked like a highlighter pen with an ink bar, so every colour on it stays readable.</summary>
        private static Style CompletionRowStyle()
        {
            if (_rowStyle != null) return _rowStyle;
            var row = new FrameworkElementFactory(typeof(Border), "row");
            row.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            row.SetValue(Border.BorderBrushProperty, Brushes.Transparent);
            row.SetValue(Border.BorderThicknessProperty, new Thickness(3, 0, 0, 0));
            row.SetValue(Border.PaddingProperty, new Thickness(7, 2, 8, 2));
            row.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            var template = new ControlTemplate(typeof(ListBoxItem)) { VisualTree = row };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, Ledger.Brush("Paper2"), "row"));
            var chosen = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            chosen.Setters.Add(new Setter(Border.BackgroundProperty, Ledger.Brush("AmberSoft"), "row"));
            chosen.Setters.Add(new Setter(Border.BorderBrushProperty, Ledger.Brush("Ink"), "row"));
            template.Triggers.Add(hover);
            template.Triggers.Add(chosen);   // later trigger wins: chosen beats hover
            var style = new Style(typeof(ListBoxItem));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            style.Seal();
            return _rowStyle = style;
        }

        // ------------------------------------------------------------------ live checks: underlines + margin notes

        private void OnAnalysisChanged()
        {
            if (_vm == null) return;
            _underlines.SetIssues(_vm.Issues);
            Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            BuildNotes();
        }

        private void BuildNotes()
        {
            NotesCanvas.Children.Clear();
            _notes.Clear();
            if (_vm == null) return;
            foreach (var n in _vm.Notes())
            {
                var view = NoteView(n);
                view.Visibility = Visibility.Collapsed;   // shown once it has a place beside its line
                NotesCanvas.Children.Add(view);
                _notes.Add((n, view));
            }
            NotesHeader.Text = _notes.Count == 0 ? "MARGIN · nothing to correct" : "";
            PlaceNotes();
        }

        private FrameworkElement NoteView(MarginNote n)
        {
            var ink = Ledger.Brush(n.IsWarning ? "Amber" : "Red");
            var panel = new StackPanel();
            var row = new DockPanel();
            var arrow = new Path
            {
                Data = (Geometry)FindResource("IconArrow"), Stroke = ink, StrokeThickness = 1.4, Stretch = Stretch.Uniform,
                Width = 22, Height = 10, Margin = new Thickness(0, 7, 6, 0), VerticalAlignment = VerticalAlignment.Top,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            };
            DockPanel.SetDock(arrow, Dock.Left);
            row.Children.Add(arrow);
            var text = new TextBlock
            {
                Text = n.Text, FontFamily = (FontFamily)FindResource("FontNote"), FontStyle = FontStyles.Italic, FontSize = 17,
                Foreground = ink, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Cursor = Cursors.Hand,
                ToolTip = "Click to select the spot in the SQL",
            };
            TextOptions.SetTextFormattingMode(text, TextFormattingMode.Ideal);
            text.MouseLeftButtonUp += (s, e) => n.GoToCommand.Execute(null);
            row.Children.Add(text);
            panel.Children.Add(row);
            if (n.HasFix)
            {
                var fix = new Button
                {
                    Content = "Fix: " + n.FixLabel, Style = (Style)FindResource("TinyButton"), Foreground = ink,
                    FontFamily = (FontFamily)FindResource("FontMonoMedium"), FontSize = 11, Margin = new Thickness(26, 2, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left, Command = n.FixCommand,
                };
                panel.Children.Add(fix);
            }
            return panel;
        }

        /// <summary>Each note sits beside its line; notes never overlap and leave when their line scrolls away.</summary>
        private void PlaceNotes()
        {
            if (_notes.Count == 0 || !IsLoaded) return;
            var tv = Editor.TextArea.TextView;
            if (!tv.VisualLinesValid) { Dispatcher.BeginInvoke(new Action(PlaceNotes), System.Windows.Threading.DispatcherPriority.Render); return; }
            double width = Math.Max(60, NotesCanvas.ActualWidth - 16);
            double height = NotesCanvas.ActualHeight;
            double next = 4;
            var doc = Editor.Document;
            foreach (var (note, view) in _notes)
            {
                if (note.Issue.Start > doc.TextLength) { view.Visibility = Visibility.Collapsed; continue; }
                int line = doc.GetLineByOffset(note.Issue.Start).LineNumber;
                double top = tv.GetVisualTopByDocumentLine(line) - tv.VerticalOffset;
                var p = tv.TranslatePoint(new Point(0, top), NotesCanvas);
                view.Width = width;
                view.Visibility = Visibility.Visible;   // a collapsed element measures as zero height
                view.Measure(new Size(width, double.PositiveInfinity));
                double h = view.DesiredSize.Height;
                double y = Math.Max(p.Y - 2, next);
                bool visible = p.Y + tv.DefaultLineHeight > 0 && y < height - 20;
                view.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                if (!visible) continue;
                Canvas.SetLeft(view, 8);
                Canvas.SetTop(view, y);
                next = y + h + NoteGap;
            }
        }

        // ------------------------------------------------------------------ results grid

        private void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(NotebookViewModel.SelectedResult): ShowResult(_vm.SelectedResult); break;
                case nameof(NotebookViewModel.ShowIndex):
                case nameof(NotebookViewModel.ShowReceipt): ApplyPanels(animate: true); break;
                case nameof(NotebookViewModel.IsFocus):
                case nameof(NotebookViewModel.Layout): ApplyLayout(animate: true); break;
            }
        }

        private void ShowResult(ResultTab tab)
        {
            ResultGrid.ItemsSource = null;
            ResultGrid.Columns.Clear();
            if (tab == null) return;
                        foreach (var col in tab.Table.Columns)
            {
                ResultGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = new GridHeader(col.Name, col.TypeName),
                    HeaderTemplate = GridKit.Header,
                    CellTemplate = GridKit.Cell(col.Key, col.IsNumeric),
                    SortMemberPath = col.SortKey ?? col.Key,
                    CanUserSort = true,
                    ClipboardContentBinding = new Binding("[" + col.Key + "]"),
                    MaxWidth = 420,
                });
            }
            ResultGrid.ItemsSource = tab.Table.Rows.DefaultView;
            if (ResultGrid.Items.Count > 0) ResultGrid.ScrollIntoView(ResultGrid.Items[0]);
        }

        // ------------------------------------------------------------------ index, menus, receipts

        private void OnNameDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.Tag is string name) || _vm == null) return;
            _vm.InsertNameCommand.Execute(SqlLexer.IsPlainName(name) ? name : SqlLexer.Quote(name));
            e.Handled = true;
        }

        private void OnColumnClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2 || !(sender is FrameworkElement fe) || !(fe.Tag is string name) || _vm == null) return;
            _vm.InsertNameCommand.Execute(SqlLexer.IsPlainName(name) ? name : SqlLexer.Quote(name));
            e.Handled = true;
        }

        private void OnReceiptClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is NotebookReceipt r) _vm?.GoToReceiptCommand.Execute(r);
        }

        private static MenuItem GroupHeader(string text) => new MenuItem
        {
            Header = new TextBlock { Text = text, FontSize = 10.5, FontFamily = (FontFamily)Application.Current.FindResource("FontMonoMedium") },
            IsEnabled = false,
        };

        private static ContextMenu MenuUnder(object sender) =>
            new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };

        private void FillExamples(ItemCollection items)
        {
            string group = null;
            foreach (var ex in _vm.Examples)
            {
                if (ex.Group != group)
                {
                    if (group != null) items.Add(new Separator());
                    group = ex.Group;
                    items.Add(GroupHeader(group == "Mistake" ? "SEE A MISTAKE CAUGHT" : "LEARN"));
                }
                items.Add(new MenuItem { Header = ex.Title, Command = _vm.ExampleCommand, CommandParameter = ex });
            }
        }

        private void FillSnippets(ItemCollection items)
        {
            items.Add(GroupHeader("TYPE THE WORD, THEN TAB"));
            foreach (var s in _vm.Snippets.Where(x => x.IsBuiltIn)) items.Add(SnippetItem(s));
            var mine = _vm.MySnippets;
            items.Add(new Separator());
            items.Add(GroupHeader("YOURS"));
            if (mine.Count == 0) items.Add(new MenuItem { Header = "None yet: select some SQL and save it below", IsEnabled = false });
            foreach (var s in mine) items.Add(SnippetItem(s));
            items.Add(new Separator());
            items.Add(new MenuItem
            {
                Header = new TextBlock { Text = _vm.HasSelection ? "Save the selected SQL as a snippet…" : "New snippet…" },
                Command = _vm.SaveSnippetCommand,
            });
            if (mine.Count > 0)
            {
                var delete = new MenuItem { Header = "Delete one of yours" };
                foreach (var s in mine) delete.Items.Add(new MenuItem { Header = new TextBlock { Text = s.Trigger + "  ·  " + s.Title }, Command = _vm.DeleteSnippetCommand, CommandParameter = s });
                items.Add(delete);
            }
        }

        private void OnExamplesMenu(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var menu = MenuUnder(sender);
            FillExamples(menu.Items);
            menu.Closed += (s, a) => Editor.Focus();
            menu.IsOpen = true;
        }

        private void OnSnippetsMenu(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var menu = MenuUnder(sender);
            FillSnippets(menu.Items);
            menu.Closed += (s, a) => { if (!(_vm?.HasSlipOpen ?? false)) Editor.Focus(); };
            menu.IsOpen = true;
        }

        /// <summary>Focus mode keeps the toolbar short: the rarer tools wait here.</summary>
        private void OnMoreMenu(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var menu = MenuUnder(sender);
            var examples = new MenuItem { Header = "Examples" };
            FillExamples(examples.Items);
            var snippets = new MenuItem { Header = "Snippets" };
            FillSnippets(snippets.Items);
            menu.Items.Add(examples);
            menu.Items.Add(snippets);
            menu.Items.Add(new MenuItem { Header = "Undo change", Command = _vm.UndoCommand });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Table list", IsChecked = _vm.IsIndexPinned, Command = _vm.ToggleIndexCommand });
            menu.Items.Add(new MenuItem { Header = "Receipt", IsChecked = _vm.IsReceiptPinned, Command = _vm.ToggleReceiptCommand });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Leave focus", InputGestureText = "Esc", Command = _vm.ToggleFocusCommand });
            menu.Closed += (s, a) => { if (!(_vm?.HasSlipOpen ?? false)) Editor.Focus(); };
            menu.IsOpen = true;
        }

        private MenuItem SnippetItem(SqlSnippet s)
        {
            // a TextBlock header, so _ in a word isn't eaten as an access key
            var item = new MenuItem
            {
                Header = new TextBlock { Text = s.Title },
                InputGestureText = s.Trigger,
                Command = _vm.InsertSnippetCommand,
                CommandParameter = s,
                ToolTip = new TextBlock { Text = s.Preview, FontFamily = (FontFamily)FindResource("FontMono"), FontSize = 12 },
            };
            return item;
        }

        private void OnDatabaseMenu(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var menu = MenuUnder(sender);
            menu.Items.Add(GroupHeader("USE DATABASE"));
            var names = _vm.DatabaseNames.Where(d => !SchemaSnapshot.IsSystem(d)).ToList();
            var system = _vm.DatabaseNames.Where(SchemaSnapshot.IsSystem).ToList();
            if (names.Count == 0)
                menu.Items.Add(new MenuItem { Header = "No databases yet: CREATE DATABASE makes one", IsEnabled = false });
            foreach (var d in names)
                menu.Items.Add(new MenuItem { Header = d, IsChecked = string.Equals(d, _vm.CurrentDatabase, StringComparison.OrdinalIgnoreCase), Command = _vm.UseDatabaseCommand, CommandParameter = d });
            if (system.Count > 0)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(GroupHeader("MARIADB'S OWN (READ WITH CARE)"));
                foreach (var d in system)
                    menu.Items.Add(new MenuItem { Header = d, IsChecked = string.Equals(d, _vm.CurrentDatabase, StringComparison.OrdinalIgnoreCase), Command = _vm.UseDatabaseCommand, CommandParameter = d });
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Reload the list", InputGestureText = "Ctrl+R", Command = _vm.RefreshSchemaCommand });
            menu.Closed += (s, a) => Editor.Focus();
            menu.IsOpen = true;
        }

        // ------------------------------------------------------------------ responsive

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = e.NewSize.Width;
            ApplyPanels(animate: false);
            NotesCol.Width = new GridLength(w < 1000 ? 180 : 230);
            PlaceNotes();
        }

        // ------------------------------------------------------------------ focus mode: side panels slide, three views

        private const double EditorMin = 140, ResultsMin = 110, PanelGap = 18;
        private bool? _indexOn, _receiptOn;
        private string _appliedLayout;
        private double _dragStartY, _dragEditor, _dragTotal;
        private bool _dragging;

        /// <summary>The table list and the receipt: wanted (not in focus, or pinned) and, for the receipt, room for it.</summary>
        private void ApplyPanels(bool animate)
        {
            double w = ActualWidth;
            bool index = _vm?.ShowIndex ?? true;
            bool receipt = (_vm?.ShowReceipt ?? true) && w >= 1180;
            Slide(IndexCol, IndexPanel, index, w < 960 ? 170 : 210, HorizontalAlignment.Right, ref _indexOn, animate);
            Slide(ReceiptCol, ReceiptPanel, receipt, 250, HorizontalAlignment.Left, ref _receiptOn, animate);
        }

        /// <summary>
        /// Opens or closes a side column. While it moves, the panel keeps its full width and is cut by the column,
        /// so it slides in and out from its edge instead of squeezing its text.
        /// </summary>
        private static void Slide(ColumnDefinition col, FrameworkElement panel, bool on, double width, HorizontalAlignment edge, ref bool? state, bool animate)
        {
            bool changed = state != on;
            state = on;
            double to = on ? width : 0;
            if (!changed)
            {
                if (Motion.IsRunning(col)) return;   // a window resize mid-slide: let the slide land first
                col.Width = new GridLength(to);
                return;
            }
            double from = col.ActualWidth;
            panel.Visibility = Visibility.Visible;
            panel.Width = Math.Max(0, width - PanelGap);
            panel.HorizontalAlignment = edge;
            Motion.Run(col, from, to,
                v => { col.Width = new GridLength(v); panel.Opacity = width <= 0 ? 1 : Math.Min(1, v / width); },
                () =>
                {
                    panel.Width = double.NaN;
                    panel.HorizontalAlignment = HorizontalAlignment.Stretch;
                    panel.Opacity = 1;
                    panel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                },
                animate ? Motion.Duration : 0);
        }

        /// <summary>Out of focus the sheet is always split; in focus it's the chosen view.</summary>
        private string EffectiveLayout => _vm == null || !_vm.IsFocus ? "split" : _vm.Layout;

        private void ApplyLayout(bool animate, bool force = false)
        {
            if (_vm == null) return;
            string layout = EffectiveLayout;
            double ratio = _vm.SplitRatio;
            bool changed = _appliedLayout != layout;
            _appliedLayout = layout;
            double e0 = EditorRow.ActualHeight, r0 = ResultsRow.ActualHeight, total = e0 + r0;
            if (!animate || (!changed && !force) || total < 1 || !IsLoaded)
            {
                if (!Motion.IsRunning(EditorRow) || !animate) { Motion.Run(EditorRow, 0, 0, v => { }); SetRows(layout, ratio); }
                return;
            }
            // Tween the editor's share, not pixels: the window's header may be folding at the same moment, and star
            // rows follow the height they're given on every frame.
            double f0 = e0 / total;
            double f1 = layout == "editor" ? 1 : layout == "results" ? 0 : ratio;
            if (layout == "split") f1 = Math.Max(Math.Min(EditorMin, total / 2) / total, Math.Min(f1, 1 - Math.Min(ResultsMin, total / 2) / total));
            EditorRow.MinHeight = 0;
            ResultsRow.MinHeight = 0;
            EditorBox.Visibility = Visibility.Visible;
            ResultsArea.Visibility = Visibility.Visible;
            Motion.Run(EditorRow, f0 * 1000, f1 * 1000, v =>
            {
                double f = Math.Max(0, Math.Min(1, v / 1000));
                EditorRow.Height = new GridLength(f, GridUnitType.Star);
                ResultsRow.Height = new GridLength(1 - f, GridUnitType.Star);
            }, () =>
            {
                SetRows(layout, ratio);
                if (layout != "results" && IsKeyboardFocusWithin == false) Editor.Focus();
            });
        }

        /// <summary>The resting state: stars for split (so the window can resize), one row only for the others.</summary>
        private void SetRows(string layout, double ratio)
        {
            bool editor = layout != "results", results = layout != "editor";
            bool split = layout == "split";
            EditorRow.MinHeight = split ? EditorMin : 0;
            ResultsRow.MinHeight = split ? ResultsMin : 0;
            EditorRow.Height = editor ? new GridLength(split ? ratio : 1, GridUnitType.Star) : new GridLength(0);
            ResultsRow.Height = results ? new GridLength(split ? 1 - ratio : 1, GridUnitType.Star) : new GridLength(0);
            EditorBox.Visibility = editor ? Visibility.Visible : Visibility.Collapsed;
            ResultsArea.Visibility = results ? Visibility.Visible : Visibility.Collapsed;
            if (editor) PlaceNotes();
        }

        private void OnSplitterDragStarted(object sender, DragStartedEventArgs e)
        {
            if (Motion.IsRunning(EditorRow)) { _dragging = false; return; }
            _dragging = true;
            _dragStartY = Mouse.GetPosition(Sheet).Y;
            _dragEditor = EditorRow.ActualHeight;
            _dragTotal = EditorRow.ActualHeight + ResultsRow.ActualHeight;
        }

        private void OnSplitterDragDelta(object sender, DragDeltaEventArgs e)
        {
            if (!_dragging || _dragTotal < 1) return;
            double dy = Mouse.GetPosition(Sheet).Y - _dragStartY;
            if (Math.Abs(dy) < 2) return;
            bool focus = _vm?.IsFocus ?? false;
            // out of focus both keep their minimum; in focus either can be pushed shut (it snaps to that view on release)
            double min = focus ? 0 : Math.Min(EditorMin, _dragTotal / 2), max = focus ? _dragTotal : _dragTotal - Math.Min(ResultsMin, _dragTotal / 2);
            double h = Math.Max(min, Math.Min(max, _dragEditor + dy));
            EditorRow.MinHeight = 0;
            ResultsRow.MinHeight = 0;
            EditorBox.Visibility = Visibility.Visible;
            ResultsArea.Visibility = Visibility.Visible;
            EditorRow.Height = new GridLength(h);
            ResultsRow.Height = new GridLength(_dragTotal - h);
        }

        private void OnSplitterDragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (!_dragging || _vm == null) return;
            _dragging = false;
            double total = EditorRow.ActualHeight + ResultsRow.ActualHeight;
            if (Math.Abs(Mouse.GetPosition(Sheet).Y - _dragStartY) < 2 || total < 1)
            {
                if (!Motion.IsRunning(EditorRow)) SetRows(EffectiveLayout, _vm.SplitRatio);   // a click, not a drag
                return;
            }
            double ratio = EditorRow.ActualHeight / total;
            if (_vm.IsFocus && ratio < 0.12) _vm.Layout = "results";
            else if (_vm.IsFocus && ratio > 0.9) _vm.Layout = "editor";
            else
            {
                _vm.SplitRatio = ratio;   // clamped to 20–85 %
                _vm.Layout = "split";
            }
            _appliedLayout = null;        // land on the result from where the drag left it
            ApplyLayout(animate: true, force: true);
        }

        private void OnSplitterDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_vm == null) return;
            if (_vm.IsFocus) _vm.CycleLayout();
            else
            {
                _vm.SplitRatio = 0.55;
                _appliedLayout = null;
                ApplyLayout(animate: true, force: true);
            }
            e.Handled = true;
        }

        /// <summary>
        /// The toolbar's own width decides (not the window's: the side panels come and go): full labels when they fit,
        /// then no Ctrl+Enter hint and "Undo", then "Tidy". Below that the buttons wrap to a second row.
        /// </summary>
        private void OnToolStripSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged) return;
            double w = e.NewSize.Width;
            RunHint.Visibility = w < 780 ? Visibility.Collapsed : Visibility.Visible;
            UndoLabel.Text = w < 780 ? "Undo" : "Undo change";
            TidyLabel.Text = w < 640 ? "Tidy" : "Tidy up";
        }
    }
}
