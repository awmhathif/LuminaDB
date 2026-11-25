using System;
using System.ComponentModel;
using System.Data;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;
using LuminaDB.App.Editor;
using LuminaDB.App.ViewModels;
using LuminaDB.Core.Sql;
using LuminaDB.Core.Tables;

namespace LuminaDB.App.Views
{
    public partial class TablesView : UserControl
    {
        private readonly IssueUnderlines _underlines = new IssueUnderlines();
        private TablesViewModel _vm;
        private RowsPage _rows;
        private ObjectEditor _def;

        public TablesView()
        {
            InitializeComponent();
            SqlEditorSetup.Apply(DefEditor, _underlines);
            DataContextChanged += (s, e) => Attach(e.NewValue as TablesViewModel);
            Unloaded += (s, e) => Attach(null);
            Loaded += (s, e) => { if (_vm == null) Attach(DataContext as TablesViewModel); };
            PreviewKeyDown += OnKeys;
            RowsGrid.AutoGenerateColumns = false;
            RowsGrid.IsReadOnly = true;
            RowsGrid.CanUserAddRows = false;
            RowsGrid.CanUserDeleteRows = false;
        }

        private void Attach(TablesViewModel vm)
        {
            if (_vm == vm) return;
            if (_rows != null) _rows.DataReloaded -= ShowRows;
            if (_def != null)
            {
                _def.AnalysisChanged -= OnAnalysisChanged;
                _def.SelectRequested -= OnSelectRequested;
            }
            _vm = vm;
            _rows = vm?.Rows;
            _def = vm?.Definition;
            if (vm == null) return;
            _rows.DataReloaded += ShowRows;
            _def.AnalysisChanged += OnAnalysisChanged;
            _def.SelectRequested += OnSelectRequested;
            DefEditor.Document = _def.Document;
            ShowRows();
            OnAnalysisChanged();
        }

        // ------------------------------------------------------------------ rows grid

