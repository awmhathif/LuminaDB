using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using LuminaDB.Core;
using LuminaDB.Core.Diagram;
using LuminaDB.Core.Tables;
using MySqlConnector;

namespace LuminaDB.App.ViewModels
{
    /// <summary>A table card on the canvas.</summary>
    public sealed class DiagramCard : ObservableObject
    {
        private bool _selected;
        public DiagramCard(DiagramTable table) { Table = table; Rows = DiagramGeometry.Rows(table.Design); }

        public DiagramTable Table { get; }
        public string Name => Table.Name;
        public List<DiagramRow> Rows { get; }
        public double X => Table.X;
        public double Y => Table.Y;
        public double Width => Table.Width;
        public double Height => Table.Height;
        public DiagramTableState State => Table.State;
        public bool IsLive => State == DiagramTableState.Live;
        public bool IsNew => State == DiagramTableState.New;
        public bool IsChanged => State == DiagramTableState.Changed;
        public bool IsDropped => State == DiagramTableState.Dropped;
        public string StateText => IsNew ? "NEW" : IsChanged ? "CHANGED" : IsDropped ? "TO DROP" : "";
        public string Tip => Name + " · " + Rows.Count + (Rows.Count == 1 ? " column" : " columns")
                             + (Table.Design.RowEstimate > 0 ? " · about " + Table.Design.RowEstimate.ToString("N0", CultureInfo.InvariantCulture) + " rows" : "")
                             + (string.IsNullOrEmpty(Table.Design.Comment) ? "" : "\n" + Table.Design.Comment);
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }

