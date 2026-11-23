using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using LuminaDB.Core;
using LuminaDB.Core.Sql;
using LuminaDB.Core.Tables;
using MySqlConnector;

namespace LuminaDB.App.ViewModels
{
    /// <summary>A column of the Rows grid: c0, c1… hold what is shown; k0, k1… hold the exact key values.</summary>
    public sealed class RowColumn
    {
        public string Key { get; set; }
        public string Name { get; set; }
        public string TypeName { get; set; }
        public bool IsNumeric { get; set; }
        public bool IsKey { get; set; }
    }

    /// <summary>One line of the filter builder: column · operator · value.</summary>
    public sealed class FilterRow : ObservableObject
    {
        private string _column, _op = "=", _value = "";
        private readonly Action _changed;
        public FilterRow(IReadOnlyList<string> columns, Action changed, Action<FilterRow> remove)
        {
            Columns = columns;
            _column = columns.FirstOrDefault();
            _changed = changed;
            RemoveCommand = new RelayCommand(() => remove(this));
        }
        public IReadOnlyList<string> Columns { get; }
        public IReadOnlyList<string> Ops => RowFilter.Ops;
        public string Column { get => _column; set { if (Set(ref _column, value)) _changed(); } }
        public string Op { get => _op; set { if (Set(ref _op, value)) { OnPropertyChanged(nameof(NeedsValue)); _changed(); } } }
        public string Value { get => _value; set => Set(ref _value, value ?? ""); }
        public bool NeedsValue => new RowFilter { Op = _op }.NeedsValue;
        public ICommand RemoveCommand { get; }
        public RowFilter ToFilter() => new RowFilter { Column = _column, Op = _op, Value = _value };
    }

    /// <summary>The Rows page: a page of the table, searched, filtered and sorted on the server; insert, edit, delete.</summary>
    public sealed class RowsPage : ObservableObject, IDisposable
    {
        public const int PageSize = 100;
        private const int CellChars = 300;
        private readonly TablesViewModel _owner;
        private readonly DispatcherTimer _searchTimer;
        private string _search = "";
        private string _sortColumn;
        private bool _sortDesc;
        private long _offset;
        private long _total;
        private bool _loading;

        public RowsPage(TablesViewModel owner)
        {
            _owner = owner;
            _searchTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(380), DispatcherPriority.Background, async (s, e) => { _searchTimer.Stop(); _offset = 0; await ReloadAsync(); }, owner.Ui);
            _searchTimer.Stop();

            AddFilterCommand = new RelayCommand(AddFilter, () => Table != null);
            ApplyFiltersCommand = new AsyncCommand(async () => { _offset = 0; await ReloadAsync(); }, () => Table != null && !_loading);
            ClearFiltersCommand = new AsyncCommand(async () => { Filters.Clear(); _search = ""; OnPropertyChanged(nameof(Search)); OnPropertyChanged(nameof(HasFilters)); _offset = 0; await ReloadAsync(); }, () => Table != null);
            FirstCommand = new AsyncCommand(() => GoAsync(0), () => _offset > 0 && !_loading);
            PrevCommand = new AsyncCommand(() => GoAsync(Math.Max(0, _offset - PageSize)), () => _offset > 0 && !_loading);
            NextCommand = new AsyncCommand(() => GoAsync(_offset + PageSize), () => _offset + PageSize < _total && !_loading);
            LastCommand = new AsyncCommand(() => GoAsync(Math.Max(0, (_total - 1) / PageSize * PageSize)), () => _offset + PageSize < _total && !_loading);
            ReloadCommand = new AsyncCommand(ReloadAsync, () => Table != null && !_loading);
            NewRowCommand = new AsyncCommand(() => OpenFormAsync(null, false), () => CanEdit && _owner.CanAct);
            EditRowCommand = new AsyncCommand(p => OpenFormAsync(p as IDictionary<string, string> ?? SelectedKeys.FirstOrDefault(), false), p => CanEdit && _owner.CanAct && (p != null || SelectedKeys.Count > 0));
            DuplicateRowCommand = new AsyncCommand(() => OpenFormAsync(SelectedKeys.FirstOrDefault(), true), () => CanEdit && _owner.CanAct && SelectedKeys.Count == 1);
            DeleteRowsCommand = new AsyncCommand(DeleteAsync, () => CanEdit && _owner.CanAct && SelectedKeys.Count > 0);
        }

