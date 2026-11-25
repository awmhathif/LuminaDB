using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using LuminaDB.Core;
using LuminaDB.Core.Sql;
using LuminaDB.Core.Tables;
using MySqlConnector;

namespace LuminaDB.App.ViewModels
{
    [DataContract]
    public sealed class TablesSettings
    {
        [DataMember] public string Database { get; set; }
        [DataMember] public string Table { get; set; }
        [DataMember] public string Page { get; set; }
    }

    /// <summary>One name in the Tables index: a table, view, procedure, function, trigger or event.</summary>
    public sealed class IndexEntry : ObservableObject
    {
        private bool _selected;
        public DbObjectKind Kind { get; set; }
        public string Name { get; set; }
        public string Detail { get; set; }
        /// <summary>Triggers: the table they belong to.</summary>
        public string Table { get; set; }
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
        public bool IsTableLike => Kind == DbObjectKind.Table || Kind == DbObjectKind.View;
        public DbObjectInfo Info(string db) => new DbObjectInfo { Kind = Kind, Database = db, Name = Name, Table = Table, Detail = Detail };
    }

    public sealed class IndexGroup : ObservableObject
    {
        public IndexGroup(string title, DbObjectKind kind) { Title = title; Kind = kind; }
        public string Title { get; }
        public DbObjectKind Kind { get; }
        public ObservableCollection<IndexEntry> Items { get; } = new ObservableCollection<IndexEntry>();
        public string CountText => Items.Count.ToString(CultureInfo.InvariantCulture);
        public bool HasItems => Items.Count > 0;
        public void Changed() { OnPropertyChanged(nameof(CountText)); OnPropertyChanged(nameof(HasItems)); }
    }

    /// <summary>A page tab over the sheet: Rows · Structure · Operations · Definition.</summary>
    public sealed class PageChip : ObservableObject
    {
        private bool _selected;
        public PageChip(string key, string label) { Key = key; Label = label; }
        public string Key { get; }
        public string Label { get; }
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    }

    /// <summary>The Tables section (SPEC §5.4): browse, edit, design and look after tables and the other objects.</summary>
    public sealed partial class TablesViewModel : ObservableObject, IDisposable
    {
        private readonly MainViewModel _main;
        private readonly MariaDbService _server;
        private readonly AppPaths _paths;
        private readonly Dispatcher _ui;
        private readonly string _settingsFile;
        private readonly TablesSettings _settings;
        private NotebookSession _session;
        private SchemaSnapshot _schema = SchemaSnapshot.Empty;
        private string _db;
        private bool _busy;
        private string _busyText = "";

        public TablesViewModel(MainViewModel main, MariaDbService server, AppPaths paths, Dispatcher ui)
        {
            _main = main;
            _server = server;
            _paths = paths;
            _ui = ui;
            _settingsFile = Path.Combine(paths.Config, "tables.json");
            _settings = JsonFile.Read<TablesSettings>(_settingsFile) ?? new TablesSettings();
            if (server != null) _session = new NotebookSession(server);

            Groups = new List<IndexGroup>
            {
                new IndexGroup("TABLES", DbObjectKind.Table),
                new IndexGroup("VIEWS", DbObjectKind.View),
                new IndexGroup("PROCEDURES", DbObjectKind.Procedure),
                new IndexGroup("FUNCTIONS", DbObjectKind.Function),
                new IndexGroup("TRIGGERS", DbObjectKind.Trigger),
                new IndexGroup("EVENTS", DbObjectKind.Event),
            };

            Rows = new RowsPage(this);
            Structure = new StructureEditor(this);
            Operations = new OperationsPage(this);
            Definition = new ObjectEditor(this);

            OpenEntryCommand = new AsyncCommand(p => OpenAsync(p as IndexEntry, null), p => CanAct);
            SelectPageCommand = new AsyncCommand(p => SelectPageAsync(p as PageChip), p => !_busy);
            UseDatabaseCommand = new AsyncCommand(p => UseDatabaseAsync(p as string), p => CanAct);
            NewTableCommand = new AsyncCommand(NewTableAsync, () => CanAct && _db != null);
            NewObjectCommand = new AsyncCommand(p => NewObjectAsync(p is DbObjectKind k ? k : DbObjectKind.View), p => CanAct && _db != null);
            RefreshCommand = new AsyncCommand(() => ReloadAsync(keepPage: true), () => _main.IsRunning && !_busy);
            UndoCommand = new AsyncCommand(UndoAsync, () => CanAct);
            DismissErrorCommand = new RelayCommand(() => Error = null);
        }

