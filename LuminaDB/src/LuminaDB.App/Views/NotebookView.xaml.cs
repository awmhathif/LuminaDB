using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Shapes;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;
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
            SqlEditorSetup.Apply(Editor, _underlines);
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
                _completion?.Close();
            }
            _vm = vm;
            if (vm == null) return;
            Editor.Document = vm.Document;
            vm.SelectRequested += OnSelectRequested;
            vm.InsertRequested += OnInsertRequested;
            vm.AnalysisChanged += OnAnalysisChanged;
            vm.PropertyChanged += OnVmChanged;
            int caret = Math.Min(vm.CaretOffset, vm.Document.TextLength);
            _syncingCaret = true;
            try { Editor.TextArea.Caret.Offset = caret; }
            finally { _syncingCaret = false; }
            Editor.ScrollTo(Editor.TextArea.Caret.Line, 1);
            OnAnalysisChanged();
            ShowResult(vm.SelectedResult);
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

        private void OnKeys(object sender, KeyEventArgs e)
        {
            if (_vm == null) return;
            var mods = Keyboard.Modifiers;
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
                if (caret - start == 2 && !char.IsDigit(Editor.Document.GetCharAt(start)) && !InsideTextOrComment(start)) ShowCompletion(force: false);
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
            else if (!force && caret - start < 2) return;

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
            w.CompletionList.IsFiltering = true;
            foreach (var (text, kind, detail, priority) in items)
                w.CompletionList.CompletionData.Add(new SqlCompletion(text, kind, detail, priority));
            w.Closed += (s, e) => { if (_completion == w) _completion = null; };
            _completion = w;
            w.Show();
            if (caret > start) w.CompletionList.SelectItem(doc.GetText(start, caret - start));
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
            if (e.PropertyName == nameof(NotebookViewModel.SelectedResult)) ShowResult(_vm.SelectedResult);
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

        private void OnExamplesMenu(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            string group = null;
            foreach (var ex in _vm.Examples)
            {
                if (ex.Group != group)
                {
                    if (group != null) menu.Items.Add(new Separator());
                    group = ex.Group;
                    menu.Items.Add(GroupHeader(group == "Mistake" ? "SEE A MISTAKE CAUGHT" : "LEARN"));
                }
                menu.Items.Add(new MenuItem { Header = ex.Title, Command = _vm.ExampleCommand, CommandParameter = ex });
            }
            menu.Closed += (s, a) => Editor.Focus();
            menu.IsOpen = true;
        }

        private void OnDatabaseMenu(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
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
            bool receipt = w >= 1180;
            ReceiptPanel.Visibility = receipt ? Visibility.Visible : Visibility.Collapsed;
            ReceiptCol.Width = receipt ? new GridLength(250) : new GridLength(0);
            IndexCol.Width = new GridLength(w < 960 ? 170 : 210);
            NotesCol.Width = new GridLength(w < 1000 ? 180 : 230);
            // narrow windows: shorter toolbar labels so the Safe updates switch always shows
            bool narrow = w < 1120;
            RunHint.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            UndoLabel.Text = narrow ? "Undo" : "Undo change";
            TidyLabel.Text = w < 960 ? "Tidy" : "Tidy up";
            PlaceNotes();
        }
    }
}