        public TableDesign Table { get; private set; }
        public bool IsView { get; private set; }
        public DataTable Data { get; private set; }
        public List<RowColumn> Columns { get; } = new List<RowColumn>();
        /// <summary>The grid must be rebuilt (new table, new page).</summary>
        public event Action DataReloaded;

        public string SortColumn => _sortColumn;
        public bool SortDescending => _sortDesc;

        public string Search
        {
            get => _search;
            set { if (Set(ref _search, value ?? "")) { _searchTimer.Stop(); _searchTimer.Start(); OnPropertyChanged(nameof(HasFilters)); } }
        }

        public ObservableCollection<FilterRow> Filters { get; } = new ObservableCollection<FilterRow>();
        public bool HasFilters => Filters.Count > 0 || _search.Length > 0;
        public bool HasFilterRows => Filters.Count > 0;

        public bool CanEdit => Table != null && !IsView && Table.RowKey != null;
        public string NoKeyNote => Table == null || IsView ? (IsView ? "A view shows rows from other tables; change them there." : null)
            : Table.RowKey == null ? "This table has no primary key, so one row can't be told from another. Add a key in Structure to edit rows here." : null;
        public bool HasNoKeyNote => NoKeyNote != null;

        private string _pageText = "", _querySql = "", _emptyText = "";
        public string PageText { get => _pageText; private set => Set(ref _pageText, value); }
        public string QuerySql { get => _querySql; private set => Set(ref _querySql, value); }
        public string EmptyText { get => _emptyText; private set { Set(ref _emptyText, value); OnPropertyChanged(nameof(IsEmpty)); } }
        public bool IsEmpty => !string.IsNullOrEmpty(_emptyText);

        /// <summary>Keys of the rows selected in the grid (set by the view).</summary>
        public List<IDictionary<string, string>> SelectedKeys { get; private set; } = new List<IDictionary<string, string>>();
        public void SetSelection(IEnumerable<IDictionary<string, string>> keys)
        {
            SelectedKeys = keys.ToList();
            OnPropertyChanged(nameof(SelectionText));
            CommandManager.InvalidateRequerySuggested();
        }
        public string SelectionText => SelectedKeys.Count == 0 ? "" : SelectedKeys.Count == 1 ? "1 row selected" : SelectedKeys.Count + " rows selected";

        public ICommand AddFilterCommand { get; }
        public ICommand ApplyFiltersCommand { get; }
        public ICommand ClearFiltersCommand { get; }
        public ICommand FirstCommand { get; }
        public ICommand PrevCommand { get; }
        public ICommand NextCommand { get; }
        public ICommand LastCommand { get; }
        public ICommand ReloadCommand { get; }
        public ICommand NewRowCommand { get; }
        public ICommand EditRowCommand { get; }
        public ICommand DuplicateRowCommand { get; }
        public ICommand DeleteRowsCommand { get; }

        private RowForm _form;
        public RowForm Form { get => _form; private set { Set(ref _form, value); OnPropertyChanged(nameof(HasForm)); } }
        public bool HasForm => _form != null;
        internal void CloseForm() => Form = null;

        public void Clear()
        {
            Table = null;
            Data = null;
            Columns.Clear();
            Filters.Clear();
            _search = "";
            Form = null;
            SelectedKeys = new List<IDictionary<string, string>>();
            PageText = ""; QuerySql = ""; EmptyText = "";
            OnPropertyChanged(nameof(Search));
            OnPropertyChanged(nameof(HasFilters));
            OnPropertyChanged(nameof(HasFilterRows));
            OnPropertyChanged(nameof(CanEdit));
            DataReloaded?.Invoke();
        }