        // ------------------------------------------------------------------ shared services for the pages

        internal MainViewModel Main => _main;
        internal SchemaSnapshot Schema => _schema;
        internal string Database => _db;
        internal Dispatcher Ui => _ui;
        internal Task<MySqlConnection> OpenConnectionAsync() => _server.OpenConnectionAsync(null);
        internal void Toast(string text) => _main.Toast(text);
        internal Task<SlipChoice> AskAsync(ChoiceSlip q) => _main.AskAsync(q);

        public bool IsRunning => _main.IsRunning;
        internal bool CanAct => _main.IsRunning && !_busy && _session != null && !_main.NeedsPassword && !_main.HasSlip;

        public bool IsBusy
        {
            get => _busy;
            private set { if (Set(ref _busy, value)) { OnPropertyChanged(nameof(BusyText)); CommandManager.InvalidateRequerySuggested(); } }
        }
        public string BusyText { get => _busy ? _busyText : ""; private set { _busyText = value ?? ""; OnPropertyChanged(); } }

        /// <summary>Runs SQL through the session: one Undo copy for the whole script, explained errors, schema refreshed after.</summary>
        internal async Task<ScriptResult> RunAsync(string script, string summary, bool useDatabase = false)
        {
            if (!CanAct) return null;
            IsBusy = true;
            BusyText = "Running…";
            Error = null;
            ScriptResult result = null;
            try
            {
                if (useDatabase && _db != null && !string.Equals(_session.CurrentDatabase, _db, StringComparison.OrdinalIgnoreCase))
                    await _session.UseAsync(_db);
                // SPEC §5.10: dropping a table or column, or emptying one, is backed up first
                var infos = SqlSplitter.Split(script).Select(st => StatementInfo.Analyze(st, _session.CurrentDatabase ?? _db)).ToList();
                var toBackUp = SafetyGate.DatabasesToBackUp(infos, _schema);
                if (toBackUp.Count > 0)
                {
                    BusyText = "Backing up " + string.Join(", ", toBackUp) + " first…";
                    if (!await _main.BackUpBeforeAsync(toBackUp, "Before " + summary)) return null;
                    BusyText = "Running…";
                }
                result = await _session.RunScriptAsync(script, _schema, summary);
                if (!result.Ok && result.Failed != null) ShowError(result.Failed);
                else if (result.Ok)
                {
                    var undo = result.Undo?.Covered == true ? " Undo change can take it back." : "";
                    LastChange = summary + " · " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) + (result.Undo?.Covered == true ? " · Undo copy saved" : "");
                    if (result.Changed) Toast("Done." + undo);
                }
            }
            catch (Exception ex) when (ex is LuminaException || ex is MySqlException)
            {
                Error = new ErrorSheet { Title = "That didn't run", Body = ex.Message, ServerText = "", CopyCommand = new RelayCommand(() => _main.CopyText(ex.Message, "Copied.")) };
            }
            finally
            {
                IsBusy = false;
            }
            if (result != null && result.Results.Any(r => r.Ok && r.Info != null && (r.Info.ChangesData || r.Info.Kind == StatementKind.Schema)))
            {
                await LoadSchemaAsync();
                _main.DataChanged();
            }
            return result;
        }

        private ErrorSheet _error;
        public ErrorSheet Error { get => _error; internal set { Set(ref _error, value); OnPropertyChanged(nameof(HasError)); } }
        public bool HasError => _error != null;
        public ICommand DismissErrorCommand { get; }