        public void MoveTo(double x, double y)
        {
            Table.X = Math.Max(0, Math.Round(x));
            Table.Y = Math.Max(0, Math.Round(y));
            OnPropertyChanged(nameof(X));
            OnPropertyChanged(nameof(Y));
        }
    }

    /// <summary>A column in the diagram's table editor.</summary>
    public sealed class DiagramColumnRow : ObservableObject
    {
        private readonly DiagramViewModel _owner;
        private bool _isPk;

        public DiagramColumnRow(DiagramViewModel owner, ColumnDef c, bool isPk)
        {
            _owner = owner; Column = c; StartName = c.Name; _isPk = isPk;
            RemoveCommand = new RelayCommand(() => _owner.RemoveColumn(this));
        }

        public ColumnDef Column { get; }
        /// <summary>The name when editing began, to follow renames into keys and indexes.</summary>
        public string StartName { get; }
        public ICommand RemoveCommand { get; }

        public string Name { get => Column.Name; set { Column.Name = (value ?? "").Trim(); OnPropertyChanged(); _owner.EditChanged(); } }
        public string Type
        {
            get => Column.BaseType;
            set
            {
                Column.BaseType = (value ?? "").Trim().ToLowerInvariant();
                if (ColumnTypes.NeedsLength(Column.BaseType) && string.IsNullOrWhiteSpace(Column.Length)) Column.Length = Column.BaseType == "varchar" ? "100" : "";
                if (!ColumnTypes.TakesLength(Column.BaseType)) Column.Length = "";
                if (!ColumnTypes.IsInteger(Column.BaseType)) Column.AutoIncrement = false;
                OnPropertyChanged(); OnPropertyChanged(nameof(Length)); OnPropertyChanged(nameof(AutoIncrement));
                _owner.EditChanged();
            }
        }
        public string Length { get => Column.Length; set { Column.Length = (value ?? "").Trim(); OnPropertyChanged(); _owner.EditChanged(); } }
        public bool NotNull { get => !Column.Nullable; set { Column.Nullable = !value; OnPropertyChanged(); _owner.EditChanged(); } }
        public bool IsPk
        {
            get => _isPk;
            set
            {
                if (!Set(ref _isPk, value)) return;
                if (value) { Column.Nullable = false; OnPropertyChanged(nameof(NotNull)); }
                _owner.EditChanged();
            }
        }
        public bool AutoIncrement
        {
            get => Column.AutoIncrement;
            set
            {
                Column.AutoIncrement = value && ColumnTypes.IsInteger(Column.BaseType);
                if (Column.AutoIncrement) { Column.Nullable = false; if (!_isPk) { _isPk = true; OnPropertyChanged(nameof(IsPk)); } OnPropertyChanged(nameof(NotNull)); }
                OnPropertyChanged(); _owner.EditChanged();
            }
        }
        public string Default { get => Column.Default; set { Column.Default = value ?? ""; OnPropertyChanged(); _owner.EditChanged(); } }
    }

    /// <summary>A relation (foreign key) in the table editor.</summary>
    public sealed class DiagramFkRow : ObservableObject
    {
        private readonly DiagramViewModel _owner;
        public DiagramFkRow(DiagramViewModel owner, ForeignKeyDef f)
        {
            _owner = owner; Key = f;
            RemoveCommand = new RelayCommand(() => _owner.RemoveForeignKey(this));
        }
        public ForeignKeyDef Key { get; }
        public ICommand RemoveCommand { get; }
        public string Text => string.Join(", ", Key.Columns) + " → " + (Key.RefDatabase != null ? Key.RefDatabase + "." : "") + Key.RefTable + "." + string.Join(", ", Key.RefColumns);
        public string Name => Key.Name;
        public IReadOnlyList<string> Rules => ForeignKeyDef.Rules;
        private static string Norm(string r) { var u = (r ?? "").Trim().ToUpperInvariant(); return ForeignKeyDef.Rules.Contains(u) ? u : "RESTRICT"; }
        public string OnDelete { get => Norm(Key.OnDelete); set { Key.OnDelete = value; OnPropertyChanged(); _owner.EditChanged(); } }
        public string OnUpdate { get => Norm(Key.OnUpdate); set { Key.OnUpdate = value; OnPropertyChanged(); _owner.EditChanged(); } }
    }

    /// <summary>
    /// Diagram (F5, SPEC §5.5): any database as an ER diagram with crow's-foot relations; drag to arrange (saved);
    /// draw or change tables and relations, review the script that makes the database match, run it;
    /// export as PNG, SVG, PDF or a CREATE script.
    /// </summary>
    public sealed class DiagramViewModel : ObservableObject
    {
        public static readonly IReadOnlyList<string> TypeChoices = ColumnTypes.Common;
        public const double MinZoom = 0.3, MaxZoom = 2.0;

        private readonly MainViewModel _main;
        private readonly Dispatcher _ui;
        private string _database;
        private List<TableDesign> _live = new List<TableDesign>();
        private DiagramDocument _doc;
        private List<DiagramTable> _tables = new List<DiagramTable>();
        private List<DiagramRelation> _relations = new List<DiagramRelation>();
        private DiagramCard _selected;
        private bool _busy;
        private string _error = "";
        private string _status = "";
        private double _zoom = 1;
        private int _revision;
        private bool _loading;

        public DiagramViewModel(MainViewModel main, Dispatcher ui)
        {
            _main = main;
            _ui = ui;
            ReloadCommand = new AsyncCommand(() => LoadAsync(), () => CanAct && _database != null);
            ArrangeCommand = new RelayCommand(Arrange, () => !_busy && _tables.Count > 0);
            NewTableCommand = new RelayCommand(NewTable, () => !_busy && _database != null && !IsEditing);
            ZoomInCommand = new RelayCommand(() => Zoom = Math.Round(_zoom * 1.2, 2));
            ZoomOutCommand = new RelayCommand(() => Zoom = Math.Round(_zoom / 1.2, 2));
            ZoomResetCommand = new RelayCommand(() => Zoom = 1);
            ReviewCommand = new AsyncCommand(ReviewAsync, () => CanAct && _database != null && !IsEditing);
            RunPlanCommand = new AsyncCommand(RunPlanAsync, () => CanAct && Plan != null && Plan.CanRun);
            ClosePlanCommand = new RelayCommand(() => Plan = null);
            CopyScriptCommand = new RelayCommand(() => _main.CopyText(ScriptText, "Script copied."), () => Plan != null);
            SaveScriptCommand = new RelayCommand(() => SaveText(ScriptText, "-changes.sql"), () => Plan != null && !Plan.IsEmpty);
            DiscardCommand = new AsyncCommand(DiscardAsync, () => !_busy && HasPending);
            ExportCommand = new RelayCommand(p => Export(p as string), _ => !_busy && _tables.Count > 0);
            ShowHiddenCommand = new RelayCommand(ShowHidden, () => !_busy && HiddenCount > 0);
            OpenExportsCommand = new RelayCommand(() => OpenFolder(_main.Paths.Exports));
            NewDatabaseCommand = new AsyncCommand(CreateDatabaseAsync, () => CanAct && !string.IsNullOrWhiteSpace(NewDatabaseName));

            EditCommand = new RelayCommand(StartEdit, () => !_busy && _selected != null && !_selected.IsDropped && !IsEditing);
            OpenInTablesCommand = new AsyncCommand(OpenInTablesAsync, () => CanAct && _selected != null && !_selected.IsNew);
            HideCommand = new RelayCommand(HideSelected, () => !_busy && _selected != null && !IsEditing);
            DropCommand = new RelayCommand(ToggleDrop, () => !_busy && _selected != null && !IsEditing);
            UndoTableCommand = new RelayCommand(UndoTable, () => !_busy && _selected != null && _selected.IsChanged && !IsEditing);

            AddColumnCommand = new RelayCommand(AddColumn, () => IsEditing);
            AddRelationCommand = new RelayCommand(AddRelation, () => IsEditing && !string.IsNullOrEmpty(RelationTarget));
            SaveEditCommand = new RelayCommand(SaveEdit, () => IsEditing && !string.IsNullOrWhiteSpace(EditName));
            CancelEditCommand = new RelayCommand(CancelEdit, () => IsEditing);
        }

        // ====================================================================== state

        public ICommand ReloadCommand { get; }
        public ICommand ArrangeCommand { get; }
        public ICommand NewTableCommand { get; }
        public ICommand ZoomInCommand { get; }
        public ICommand ZoomOutCommand { get; }
        public ICommand ZoomResetCommand { get; }
        public ICommand ReviewCommand { get; }
        public ICommand RunPlanCommand { get; }
        public ICommand ClosePlanCommand { get; }
        public ICommand CopyScriptCommand { get; }
        public ICommand SaveScriptCommand { get; }
        public ICommand DiscardCommand { get; }
        public ICommand ExportCommand { get; }
        public ICommand ShowHiddenCommand { get; }
        public ICommand OpenExportsCommand { get; }
        public ICommand NewDatabaseCommand { get; }
        public ICommand EditCommand { get; }
        public ICommand OpenInTablesCommand { get; }
        public ICommand HideCommand { get; }
        public ICommand DropCommand { get; }
        public ICommand UndoTableCommand { get; }
        public ICommand AddColumnCommand { get; }
        public ICommand AddRelationCommand { get; }
        public ICommand SaveEditCommand { get; }
        public ICommand CancelEditCommand { get; }

        public ObservableCollection<string> Databases { get; } = new ObservableCollection<string>();
        public ObservableCollection<DiagramCard> Cards { get; } = new ObservableCollection<DiagramCard>();
        public IReadOnlyList<DiagramRelation> Relations => _relations;
        /// <summary>Bumped whenever a relation route changes, so the drawing layer repaints.</summary>
        public int Revision { get => _revision; private set => Set(ref _revision, value); }

        private bool CanAct => !_busy && _main.IsRunning && !_main.NeedsPassword;
        public bool ServerStopped => !_main.IsRunning;
        public string ServerNote => "Start the database server on Today (F1) to see diagrams.";
        public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
        public string Error { get => _error; set { if (Set(ref _error, value ?? "")) OnPropertyChanged(nameof(HasError)); } }
        public bool HasError => _error.Length > 0;
        public string Status { get => _status; private set => Set(ref _status, value ?? ""); }

        public string Database
        {
            get => _database;
            set
            {
                if (_loading || string.Equals(_database, value, StringComparison.Ordinal)) return;
                _database = value;
                OnPropertyChanged();
                RememberDatabase(value);
                if (value != null && !IsEditing) _ = LoadAsync();
            }
        }

        public double Zoom
        {
            get => _zoom;
            set
            {
                var z = Math.Max(MinZoom, Math.Min(MaxZoom, value));
                if (Set(ref _zoom, z)) OnPropertyChanged(nameof(ZoomText));
            }
        }
        public string ZoomText => Math.Round(_zoom * 100).ToString(CultureInfo.InvariantCulture) + "%";

        public double CanvasWidth => _tables.Count == 0 ? 800 : Math.Max(800, _tables.Max(t => t.Right) + 400);
        public double CanvasHeight => _tables.Count == 0 ? 500 : Math.Max(500, _tables.Max(t => t.Bottom) + 300);

        public bool HasTables => Cards.Count > 0;
        public bool IsEmpty => _database != null && Cards.Count == 0 && !_busy;
        public bool HasDatabase => _database != null;
        public bool NoDatabases => Databases.Count == 0 && _main.IsRunning;
        public string Summary
        {
            get
            {
                if (_database == null) return "";
                int n = Cards.Count, r = _relations.Count;
                return n + (n == 1 ? " table" : " tables") + " · " + r + (r == 1 ? " relation" : " relations") + (HiddenCount > 0 ? " · " + HiddenCount + " hidden" : "");
            }
        }
        public int HiddenCount => _doc?.Hidden.Count(h => _live.Any(t => string.Equals(t.Name, h, StringComparison.OrdinalIgnoreCase)) || (_doc.DraftOf(h) != null)) ?? 0;
        public bool HasHidden => HiddenCount > 0;

        public bool HasPending => _doc != null && _doc.HasPendingChanges;
        public string PendingText
        {
            get
            {
                if (_doc == null) return "";
                int n = _doc.Drafts.Count(d => d.OriginalName == null), c = _doc.Drafts.Count(d => d.OriginalName != null), x = _doc.Drops.Count;
                var parts = new List<string>();
                if (n > 0) parts.Add(n + (n == 1 ? " new table" : " new tables"));
                if (c > 0) parts.Add(c + (c == 1 ? " changed table" : " changed tables"));
                if (x > 0) parts.Add(x + (x == 1 ? " table to drop" : " tables to drop"));
                return string.Join(", ", parts) + " — on the diagram only, not in the database yet.";
            }
        }

        public DiagramCard Selected
        {
            get => _selected;
            set
            {
                if (IsEditing && value != _selected) return;   // finish or cancel the edit first
                if (_selected != null) _selected.IsSelected = false;
                Set(ref _selected, value);
                if (_selected != null) _selected.IsSelected = true;
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedHeading));
                OnPropertyChanged(nameof(SelectedNote));
                OnPropertyChanged(nameof(SelectedRelations));
                OnPropertyChanged(nameof(DropLabel));
                CommandManager.InvalidateRequerySuggested();
            }
        }
        public bool HasSelection => _selected != null;
        public string SelectedHeading => _selected?.Name ?? "";
        public string SelectedNote =>
            _selected == null ? "" :
            _selected.IsNew ? "Drawn on the diagram. Review script creates it." :
            _selected.IsChanged ? "Changed on the diagram. Review script shows the ALTER." :
            _selected.IsDropped ? "Marked to drop. Review script drops it (after a backup)." :
            _selected.Rows.Count + (_selected.Rows.Count == 1 ? " column" : " columns") + (_selected.Table.Design.RowEstimate > 0 ? " · about " + _selected.Table.Design.RowEstimate.ToString("N0", CultureInfo.InvariantCulture) + " rows" : "");
        public List<string> SelectedRelations
        {
            get
            {
                if (_selected == null) return new List<string>();
                var list = new List<string>();
                foreach (var r in _relations.Where(r => r.Child == _selected.Table))
                    list.Add("→ " + r.Parent.Name + " (" + string.Join(", ", r.ForeignKey.Columns) + "): " + r.Meaning);
                foreach (var r in _relations.Where(r => r.Parent == _selected.Table && r.Child != _selected.Table))
                    list.Add("← " + r.Child.Name + " (" + string.Join(", ", r.ForeignKey.Columns) + ")");
                return list;
            }
        }
        public string DropLabel => _selected == null ? "Drop" : _selected.IsNew ? "Delete" : _selected.IsDropped ? "Keep table" : "Drop table";

        // ====================================================================== loading

        public void OnShown()
        {
            OnPropertyChanged(nameof(ServerStopped));
            if (!_main.IsRunning) return;
            ReloadDatabases();
            if (_database != null && !IsEditing && !_busy) _ = LoadAsync();
        }

        private void ReloadDatabases()
        {
            var names = _main.Databases.Select(d => d.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            _loading = true;
            try
            {
                Databases.Clear();
                foreach (var n in names) Databases.Add(n);
            }
            finally { _loading = false; }
            if (_database == null || !names.Contains(_database, StringComparer.OrdinalIgnoreCase))
            {
                var last = LastDatabase();
                _database = names.FirstOrDefault(n => string.Equals(n, last, StringComparison.OrdinalIgnoreCase))
                            ?? names.FirstOrDefault(n => !string.Equals(n, "test", StringComparison.OrdinalIgnoreCase)) ?? names.FirstOrDefault();
                if (_database == null) ClearCanvas();
            }
            OnPropertyChanged(nameof(Database));
            OnPropertyChanged(nameof(NoDatabases));
            OnPropertyChanged(nameof(HasDatabase));
        }

        private string LastFile => Path.Combine(_main.Paths.Config, "diagrams", "last-database.txt");

        private string LastDatabase()
        {
            try { return File.Exists(LastFile) ? File.ReadAllText(LastFile).Trim() : null; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        private void RememberDatabase(string db)
        {
            if (string.IsNullOrEmpty(db)) return;
            try { Directory.CreateDirectory(Path.GetDirectoryName(LastFile)); File.WriteAllText(LastFile, db); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { AppLog.Error("diagram: remember database", ex); }
        }

        private void ClearCanvas()
        {
            _live = new List<TableDesign>();
            _doc = null;
            _tables = new List<DiagramTable>();
            _relations = new List<DiagramRelation>();
            Cards.Clear();
            Selected = null;
            Plan = null;
            NotifyCanvas();
        }

        public async Task LoadAsync()
        {
            var db = _database;
            if (db == null || !_main.IsRunning) return;
            await RunAsync("Reading " + db, async ct =>
            {
                List<TableDesign> live;
                using (var c = await _main.Server.OpenConnectionAsync(ct: ct))
                    live = await DiagramReader.ReadAsync(c, db, ct);
                if (!string.Equals(db, _database, StringComparison.Ordinal)) return;   // switched meanwhile
                _live = live;
                var doc = _doc != null && string.Equals(_doc.Database, db, StringComparison.Ordinal) ? _doc : DiagramDocument.Load(_main.Paths, db);
                _doc = doc;
                var keep = _selected?.Name;
                Compose();
                if (keep != null) Selected = Cards.FirstOrDefault(x => string.Equals(x.Name, keep, StringComparison.OrdinalIgnoreCase));
                Status = "";
            });
        }

        /// <summary>Rebuilds cards and relations from the live tables and the diagram file.</summary>
        private void Compose()
        {
            _tables = _doc.Compose(_live);
            bool any = _tables.Any(t => t.Placed);
            if (!any) DiagramLayout.Arrange(_tables);
            else DiagramLayout.PlaceNew(_tables);
            if (_tables.Any(t => !t.Placed))
            {
                foreach (var t in _tables) { _doc.SetPosition(t.Name, t.X, t.Y); t.Placed = true; }
                SaveDoc();
            }
            _relations = DiagramGeometry.Relations(_tables);
            var sel = _selected?.Name;
            _selected = null;
            Cards.Clear();
            foreach (var t in _tables) Cards.Add(new DiagramCard(t));
            if (sel != null)
            {
                var again = Cards.FirstOrDefault(x => string.Equals(x.Name, sel, StringComparison.OrdinalIgnoreCase));
                if (again != null) { _selected = again; again.IsSelected = true; }
            }
            NotifyCanvas();
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedHeading));
            OnPropertyChanged(nameof(SelectedNote));
            OnPropertyChanged(nameof(SelectedRelations));
            OnPropertyChanged(nameof(DropLabel));
        }

        private void NotifyCanvas()
        {
            Revision++;
            OnPropertyChanged(nameof(Relations));
            OnPropertyChanged(nameof(CanvasWidth));
            OnPropertyChanged(nameof(CanvasHeight));
            OnPropertyChanged(nameof(HasTables));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(HasPending));
            OnPropertyChanged(nameof(PendingText));
            OnPropertyChanged(nameof(HiddenCount));
            OnPropertyChanged(nameof(HasHidden));
            CommandManager.InvalidateRequerySuggested();
        }

        private void SaveDoc()
        {
            if (_doc == null) return;
            try { _doc.Save(_main.Paths); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                AppLog.Error("diagram save failed", ex);
                Error = "Couldn't save the diagram layout: " + ex.Message;
            }
        }

        // ====================================================================== moving and arranging

        /// <summary>Called while a card is dragged (canvas coordinates, unzoomed).</summary>
        public void MoveCard(DiagramCard card, double x, double y)
        {
            card.MoveTo(x, y);
            DiagramGeometry.RouteAll(_relations, _tables);
            Revision++;
        }

        /// <summary>Called when the drag ends: the new place is saved.</summary>
        public void EndMove(DiagramCard card)
        {
            if (_doc == null) return;
            _doc.SetPosition(card.Name, card.X, card.Y);
            SaveDoc();
            OnPropertyChanged(nameof(CanvasWidth));
            OnPropertyChanged(nameof(CanvasHeight));
        }

        private void Arrange()
        {
            if (_tables.Count == 0 || _doc == null) return;
            DiagramLayout.Arrange(_tables);
            foreach (var t in _tables) _doc.SetPosition(t.Name, t.X, t.Y);
            SaveDoc();
            foreach (var c in Cards) c.MoveTo(c.Table.X, c.Table.Y);
            DiagramGeometry.RouteAll(_relations, _tables);
            NotifyCanvas();
            Status = "Arranged: parents on the left, the tables that point at them to the right.";
        }

        private void ShowHidden()
        {
            if (_doc == null) return;
            _doc.Hidden.Clear();
            SaveDoc();
            Compose();
        }

        private void HideSelected()
        {
            if (_selected == null || _doc == null) return;
            var name = _selected.Name;
            if (!_doc.IsHidden(name)) _doc.Hidden.Add(name);
            Selected = null;
            SaveDoc();
            Compose();
            Status = name + " is hidden from the diagram (it stays in the database). Show it again from the summary line.";
        }

        // ====================================================================== drawing tables

        private void NewTable()
        {
            if (_doc == null) return;
            var taken = new HashSet<string>(_tables.Select(t => t.Name).Concat(_live.Select(t => t.Name)), StringComparer.OrdinalIgnoreCase);
            string name = "new_table";
            for (int i = 2; taken.Contains(name); i++) name = "new_table_" + i;
            var t = TableDesign.NewTable(_database, name);
            t.OriginalName = null;
            var size = DiagramGeometry.MeasureTable(t);
            var spot = DiagramLayout.FreeSpot(_tables, size.Width + 140, size.Height + 120, ViewLeft + 40, ViewTop + 40);
            _doc.Drafts.Add(t);
            _doc.SetPosition(name, spot.X, spot.Y);
            SaveDoc();
            Compose();
            Selected = Cards.FirstOrDefault(c => c.Name == name);
            StartEdit();
        }

        /// <summary>Set by the view: the top-left of what is visible, in canvas coordinates (new cards appear there).</summary>
        public double ViewLeft { get; set; }
        public double ViewTop { get; set; }

        private void ToggleDrop()
        {
            if (_selected == null || _doc == null) return;
            var name = _selected.Name;
            if (_selected.IsNew)
            {
                var d = _doc.DraftOf(name);
                if (d != null) _doc.Drafts.Remove(d);
                _doc.Positions.RemoveAll(p => string.Equals(p.Table, name, StringComparison.OrdinalIgnoreCase));
                Selected = null;
                Status = name + " deleted from the diagram.";
            }
            else
            {
                var liveName = _selected.Table.Design.OriginalName ?? name;
                if (_doc.IsDropped(liveName)) { _doc.Drops.RemoveAll(x => string.Equals(x, liveName, StringComparison.OrdinalIgnoreCase)); Status = liveName + " will be kept."; }
                else
                {
                    var draft = _doc.DraftForExisting(liveName);
                    if (draft != null) _doc.Drafts.Remove(draft);
                    _doc.Drops.Add(liveName);
                    Status = liveName + " is marked to drop. Nothing happens until you review and run the script.";
                }
            }
            SaveDoc();
            Compose();
        }

        private void UndoTable()
        {
            if (_selected == null || _doc == null || !_selected.IsChanged) return;
            var d = _doc.DraftForExisting(_selected.Table.Design.OriginalName ?? _selected.Name);
            if (d != null)
            {
                if (!string.Equals(d.Name, d.OriginalName, StringComparison.OrdinalIgnoreCase))
                {
                    var p = _doc.PositionOf(d.Name);
                    if (p != null) p.Table = d.OriginalName;
                }
                _doc.Drafts.Remove(d);
            }
            SaveDoc();
            Compose();
            Status = "Changes to that table undone.";
        }

        private async Task DiscardAsync()
        {
            if (_doc == null) return;
            var choice = await _main.AskAsync(new ChoiceSlip("DIAGRAM", "Forget the changes on the diagram?",
                PendingText.Replace(" — on the diagram only, not in the database yet.", "") + " will be forgotten. The database isn't touched; the layout stays.",
                alarm: false, primary: "Forget them", secondary: "Keep", detail: null));
            if (choice != SlipChoice.Primary) return;
            foreach (var d in _doc.Drafts.Where(d => d.OriginalName != null && !string.Equals(d.Name, d.OriginalName, StringComparison.OrdinalIgnoreCase)))
            {
                var p = _doc.PositionOf(d.Name);
                if (p != null) p.Table = d.OriginalName;
            }
            _doc.DiscardChanges();
            SaveDoc();
            Plan = null;
            Compose();
        }

        private async Task OpenInTablesAsync()
        {
            if (_selected == null) return;
            var name = _selected.Table.Design.OriginalName ?? _selected.Name;
            var db = _database;
            _main.Select("tables");
            await _main.Tables.OpenTableAsync(db, name);
        }

        private async Task CreateDatabaseAsync()
        {
            var name = (NewDatabaseName ?? "").Trim();
            var problem = DesignChecker.NameProblem(name, "Database");
            if (problem != null) { Error = problem; return; }
            if (_main.Databases.Any(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))) { Error = "There's already a database called " + name + "."; return; }
            await RunAsync("Creating " + name, async ct =>
            {
                using (var c = await _main.Server.OpenConnectionAsync(ct: ct))
                using (var cmd = new MySqlCommand("CREATE DATABASE " + DdlBuilder.Q(name) + " CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", c))
                    await cmd.ExecuteNonQueryAsync(ct);
                _main.Receipt("diagram · new database " + name, "created", "ok");
            });
            if (HasError) return;
            NewDatabaseName = "";
            await _main.RefreshNowAsync();
            ReloadDatabases();
            _database = null;
            Database = name;
        }

        private string _newDb = "";
        public string NewDatabaseName { get => _newDb; set { if (Set(ref _newDb, value ?? "")) CommandManager.InvalidateRequerySuggested(); } }

        // ====================================================================== the table editor

        private TableDesign _edit;
        private string _editName = "";
        private string _relationTarget;
        private string _relationColumn = "";
        private bool _relationRequired = true;

        public bool IsEditing => _edit != null;
        public bool NotEditing => _edit == null;
        public ObservableCollection<DiagramColumnRow> EditColumns { get; } = new ObservableCollection<DiagramColumnRow>();
        public ObservableCollection<DiagramFkRow> EditKeys { get; } = new ObservableCollection<DiagramFkRow>();
        public ObservableCollection<string> EditIssues { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> RelationTargets { get; } = new ObservableCollection<string>();
        public bool HasEditIssues => EditIssues.Count > 0;
        public bool HasEditKeys => EditKeys.Count > 0;
        public string EditHeading => _edit == null ? "" : _edit.OriginalName == null ? "NEW TABLE · " + _database : "EDIT TABLE · " + _database;

        public string EditName { get => _editName; set { if (Set(ref _editName, (value ?? "").Trim())) EditChanged(); } }
        public string RelationTarget { get => _relationTarget; set { if (Set(ref _relationTarget, value)) { RelationColumn = SuggestColumn(); CommandManager.InvalidateRequerySuggested(); } } }
        public string RelationColumn { get => _relationColumn; set => Set(ref _relationColumn, value ?? ""); }
        public bool RelationRequired { get => _relationRequired; set => Set(ref _relationRequired, value); }

        private void StartEdit()
        {
            if (_selected == null || _doc == null) return;
            var source = _selected.Table.Design;
            _edit = source.Clone();
            _edit.Database = _database;
            _editName = _edit.Name;
            EditColumns.Clear();
            var pk = new HashSet<string>((_edit.PrimaryKey?.Columns ?? new List<string>()).Select(TableDesign.IndexColumnName), StringComparer.OrdinalIgnoreCase);
            foreach (var c in _edit.Columns) EditColumns.Add(new DiagramColumnRow(this, c, pk.Contains(c.Name)));
            EditKeys.Clear();
            foreach (var f in _edit.ForeignKeys) EditKeys.Add(new DiagramFkRow(this, f));
            RelationTargets.Clear();
            foreach (var n in _tables.Where(t => !t.Table_IsDropped()).Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)) RelationTargets.Add(n);
            _relationTarget = null;
            _relationColumn = "";
            _relationRequired = true;
            OnPropertyChanged(nameof(RelationTarget));
            OnPropertyChanged(nameof(RelationColumn));
            OnPropertyChanged(nameof(RelationRequired));
            NotifyEdit();
            CheckEdit();
        }

        private void NotifyEdit()
        {
            OnPropertyChanged(nameof(IsEditing));
            OnPropertyChanged(nameof(NotEditing));
            OnPropertyChanged(nameof(EditName));
            OnPropertyChanged(nameof(EditHeading));
            OnPropertyChanged(nameof(HasEditKeys));
            CommandManager.InvalidateRequerySuggested();
        }

        internal void EditChanged() => CheckEdit();

        private void CheckEdit()
        {
            EditIssues.Clear();
            if (_edit == null) { OnPropertyChanged(nameof(HasEditIssues)); return; }
            var probe = BuildEdited();
            var nameProblem = DesignChecker.NameProblem(probe.Name, "Table");
            if (nameProblem != null) EditIssues.Add(nameProblem);
            if (_tables.Any(t => !ReferenceEquals(t, _selected?.Table) && string.Equals(t.Name, probe.Name, StringComparison.OrdinalIgnoreCase))
                || (_edit.OriginalName == null || !string.Equals(_edit.OriginalName, probe.Name, StringComparison.OrdinalIgnoreCase)) && _live.Any(t => string.Equals(t.Name, probe.Name, StringComparison.OrdinalIgnoreCase) && !string.Equals(t.Name, _edit.OriginalName, StringComparison.OrdinalIgnoreCase)))
                EditIssues.Add("There's already a table called " + probe.Name + ".");
            foreach (var i in DesignChecker.Check(probe)) EditIssues.Add((i.Severity == DesignSeverity.Bad ? "" : "Tip: ") + i.Message);
            OnPropertyChanged(nameof(HasEditIssues));
        }

        /// <summary>The working copy with the editor's PK ticks, renames and name applied.</summary>
        private TableDesign BuildEdited()
        {
            var d = _edit.Clone();
            d.Name = _editName;
            d.Columns = EditColumns.Select(r => r.Column.Clone()).ToList();
            var map = EditColumns.Where(r => !string.Equals(r.StartName, r.Column.Name, StringComparison.Ordinal)).ToDictionary(r => r.StartName, r => r.Column.Name, StringComparer.OrdinalIgnoreCase);
            string Map(string col) { var n = TableDesign.IndexColumnName(col); return map.TryGetValue(n, out var to) ? to + col.Substring(n.Length) : col; }
            foreach (var i in d.Indexes) i.Columns = i.Columns.Select(Map).ToList();
            foreach (var f in d.ForeignKeys)
            {
                f.Columns = f.Columns.Select(Map).ToList();
                if (string.Equals(f.RefTable, _edit.Name, StringComparison.OrdinalIgnoreCase) && f.RefDatabase == null) f.RefColumns = f.RefColumns.Select(Map).ToList();   // self-reference
            }
            var pkCols = EditColumns.Where(r => r.IsPk).Select(r => r.Column.Name).ToList();
            d.Indexes.RemoveAll(i => i.Kind == IndexKind.Primary);
            if (pkCols.Count > 0) d.Indexes.Insert(0, new IndexDef { Name = "PRIMARY", Kind = IndexKind.Primary, Columns = pkCols });
            return d;
        }

        internal void RemoveColumn(DiagramColumnRow row)
        {
            if (_edit == null) return;
            EditColumns.Remove(row);
            var name = row.StartName;
            foreach (var i in _edit.Indexes) i.Columns.RemoveAll(c => string.Equals(TableDesign.IndexColumnName(c), name, StringComparison.OrdinalIgnoreCase));
            _edit.Indexes.RemoveAll(i => i.Kind != IndexKind.Primary && i.Columns.Count == 0);
            foreach (var k in EditKeys.Where(k => k.Key.Columns.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                _edit.ForeignKeys.Remove(k.Key);
                EditKeys.Remove(k);
            }
            OnPropertyChanged(nameof(HasEditKeys));
            CheckEdit();
        }

        internal void RemoveForeignKey(DiagramFkRow row)
        {
            if (_edit == null) return;
            _edit.ForeignKeys.Remove(row.Key);
            EditKeys.Remove(row);
            OnPropertyChanged(nameof(HasEditKeys));
            CheckEdit();
        }

        private void AddColumn()
        {
            if (_edit == null) return;
            var taken = new HashSet<string>(EditColumns.Select(r => r.Column.Name), StringComparer.OrdinalIgnoreCase);
            string name = "column";
            for (int i = 2; taken.Contains(name); i++) name = "column" + i;
            var c = new ColumnDef { Name = name, BaseType = "varchar", Length = "100", Nullable = true };
            _edit.Columns.Add(c);
            EditColumns.Add(new DiagramColumnRow(this, c, false));
            CheckEdit();
        }

        private string SuggestColumn()
        {
            var target = _tables.FirstOrDefault(t => string.Equals(t.Name, _relationTarget, StringComparison.OrdinalIgnoreCase));
            var pk = target?.Design.PrimaryKey;
            if (pk == null || pk.Columns.Count != 1) return "";
            var key = TableDesign.IndexColumnName(pk.Columns[0]);
            var name = SingularOf(target.Name) + "_" + key;
            if (string.Equals(target.Name, _editName, StringComparison.OrdinalIgnoreCase) || EditColumns.Any(r => string.Equals(r.Column.Name, name, StringComparison.OrdinalIgnoreCase) && EditKeys.Any(k => k.Key.Columns.Contains(name, StringComparer.OrdinalIgnoreCase))))
                name = "parent_" + key;
            return name;
        }

        private static string SingularOf(string n)
        {
            if (n.EndsWith("ies", StringComparison.OrdinalIgnoreCase) && n.Length > 4) return n.Substring(0, n.Length - 3) + "y";
            if (n.EndsWith("sses", StringComparison.OrdinalIgnoreCase)) return n.Substring(0, n.Length - 2);
            if (n.EndsWith("s", StringComparison.OrdinalIgnoreCase) && !n.EndsWith("ss", StringComparison.OrdinalIgnoreCase) && n.Length > 3) return n.Substring(0, n.Length - 1);
            return n;
        }

        private void AddRelation()
        {
            if (_edit == null || string.IsNullOrEmpty(_relationTarget)) return;
            var parentCard = _tables.FirstOrDefault(t => string.Equals(t.Name, _relationTarget, StringComparison.OrdinalIgnoreCase));
            if (parentCard == null) return;
            bool self = ReferenceEquals(parentCard, _selected?.Table);
            // the key as the editor has it now (a self-reference follows unsaved PK ticks)
            var parent = self ? BuildEdited() : parentCard.Design;
            var working = BuildEdited();
            var before = new HashSet<string>(working.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var problem = DiagramSync.AddRelation(working, parent, string.IsNullOrWhiteSpace(_relationColumn) ? null : _relationColumn, _relationRequired);
            if (problem != null) { Error = problem; return; }
            Error = "";
            if (self) foreach (var f in working.ForeignKeys) if (string.Equals(f.RefTable, parent.Name, StringComparison.OrdinalIgnoreCase)) f.RefTable = _editName;
            // carry the result back into the editor: new column, new FK, new index
            foreach (var c in working.Columns.Where(c => !before.Contains(c.Name)))
            {
                _edit.Columns.Add(c);
                EditColumns.Add(new DiagramColumnRow(this, c, false));
            }
            var fk = working.ForeignKeys.Last();
            _edit.ForeignKeys.Add(fk);
            EditKeys.Add(new DiagramFkRow(this, fk));
            foreach (var i in working.Indexes.Where(i => i.Kind != IndexKind.Primary && !_edit.Indexes.Any(x => string.Equals(x.Name, i.Name, StringComparison.OrdinalIgnoreCase))))
                _edit.Indexes.Add(i);
            RelationTarget = null;
            OnPropertyChanged(nameof(HasEditKeys));
            CheckEdit();
        }

        private void CancelEdit()
        {
            bool wasNew = _edit?.OriginalName == null;
            var name = _edit?.Name;
            _edit = null;
            EditColumns.Clear();
            EditKeys.Clear();
            EditIssues.Clear();
            NotifyEdit();
            // a brand-new table that was never given a column or a name of its own: throw it away
            if (wasNew && name != null && _doc != null && name.StartsWith("new_table", StringComparison.Ordinal))
            {
                var d = _doc.DraftOf(name);
                if (d != null && d.Columns.Count <= 1 && d.ForeignKeys.Count == 0)
                {
                    _doc.Drafts.Remove(d);
                    _doc.Positions.RemoveAll(p => string.Equals(p.Table, name, StringComparison.OrdinalIgnoreCase));
                    SaveDoc();
                    _selected = null;
                    Compose();
                }
            }
        }

        private void SaveEdit()
        {
            if (_edit == null || _doc == null) return;
            var done = BuildEdited();
            if (DesignChecker.NameProblem(done.Name, "Table") != null) { Error = DesignChecker.NameProblem(done.Name, "Table"); return; }
            if (EditIssues.Any(i => i.StartsWith("There's already a table", StringComparison.Ordinal))) { Error = "Pick another name: there's already a table called " + done.Name + "."; return; }
            Error = "";
            var oldShownName = _selected?.Name ?? _edit.Name;

            // replace this table's draft
            var existingDraft = done.OriginalName == null ? _doc.DraftOf(oldShownName) : _doc.DraftForExisting(done.OriginalName);
            if (existingDraft != null) _doc.Drafts.Remove(existingDraft);
            var live = done.OriginalName == null ? null : _live.FirstOrDefault(t => string.Equals(t.Name, done.OriginalName, StringComparison.OrdinalIgnoreCase));
            bool sameAsLive = live != null && string.Equals(live.Name, done.Name, StringComparison.Ordinal) && DdlBuilder.Alter(live, done).Count == 0;
            if (!sameAsLive) _doc.Drafts.Add(done);

            // a rename: move the card's place and the relations that point at it
            if (!string.Equals(oldShownName, done.Name, StringComparison.Ordinal))
            {
                var p = _doc.PositionOf(oldShownName);
                if (p != null) p.Table = done.Name;
                DiagramSync.RenameReferences(_doc.Drafts.Where(d => !ReferenceEquals(d, done)), oldShownName, done.Name);
                for (int i = 0; i < _doc.Hidden.Count; i++) if (string.Equals(_doc.Hidden[i], oldShownName, StringComparison.OrdinalIgnoreCase)) _doc.Hidden[i] = done.Name;
            }
            SaveDoc();
            _edit = null;
            EditColumns.Clear();
            EditKeys.Clear();
            EditIssues.Clear();
            NotifyEdit();
            _selected = null;
            Compose();
            Selected = Cards.FirstOrDefault(c => string.Equals(c.Name, done.Name, StringComparison.OrdinalIgnoreCase));
            Status = sameAsLive ? done.Name + " matches the database." : done.Name + " saved on the diagram. Review script when you're ready to change the database.";
        }

        // ====================================================================== review and run

        private SyncPlan _plan;
        public SyncPlan Plan
        {
            get => _plan;
            private set
            {
                Set(ref _plan, value);
                OnPropertyChanged(nameof(IsReviewing));
                OnPropertyChanged(nameof(ScriptText));
                OnPropertyChanged(nameof(PlanHeading));
                OnPropertyChanged(nameof(PlanBlockers));
                OnPropertyChanged(nameof(PlanNotes));
                OnPropertyChanged(nameof(HasBlockers));
                OnPropertyChanged(nameof(RunLabel));
                CommandManager.InvalidateRequerySuggested();
            }
        }
        public bool IsReviewing => _plan != null;
        public string ScriptText => _plan == null ? "" : _plan.IsEmpty ? "-- nothing to change" : _plan.ToScript(_database);
        public string PlanHeading => _plan?.Summary ?? "";
        public List<string> PlanBlockers => _plan?.Blockers ?? new List<string>();
        public List<string> PlanNotes => _plan?.Notes ?? new List<string>();
        public bool HasBlockers => _plan != null && _plan.Blockers.Count > 0;
        public string RunLabel => "Run on " + _database;

        private async Task ReviewAsync()
        {
            await LoadAsync();   // compare with the database as it is right now
            if (_doc == null || HasError) return;
            var plan = DiagramSync.Plan(_database, _live, _doc.Drafts, _doc.Drops);
            Plan = plan;
            if (plan.IsEmpty && _doc.Drafts.Count > 0)
            {
                // drafts that turned out to match (e.g. changed in Tables meanwhile): tidy them away
                _doc.Drafts.Clear();
                SaveDoc();
                Compose();
            }
        }

        private async Task RunPlanAsync()
        {
            var plan = _plan;
            var db = _database;
            if (plan == null || !plan.CanRun) return;
            if (plan.Drops > 0)
            {
                var choice = await _main.AskAsync(new ChoiceSlip("DIAGRAM", "Drop " + plan.Drops + (plan.Drops == 1 ? " table?" : " tables?"),
                    "Dropping deletes the table and all its rows. A backup of " + db + " is taken first, so you can get it back from Backups (F10).",
                    alarm: true, primary: "Back up and run", secondary: "Cancel", detail: null));
                if (choice != SlipChoice.Primary) return;
            }
            int ran = 0;
            await RunAsync("Applying the diagram to " + db, async ct =>
            {
                if (_live.Count > 0 && !await _main.BackUpBeforeAsync(new[] { db }, "Before applying the diagram to " + db))
                {
                    _main.Receipt("diagram · " + db, "not run: no backup", "info");
                    return;
                }
                var slip = _main.Receipt("diagram · " + db, "running " + plan.Statements.Count + " statements…", "run");
                using (var c = await _main.Server.OpenConnectionAsync(ct: ct))
                {
                    foreach (var st in plan.Statements)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            using (var cmd = new MySqlCommand(st, c) { CommandTimeout = 0 }) await cmd.ExecuteNonQueryAsync(ct);
                            ran++;
                        }
                        catch (MySqlException ex)
                        {
                            slip.Kind = "bad";
                            slip.Message = "stopped at statement " + (ran + 1) + " of " + plan.Statements.Count + ": " + ex.Message;
                            Error = "The server refused statement " + (ran + 1) + " of " + plan.Statements.Count + ": " + ex.Message
                                    + (ran > 0 ? " The " + ran + " before it ran; review again to see what's left." : "");
                            return;
                        }
                    }
                }
                slip.Kind = "ok";
                slip.Message = plan.Summary;
                _doc.DiscardChanges();
                SaveDoc();
                _main.Toast(db + " now matches the diagram.");
            });
            _main.DataChanged();
            Plan = null;
            await LoadAsync();
            if (ran > 0 && HasError) Plan = DiagramSync.Plan(_database, _live, _doc.Drafts, _doc.Drops);
        }

        // ====================================================================== export

        /// <summary>Set by the view: draws the diagram to a PNG file (WPF rendering lives in the view layer).</summary>
        public Action<DiagramScene, string> RenderPng { get; set; }

        public DiagramScene Scene() =>
            new DiagramScene(_database + " · " + _tables.Count + (_tables.Count == 1 ? " table" : " tables") + " · " + DateTime.Now.ToString("d MMM yyyy", CultureInfo.InvariantCulture), _tables);

        private void Export(string kind)
        {
            if (_tables.Count == 0 || _database == null) return;
            kind = (kind ?? "png").ToLowerInvariant();
            try
            {
                var file = ExportPath(kind == "sql" ? "-create.sql" : "-diagram." + kind);
                var scene = Scene();
                switch (kind)
                {
                    case "svg": File.WriteAllText(file, DiagramSvg.Render(scene), new System.Text.UTF8Encoding(false)); break;
                    case "pdf": File.WriteAllBytes(file, DiagramPdf.Render(scene)); break;
                    case "sql": File.WriteAllText(file, DiagramSync.CreateScript(_database, _tables.Where(t => !t.Table_IsDropped()).Select(t => t.Design).ToList(), includeCreateDatabase: true), new System.Text.UTF8Encoding(false)); break;
                    default:
                        if (RenderPng == null) { Error = "PNG export isn't available here."; return; }
                        RenderPng(scene, file);
                        break;
                }
                LastExport = file;
                _main.Receipt("diagram · export " + kind.ToUpperInvariant(), Path.GetFileName(file), "ok");
                _main.Toast("Saved " + Path.GetFileName(file) + " in exports.");
                if (kind == "pdf" && scene.Tables.Any(t => t.Name.Any(ch => ch > 255) || t.Design.Columns.Any(c => c.Name.Any(ch => ch > 255))))
                    Status = "The PDF prints non-Latin names as \"?\" (its built-in font has no Sinhala or Tamil letters). Use PNG or SVG for those.";
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                AppLog.Error("diagram export failed", ex);
                Error = "Couldn't save the file: " + ex.Message;
            }
        }

        private string _lastExport;
        public string LastExport { get => _lastExport; private set { Set(ref _lastExport, value); OnPropertyChanged(nameof(HasLastExport)); OnPropertyChanged(nameof(LastExportName)); } }
        public bool HasLastExport => !string.IsNullOrEmpty(_lastExport);
        public string LastExportName => Path.GetFileName(_lastExport ?? "");
        private ICommand _showLast;
        public ICommand ShowLastExportCommand => _showLast ?? (_showLast = new RelayCommand(() => ShowFile(_lastExport), () => HasLastExport));

        private string ExportPath(string suffix)
        {
            Directory.CreateDirectory(_main.Paths.Exports);
            var safe = new string(_database.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch).ToArray());
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture);
            var file = Path.Combine(_main.Paths.Exports, safe + suffix.Insert(suffix.LastIndexOf('.'), "-" + stamp));
            for (int i = 2; File.Exists(file); i++) file = Path.Combine(_main.Paths.Exports, safe + suffix.Insert(suffix.LastIndexOf('.'), "-" + stamp + "-" + i));
            return file;
        }

        private void SaveText(string text, string suffix)
        {
            try
            {
                var file = ExportPath(suffix);
                File.WriteAllText(file, text, new System.Text.UTF8Encoding(false));
                LastExport = file;
                _main.Toast("Saved " + Path.GetFileName(file) + " in exports.");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { Error = "Couldn't save the file: " + ex.Message; }
        }

        private void ShowFile(string file)
        {
            if (string.IsNullOrEmpty(file)) return;
            try { Process.Start("explorer.exe", "/select,\"" + file + "\"")?.Dispose(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException) { _main.Toast("Couldn't open the folder: " + ex.Message); }
        }

        private void OpenFolder(string folder)
        {
            try { Directory.CreateDirectory(folder); Process.Start("explorer.exe", "\"" + folder + "\"")?.Dispose(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException || ex is UnauthorizedAccessException) { _main.Toast("Couldn't open the folder: " + ex.Message); }
        }

        // ====================================================================== plumbing

        private async Task RunAsync(string what, Func<CancellationToken, Task> work)
        {
            if (_busy) return;
            IsBusy = true;
            Error = "";
            Status = what + "…";
            OnPropertyChanged(nameof(IsEmpty));
            using (var cts = new CancellationTokenSource())
            {
                try { await work(cts.Token); }
                catch (OperationCanceledException) { Error = what + ": stopped."; }
                catch (Exception ex) when (ex is LuminaException || ex is MySqlException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
                {
                    AppLog.Error(what + " failed", ex);
                    Error = ex.Message;
                }
                finally
                {
                    IsBusy = false;
                    if (Status == what + "…") Status = "";
                    OnPropertyChanged(nameof(IsEmpty));
                }
            }
        }
    }

    internal static class DiagramTableExtensions
    {
        public static bool Table_IsDropped(this DiagramTable t) => t.State == DiagramTableState.Dropped;
    }
}