        public async Task LoadAsync(TableDesign t, bool resetQuery)
        {
            if (t == null) { Clear(); return; }
            if (resetQuery)
            {
                Filters.Clear();
                _search = "";
                _sortColumn = null;
                _sortDesc = false;
                _offset = 0;
                Form = null;
                OnPropertyChanged(nameof(Search));
            }
            Table = t;
            IsView = _owner.Find(DbObjectKind.View, t.Name) != null && _owner.Find(DbObjectKind.Table, t.Name) == null;
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(NoKeyNote));
            OnPropertyChanged(nameof(HasNoKeyNote));
            OnPropertyChanged(nameof(HasFilters));
            OnPropertyChanged(nameof(HasFilterRows));
            await ReloadAsync();
        }

        private void AddFilter()
        {
            if (Table == null) return;
            Filters.Add(new FilterRow(Table.Columns.Select(c => c.Name).ToList(), () => { }, f => { Filters.Remove(f); OnPropertyChanged(nameof(HasFilters)); OnPropertyChanged(nameof(HasFilterRows)); _ = ReloadAsync(); }));
            OnPropertyChanged(nameof(HasFilters));
            OnPropertyChanged(nameof(HasFilterRows));
        }

        public async Task SortByAsync(string column)
        {
            if (Table?.Column(column) == null) return;
            if (string.Equals(_sortColumn, column, StringComparison.OrdinalIgnoreCase))
            {
                if (_sortDesc) { _sortColumn = null; _sortDesc = false; }   // third click: back to key order
                else _sortDesc = true;
            }
            else { _sortColumn = column; _sortDesc = false; }
            _offset = 0;
            await ReloadAsync();
        }

        private async Task GoAsync(long offset) { _offset = offset; await ReloadAsync(); }

        private RowQuery Query() => new RowQuery
        {
            Table = Table,
            Search = _search,
            Filters = Filters.Where(f => !f.NeedsValue || f.Value.Length > 0).Select(f => f.ToFilter()).ToList(),
            SortColumn = _sortColumn,
            SortDescending = _sortDesc,
            Offset = _offset,
            Limit = PageSize,
        };

        public async Task ReloadAsync()
        {
            if (Table == null || !_owner.IsRunning || _loading) return;
            _loading = true;
            CommandManager.InvalidateRequerySuggested();
            var q = Query();
            QuerySql = q.SelectSql();
            var t = Table;
            try
            {
                var dt = new DataTable();
                Columns.Clear();
                var key = t.RowKey ?? Array.Empty<string>();
                using (var c = await _owner.OpenConnectionAsync())
                {
                    using (var count = new MySqlCommand(q.CountSql(), c))
                        _total = Convert.ToInt64(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
                    if (_offset >= _total && _total > 0) { _offset = Math.Max(0, (_total - 1) / PageSize * PageSize); q.Offset = _offset; QuerySql = q.SelectSql(); }
                    using (var cmd = new MySqlCommand(q.SelectSql(), c))
                    using (var r = await cmd.ExecuteReaderAsync())
                    {
                        int n = r.FieldCount;
                        var keyIndex = new int[key.Count];
                        for (int i = 0; i < n; i++)
                        {
                            var name = r.GetName(i);
                            var type = r.GetDataTypeName(i);
                            var col = t.Column(name);
                            Columns.Add(new RowColumn { Key = "c" + i, Name = name, TypeName = col?.FullType.ToLowerInvariant() ?? type.ToLowerInvariant(), IsNumeric = NotebookSession.IsNumericType(type), IsKey = key.Contains(name, StringComparer.OrdinalIgnoreCase) });
                            dt.Columns.Add("c" + i, typeof(object));
                        }
                        for (int k = 0; k < key.Count; k++)
                        {
                            dt.Columns.Add("k" + k, typeof(string));
                            keyIndex[k] = Columns.FindIndex(x => string.Equals(x.Name, key[k], StringComparison.OrdinalIgnoreCase));
                        }
                        dt.BeginLoadData();
                        var values = new object[n + key.Count];
                        while (await r.ReadAsync())
                        {
                            var raw = new object[n];
                            for (int i = 0; i < n; i++)
                            {
                                object v;
                                try { v = r.GetValue(i); }
                                catch (Exception ex) when (ex is InvalidCastException || ex is MySqlConversionException) { v = r.IsDBNull(i) ? (object)DBNull.Value : r.GetString(i); }
                                raw[i] = v;
                                values[i] = v is DBNull ? DBNull.Value : (object)NotebookSession.Display(v, r.GetDataTypeName(i), CellChars);
                            }
                            for (int k = 0; k < key.Count; k++)
                                values[n + k] = keyIndex[k] >= 0 ? RowStore.Text(raw[keyIndex[k]], t.Column(key[k])) : null;
                            dt.Rows.Add(values);
                        }
                        dt.EndLoadData();
                    }
                }
                Data = dt;
                long from = dt.Rows.Count == 0 ? 0 : _offset + 1, to = _offset + dt.Rows.Count;
                PageText = _total == 0 ? "no rows" : from.ToString("N0", CultureInfo.InvariantCulture) + "–" + to.ToString("N0", CultureInfo.InvariantCulture) + " of " + _total.ToString("N0", CultureInfo.InvariantCulture);
                EmptyText = _total > 0 ? "" : HasFilters ? "No rows match. Change the search or the filters." : IsView ? "This view has no rows right now." : "No rows yet." + (CanEdit ? " New row adds one." : "");
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException)
            {
                Data = null;
                Columns.Clear();
                PageText = "";
                var me = ex as MySqlException;
                var e = me != null ? ErrorExplainer.Explain(me.Number, me.Message, SqlSplitter.Split(q.SelectSql()).FirstOrDefault(), _owner.Schema, _owner.Database) : null;
                EmptyText = e != null ? e.Title + ". " + e.Body : ex.Message;
            }
            finally
            {
                _loading = false;
                SetSelection(Array.Empty<IDictionary<string, string>>());
                DataReloaded?.Invoke();
                CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>The key of a grid row (from the hidden k0, k1… columns).</summary>
        public IDictionary<string, string> KeyOf(DataRowView row)
        {
            var key = Table?.RowKey;
            if (key == null || row == null) return null;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int k = 0; k < key.Count; k++) d[key[k]] = row["k" + k] as string;
            return d;
        }

        // ------------------------------------------------------------------ form

        private async Task OpenFormAsync(IDictionary<string, string> key, bool duplicate)
        {
            if (!CanEdit) return;
            Dictionary<string, string> row = null;
            if (key != null)
            {
                try
                {
                    using (var c = await _owner.OpenConnectionAsync()) row = await RowStore.LoadRowAsync(c, Table, key);
                }
                catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { _owner.Toast("Couldn't read that row: " + ex.Message); return; }
                if (row == null) { _owner.Toast("That row isn't there any more. Reloading."); await ReloadAsync(); return; }
            }
            bool insert = key == null || duplicate;
            var form = new RowForm(this, Table, insert, row, duplicate ? null : key);
            Form = form;
            await form.LoadLookupsAsync();
        }

        internal async Task<bool> SaveAsync(RowForm form, string sql, string summary)
        {
            var r = await _owner.RunAsync(sql, summary);
            if (r == null || !r.Ok) { form.ServerProblem = _owner.Error?.Title; return false; }
            var first = r.Results.FirstOrDefault();
            if (form.IsInsert && first != null && first.LastInsertId > 0 && Table.Columns.Any(c => c.AutoIncrement))
                _owner.Toast("Added. Its id is " + first.LastInsertId.ToString(CultureInfo.InvariantCulture) + ". Undo change can take it back.");
            Form = null;
            await _owner.LoadDesignAsync();
            Table = _owner.Design ?? Table;
            await ReloadAsync();
            return true;
        }

        private async Task DeleteAsync()
        {
            var keys = SelectedKeys.ToList();
            if (keys.Count == 0 || Table == null) return;
            var sql = RowSql.Delete(Table, keys);
            var what = keys.Count == 1 ? "this row" : keys.Count + " rows";
            var choice = await _owner.AskAsync(new ChoiceSlip("DELETE", "Delete " + what + "?",
                "From " + Table.Name + ". Undo keeps a copy, so you can take it back. Rows in other tables that point here may stop it, or go with it (ON DELETE CASCADE).",
                alarm: true, primary: "Delete " + what, secondary: null, detail: sql));
            if (choice != SlipChoice.Primary) return;
            var r = await _owner.RunAsync(sql, "Deleted " + what + " from " + Table.Name);
            if (r != null && r.Ok)
            {
                Form = null;
                await ReloadAsync();
            }
        }

        // ------------------------------------------------------------------ lookups for the form

        internal async Task<List<LookupItem>> LookupAsync(ForeignKeyDef fk, string search)
        {
            var refDb = fk.RefDatabase ?? Table.Database;
            var refCol = fk.RefColumns.FirstOrDefault();
            if (refCol == null) return new List<LookupItem>();
            var label = RowStore.LabelColumn(_owner.Schema.FindTable(refDb, fk.RefTable), refCol);
            try
            {
                using (var c = await _owner.OpenConnectionAsync())
                    return await RowStore.LookupAsync(c, refDb, fk.RefTable, refCol, label, search, 60);
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { AppLog.Error("lookup failed", ex); return new List<LookupItem>(); }
        }

        /// <summary>The label of the referenced row, or null when there's no row with that value.</summary>
        internal async Task<(bool found, string label)> FindReferencedAsync(ForeignKeyDef fk, ColumnDef col, string value)
        {
            var refDb = fk.RefDatabase ?? Table.Database;
            var refCol = fk.RefColumns.FirstOrDefault();
            var label = RowStore.LabelColumn(_owner.Schema.FindTable(refDb, fk.RefTable), refCol);
            var sql = "SELECT " + (label != null ? DdlBuilder.Q(label) : "1") + " FROM " + DdlBuilder.Q(refDb) + "." + DdlBuilder.Q(fk.RefTable) +
                      " WHERE " + DdlBuilder.Q(refCol) + " = " + RowSql.Literal(col, value) + " LIMIT 1";
            try
            {
                using (var c = await _owner.OpenConnectionAsync())
                using (var cmd = new MySqlCommand(sql, c))
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    if (!await r.ReadAsync()) return (false, null);
                    return (true, label == null || r.IsDBNull(0) ? null : RowStore.Text(r.GetValue(0), null));
                }
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { AppLog.Error("lookup check failed", ex); return (true, null); }
        }

        public void Dispose() => _searchTimer.Stop();
    }

    /// <summary>The insert / edit form, generated from the table's columns.</summary>
    public sealed class RowForm : ObservableObject
    {
        private readonly RowsPage _page;
        private readonly TableDesign _t;
        private readonly Dictionary<string, string> _original;
        private readonly IDictionary<string, string> _key;
        private string _sql = "", _problemText = "", _serverProblem;
        private bool _saving;

        public RowForm(RowsPage page, TableDesign t, bool insert, Dictionary<string, string> row, IDictionary<string, string> key)
        {
            _page = page;
            _t = t;
            IsInsert = insert;
            _original = row ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _key = key;
            foreach (var c in t.Columns)
            {
                var fk = t.ForeignKeys.FirstOrDefault(f => f.Columns.Count == 1 && string.Equals(f.Columns[0], c.Name, StringComparison.OrdinalIgnoreCase));
                row?.TryGetValue(c.Name, out _);
                string value = null;
                bool has = row != null && row.TryGetValue(c.Name, out value);
                var field = new FieldEditor(this, c, fk, insert, has ? value : null, has);
                // a duplicated row gets a fresh auto number
                if (insert && row != null && c.AutoIncrement) field.UseDefault = true;
                Fields.Add(field);
            }
            SaveCommand = new AsyncCommand(SaveAsync, () => !_saving);
            CancelCommand = new RelayCommand(() => _page.CloseForm());
            Recompute();
        }

        public bool IsInsert { get; }
        public string Title => IsInsert ? "New row" : "Row " + string.Join(", ", _key.Select(k => k.Key + " = " + (k.Value ?? "NULL")));
        public string Eyebrow => IsInsert ? "INSERT INTO " + _t.Name.ToUpperInvariant() : "EDIT · " + _t.Name.ToUpperInvariant();
        public ObservableCollection<FieldEditor> Fields { get; } = new ObservableCollection<FieldEditor>();
        public string Sql { get => _sql; private set => Set(ref _sql, value); }
        public string ProblemText { get => _problemText; private set { Set(ref _problemText, value); OnPropertyChanged(nameof(HasProblems)); } }
        public bool HasProblems => !string.IsNullOrEmpty(_problemText);
        public string ServerProblem { get => _serverProblem; set { Set(ref _serverProblem, value); OnPropertyChanged(nameof(HasServerProblem)); } }
        public bool HasServerProblem => !string.IsNullOrEmpty(_serverProblem);
        public string SaveLabel => IsInsert ? "Add row" : "Save changes";
        public ICommand SaveCommand { get; }
        public ICommand CancelCommand { get; }

        internal RowsPage Page => _page;
        internal string OriginalOf(string column) => _original.TryGetValue(column, out var v) ? v : null;

        internal async Task LoadLookupsAsync()
        {
            foreach (var f in Fields.Where(f => f.ForeignKey != null)) await f.RefreshLookupAsync(null);
        }

        private Dictionary<string, FieldValue> Values(bool changedOnly) =>
            Fields.Where(f => !f.Column.IsGenerated && (!changedOnly || f.IsChanged)).ToDictionary(f => f.Column.Name, f => f.Value, StringComparer.OrdinalIgnoreCase);

        private bool _tried;

        internal void Recompute()
        {
            int bad = 0;
            foreach (var f in Fields)
            {
                var p = f.Column.IsGenerated || (!IsInsert && !f.IsChanged) ? null : RowSql.Problem(f.Column, f.Value, IsInsert);
                if (p == null && f.ReferenceProblem != null && !f.Value.IsNull && !f.Value.UseDefault) p = f.ReferenceProblem;
                // a required value nobody typed: after the first Save, say so instead of quietly sending ''
                if (p == null && _tried && IsInsert && !f.Touched && !f.Column.IsGenerated && !f.Column.Nullable && !f.Column.AutoIncrement
                    && string.IsNullOrEmpty(f.Column.Default) && !f.Value.UseDefault && string.IsNullOrEmpty(f.Text))
                    p = f.Name + " is required and has no default. Type a value.";
                f.Problem = p;
                if (p != null) bad++;
            }
            try
            {
                Sql = IsInsert ? RowSql.Insert(_t, Values(false)) : RowSql.Update(_t, _key, Values(true)) ?? "-- nothing changed yet";
            }
            catch (LuminaException ex) { Sql = "-- " + ex.Message; }
            var badFields = Fields.Where(f => f.HasProblem).ToList();
            ProblemText = bad == 0 ? "" : bad == 1 ? badFields[0].Problem
                : bad + " fields need fixing first: " + string.Join(", ", badFields.Select(f => f.Name)) + ".";
        }

        private async Task SaveAsync()
        {
            _tried = true;
            Recompute();
            if (HasProblems) return;
            if (!IsInsert && Sql.StartsWith("--", StringComparison.Ordinal)) { _page.CloseForm(); return; }
            _saving = true;
            ServerProblem = null;
            try
            {
                var summary = IsInsert ? "Added a row to " + _t.Name : "Changed " + Title.Substring(4) + " in " + _t.Name;
                await _page.SaveAsync(this, Sql, summary);
            }
            finally { _saving = false; CommandManager.InvalidateRequerySuggested(); }
        }
    }

    /// <summary>One field of the row form, with the right editor for its type.</summary>
    public sealed class FieldEditor : ObservableObject
    {
        private readonly RowForm _form;
        private readonly DispatcherTimer _lookupTimer;
        private string _text;
        private bool _isNull, _useDefault;
        private string _problem;
        private string _referenceLabel, _referenceProblem;

        public FieldEditor(RowForm form, ColumnDef c, ForeignKeyDef fk, bool insert, string value, bool hasValue)
        {
            _form = form;
            Column = c;
            ForeignKey = fk;
            var t = (c.BaseType ?? "").ToLowerInvariant();
            Kind = c.IsGenerated ? "generated"
                : fk != null ? "fk"
                : t == "enum" ? "enum"
                : t == "tinyint" && c.Length == "1" || t == "boolean" || t == "bool" ? "bool"
                : t == "date" ? "date"
                : t == "datetime" || t == "timestamp" ? "datetime"
                : t == "json" || t == "text" || t == "mediumtext" || t == "longtext" ? "long"
                : "text";
            if (Kind == "enum") Options = ColumnTypes.EnumValues(c.Length);
            _isNull = hasValue ? value == null : false;
            _text = value ?? "";
            if (insert && !hasValue)
            {
                // new row: start from the default the server would use
                _useDefault = CanUseDefault;
                if (!_useDefault && c.Nullable) _isNull = true;
                if (Kind == "bool" && !_useDefault && !c.Nullable) _text = "0";
            }
            _lookupTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background, async (s, e) => { _lookupTimer.Stop(); await RefreshLookupAsync(_text); }, Dispatcher.CurrentDispatcher);
            _lookupTimer.Stop();
            PickCommand = new RelayCommand(p => { if (p is LookupItem li) Text = li.Value; else if (p is string s) Text = s; });
        }

        public ColumnDef Column { get; }
        public ForeignKeyDef ForeignKey { get; }
        /// <summary>text · long · enum · bool · date · datetime · fk · generated</summary>
        public string Kind { get; }
        public bool IsText => Kind == "text";
        public bool IsLong => Kind == "long";
        public bool IsEnum => Kind == "enum";
        public bool IsBool => Kind == "bool";
        public bool IsDate => Kind == "date" || Kind == "datetime";
        public bool IsForeignKey => Kind == "fk";
        public bool IsGenerated => Kind == "generated";
        public IReadOnlyList<string> Options { get; } = Array.Empty<string>();
        public ObservableCollection<LookupItem> Lookup { get; } = new ObservableCollection<LookupItem>();
        public ICommand PickCommand { get; }

        public string Name => Column.Name;
        public string TypeLabel
        {
            get
            {
                var parts = new List<string> { Column.FullType.ToLowerInvariant() };
                if (!Column.Nullable) parts.Add("required");
                if (Column.AutoIncrement) parts.Add("auto number");
                if (IsGenerated) parts.Add("worked out by the server");
                if (ForeignKey != null) parts.Add("→ " + ForeignKey.RefTable + "." + ForeignKey.RefColumns.FirstOrDefault());
                return string.Join(" · ", parts);
            }
        }

        public bool CanBeNull => Column.Nullable && !IsGenerated;
        public bool CanUseDefault => !IsGenerated && (Column.AutoIncrement || !string.IsNullOrEmpty(Column.Default) && Column.Default != "NULL" || Column.Nullable);
        public string DefaultLabel => Column.AutoIncrement ? "next number" : !string.IsNullOrEmpty(Column.Default) && Column.Default != "NULL" ? "default: " + Column.Default : "default (empty)";

        public string Text
        {
            get => _text;
            set
            {
                if (!Set(ref _text, value ?? "")) return;
                Touched = true;
                if (_isNull) { _isNull = false; OnPropertyChanged(nameof(IsNull)); }
                if (_useDefault) { _useDefault = false; OnPropertyChanged(nameof(UseDefault)); }
                OnPropertyChanged(nameof(BoolValue));
                OnPropertyChanged(nameof(DateValue));
                OnPropertyChanged(nameof(IsChanged));
                OnPropertyChanged(nameof(ShowsValue));
                if (ForeignKey != null) { _lookupTimer.Stop(); _lookupTimer.Start(); }
                _form.Recompute();
            }
        }

        public bool IsNull
        {
            get => _isNull;
            set
            {
                if (!Set(ref _isNull, value)) return;
                if (value && _useDefault) { _useDefault = false; OnPropertyChanged(nameof(UseDefault)); }
                OnPropertyChanged(nameof(IsChanged));
                OnPropertyChanged(nameof(ShowsValue));
                _form.Recompute();
            }
        }

        public bool UseDefault
        {
            get => _useDefault;
            set
            {
                if (!Set(ref _useDefault, value)) return;
                if (value && _isNull) { _isNull = false; OnPropertyChanged(nameof(IsNull)); }
                OnPropertyChanged(nameof(IsChanged));
                OnPropertyChanged(nameof(ShowsValue));
                _form.Recompute();
            }
        }

        /// <summary>The text box shows a value (not NULL / default).</summary>
        public bool ShowsValue => !_isNull && !_useDefault;

        public bool BoolValue
        {
            get => _text == "1" || string.Equals(_text, "true", StringComparison.OrdinalIgnoreCase);
            set => Text = value ? "1" : "0";
        }

        /// <summary>For the calendar: the date part of the text, keeping any time already typed.</summary>
        public DateTime? DateValue
        {
            get
            {
                var s = (_text ?? "").Trim();
                if (s.Length >= 10 && DateTime.TryParseExact(s.Substring(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
                return null;
            }
            set
            {
                if (value == null) return;
                var date = value.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (Kind == "datetime")
                {
                    var s = (_text ?? "").Trim();
                    var time = s.Length > 10 ? s.Substring(10).Trim() : "00:00:00";
                    Text = date + " " + (time.Length == 0 ? "00:00:00" : time);
                }
                else Text = date;
            }
        }

        public FieldValue Value => _useDefault ? new FieldValue { UseDefault = true } : _isNull ? FieldValue.Of(null) : FieldValue.Of(_text);

        /// <summary>Someone typed in this field (an empty text typed on purpose is then allowed).</summary>
        public bool Touched { get; private set; }

        public bool IsChanged
        {
            get
            {
                if (_form.IsInsert) return true;
                if (_useDefault) return true;
                var was = _form.OriginalOf(Column.Name);
                return _isNull ? was != null : was == null || was != _text;
            }
        }

        public string Problem { get => _problem; set { Set(ref _problem, value); OnPropertyChanged(nameof(HasProblem)); } }
        public bool HasProblem => !string.IsNullOrEmpty(_problem);

        /// <summary>What the foreign-key value points at ("→ Networks"), or why it points nowhere.</summary>
        public string ReferenceLabel { get => _referenceLabel; private set { Set(ref _referenceLabel, value); OnPropertyChanged(nameof(HasReferenceLabel)); } }
        public bool HasReferenceLabel => !string.IsNullOrEmpty(_referenceLabel);
        public string ReferenceProblem => _referenceProblem;

        internal async Task RefreshLookupAsync(string search)
        {
            if (ForeignKey == null) return;
            var items = await _form.Page.LookupAsync(ForeignKey, string.IsNullOrWhiteSpace(search) || search == _form.OriginalOf(Column.Name) ? null : search);
            Lookup.Clear();
            foreach (var i in items) Lookup.Add(i);
            await CheckReferenceAsync();
        }

        private async Task CheckReferenceAsync()
        {
            var v = (_text ?? "").Trim();
            if (_isNull || _useDefault || v.Length == 0) { _referenceProblem = null; ReferenceLabel = null; _form.Recompute(); return; }
            var hit = Lookup.FirstOrDefault(i => i.Value == v);
            if (hit != null) { _referenceProblem = null; ReferenceLabel = string.IsNullOrEmpty(hit.Label) ? "→ found in " + ForeignKey.RefTable : "→ " + hit.Label; }
            else
            {
                var (found, label) = await _form.Page.FindReferencedAsync(ForeignKey, Column, v);
                _referenceProblem = found ? null : "There's no " + ForeignKey.RefTable + " row with " + ForeignKey.RefColumns.FirstOrDefault() + " = " + v + ". Pick one from the list.";
                ReferenceLabel = found ? (label == null ? "→ found in " + ForeignKey.RefTable : "→ " + label) : null;
            }
            _form.Recompute();
        }
    }
}