        private void ShowRows()
        {
            if (_rows == null) return;
            RowsGrid.ItemsSource = null;
            RowsGrid.Columns.Clear();
            if (_rows.Data == null) return;
            foreach (var col in _rows.Columns)
            {
                string sort = string.Equals(_rows.SortColumn, col.Name, StringComparison.OrdinalIgnoreCase) ? (_rows.SortDescending ? " ↓" : " ↑") : "";
                RowsGrid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = new GridHeader(col.Name, col.TypeName + (col.IsKey ? " · key" : "")) { Sort = sort },
                    HeaderTemplate = GridKit.Header,
                    CellTemplate = GridKit.Cell(col.Key, col.IsNumeric),
                    SortMemberPath = col.Name,
                    CanUserSort = true,
                    ClipboardContentBinding = new Binding("[" + col.Key + "]"),
                    MaxWidth = 380,
                });
            }
            RowsGrid.ItemsSource = _rows.Data.DefaultView;
        }

        private async void OnRowsSorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true;   // sorting happens on the server, across every page
            if (_rows == null || !(e.Column.SortMemberPath is string name)) return;
            try { await _rows.SortByAsync(name); }
            catch (Exception ex) { AppLog.Error("sort failed", ex); }
        }

        private void OnRowsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_rows == null) return;
            _rows.SetSelection(RowsGrid.SelectedItems.OfType<DataRowView>().Select(_rows.KeyOf).Where(k => k != null));
        }

        private void OnRowsDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_rows == null) return;
            var row = ItemsControl.ContainerFromElement(RowsGrid, e.OriginalSource as DependencyObject) as DataGridRow;
            if (row?.Item is DataRowView view)
            {
                var key = _rows.KeyOf(view);
                if (key != null && _rows.EditRowCommand.CanExecute(key)) _rows.EditRowCommand.Execute(key);
                e.Handled = true;
            }
        }

        private void OnRowsKey(object sender, KeyEventArgs e)
        {
            if (_rows == null) return;
            if (e.Key == Key.Delete && Keyboard.Modifiers == ModifierKeys.None) { Run(_rows.DeleteRowsCommand); e.Handled = true; }
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { Run(_rows.EditRowCommand); e.Handled = true; }
        }

        private void OnFilterKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && _rows != null) { Run(_rows.ApplyFiltersCommand); e.Handled = true; }
        }

        private void OnCalendarPicked(object sender, SelectionChangedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Parent is Border b && b.Parent is Popup p && p.Tag is ToggleButton t) t.IsChecked = false;
        }

        private void OnCalendarClosed(object sender, EventArgs e)
        {
            Mouse.Capture(null);   // the calendar keeps the mouse after a pick unless released
        }

        // ------------------------------------------------------------------ definition editor

        private void OnAnalysisChanged()
        {
            if (_def == null) return;
            _underlines.SetIssues(_def.Issues);
            DefEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
        }

        private void OnSelectRequested(int offset, int length)
        {
            var doc = DefEditor.Document;
            offset = Math.Max(0, Math.Min(offset, doc.TextLength));
            length = Math.Max(0, Math.Min(length, doc.TextLength - offset));
            DefEditor.Select(offset, length);
            DefEditor.TextArea.Caret.Offset = offset + length;
            var loc = doc.GetLocation(offset);
            DefEditor.ScrollTo(loc.Line, loc.Column);
            DefEditor.Focus();
        }

        private void OnNoteClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is MarginNote n) n.GoToCommand?.Execute(null);
        }

        // ------------------------------------------------------------------ keys

        private void OnKeys(object sender, KeyEventArgs e)
        {
            if (_vm == null) return;
            var mods = Keyboard.Modifiers;
            if (e.Key == Key.S && mods == ModifierKeys.Control)
            {
                if (_vm.IsRowsPage && _rows.HasForm) Run(_rows.Form.SaveCommand);
                else if (_vm.IsStructurePage) Run(_vm.Structure.ApplyCommand);
                else if (_vm.IsDefinitionPage) Run(_def.SaveCommand);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _vm.IsRowsPage && _rows.HasForm)
            {
                Run(_rows.Form.CancelCommand);
                e.Handled = true;
            }
            else if (e.Key == Key.F && mods == ModifierKeys.Control && _vm.IsRowsPage)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
        }

        private static void Run(ICommand c)
        {
            if (c != null && c.CanExecute(null)) c.Execute(null);
        }

        // ------------------------------------------------------------------ menus

        private static MenuItem GroupHeader(string text) => new MenuItem
        {
            Header = new TextBlock { Text = text, FontSize = 11, FontFamily = (FontFamily)Application.Current.FindResource("FontMonoMedium") },
            IsEnabled = false,
        };

        private void OnDatabaseMenu(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
            menu.Items.Add(GroupHeader("DATABASE"));
            var names = _vm.DatabaseNames.Where(d => !SchemaSnapshot.IsSystem(d)).ToList();
            var system = _vm.DatabaseNames.Where(SchemaSnapshot.IsSystem).ToList();
            if (names.Count == 0)
                menu.Items.Add(new MenuItem { Header = _vm.IsRunning ? "No databases yet: make one on Today" : "Start the server first", IsEnabled = false });
            foreach (var d in names)
                menu.Items.Add(new MenuItem { Header = d, IsChecked = string.Equals(d, _vm.Database, StringComparison.OrdinalIgnoreCase), Command = _vm.UseDatabaseCommand, CommandParameter = d });
            if (system.Count > 0)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(GroupHeader("MARIADB'S OWN (CHANGE WITH CARE)"));
                foreach (var d in system)
                    menu.Items.Add(new MenuItem { Header = d, IsChecked = string.Equals(d, _vm.Database, StringComparison.OrdinalIgnoreCase), Command = _vm.UseDatabaseCommand, CommandParameter = d });
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = "Reload the list", InputGestureText = "Ctrl+R", Command = _vm.RefreshCommand });
            menu.IsOpen = true;
        }

        private void OnNewObjectMenu(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
            menu.Items.Add(GroupHeader("NEW, FROM A TEMPLATE"));
            void Add(string label, DbObjectKind kind, string hint) =>
                menu.Items.Add(new MenuItem { Header = label, ToolTip = hint, Command = _vm.NewObjectCommand, CommandParameter = kind });
            Add("View", DbObjectKind.View, "A saved SELECT you can read like a table");
            Add("Procedure", DbObjectKind.Procedure, "Steps you run with CALL");
            Add("Function", DbObjectKind.Function, "Returns one value, usable inside SELECT");
            Add("Trigger", DbObjectKind.Trigger, "Runs by itself when rows change");
            Add("Event", DbObjectKind.Event, "Runs by itself on a schedule");
            menu.IsOpen = true;
        }

        // ------------------------------------------------------------------ responsive

        private void OnSizeChanged(object sender, SizeChangedEventArgs e)
        {
            double w = e.NewSize.Width;
            IndexCol.Width = new GridLength(w < 980 ? 180 : 220);
            SqlCol.Width = new GridLength(w < 1100 ? 270 : 330);
            DefNotesCol.Width = new GridLength(w < 1050 ? 190 : 240);
            OpsPanel.ItemWidth = w < 1100 ? 360 : 400;
        }
    }
}
