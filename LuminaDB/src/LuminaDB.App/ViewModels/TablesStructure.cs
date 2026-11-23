using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using LuminaDB.Core;
using LuminaDB.Core.Sql;
using LuminaDB.Core.Tables;
using MySqlConnector;

namespace LuminaDB.App.ViewModels
{
    /// <summary>A column line of the structure editor.</summary>
    public sealed class StructureColumn : ObservableObject
    {
        private readonly StructureEditor _owner;
        private readonly ColumnDef _d;
        private string _problem;
        private int _position;

        public StructureColumn(StructureEditor owner, ColumnDef d)
        {
            _owner = owner;
            _d = d;
            MoveUpCommand = new RelayCommand(() => _owner.Move(this, -1), () => _owner.Columns.IndexOf(this) > 0);
            MoveDownCommand = new RelayCommand(() => _owner.Move(this, 1), () => _owner.Columns.IndexOf(this) < _owner.Columns.Count - 1);
            RemoveCommand = new RelayCommand(() => _owner.Remove(this));
        }

        public ColumnDef ToDef() => _d.Clone();
        public bool IsNew => _d.IsNew;
        public string OriginalName => _d.OriginalName;
        public int Position { get => _position; set => Set(ref _position, value); }

        public string Name
        {
            get => _d.Name;
            set
            {
                var old = _d.Name;
                value = value ?? "";
                if (old == value) return;
                _d.Name = value;
                OnPropertyChanged();
                _owner.RenameReferences(old, value);
                _owner.Changed();
            }
        }