        private void ShowError(StatementResult r)
        {
            var e = r.Explanation;
            Error = new ErrorSheet
            {
                Title = e?.Title ?? (r.Cancelled ? "Stopped" : "That didn't work"),
                Body = e?.Body ?? r.ErrorMessage,
                ServerText = r.ErrorCode > 0 ? "#" + r.ErrorCode + " · " + r.ErrorMessage : r.ErrorMessage,
                CopyCommand = new RelayCommand(() => _main.CopyText("MariaDB error #" + r.ErrorCode + ": " + r.ErrorMessage + Environment.NewLine + r.Statement?.Text, "Copied the error and the SQL.")),
            };
            if (e != null && e.IsConnectionProblem) _session.Reset();
        }

        private string _lastChange;
        public string LastChange { get => _lastChange; private set { Set(ref _lastChange, value); OnPropertyChanged(nameof(HasLastChange)); } }
        public bool HasLastChange => !string.IsNullOrEmpty(_lastChange);

        // ------------------------------------------------------------------ server

        public async Task OnServerStartedAsync()
        {
            if (_session == null) return;
            _session.Reset();
            await LoadSchemaAsync();
            var want = _settings.Database;
            if (want == null || _schema.Find(want) == null)
                want = _main.Notebook.CurrentDatabase ?? _schema.Databases.Keys.Where(d => !SchemaSnapshot.IsSystem(d)).OrderBy(d => d, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            await SetDatabaseAsync(want, restore: true);
        }

        public void OnServerStopped()
        {
            _session?.Reset();
            _schema = SchemaSnapshot.Empty;
            DatabaseNames.Clear();
            foreach (var g in Groups) { g.Items.Clear(); g.Changed(); }
            Selected = null;
            Design = null;
            Rows.Clear();
            Structure.Clear();
            Definition.Clear();
            Pages.Clear();
            Error = null;
            NotifyAll();
        }

        private void NotifyAll()
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(DatabaseLabel));
            OnPropertyChanged(nameof(IndexNote));
            OnPropertyChanged(nameof(HasIndexNote));
            OnPropertyChanged(nameof(HasSheet));
            OnPropertyChanged(nameof(EmptyNote));
            CommandManager.InvalidateRequerySuggested();
        }

        internal async Task LoadSchemaAsync()
        {
            if (!_main.IsRunning || _server == null) return;
            try { _schema = await SchemaReader.ReadAsync(_server); }
            catch (Exception ex) when (ex is LuminaException || ex is MySqlException) { AppLog.Error("tables: reading schema failed", ex); return; }
            DatabaseNames.Clear();
            foreach (var d in _schema.Databases.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) DatabaseNames.Add(d);
            await LoadIndexAsync();
        }

        // ------------------------------------------------------------------ database + index

        public ObservableCollection<string> DatabaseNames { get; } = new ObservableCollection<string>();
        public List<IndexGroup> Groups { get; }
        public string DatabaseLabel => _db ?? "no database";
        public string IndexNote => !_main.IsRunning ? "Start the server to see tables."
            : _db == null ? "Pick a database above, or make one on Today."
            : Groups[0].Items.Count == 0 && Groups.Skip(1).All(g => g.Items.Count == 0) ? "No tables yet. New table makes one." : null;
        public bool HasIndexNote => IndexNote != null;

        private string _filter = "";
        /// <summary>Narrows the index to names containing this text.</summary>
        public string IndexFilter
        {
            get => _filter;
            set { if (Set(ref _filter, value ?? "")) _ = LoadIndexAsync(); }
        }

        private async Task LoadIndexAsync()
        {
            var selectedKind = Selected?.Kind;
            var selectedName = Selected?.Name;
            foreach (var g in Groups) g.Items.Clear();
            var db = _schema.Find(_db);
            if (db != null)
            {
                foreach (var t in db.Tables.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                    Add(new IndexEntry { Kind = t.IsView ? DbObjectKind.View : DbObjectKind.Table, Name = t.Name, Detail = t.IsView ? "view" : t.RowEstimate.ToString("N0", CultureInfo.InvariantCulture) });
                try
                {
                    using (var c = await OpenConnectionAsync())
                        foreach (var o in await DbObjects.ListAsync(c, _db))
                            Add(new IndexEntry { Kind = o.Kind, Name = o.Name, Detail = o.Detail, Table = o.Table });
                }
                catch (Exception ex) when (ex is LuminaException || ex is MySqlException) { AppLog.Error("tables: listing objects failed", ex); }
            }
            foreach (var g in Groups) g.Changed();
            if (selectedName != null)
            {
                var again = Find(selectedKind.Value, selectedName);
                if (again != null) { again.IsSelected = true; _selected = again; }
            }
            NotifyAll();

            void Add(IndexEntry e)
            {
                if (_filter.Length > 0 && e.Name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) return;
                Groups.First(g => g.Kind == e.Kind).Items.Add(e);
            }
        }

        internal IndexEntry Find(DbObjectKind kind, string name) =>
            Groups.First(g => g.Kind == kind).Items.FirstOrDefault(i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

        private async Task UseDatabaseAsync(string db)
        {
            if (!await LeaveCurrentAsync()) return;
            await SetDatabaseAsync(db, restore: false);
        }

        private async Task SetDatabaseAsync(string db, bool restore)
        {
            _db = db != null && _schema.Find(db) != null ? db : null;
            _settings.Database = _db;
            if (!restore) _settings.Table = null;
            SaveSettings();
            Selected = null;
            Design = null;
            Pages.Clear();
            Rows.Clear();
            Structure.Clear();
            Definition.Clear();
            Error = null;
            OnPropertyChanged(nameof(DatabaseLabel));
            await LoadIndexAsync();
            if (restore && _settings.Table != null)
            {
                var e = Find(DbObjectKind.Table, _settings.Table) ?? Find(DbObjectKind.View, _settings.Table);
                if (e != null) await OpenAsync(e, _settings.Page);
            }
            else
            {
                var first = Groups[0].Items.FirstOrDefault();
                if (first != null) await OpenAsync(first, "rows");
            }
            NotifyAll();
        }

        /// <summary>From Notebook or Today: show this table.</summary>
        /// <summary>From another page: show this database (fresh, so a just-imported one is there).</summary>
        public async Task OpenDatabaseAsync(string db)
        {
            if (!_main.IsRunning) return;
            await LoadSchemaAsync();
            if (_schema.Find(db) == null) { _main.Toast("There's no database called " + db + "."); return; }
            if (!string.Equals(db, _db, StringComparison.OrdinalIgnoreCase)) await UseDatabaseAsync(db);
        }

        public async Task OpenTableAsync(string db, string table)
        {
            if (!_main.IsRunning) return;
            if (_schema.Find(db) == null) await LoadSchemaAsync();
            if (!string.Equals(db, _db, StringComparison.OrdinalIgnoreCase)) await SetDatabaseAsync(db, restore: false);
            var e = Find(DbObjectKind.Table, table) ?? Find(DbObjectKind.View, table);
            if (e != null) await OpenAsync(e, "rows");
        }

        // ------------------------------------------------------------------ selection + pages

        private IndexEntry _selected;
        public IndexEntry Selected
        {
            get => _selected;
            private set
            {
                if (_selected != null) _selected.IsSelected = false;
                Set(ref _selected, value);
                if (value != null) value.IsSelected = true;
                OnPropertyChanged(nameof(HasSheet));
                OnPropertyChanged(nameof(EmptyNote));
            }
        }

        private TableDesign _design;
        /// <summary>The selected table as it is in the database (null for objects and before a new table is created).</summary>
        public TableDesign Design
        {
            get => _design;
            private set { Set(ref _design, value); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Meta)); OnPropertyChanged(nameof(KindWord)); }
        }

        private bool _creating;
        public bool IsCreatingTable { get => _creating; private set { Set(ref _creating, value); OnPropertyChanged(nameof(HasSheet)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(KindWord)); OnPropertyChanged(nameof(Meta)); } }

        public bool HasSheet => _selected != null || _creating || Definition.IsNew;
        public string EmptyNote => !_main.IsRunning ? "Start the server (top right) to see your tables." : _db == null ? "Choose a database on the left." : "Pick a table on the left, or make a new one.";

        public string Title => _creating ? "New table" : Definition.IsNew && _selected == null ? "New " + Definition.KindWord.ToLowerInvariant() : _selected?.Name ?? "";
        public string KindWord => _creating ? "TABLE · NOT CREATED YET" : _selected == null ? (Definition.IsNew ? Definition.KindWord + " · NOT SAVED YET" : "") : _selected.Kind.ToString().ToUpperInvariant() + " · " + _db;

        public string Meta
        {
            get
            {
                if (_creating) return "Add columns below. Nothing is created until you press Create table.";
                if (_selected == null) return Definition.IsNew ? "Write it below, then Save." : "";
                if (_selected.Kind == DbObjectKind.View) return "A saved SELECT. Rows come from the tables it reads.";
                if (!_selected.IsTableLike) return _selected.Detail ?? "";
                if (_design == null) return "";
                var parts = new List<string> { _design.Engine, _design.Collation, "≈ " + _design.RowEstimate.ToString("N0", CultureInfo.InvariantCulture) + " rows", Size(_design.Bytes) };
                if (_design.AutoIncrementNext.HasValue) parts.Add("next id " + _design.AutoIncrementNext.Value.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(_design.Comment)) parts.Add("“" + _design.Comment + "”");
                return string.Join(" · ", parts.Where(p => !string.IsNullOrEmpty(p)));
            }
        }

        internal static string Size(long bytes) =>
            bytes < 1024 ? bytes + " B" : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB" : (bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";

        public ObservableCollection<PageChip> Pages { get; } = new ObservableCollection<PageChip>();
        private string _page = "";
        public string Page
        {
            get => _page;
            private set
            {
                if (!Set(ref _page, value)) return;
                foreach (var p in Pages) p.IsSelected = p.Key == value;
                OnPropertyChanged(nameof(IsRowsPage));
                OnPropertyChanged(nameof(IsStructurePage));
                OnPropertyChanged(nameof(IsOperationsPage));
                OnPropertyChanged(nameof(IsDefinitionPage));
                if (_selected != null && _selected.IsTableLike) { _settings.Page = value; SaveSettings(); }
            }
        }
        public bool IsRowsPage => _page == "rows";
        public bool IsStructurePage => _page == "structure";
        public bool IsOperationsPage => _page == "operations";
        public bool IsDefinitionPage => _page == "definition";

        public ICommand OpenEntryCommand { get; }
        public ICommand SelectPageCommand { get; }
        public ICommand UseDatabaseCommand { get; }
        public ICommand NewTableCommand { get; }
        public ICommand NewObjectCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand UndoCommand { get; }

        /// <summary>Unsaved structure or definition edits: ask before they're thrown away.</summary>
        internal async Task<bool> LeaveCurrentAsync()
        {
            string what = Structure.HasChanges && (IsStructurePage || _creating) ? "the changes to " + (_creating ? "the new table" : _selected?.Name)
                : Definition.IsDirty ? "the changes to " + (Definition.IsNew ? "the new " + Definition.KindWord.ToLowerInvariant() : Definition.Name) : null;
            if (what == null) return true;
            var choice = await _main.AskAsync(new ChoiceSlip("NOT SAVED", "Leave without saving?", "You haven't applied " + what + ". Leaving throws them away.",
                alarm: false, primary: "Discard them", secondary: null, detail: null));
            return choice == SlipChoice.Primary;
        }

        private async Task OpenAsync(IndexEntry e, string page)
        {
            if (e == null || !_main.IsRunning) return;
            if (e != _selected && !await LeaveCurrentAsync()) return;
            Error = null;
            IsCreatingTable = false;
            Definition.Clear();
            Selected = e;
            Pages.Clear();
            if (e.IsTableLike)
            {
                _settings.Table = e.Name;
                SaveSettings();
                if (!await LoadDesignAsync()) return;
                Pages.Add(new PageChip("rows", "Rows"));
                if (e.Kind == DbObjectKind.Table) Pages.Add(new PageChip("structure", "Structure"));
                else Pages.Add(new PageChip("definition", "Definition"));
                Pages.Add(new PageChip("operations", "Operations"));
                var want = page ?? _page;
                if (Pages.All(p => p.Key != want)) want = "rows";
                _page = null;
                Page = want;
                await ShowPageAsync();
            }
            else
            {
                Design = null;
                Pages.Add(new PageChip("definition", "Definition"));
                _page = null;
                Page = "definition";
                await Definition.LoadAsync(e.Info(_db));
            }
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(KindWord));
            OnPropertyChanged(nameof(Meta));
        }

        /// <summary>Reads the selected table's design afresh.</summary>
        internal async Task<bool> LoadDesignAsync()
        {
            if (_selected == null || !_selected.IsTableLike) return false;
            try
            {
                using (var c = await OpenConnectionAsync())
                    Design = await TableDesignReader.ReadAsync(c, _db, _selected.Name);
                return true;
            }
            catch (Exception ex) when (ex is LuminaException || ex is MySqlException)
            {
                Design = null;
                Error = new ErrorSheet { Title = "Couldn't read " + _selected.Name, Body = ex.Message, ServerText = "" };
                return false;
            }
        }

        private async Task ShowPageAsync()
        {
            switch (_page)
            {
                case "rows": await Rows.LoadAsync(_design, resetQuery: Rows.Table?.Name != _design?.Name || Rows.Table?.Database != _design?.Database); break;
                case "structure": Structure.Load(_design, isNew: false); break;
                case "operations": await Operations.LoadAsync(_design, _selected?.Kind == DbObjectKind.View); break;
                case "definition": if (_selected != null) await Definition.LoadAsync(_selected.Info(_db)); break;
            }
        }

        private async Task SelectPageAsync(PageChip chip)
        {
            if (chip == null || chip.Key == _page) return;
            if (!await LeaveCurrentAsync()) return;
            Error = null;
            Page = chip.Key;
            await ShowPageAsync();
        }

        /// <summary>Reload everything after a change made here (or Ctrl+R).</summary>
        internal async Task ReloadAsync(bool keepPage)
        {
            await LoadSchemaAsync();
            if (_selected == null) return;
            var again = Find(_selected.Kind, _selected.Name);
            if (again == null)
            {
                if (Definition.IsNew) { ShowUnsavedDefinition(); return; }
                Selected = null; Design = null; Pages.Clear(); NotifyAll(); return;
            }
            if (again != _selected) Selected = again;
            if (_selected.IsTableLike) await LoadDesignAsync();
            OnPropertyChanged(nameof(Meta));
            if (keepPage) await ShowPageAsync();
        }

        /// <summary>After a rename/move/drop: open this entry (or nothing).</summary>
        internal async Task ReopenAsync(DbObjectKind kind, string name, string page)
        {
            await LoadSchemaAsync();
            var e = name == null ? null : Find(kind, name);
            _selected = null;
            if (e != null) await OpenAsync(e, page);
            else { Selected = null; Design = null; Pages.Clear(); Rows.Clear(); NotifyAll(); }
        }

        private async Task NewTableAsync()
        {
            if (!await LeaveCurrentAsync()) return;
            Error = null;
            Selected = null;
            Definition.Clear();
            Design = null;
            Pages.Clear();
            Pages.Add(new PageChip("structure", "Structure"));
            IsCreatingTable = true;
            _page = null;
            Page = "structure";
            Structure.Load(TableDesign.NewTable(_db, ""), isNew: true);
        }

        private async Task NewObjectAsync(DbObjectKind kind)
        {
            if (!await LeaveCurrentAsync()) return;
            Error = null;
            var table = _selected != null && _selected.Kind == DbObjectKind.Table ? _schema.FindTable(_db, _selected.Name)
                : _schema.Find(_db)?.Tables.Values.Where(t => !t.IsView).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (kind == DbObjectKind.Trigger && table == null) { Toast("A trigger belongs to a table. Make a table first."); return; }
            Selected = null;
            IsCreatingTable = false;
            Design = null;
            Pages.Clear();
            Pages.Add(new PageChip("definition", "Definition"));
            _page = null;
            Page = "definition";
            Definition.StartNew(kind, table);
            OnPropertyChanged(nameof(HasSheet));
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(KindWord));
            OnPropertyChanged(nameof(Meta));
        }

        /// <summary>The new table / object was created: show it.</summary>
        internal async Task CreatedAsync(DbObjectKind kind, string name, string page)
        {
            IsCreatingTable = false;
            Structure.Clear();
            Definition.Clear();
            await ReopenAsync(kind, name, page);
        }

        // ------------------------------------------------------------------ undo

        private async Task UndoAsync()
        {
            if (!CanAct) return;
            UndoBatch latest;
            try { latest = await _session.Undo.LatestAsync(); }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { Toast("Couldn't read the Undo list: " + ex.Message); return; }
            if (latest == null) { Toast("Nothing to undo yet. Changes you make here can be taken back."); return; }
            var headline = new SqlStatement(latest.Statement, 0, latest.Statement.Length, ";", true).Headline(120);
            var choice = await _main.AskAsync(new ChoiceSlip("UNDO", "Take back the last change?",
                "Back to how it was at " + latest.Created.ToString("HH:mm", CultureInfo.InvariantCulture) + ": " + latest.What + ". Anything changed in those tables since then is replaced too.",
                alarm: false, primary: "Undo it", secondary: null, detail: latest.Summary ?? headline));
            if (choice != SlipChoice.Primary) return;
            IsBusy = true;
            BusyText = "Putting it back…";
            try
            {
                await _session.Undo.RestoreAsync(latest.Id);
                LastChange = "Undone: " + (latest.Summary ?? headline);
                Toast("Done. It's back as it was at " + latest.Created.ToString("HH:mm", CultureInfo.InvariantCulture) + ".");
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException)
            {
                Error = new ErrorSheet { Title = "Couldn't undo", Body = ex.Message, ServerText = "" };
                AppLog.Error("tables undo failed", ex);
            }
            finally { IsBusy = false; }
            _main.DataChanged();
            bool draft = _selected == null && Definition.IsNew;
            await ReopenAsync(_selected?.Kind ?? DbObjectKind.Table, _selected?.Name, _page);
            if (draft && _selected == null) ShowUnsavedDefinition();   // an unsaved definition is never thrown away by Undo
        }

        // ------------------------------------------------------------------ pages

        public RowsPage Rows { get; }
        public StructureEditor Structure { get; }
        public OperationsPage Operations { get; }
        public ObjectEditor Definition { get; }

        /// <summary>An object was dropped but its text stays in the editor, unsaved.</summary>
        internal void ShowUnsavedDefinition()
        {
            Selected = null;
            Design = null;
            Pages.Clear();
            Pages.Add(new PageChip("definition", "Definition"));
            _page = null;
            Page = "definition";
            HeaderChanged();
        }

        internal void HeaderChanged()
        {
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(KindWord));
            OnPropertyChanged(nameof(Meta));
            OnPropertyChanged(nameof(HasSheet));
        }

        private void SaveSettings()
        {
            try { Directory.CreateDirectory(_paths.Config); JsonFile.Write(_settingsFile, _settings); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { AppLog.Error("saving tables settings failed", ex); }
        }

        public void Dispose()
        {
            Rows.Dispose();
            _session?.Dispose();
        }
    }
}