        public string Type
        {
            get => _d.BaseType;
            set
            {
                value = (value ?? "").Trim().ToLowerInvariant();
                if (_d.BaseType == value) return;
                _d.BaseType = value;
                if (value == "json" || ColumnTypes.IsText(value) && value != "text" || ColumnTypes.IsDateLike(value) && value != "datetime" && value != "timestamp" && value != "time")
                {
                    _d.Length = "";
                    OnPropertyChanged(nameof(Length));
                }
                if (value == "varchar" && string.IsNullOrEmpty(_d.Length)) { _d.Length = "100"; OnPropertyChanged(nameof(Length)); }
                if (value == "decimal" && string.IsNullOrEmpty(_d.Length)) { _d.Length = "10,2"; OnPropertyChanged(nameof(Length)); }
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNumeric));
                OnPropertyChanged(nameof(LengthHint));
                _owner.Changed();
            }
        }

        public string Length { get => _d.Length; set { if (_d.Length == (value ?? "")) return; _d.Length = value ?? ""; OnPropertyChanged(); _owner.Changed(); } }
        public string LengthHint => _d.BaseType == "enum" || _d.BaseType == "set" ? "'small','large'" : ColumnTypes.IsDecimal(_d.BaseType) ? "10,2" : ColumnTypes.NeedsLength(_d.BaseType) ? "100" : "";
        public string Default { get => _d.Default; set { if (_d.Default == (value ?? "")) return; _d.Default = value ?? ""; OnPropertyChanged(); _owner.Changed(); } }
        public string Comment { get => _d.Comment; set { if (_d.Comment == (value ?? "")) return; _d.Comment = value ?? ""; OnPropertyChanged(); _owner.Changed(); } }
        public bool IsNumeric => ColumnTypes.IsNumeric(_d.BaseType);

        public bool NotNull
        {
            get => !_d.Nullable;
            set { if (_d.Nullable == !value) return; _d.Nullable = !value; OnPropertyChanged(); _owner.Changed(); }
        }

        public bool Unsigned { get => _d.Unsigned; set { if (_d.Unsigned == value) return; _d.Unsigned = value; OnPropertyChanged(); _owner.Changed(); } }

        public bool AutoIncrement
        {
            get => _d.AutoIncrement;
            set
            {
                if (_d.AutoIncrement == value) return;
                _d.AutoIncrement = value;
                if (value) { _d.Nullable = false; OnPropertyChanged(nameof(NotNull)); }
                OnPropertyChanged();
                _owner.Changed();
            }
        }

        public bool IsPrimary { get => _owner.IsInPrimary(_d.Name); set => _owner.SetPrimary(this, value); }
        public bool IsUnique { get => _owner.HasOwnUnique(_d.Name); set => _owner.SetUnique(this, value); }

        public bool IsGenerated => _d.IsGenerated;
        public string GeneratedText => _d.IsGenerated ? "= " + _d.GenerationExpression + (_d.GeneratedStored ? " (stored)" : "") : "";

        internal void ForceNotNull() { if (_d.Nullable) { _d.Nullable = false; OnPropertyChanged(nameof(NotNull)); } }
        internal void RefreshKeys() { OnPropertyChanged(nameof(IsPrimary)); OnPropertyChanged(nameof(IsUnique)); }

        public string Problem { get => _problem; set { Set(ref _problem, value); OnPropertyChanged(nameof(HasProblem)); } }
        public bool HasProblem => !string.IsNullOrEmpty(_problem);

        public ICommand MoveUpCommand { get; }
        public ICommand MoveDownCommand { get; }
        public ICommand RemoveCommand { get; }
    }

    public sealed class IndexRow : ObservableObject
    {
        private readonly StructureEditor _owner;
        private string _name, _kind, _columns;
        public IndexRow(StructureEditor owner, IndexDef d)
        {
            _owner = owner;
            _name = d.Name;
            _kind = d.KindWord;
            _columns = string.Join(", ", d.Columns);
            RemoveCommand = new RelayCommand(() => _owner.Remove(this));
        }
        public static IReadOnlyList<string> Kinds { get; } = new[] { "PRIMARY KEY", "UNIQUE", "INDEX", "FULLTEXT" };
        public IReadOnlyList<string> KindList => Kinds;
        public string Name { get => _name; set { if (Set(ref _name, value ?? "")) _owner.Changed(); } }
        public string Kind { get => _kind; set { if (Set(ref _kind, value ?? "INDEX")) { OnPropertyChanged(nameof(IsPrimary)); _owner.Changed(true); } } }
        public bool IsPrimary => _kind == "PRIMARY KEY";
        public string ColumnsText { get => _columns; set { if (Set(ref _columns, value ?? "")) _owner.Changed(true); } }
        public List<string> ColumnList => StructureEditor.Split(_columns);
        internal void SetColumns(IEnumerable<string> cols) { _columns = string.Join(", ", cols); OnPropertyChanged(nameof(ColumnsText)); }
        public ICommand RemoveCommand { get; }
        public IndexDef ToDef() => new IndexDef
        {
            Name = IsPrimary ? "PRIMARY" : _name,
            Kind = _kind == "PRIMARY KEY" ? IndexKind.Primary : _kind == "UNIQUE" ? IndexKind.Unique : _kind == "FULLTEXT" ? IndexKind.Fulltext : IndexKind.Index,
            Columns = ColumnList,
        };
    }

    public sealed class ForeignKeyRow : ObservableObject
    {
        private readonly StructureEditor _owner;
        private string _name, _columns, _refTable, _refColumns, _onDelete, _onUpdate;
        private readonly string _refDb;
        public ForeignKeyRow(StructureEditor owner, ForeignKeyDef d)
        {
            _owner = owner;
            _name = d.Name; _columns = string.Join(", ", d.Columns); _refTable = d.RefTable; _refColumns = string.Join(", ", d.RefColumns);
            _onDelete = d.OnDelete; _onUpdate = d.OnUpdate; _refDb = d.RefDatabase;
            RemoveCommand = new RelayCommand(() => _owner.Remove(this));
        }
        public IReadOnlyList<string> Rules => ForeignKeyDef.Rules;
        public IReadOnlyList<string> Tables => _owner.TableNames;
        public string Name { get => _name; set { if (Set(ref _name, value ?? "")) _owner.Changed(); } }
        public string ColumnsText { get => _columns; set { if (Set(ref _columns, value ?? "")) _owner.Changed(); } }
        public string RefTable
        {
            get => _refTable;
            set
            {
                if (!Set(ref _refTable, value ?? "")) return;
                // point at the other table's primary key by default
                var t = _owner.Schema.FindTable(_refDb ?? _owner.Database, _refTable);
                var pk = t?.PrimaryKey.Select(c => c.Name).ToList();
                if (pk != null && pk.Count > 0 && string.IsNullOrWhiteSpace(_refColumns)) { _refColumns = string.Join(", ", pk); OnPropertyChanged(nameof(RefColumnsText)); }
                _owner.Changed();
            }
        }
        public string RefColumnsText { get => _refColumns; set { if (Set(ref _refColumns, value ?? "")) _owner.Changed(); } }
        public string OnDelete { get => _onDelete; set { if (Set(ref _onDelete, value ?? "RESTRICT")) _owner.Changed(); } }
        public string OnUpdate { get => _onUpdate; set { if (Set(ref _onUpdate, value ?? "RESTRICT")) _owner.Changed(); } }
        internal void SetColumns(string text) { _columns = text; OnPropertyChanged(nameof(ColumnsText)); }
        public ICommand RemoveCommand { get; }
        public ForeignKeyDef ToDef() => new ForeignKeyDef
        {
            Name = _name, Columns = StructureEditor.Split(_columns), RefDatabase = _refDb, RefTable = (_refTable ?? "").Trim(),
            RefColumns = StructureEditor.Split(_refColumns), OnDelete = _onDelete, OnUpdate = _onUpdate,
        };
    }

    public sealed class CheckRow : ObservableObject
    {
        private readonly StructureEditor _owner;
        private string _name, _expr;
        public CheckRow(StructureEditor owner, CheckDef d)
        {
            _owner = owner; _name = d.Name; _expr = d.Expression;
            RemoveCommand = new RelayCommand(() => _owner.Remove(this));
        }
        public string Name { get => _name; set { if (Set(ref _name, value ?? "")) _owner.Changed(); } }
        public string Expression { get => _expr; set { if (Set(ref _expr, value ?? "")) _owner.Changed(); } }
        public ICommand RemoveCommand { get; }
        public CheckDef ToDef() => new CheckDef { Name = _name, Expression = _expr };
    }

    public sealed class IssueLine
    {
        public string Mark { get; set; }
        public string Text { get; set; }
        public bool IsBad { get; set; }
    }

    /// <summary>The structure editor: columns, keys, indexes, foreign keys and checks, with the SQL it will run always on show.</summary>
    public sealed class StructureEditor : ObservableObject
    {
        private readonly TablesViewModel _owner;
        private TableDesign _before;
        private bool _isNew;
        private bool _loading;
        private string _script = "", _tableName = "", _engine = "InnoDB", _comment = "", _collation = "utf8mb4_unicode_ci";
        private int _counter;

        public StructureEditor(TablesViewModel owner)
        {
            _owner = owner;
            AddColumnCommand = new RelayCommand(AddColumn, () => _before != null);
            AddIndexCommand = new RelayCommand(() => { Indexes.Add(new IndexRow(this, new IndexDef { Name = "idx_" + (Columns.LastOrDefault()?.Name ?? "new"), Kind = IndexKind.Index })); Changed(true); }, () => _before != null);
            AddForeignKeyCommand = new RelayCommand(AddForeignKey, () => _before != null);
            AddCheckCommand = new RelayCommand(() => { Checks.Add(new CheckRow(this, new CheckDef { Name = "chk_" + (TableName.Length > 0 ? TableName : "table") + "_" + (Checks.Count + 1), Expression = "" })); Changed(); }, () => _before != null);
            ApplyCommand = new AsyncCommand(ApplyAsync, () => CanApply && _owner.CanAct);
            DiscardCommand = new RelayCommand(Discard, () => HasChanges);
            CopySqlCommand = new RelayCommand(() => _owner.Main.CopyText(_script, "Copied the SQL."), () => _script.Length > 0);
            OpenTriggerCommand = new RelayCommand(p =>
            {
                var e = _owner.Find(DbObjectKind.Trigger, p as string);
                if (e != null && _owner.OpenEntryCommand.CanExecute(e)) _owner.OpenEntryCommand.Execute(e);
            });
        }

        internal SchemaSnapshot Schema => _owner.Schema;
        internal string Database => _owner.Database;

        public bool IsNew => _isNew;
        public ObservableCollection<StructureColumn> Columns { get; } = new ObservableCollection<StructureColumn>();
        public ObservableCollection<IndexRow> Indexes { get; } = new ObservableCollection<IndexRow>();
        public ObservableCollection<ForeignKeyRow> ForeignKeys { get; } = new ObservableCollection<ForeignKeyRow>();
        public ObservableCollection<CheckRow> Checks { get; } = new ObservableCollection<CheckRow>();
        public ObservableCollection<TriggerInfo> Triggers { get; } = new ObservableCollection<TriggerInfo>();
        public ObservableCollection<IssueLine> Issues { get; } = new ObservableCollection<IssueLine>();
        public bool HasTriggers => Triggers.Count > 0;
        public bool HasIssues => Issues.Count > 0;

        public IReadOnlyList<string> Types => ColumnTypes.Common;
        public IReadOnlyList<string> Engines { get; } = new[] { "InnoDB", "Aria", "MyISAM", "MEMORY" };
        public ObservableCollection<string> Collations { get; } = new ObservableCollection<string>();
        public List<string> TableNames => _owner.Schema.Find(_owner.Database)?.Tables.Values.Where(t => !t.IsView).Select(t => t.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList() ?? new List<string>();

        public string TableName { get => _tableName; set { if (Set(ref _tableName, value ?? "")) Changed(); } }
        public string Engine { get => _engine; set { if (Set(ref _engine, value ?? "InnoDB")) Changed(); } }
        public string TableComment { get => _comment; set { if (Set(ref _comment, value ?? "")) Changed(); } }
        public string Collation { get => _collation; set { if (Set(ref _collation, value ?? "utf8mb4_unicode_ci")) Changed(); } }

        public string Script { get => _script; private set { Set(ref _script, value ?? ""); OnPropertyChanged(nameof(HasChanges)); OnPropertyChanged(nameof(CanApply)); OnPropertyChanged(nameof(ScriptCaption)); } }
        public bool HasChanges => _script.Length > 0 && (_isNew || !_script.StartsWith("--", StringComparison.Ordinal));
        public bool CanApply => HasChanges && !Issues.Any(i => i.IsBad);
        public string ApplyLabel => _isNew ? "Create table" : "Apply changes";
        public string ScriptCaption => !HasChanges ? "Nothing to run yet: change something on the left." : Issues.Any(i => i.IsBad) ? "Fix the marked lines first; then this runs." : "This is exactly what runs. One Undo copy covers it all.";

        public ICommand AddColumnCommand { get; }
        public ICommand AddIndexCommand { get; }
        public ICommand AddForeignKeyCommand { get; }
        public ICommand AddCheckCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand DiscardCommand { get; }
        public ICommand CopySqlCommand { get; }
        public ICommand OpenTriggerCommand { get; }

        public void Clear()
        {
            _before = null;
            Columns.Clear(); Indexes.Clear(); ForeignKeys.Clear(); Checks.Clear(); Triggers.Clear(); Issues.Clear();
            Script = "";
        }

        public void Load(TableDesign design, bool isNew)
        {
            if (design == null) { Clear(); return; }
            _loading = true;
            _isNew = isNew;
            _before = design.Clone();
            Columns.Clear(); Indexes.Clear(); ForeignKeys.Clear(); Checks.Clear(); Triggers.Clear();
            foreach (var c in design.Columns) Columns.Add(new StructureColumn(this, c.Clone()));
            foreach (var i in design.Indexes) Indexes.Add(new IndexRow(this, i));
            foreach (var f in design.ForeignKeys) ForeignKeys.Add(new ForeignKeyRow(this, f));
            foreach (var c in design.Checks) Checks.Add(new CheckRow(this, c));
            foreach (var t in design.Triggers) Triggers.Add(t);
            _tableName = design.Name; _engine = design.Engine ?? "InnoDB"; _comment = design.Comment ?? ""; _collation = design.Collation ?? "utf8mb4_unicode_ci";
            _counter = Columns.Count;
            _loading = false;
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(TableName));
            OnPropertyChanged(nameof(Engine));
            OnPropertyChanged(nameof(TableComment));
            OnPropertyChanged(nameof(Collation));
            OnPropertyChanged(nameof(ApplyLabel));
            OnPropertyChanged(nameof(HasTriggers));
            OnPropertyChanged(nameof(TableNames));
            _ = LoadCollationsAsync();
            Changed(true);
        }

        private async Task LoadCollationsAsync()
        {
            if (Collations.Count > 0 || !_owner.IsRunning) return;
            try
            {
                using (var c = await _owner.OpenConnectionAsync())
                    foreach (var name in await TableOps.CollationsAsync(c)) Collations.Add(name);
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { AppLog.Error("collations", ex); }
        }

        internal static List<string> Split(string text) =>
            (text ?? "").Split(',').Select(s => s.Trim().Trim('`')).Where(s => s.Length > 0).ToList();

        // ------------------------------------------------------------------ keys from the column checkboxes

        private IndexRow PrimaryRow => Indexes.FirstOrDefault(i => i.IsPrimary);
        internal bool IsInPrimary(string col) => PrimaryRow?.ColumnList.Any(c => string.Equals(TableDesign.IndexColumnName(c), col, StringComparison.OrdinalIgnoreCase)) == true;
        internal bool HasOwnUnique(string col) => Indexes.Any(i => i.Kind == "UNIQUE" && i.ColumnList.Count == 1 && string.Equals(TableDesign.IndexColumnName(i.ColumnList[0]), col, StringComparison.OrdinalIgnoreCase));

        internal void SetPrimary(StructureColumn c, bool on)
        {
            var pk = PrimaryRow;
            if (on)
            {
                if (pk == null) { pk = new IndexRow(this, new IndexDef { Name = "PRIMARY", Kind = IndexKind.Primary }); Indexes.Insert(0, pk); }
                if (!IsInPrimary(c.Name)) pk.SetColumns(pk.ColumnList.Concat(new[] { c.Name }));
                c.ForceNotNull();
            }
            else if (pk != null)
            {
                var rest = pk.ColumnList.Where(x => !string.Equals(TableDesign.IndexColumnName(x), c.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (rest.Count == 0) Indexes.Remove(pk); else pk.SetColumns(rest);
            }
            Changed(true);
        }

        internal void SetUnique(StructureColumn c, bool on)
        {
            if (on && !HasOwnUnique(c.Name)) Indexes.Add(new IndexRow(this, new IndexDef { Name = c.Name, Kind = IndexKind.Unique, Columns = { c.Name } }));
            else if (!on) foreach (var i in Indexes.Where(i => i.Kind == "UNIQUE" && i.ColumnList.Count == 1 && string.Equals(TableDesign.IndexColumnName(i.ColumnList[0]), c.Name, StringComparison.OrdinalIgnoreCase)).ToList()) Indexes.Remove(i);
            Changed(true);
        }

        /// <summary>A column was renamed: keys, indexes and foreign keys follow it.</summary>
        internal void RenameReferences(string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName)) return;
            string Map(string entry)
            {
                var n = TableDesign.IndexColumnName(entry);
                return string.Equals(n, oldName, StringComparison.OrdinalIgnoreCase) ? newName + entry.Substring(n.Length) : entry;
            }
            foreach (var i in Indexes)
            {
                var list = i.ColumnList;
                var mapped = list.Select(Map).ToList();
                if (!list.SequenceEqual(mapped)) i.SetColumns(mapped);
                if (!i.IsPrimary && i.Kind == "UNIQUE" && string.Equals(i.Name, oldName, StringComparison.OrdinalIgnoreCase) && i.ColumnList.Count == 1) i.Name = newName;
            }
            foreach (var f in ForeignKeys)
            {
                var list = Split(f.ColumnsText);
                var mapped = list.Select(Map).ToList();
                if (!list.SequenceEqual(mapped)) f.SetColumns(string.Join(", ", mapped));
            }
        }

        // ------------------------------------------------------------------ editing

        private void AddColumn()
        {
            _counter++;
            var name = "column" + _counter;
            while (Columns.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) name = "column" + ++_counter;
            Columns.Add(new StructureColumn(this, new ColumnDef { Name = name, BaseType = "varchar", Length = "100" }));
            Changed();
        }

        private void AddForeignKey()
        {
            var col = Columns.LastOrDefault(c => c.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase)) ?? Columns.LastOrDefault();
            var refTable = col != null && col.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) ? Guess(col.Name.Substring(0, col.Name.Length - 3)) : "";
            var row = new ForeignKeyRow(this, new ForeignKeyDef { Name = "fk_" + (TableName.Length > 0 ? TableName : "table") + "_" + (col?.Name ?? "ref"), Columns = { col?.Name ?? "" }, RefTable = "" });
            ForeignKeys.Add(row);
            if (refTable.Length > 0) row.RefTable = refTable;
            Changed();

            string Guess(string stem)
            {
                var names = TableNames;
                return names.FirstOrDefault(n => string.Equals(n, stem, StringComparison.OrdinalIgnoreCase))
                    ?? names.FirstOrDefault(n => string.Equals(n, stem + "s", StringComparison.OrdinalIgnoreCase))
                    ?? names.FirstOrDefault(n => string.Equals(n, stem + "es", StringComparison.OrdinalIgnoreCase)) ?? "";
            }
        }

        internal void Move(StructureColumn c, int by)
        {
            int i = Columns.IndexOf(c), j = i + by;
            if (i < 0 || j < 0 || j >= Columns.Count) return;
            Columns.Move(i, j);
            Changed();
        }

        internal void Remove(StructureColumn c)
        {
            Columns.Remove(c);
            foreach (var i in Indexes.ToList())
            {
                var rest = i.ColumnList.Where(x => !string.Equals(TableDesign.IndexColumnName(x), c.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (rest.Count == 0) Indexes.Remove(i);
                else if (rest.Count != i.ColumnList.Count) i.SetColumns(rest);
            }
            foreach (var f in ForeignKeys.Where(f => Split(f.ColumnsText).Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList()) ForeignKeys.Remove(f);
            Changed(true);
        }

        internal void Remove(IndexRow i) { Indexes.Remove(i); Changed(true); }
        internal void Remove(ForeignKeyRow f) { ForeignKeys.Remove(f); Changed(); }
        internal void Remove(CheckRow c) { Checks.Remove(c); Changed(); }

        private void Discard()
        {
            if (_before == null) return;
            Load(_before.Clone(), _isNew);
        }

        // ------------------------------------------------------------------ the design, the SQL, the checks

        public TableDesign BuildDesign()
        {
            var d = _before.Clone();
            d.Name = _isNew ? _tableName.Trim() : _before.Name;
            d.Engine = _engine;
            d.Comment = _comment;
            if (_isNew) d.Collation = _collation;
            d.Columns = Columns.Select(c => c.ToDef()).ToList();
            d.Indexes = Indexes.Select(i => i.ToDef()).Where(i => i.Columns.Count > 0).ToList();
            d.ForeignKeys = ForeignKeys.Select(f => f.ToDef()).ToList();
            d.Checks = Checks.Select(c => c.ToDef()).ToList();
            return d;
        }

        internal void Changed(bool keys = false)
        {
            if (_loading || _before == null) return;
            if (keys) foreach (var c in Columns) c.RefreshKeys();
            for (int i = 0; i < Columns.Count; i++) Columns[i].Position = i + 1;
            var after = BuildDesign();
            var issues = DesignChecker.Check(after, _owner.Schema);
            Issues.Clear();
            foreach (var i in issues.OrderBy(i => i.Severity == DesignSeverity.Bad ? 0 : 1))
                Issues.Add(new IssueLine { Mark = i.Severity == DesignSeverity.Bad ? "FIX" : "NOTE", Text = i.Message, IsBad = i.Severity == DesignSeverity.Bad });
            foreach (var c in Columns)
                c.Problem = issues.FirstOrDefault(i => string.Equals(i.Column, c.Name, StringComparison.OrdinalIgnoreCase) && i.Severity == DesignSeverity.Bad)?.Message;
            OnPropertyChanged(nameof(HasIssues));
            string script;
            try { script = _isNew ? DdlBuilder.Create(after) : string.Join("\n\n", DdlBuilder.Alter(_before, after)); }
            catch (Exception ex) when (ex is LuminaException || ex is InvalidOperationException || ex is ArgumentException) { script = "-- " + ex.Message; }
            Script = script;
            CommandManager.InvalidateRequerySuggested();
        }

        private async Task ApplyAsync()
        {
            Changed();
            if (!CanApply) return;
            var after = BuildDesign();
            var kept = new HashSet<string>(after.Columns.Where(c => !c.IsNew).Select(c => c.OriginalName), StringComparer.OrdinalIgnoreCase);
            var dropped = _isNew ? new List<string>() : _before.Columns.Where(c => !kept.Contains(c.Name)).Select(c => c.Name).ToList();
            if (dropped.Count > 0)
            {
                var what = dropped.Count == 1 ? "column " + dropped[0] : dropped.Count + " columns (" + string.Join(", ", dropped) + ")";
                var choice = await _owner.AskAsync(new ChoiceSlip("CAREFUL", "Drop " + what + "?",
                    "The values in " + (dropped.Count == 1 ? "it" : "them") + " go too. Undo keeps a copy of the table, so you can take it back.",
                    alarm: true, primary: "Drop and apply", secondary: null, detail: null));
                if (choice != SlipChoice.Primary) return;
            }
            var name = after.Name;
            var r = await _owner.RunAsync(_script, (_isNew ? "Created table " : "Changed the structure of ") + name);
            if (r == null || !r.Ok) return;
            if (_isNew) await _owner.CreatedAsync(DbObjectKind.Table, name, "structure");
            else await _owner.ReloadAsync(keepPage: true);
        }
    }

    /// <summary>Operations: rename, copy, move, collation, maintenance, empty and drop.</summary>
    public sealed class OperationsPage : ObservableObject
    {
        private readonly TablesViewModel _owner;
        private TableDesign _t;
        private bool _isView;
        private string _renameTo = "", _copyName = "", _copyDb, _moveDb, _collation, _referenced = "";
        private bool _copyData = true;

        public OperationsPage(TablesViewModel owner)
        {
            _owner = owner;
            RenameCommand = new AsyncCommand(RenameAsync, () => _t != null && RenameProblem == null && !SameName(_renameTo, _t.Name) && _owner.CanAct);
            CopyCommand = new AsyncCommand(CopyAsync, () => _t != null && CopyProblem == null && _owner.CanAct);
            MoveCommand = new AsyncCommand(MoveAsync, () => _t != null && MoveProblem == null && _moveDb != null && _owner.CanAct);
            ConvertCommand = new AsyncCommand(ConvertAsync, () => _t != null && _collation != null && !string.Equals(_collation, _t.Collation, StringComparison.OrdinalIgnoreCase) && _owner.CanAct);
            MaintenanceCommand = new AsyncCommand(p => MaintainAsync(p as string), p => _t != null && _owner.CanAct);
            EmptyCommand = new AsyncCommand(EmptyAsync, () => _t != null && !_isView && _owner.CanAct);
            DropCommand = new AsyncCommand(DropAsync, () => _t != null && _owner.CanAct);
        }

        public bool IsTable => !_isView;
        public string Name => _t?.Name ?? "";
        public ObservableCollection<string> Databases { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> OtherDatabases { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> Collations { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> MaintenanceLines { get; } = new ObservableCollection<string>();
        public bool HasMaintenanceLines => MaintenanceLines.Count > 0;

        public string RenameTo { get => _renameTo; set { if (Set(ref _renameTo, value ?? "")) { OnPropertyChanged(nameof(RenameProblem)); OnPropertyChanged(nameof(RenameSql)); } } }
        public string RenameProblem => _t == null || SameName(_renameTo, _t.Name) ? null : TableOps.NewNameProblem(_owner.Schema, _t.Database, _renameTo.Trim());
        public string RenameSql => _t == null || string.IsNullOrWhiteSpace(_renameTo) || SameName(_renameTo, _t.Name) ? "" : TableOps.Rename(_t, _renameTo.Trim());

        public string CopyName { get => _copyName; set { if (Set(ref _copyName, value ?? "")) Copied(); } }
        public string CopyDatabase { get => _copyDb; set { if (Set(ref _copyDb, value)) Copied(); } }
        public bool CopyWithData { get => _copyData; set { if (Set(ref _copyData, value)) Copied(); } }
        public string CopyProblem => _t == null ? null : TableOps.NewNameProblem(_owner.Schema, _copyDb ?? _t.Database, _copyName.Trim());
        public string CopySql => _t == null || string.IsNullOrWhiteSpace(_copyName) || _isView ? "" : string.Join("\n", TableOps.Copy(_t, _copyDb ?? _t.Database, _copyName.Trim(), _copyData));
        private void Copied() { OnPropertyChanged(nameof(CopyProblem)); OnPropertyChanged(nameof(CopySql)); }

        public string MoveDatabase { get => _moveDb; set { if (Set(ref _moveDb, value)) { OnPropertyChanged(nameof(MoveProblem)); OnPropertyChanged(nameof(MoveSql)); } } }
        public string MoveProblem => _t == null || _moveDb == null ? null : TableOps.NewNameProblem(_owner.Schema, _moveDb, _t.Name);
        public string MoveSql => _t == null || _moveDb == null ? "" : TableOps.Move(_t, _moveDb);
        public bool CanMove => OtherDatabases.Count > 0 && !_isView;

        public string Collation { get => _collation; set { if (Set(ref _collation, value)) OnPropertyChanged(nameof(ConvertSql)); } }
        public string CurrentCollation => _t?.Collation ?? "";
        public string ConvertSql => _t == null || _collation == null || string.Equals(_collation, _t.Collation, StringComparison.OrdinalIgnoreCase) ? "" : TableOps.ConvertCollation(_t, _collation);

        public string ReferencedBy { get => _referenced; private set { Set(ref _referenced, value); OnPropertyChanged(nameof(HasReferences)); } }
        public bool HasReferences => !string.IsNullOrEmpty(_referenced);
        public string EmptySql => _t == null ? "" : TableOps.Empty(_t);
        public string DropSql => _t == null ? "" : TableOps.Drop(_t, _isView);
        public string DropLabel => _isView ? "Drop view" : "Drop table";

        public ICommand RenameCommand { get; }
        public ICommand CopyCommand { get; }
        public ICommand MoveCommand { get; }
        public ICommand ConvertCommand { get; }
        public ICommand MaintenanceCommand { get; }
        public ICommand EmptyCommand { get; }
        public ICommand DropCommand { get; }

        private static bool SameName(string a, string b) => string.Equals((a ?? "").Trim(), b, StringComparison.Ordinal);

        public async Task LoadAsync(TableDesign t, bool isView)
        {
            _t = t;
            _isView = isView;
            if (t == null) return;
            _renameTo = t.Name;
            _copyName = t.Name + "_copy";
            Databases.Clear(); OtherDatabases.Clear();
            foreach (var d in _owner.Schema.Databases.Keys.Where(d => !SchemaSnapshot.IsSystem(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                Databases.Add(d);
                if (!string.Equals(d, t.Database, StringComparison.OrdinalIgnoreCase)) OtherDatabases.Add(d);
            }
            _copyDb = t.Database;
            _moveDb = OtherDatabases.FirstOrDefault();
            _collation = t.Collation;
            MaintenanceLines.Clear();
            ReferencedBy = "";
            try
            {
                using (var c = await _owner.OpenConnectionAsync())
                {
                    if (Collations.Count == 0) foreach (var n in await TableOps.CollationsAsync(c)) Collations.Add(n);
                    var refs = isView ? new List<string>() : await TableOps.ReferencedByAsync(c, t.Database, t.Name);
                    ReferencedBy = refs.Count == 0 ? "" : "Other tables point here: " + string.Join(", ", refs) + ".";
                }
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { AppLog.Error("operations load", ex); }
            foreach (var p in new[] { nameof(IsTable), nameof(Name), nameof(RenameTo), nameof(RenameProblem), nameof(RenameSql), nameof(CopyName), nameof(CopyDatabase), nameof(CopyProblem), nameof(CopySql),
                                      nameof(MoveDatabase), nameof(MoveProblem), nameof(MoveSql), nameof(CanMove), nameof(Collation), nameof(CurrentCollation), nameof(ConvertSql), nameof(EmptySql), nameof(DropSql), nameof(DropLabel), nameof(HasMaintenanceLines) })
                OnPropertyChanged(p);
        }

        private DbObjectKind Kind => _isView ? DbObjectKind.View : DbObjectKind.Table;

        private async Task RenameAsync()
        {
            var to = _renameTo.Trim();
            var r = await _owner.RunAsync(TableOps.Rename(_t, to), "Renamed " + _t.Name + " to " + to);
            if (r != null && r.Ok) await _owner.ReopenAsync(Kind, to, "operations");
        }

        private async Task CopyAsync()
        {
            var db = _copyDb ?? _t.Database;
            var name = _copyName.Trim();
            var r = await _owner.RunAsync(string.Join("\n", TableOps.Copy(_t, db, name, _copyData)), "Copied " + _t.Name + " to " + db + "." + name);
            if (r != null && r.Ok) { _owner.Toast("Copied to " + db + "." + name + (_copyData ? ", rows and all." : ", empty.")); await _owner.ReopenAsync(Kind, _t.Name, "operations"); }
        }

        private async Task MoveAsync()
        {
            var to = _moveDb;
            var choice = await _owner.AskAsync(new ChoiceSlip("MOVE", "Move " + _t.Name + " to " + to + "?",
                "It leaves " + _t.Database + ". PHP code that uses " + _t.Database + "." + _t.Name + " will stop finding it. Undo can move it back.",
                alarm: false, primary: "Move it", secondary: null, detail: TableOps.Move(_t, to)));
            if (choice != SlipChoice.Primary) return;
            var r = await _owner.RunAsync(TableOps.Move(_t, to), "Moved " + _t.Name + " to " + to);
            if (r != null && r.Ok) { _owner.Toast("Moved to " + to + "."); await _owner.ReopenAsync(Kind, null, null); }
        }

        private async Task ConvertAsync()
        {
            var to = _collation;
            var choice = await _owner.AskAsync(new ChoiceSlip("COLLATION", "Change to " + to + "?",
                "Every text column is converted and the table is rebuilt. Sorting and comparing text follow the new rules. " +
                (DdlBuilder.CharsetOf(to) != "utf8mb4" ? "This character set can't store every language or emoji; anything it can't hold becomes '?'. " : "") +
                "Undo keeps a copy.", alarm: DdlBuilder.CharsetOf(to) != "utf8mb4", primary: "Convert it", secondary: null, detail: TableOps.ConvertCollation(_t, to)));
            if (choice != SlipChoice.Primary) return;
            var r = await _owner.RunAsync(TableOps.ConvertCollation(_t, to), "Converted " + _t.Name + " to " + to);
            if (r != null && r.Ok) await _owner.ReloadAsync(keepPage: true);
        }

        private async Task MaintainAsync(string verb)
        {
            if (verb == null) return;
            var r = await _owner.RunAsync(TableOps.Maintenance(_t, verb), verb + " TABLE " + _t.Name);
            MaintenanceLines.Clear();
            if (r != null && r.Ok && r.Results.Count > 0 && r.Results[0].Tables.Count > 0)
                foreach (System.Data.DataRow row in r.Results[0].Tables[0].Rows.Rows)
                    MaintenanceLines.Add(verb + " · " + Plain(Convert.ToString(row[2], System.Globalization.CultureInfo.InvariantCulture), Convert.ToString(row[3], System.Globalization.CultureInfo.InvariantCulture)));
            OnPropertyChanged(nameof(HasMaintenanceLines));
        }

        private static string Plain(string type, string text)
        {
            if (text.IndexOf("doing recreate + analyze", StringComparison.OrdinalIgnoreCase) >= 0) return "InnoDB rebuilt the table instead (that is how it optimizes).";
            if (text.IndexOf("doesn't support repair", StringComparison.OrdinalIgnoreCase) >= 0) return "InnoDB repairs itself when the server starts; nothing to do here.";
            if (string.Equals(type, "status", StringComparison.OrdinalIgnoreCase) && string.Equals(text, "OK", StringComparison.OrdinalIgnoreCase)) return "OK, all good.";
            if (string.Equals(text, "Table is already up to date", StringComparison.OrdinalIgnoreCase)) return "Already up to date.";
            return type + ": " + text;
        }

        private async Task EmptyAsync()
        {
            long? rows = null;
            try { using (var c = await _owner.OpenConnectionAsync()) using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM " + DdlBuilder.TableName(_t), c)) rows = Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { AppLog.Error("count", ex); }
            var choice = await _owner.AskAsync(new ChoiceSlip("CAREFUL", "Empty " + _t.Name + "?",
                "Deletes " + (rows.HasValue ? "all " + rows.Value.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " rows" : "every row") +
                ". The table and its columns stay. Undo keeps a copy, so you can take it back." + (HasReferences ? " " + _referenced : ""),
                alarm: true, primary: "Empty it", secondary: null, detail: TableOps.Empty(_t)));
            if (choice != SlipChoice.Primary) return;
            var r = await _owner.RunAsync(TableOps.Empty(_t), "Emptied " + _t.Name);
            if (r != null && r.Ok) await _owner.ReloadAsync(keepPage: true);
        }

        private async Task DropAsync()
        {
            var choice = await _owner.AskAsync(new ChoiceSlip("CAREFUL", (_isView ? "Drop the view " : "Drop the table ") + _t.Name + "?",
                (_isView ? "The view goes; the tables it reads stay." : "The table and every row in it go. Undo keeps a copy, so you can take it back.") +
                (HasReferences ? " " + _referenced + " They stop working until it is back." : ""),
                alarm: true, primary: DropLabel, secondary: null, detail: TableOps.Drop(_t, _isView)));
            if (choice != SlipChoice.Primary) return;
            var name = _t.Name;
            var r = await _owner.RunAsync(TableOps.Drop(_t, _isView), "Dropped " + name);
            if (r != null && r.Ok) { _owner.Toast("Dropped " + name + ". Undo change can bring it back."); await _owner.ReopenAsync(Kind, null, null); }
        }
    }
}
