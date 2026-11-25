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
using ICSharpCode.AvalonEdit.Document;
using LuminaDB.Core;
using LuminaDB.Core.Import;
using LuminaDB.Core.Sql;

namespace LuminaDB.App.ViewModels
{
    [DataContract]
    public sealed class NotebookSettings
    {
        [DataMember] public bool SafeUpdates { get; set; } = true;
        [DataMember] public string Database { get; set; }
    }

    /// <summary>A table in the schema index (left column).</summary>
    public sealed class SchemaTableItem : ObservableObject
    {
        private bool _expanded;
        public SchemaTableItem(TableSchema t) { Table = t; }
        public TableSchema Table { get; }
        public string Name => Table.Name;
        public string Rows => Table.IsView ? "view" : Table.RowEstimate.ToString("N0", CultureInfo.InvariantCulture);
        public IEnumerable<ColumnSchema> Columns => Table.Columns;
        public bool IsExpanded { get => _expanded; set => Set(ref _expanded, value); }
    }

    /// <summary>Column header object: shows name + type, copies as just the name.</summary>
    public sealed class GridHeader
    {
        public GridHeader(string name, string type) { Name = name; Type = (type ?? "").ToLowerInvariant(); }
        public string Name { get; }
        public string Type { get; }
        /// <summary>" ▲" / " ▼" when the grid is sorted by this column.</summary>
        public string Sort { get; set; } = "";
        public override string ToString() => Name;
    }

    public sealed class ResultTab : ObservableObject
    {
        private bool _selected;
        public ResultTab(int index, StatementResult r, ResultTable t)
        {
            Index = index;
            Table = t;
            var first = r.Info?.Tables.FirstOrDefault(x => !x.IsDerived)?.Name;
            Name = first ?? r.Info?.Verb.ToLowerInvariant() ?? "result";
            Title = index + " · " + Name;
            Caption = t.RowCount.ToString("N0", CultureInfo.InvariantCulture) + (t.Truncated ? "+" : "") + (t.RowCount == 1 ? " row" : " rows");
        }
        public int Index { get; }
        /// <summary>The table it came from (or the command), for file names.</summary>
        public string Name { get; }
        public string Title { get; }
        public string Caption { get; }
        public ResultTable Table { get; }
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    }

    /// <summary>The explanation shown under the editor when a command fails.</summary>
    public sealed class ErrorSheet
    {
        public string Title { get; set; }
        public string Body { get; set; }
        public string ServerText { get; set; }
        public string Where { get; set; }
        public string FixLabel { get; set; }
        public bool HasFix => FixCommand != null;
        public bool HasWhere => GoToCommand != null;
        public ICommand FixCommand { get; set; }
        public ICommand GoToCommand { get; set; }
        public ICommand CopyCommand { get; set; }
        /// <summary>"Run from line 3 to here" when the command needed earlier lines that hadn't run.</summary>
        public string RunFromLabel { get; set; }
        public ICommand RunFromCommand { get; set; }
        public bool HasRunFrom => RunFromCommand != null;
    }

    /// <summary>One slip on the Notebook's receipt tape.</summary>
    public sealed class NotebookReceipt
    {
        public string Time { get; set; }
        public string Headline { get; set; }
        public string Outcome { get; set; }
        /// <summary>ok | bad | info | run</summary>
        public string Kind { get; set; }
        public string Note { get; set; }
        public bool HasNote => !string.IsNullOrEmpty(Note);
        public bool IsError => Kind == "bad";
        public int Offset { get; set; } = -1;
        /// <summary>The sheet (tab) the command was in; null once that tab is closed.</summary>
        public NotebookSheet Sheet { get; set; }
    }

    /// <summary>A margin correction with its one-click fix.</summary>
    public sealed class MarginNote
    {
        public SqlIssue Issue { get; set; }
        public string Text => Issue.Message;
        public bool IsWarning => Issue.Severity != IssueSeverity.Bad;
        public string FixLabel => Issue.FixLabel;
        public bool HasFix => Issue.HasFix;
        public ICommand FixCommand { get; set; }
        public ICommand GoToCommand { get; set; }
    }

    public sealed partial class NotebookViewModel : ObservableObject, IDisposable
    {
        private const int MaxReceipts = 80;
        private const int LiveCheckLimit = 200_000;   // characters; bigger scripts are checked per command

        private readonly MainViewModel _main;
        private readonly AppPaths _paths;
        private readonly Dispatcher _ui;
        private readonly DispatcherTimer _analyseTimer;
        private readonly DispatcherTimer _saveTimer;
        private readonly string _settingsFile;
        private readonly NotebookSettings _settings;
        private NotebookSession _session;
        private MariaDbService _server;
        private SchemaSnapshot _schema = SchemaSnapshot.Empty;
        private SqlAnalysis _analysis;
        private bool _executing;
        private int _caret;
        private int _selStart, _selLength;
        private readonly SnippetStore _snippets;

        public NotebookViewModel(MainViewModel main, MariaDbService server, AppPaths paths, Dispatcher ui)
        {
            _main = main;
            _server = server;
            _paths = paths;
            _ui = ui;
            _settingsFile = Path.Combine(paths.Config, "notebook.json");
            _settings = JsonFile.Read<NotebookSettings>(_settingsFile) ?? new NotebookSettings();
            _snippets = new SnippetStore(Path.Combine(paths.Config, "snippets.json"));
            SetUpSheets(paths);

            _analyseTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(260), DispatcherPriority.Background, (s, e) => { _analyseTimer.Stop(); Analyse(); }, ui);
            _analyseTimer.Stop();
            _saveTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (s, e) => { _saveTimer.Stop(); SaveNow(); }, ui);
            _saveTimer.Stop();

            if (server != null) CreateSession();

            RunCommand = new AsyncCommand(() => RunAsync(false), () => CanRun);
            RunAllCommand = new AsyncCommand(() => RunAsync(true), () => CanRun);
            RunFromCommand = new AsyncCommand(RunFromHereAsync, () => CanRun && _analysis?.CurrentNeed != null);
            ExportResultCommand = new AsyncCommand(p => ExportResultAsync(p as string), p => HasGrid && !_exporting);
            ShowLastExportCommand = new RelayCommand(ShowLastExport, () => _lastExport != null && File.Exists(_lastExport));
            StopCommand = new AsyncCommand(StopAsync, () => _executing);
            TidyCommand = new RelayCommand(Tidy, () => !_executing);
            UndoCommand = new AsyncCommand(UndoAsync, () => _main.IsRunning && !_executing);
            UseDatabaseCommand = new AsyncCommand(p => UseDatabaseAsync(p as string), p => _main.IsRunning && !_executing);
            ExampleCommand = new RelayCommand(p => InsertExample(p as NotebookExample));
            PeekCommand = new AsyncCommand(p => PeekAsync(p as SchemaTableItem), p => CanRun);
            InsertNameCommand = new RelayCommand(p => InsertRequested?.Invoke(p as string));
            ToggleTableCommand = new RelayCommand(p => { if (p is SchemaTableItem t) t.IsExpanded = !t.IsExpanded; });
            SelectResultCommand = new RelayCommand(p => SelectedResult = p as ResultTab);
            RefreshSchemaCommand = new AsyncCommand(LoadSchemaAsync, () => _main.IsRunning);
            GoToReceiptCommand = new RelayCommand(p => { if (p is NotebookReceipt r && r.Offset >= 0) GoTo(r.Sheet, r.Offset, 0); });
            InsertSnippetCommand = new RelayCommand(p => { if (p is SqlSnippet s) SnippetRequested?.Invoke(s); });
            SaveSnippetCommand = new RelayCommand(OpenSaveSnippet, () => !_main.HasSlip);
            DeleteSnippetCommand = new AsyncCommand(p => DeleteSnippetAsync(p as SqlSnippet));

            Analyse();
        }

        private void CreateSession()
        {
            _session?.Dispose();
            _session = new NotebookSession(_server);
            _session.DatabaseChanged += db => _ui.BeginInvoke(new Action(() =>
            {
                OnPropertyChanged(nameof(CurrentDatabase));
                OnPropertyChanged(nameof(DatabaseLabel));
                RebuildIndex();
                _settings.Database = db;
                SaveSettings();
                ScheduleAnalysis();
            }));
        }

        // ------------------------------------------------------------------ editor state

        /// <summary>Shows a place in a sheet: switches to that tab first (when it's still open).</summary>
        private void GoTo(NotebookSheet sheet, int offset, int length)
        {
            if (sheet != null && sheet != _sheet)
            {
                if (!Sheets.Contains(sheet) || !CanSwitchSheet) return;
                SelectSheet(sheet);
            }
            SelectRequested?.Invoke(Math.Min(offset, Document.TextLength), length);
        }

        /// <summary>The view asks to move the caret / select (offset, length).</summary>
        public event Action<int, int> SelectRequested;
        /// <summary>The view inserts text at the caret.</summary>
        public event Action<string> InsertRequested;
        /// <summary>Fresh analysis (issues, notes) is available.</summary>
        public event Action AnalysisChanged;
        /// <summary>The view writes this snippet at the caret (replacing a selection), with Tab-able blanks.</summary>
        public event Action<SqlSnippet> SnippetRequested;

        public int CaretOffset
        {
            get => _caret;
            set { if (_caret == value) return; _caret = value; ScheduleAnalysis(); }
        }

        public void SetSelection(int start, int length) { _selStart = start; _selLength = length; }

        private void ScheduleAnalysis() { _analyseTimer.Stop(); _analyseTimer.Start(); }

        // ------------------------------------------------------------------ server / schema

        public SchemaSnapshot Schema => _schema;
        public ObservableCollection<SchemaTableItem> Tables { get; } = new ObservableCollection<SchemaTableItem>();
        public ObservableCollection<string> DatabaseNames { get; } = new ObservableCollection<string>();
        public string CurrentDatabase => _session?.CurrentDatabase;
        public string DatabaseLabel => CurrentDatabase ?? "no database";
        public bool HasTables => Tables.Count > 0;
        public string IndexNote => !_main.IsRunning ? "Start the server to see tables."
            : CurrentDatabase == null ? "Pick a database above, or CREATE DATABASE in the Notebook."
            : Tables.Count == 0 ? "No tables yet. CREATE TABLE makes one." : null;
        public bool HasIndexNote => IndexNote != null;

        public bool SafeUpdates
        {
            get => _settings.SafeUpdates;
            set
            {
                if (_settings.SafeUpdates == value) return;
                _settings.SafeUpdates = value;
                SaveSettings();
                OnPropertyChanged();
                if (!value) _main.Toast("Safe updates off: only DROP DATABASE asks first. Undo still works.");
            }
        }

        public async Task OnServerStartedAsync()
        {
            if (_session == null) return;
            _session.Reset();
            await LoadSchemaAsync();
            var want = _settings.Database;
            if (CurrentDatabase == null && want != null && _schema.Find(want) != null)
            {
                try { await _session.UseAsync(want); }
                catch (Exception ex) when (ex is LuminaException || ex is MySqlConnector.MySqlException) { }
            }
            Notify();
        }

        public void OnServerStopped()
        {
            _session?.Reset();
            _schema = SchemaSnapshot.Empty;
            DatabaseNames.Clear();
            Tables.Clear();
            Notify();
            ScheduleAnalysis();
        }

        private void Notify()
        {
            OnPropertyChanged(nameof(CurrentDatabase));
            OnPropertyChanged(nameof(DatabaseLabel));
            OnPropertyChanged(nameof(HasTables));
            OnPropertyChanged(nameof(IndexNote));
            OnPropertyChanged(nameof(HasIndexNote));
            CommandManager.InvalidateRequerySuggested();
        }

        public async Task LoadSchemaAsync()
        {
            if (!_main.IsRunning || _server == null) return;
            try
            {
                _schema = await SchemaReader.ReadAsync(_server);
            }
            catch (Exception ex) when (ex is LuminaException || ex is MySqlConnector.MySqlException)
            {
                AppLog.Error("reading schema failed", ex);
                return;
            }
            DatabaseNames.Clear();
            foreach (var d in _schema.Databases.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) DatabaseNames.Add(d);
            RebuildIndex();
            ScheduleAnalysis();
        }

        private void RebuildIndex()
        {
            var expanded = new HashSet<string>(Tables.Where(t => t.IsExpanded).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
            Tables.Clear();
            var db = _schema.Find(CurrentDatabase);
            if (db != null)
                foreach (var t in db.Tables.Values.OrderBy(t => t.IsView).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
                    Tables.Add(new SchemaTableItem(t) { IsExpanded = expanded.Contains(t.Name) });
            Notify();
        }

        private async Task UseDatabaseAsync(string db)
        {
            if (_session == null) return;
            try
            {
                await _session.UseAsync(string.IsNullOrEmpty(db) ? null : db);
                if (db != null) _main.Toast("Using " + db + ".");
            }
            catch (LuminaException ex) { _main.Toast(ex.Message); }
            catch (MySqlConnector.MySqlException ex) { _main.Toast(ex.Message); }
            Notify();
        }

        /// <summary>From Today: open this database in the Notebook.</summary>
        public async Task OpenDatabaseAsync(string db)
        {
            if (!_main.IsRunning) return;
            if (_schema.Find(db) == null) await LoadSchemaAsync();
            await UseDatabaseAsync(db);
        }

        // ------------------------------------------------------------------ live checks

        public SqlAnalysis Analysis => _analysis;
        public IReadOnlyList<SqlIssue> Issues => _analysis?.Issues ?? (IReadOnlyList<SqlIssue>)Array.Empty<SqlIssue>();

        private string _verdictMark = "—", _verdictText = "", _verdictKind = "info";
        public string VerdictMark { get => _verdictMark; private set => Set(ref _verdictMark, value); }
        public string VerdictText { get => _verdictText; private set => Set(ref _verdictText, value); }
        /// <summary>ok | bad | run | info</summary>
        public string VerdictKind { get => _verdictKind; private set => Set(ref _verdictKind, value); }

        public void Analyse()
        {
            var text = Document.Text;
            var guard = new SqlGuard(_schema);
            if (text.Length > LiveCheckLimit)
            {
                // huge script (a dump): check only the command under the caret
                var all = SqlSplitter.Split(text);
                var cur = SqlSplitter.StatementAt(all, _caret);
                _analysis = cur == null ? guard.Analyze("", CurrentDatabase) : guard.Analyze(text.Substring(0, cur.End), CurrentDatabase, _caret);
                if (cur != null) _analysis.Issues.RemoveAll(i => i.Start < cur.Start);
            }
            else _analysis = guard.Analyze(text, CurrentDatabase, _caret);
            _analysis.Script = text;

            var cmd = _analysis.Current;
            var mine = cmd == null ? new List<SqlIssue>() : _analysis.IssuesIn(cmd.Statement).ToList();
            var bad = mine.FirstOrDefault(i => i.Severity == IssueSeverity.Bad);
            var warn = mine.FirstOrDefault(i => i.Severity == IssueSeverity.Warn);
            if (cmd == null || cmd.Kind == StatementKind.Empty)
            {
                VerdictMark = "—"; VerdictKind = "info";
                VerdictText = "Write SQL above. Ctrl+Enter runs the command under the cursor.";
            }
            else if (bad != null) { VerdictMark = "FIX"; VerdictKind = "bad"; VerdictText = bad.Message; }
            else if (_analysis.CurrentNeed != null)
            {
                VerdictMark = "NOT YET"; VerdictKind = "run";
                VerdictText = _analysis.CurrentNeed.Message + " Press Run all, or:";
            }
            else if (warn != null) { VerdictMark = "CAREFUL"; VerdictKind = "run"; VerdictText = warn.Message; }
            else
            {
                VerdictMark = "OK"; VerdictKind = "ok";
                VerdictText = string.IsNullOrEmpty(_analysis.Summary) ? "Looks fine." : _analysis.Summary;
            }
            int others = _analysis.Issues.Count(i => i.Severity == IssueSeverity.Bad) - (bad != null ? 1 : 0);
            IssueCount = others > 0 ? "+" + others + " more to fix in this script" : "";
            RunFromLabel = bad == null && _analysis.CurrentNeed != null ? "Run from line " + LineOf(_analysis.CurrentNeed.FromOffset) + " to here" : null;
            AnalysisChanged?.Invoke();
        }

        private string _issueCount = "";
        public string IssueCount { get => _issueCount; private set => Set(ref _issueCount, value); }

        private string _runFromLabel;
        /// <summary>"Run from line 3 to here" when the command under the cursor needs lines above that haven't run.</summary>
        public string RunFromLabel { get => _runFromLabel; private set { if (Set(ref _runFromLabel, value)) { OnPropertyChanged(nameof(HasRunFrom)); CommandManager.InvalidateRequerySuggested(); } } }
        public bool HasRunFrom => _runFromLabel != null;
        public ICommand RunFromCommand { get; }

        private int LineOf(int offset) => Document.GetLineByOffset(Math.Max(0, Math.Min(offset, Document.TextLength))).LineNumber;

        private Task RunFromHereAsync()
        {
            Analyse();   // the text may have moved since the last check
            var need = _analysis?.CurrentNeed;
            var cur = _analysis?.Current;
            if (need == null || cur == null) return RunAsync(false);
            return RunAsync(false, need.FromOffset, cur.Statement.Start);
        }

        /// <summary>Margin notes for the view: at most one per line, Bad before Warn.</summary>
        public List<MarginNote> Notes()
        {
            var list = new List<MarginNote>();
            if (_analysis == null) return list;
            var seenLines = new HashSet<int>();
            foreach (var i in _analysis.Issues.OrderBy(i => i.Severity == IssueSeverity.Bad ? 0 : 1).ThenBy(i => i.Start))
            {
                if (i.Severity == IssueSeverity.Info || i.Start > Document.TextLength) continue;
                int line = Document.GetLineByOffset(Math.Min(i.Start, Document.TextLength)).LineNumber;
                if (!seenLines.Add(line)) continue;
                var issue = i;
                list.Add(new MarginNote
                {
                    Issue = issue,
                    FixCommand = issue.HasFix ? new RelayCommand(() => ApplyIssueFix(issue)) : null,
                    GoToCommand = new RelayCommand(() => SelectRequested?.Invoke(issue.Start, issue.Length)),
                });
            }
            return list.OrderBy(n => n.Issue.Start).ToList();
        }

        private void ApplyIssueFix(SqlIssue issue)
        {
            if (_analysis == null || _analysis.Script != Document.Text)
            {
                Analyse();
                issue = _analysis.Issues.FirstOrDefault(i => i.Code == issue.Code && i.Start == issue.Start) ?? issue;
                if (_analysis.Script != Document.Text) return;
            }
            ApplyFixes(issue.Fixes);
        }

        private void ApplyFixes(IEnumerable<TextFix> fixes)
        {
            var list = fixes.OrderByDescending(f => f.Start).ToList();
            if (list.Count == 0) return;
            Document.BeginUpdate();
            try
            {
                foreach (var f in list)
                    if (f.Start >= 0 && f.Start + f.Length <= Document.TextLength) Document.Replace(f.Start, f.Length, f.Text);
            }
            finally { Document.EndUpdate(); }
            var last = list.Last();
            SelectRequested?.Invoke(Math.Min(last.Start + last.Text.Length, Document.TextLength), 0);
            Analyse();
        }

        // ------------------------------------------------------------------ completion

        /// <summary>Names for autocomplete: after "x." the columns of x, otherwise tables, columns and SQL words.</summary>
        public IEnumerable<(string text, string kind, string detail, double priority)> Completions(string qualifier)
        {
            var db = CurrentDatabase;
            var stmt = _analysis?.Current;
            if (!string.IsNullOrEmpty(qualifier))
            {
                var r = stmt?.Tables.FirstOrDefault(t => string.Equals(t.Alias, qualifier, StringComparison.OrdinalIgnoreCase))
                        ?? stmt?.Tables.FirstOrDefault(t => string.Equals(t.Name, qualifier, StringComparison.OrdinalIgnoreCase));
                var table = r != null ? _schema.FindTable(r.ResolvedDatabase, r.Name) : _schema.FindTable(db, qualifier);
                if (table != null)
                {
                    foreach (var c in table.Columns) yield return (Q(c.Name), "column", c.Type, 3);
                    yield break;
                }
                var otherDb = _schema.Find(qualifier);
                if (otherDb != null) foreach (var t in otherDb.Tables.Values) yield return (Q(t.Name), t.IsView ? "view" : "table", null, 3);
                yield break;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var dbs = _schema.Find(db);
            // columns of the tables this command uses come first
            if (stmt != null)
                foreach (var r in stmt.Tables.Where(t => !t.IsDerived && t.Name != null))
                {
                    var t = _schema.FindTable(r.ResolvedDatabase, r.Name);
                    if (t == null) continue;
                    foreach (var c in t.Columns) if (seen.Add(c.Name)) yield return (Q(c.Name), "column", t.Name + " · " + c.Type, 3);
                }
            if (dbs != null)
                foreach (var t in dbs.Tables.Values) if (seen.Add(t.Name)) yield return (Q(t.Name), t.IsView ? "view" : "table", t.RowEstimate.ToString("N0", CultureInfo.InvariantCulture) + " rows", 2);
            foreach (var d in _schema.Databases.Keys) if (seen.Add(d)) yield return (Q(d), "database", null, 1);
            foreach (var w in SqlKeywords.CompletionWords())
                if (seen.Add(w)) yield return (w, SqlKeywords.Functions.Contains(w) && !SqlKeywords.Reserved.Contains(w) ? "function" : "keyword", null, 0);
            // last, so a half-typed word still picks the keyword; typing a snippet's whole word picks the snippet
            foreach (var s in _snippets.All) yield return (s.Trigger, "snippet", s.Title, -1);
        }

        // ------------------------------------------------------------------ snippets

        public IReadOnlyList<SqlSnippet> Snippets => _snippets.All;
        public IReadOnlyList<SqlSnippet> MySnippets => _snippets.Mine;
        public SqlSnippet FindSnippet(string trigger) => _snippets.Find(trigger);
        public bool HasSelection => _selLength > 0;
        public bool HasSlipOpen => _main.HasSlip;

        public ICommand InsertSnippetCommand { get; }
        public ICommand SaveSnippetCommand { get; }
        public ICommand DeleteSnippetCommand { get; }

        private void OpenSaveSnippet()
        {
            string body = "";
            if (_selLength > 0 && _selStart + _selLength <= Document.TextLength) body = Document.GetText(_selStart, _selLength).Trim();
            _main.Slip = new SaveSnippetSlip(_main.CloseSlip, body, _snippets.HasMine, (trigger, title, sql) =>
            {
                bool replaced = _snippets.HasMine(trigger);
                var s = _snippets.Save(trigger, title, sql);
                _main.Toast((replaced ? "Replaced your snippet " : "Saved. Type ") + s.Trigger + (replaced ? "." : " then Tab to write it."));
                // back to the page once the slip has gone, so the word can be typed straight away
                int caret = _caret;
                _ui.BeginInvoke(DispatcherPriority.Input, new Action(() => SelectRequested?.Invoke(Math.Min(caret, Document.TextLength), 0)));
            });
        }

        private async Task DeleteSnippetAsync(SqlSnippet s)
        {
            if (s == null || s.IsBuiltIn) return;
            var choice = await _main.AskAsync(new ChoiceSlip("DELETE SNIPPET", "Delete " + s.Trigger + "?",
                "It goes from the list for good. The SQL you already wrote with it stays in the notebook.", true, "Delete it", null, s.Preview));
            if (choice != SlipChoice.Primary) return;
            if (_snippets.Delete(s.Trigger)) _main.Toast("Deleted the snippet " + s.Trigger + ".");
        }

        private static string Q(string name) => SqlLexer.IsPlainName(name) ? name : SqlLexer.Quote(name);

        // ------------------------------------------------------------------ results

        public ObservableCollection<ResultTab> Results { get; } = new ObservableCollection<ResultTab>();
        private ResultTab _selectedResult;
        public ResultTab SelectedResult
        {
            get => _selectedResult;
            set
            {
                if (_selectedResult != null) _selectedResult.IsSelected = false;
                if (Set(ref _selectedResult, value) && value != null) value.IsSelected = true;
                OnPropertyChanged(nameof(HasGrid));
                OnPropertyChanged(nameof(ShowEmpty));
                OnPropertyChanged(nameof(GridCaption));
            }
        }
        public bool HasGrid => _selectedResult != null && _error == null;
        public bool HasSeveralResults => Results.Count > 1;
        public string GridCaption => _selectedResult == null ? "" : _selectedResult.Caption + (_selectedResult.Table.Truncated ? " · stopped at " + NotebookSession.DefaultRowCap.ToString("N0", CultureInfo.InvariantCulture) + "; add LIMIT or WHERE" : "");

        private ErrorSheet _error;
        public ErrorSheet Error { get => _error; private set { Set(ref _error, value); OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(HasGrid)); OnPropertyChanged(nameof(ShowEmpty)); } }
        public bool HasError => _error != null;

        private string _resultMessage;
        public string ResultMessage { get => _resultMessage; private set { Set(ref _resultMessage, value); OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(HasResultMessage)); } }
        public bool HasResultMessage => !string.IsNullOrEmpty(_resultMessage) && _error == null && _selectedResult == null;
        public bool ShowEmpty => _error == null && _selectedResult == null && string.IsNullOrEmpty(_resultMessage);

        public ObservableCollection<NotebookReceipt> Receipts { get; } = new ObservableCollection<NotebookReceipt>();
        public bool HasReceipts => Receipts.Count > 0;

        private void AddReceipt(NotebookReceipt r)
        {
            r.Time = r.Time ?? DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            r.Sheet = r.Sheet ?? _sheet;
            Receipts.Insert(0, r);
            while (Receipts.Count > MaxReceipts) Receipts.RemoveAt(Receipts.Count - 1);
            OnPropertyChanged(nameof(HasReceipts));
        }

        // ------------------------------------------------------------------ running

        public ICommand RunCommand { get; }
        public ICommand RunAllCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand TidyCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand UseDatabaseCommand { get; }
        public ICommand ExampleCommand { get; }
        public ICommand PeekCommand { get; }
        public ICommand InsertNameCommand { get; }
        public ICommand ToggleTableCommand { get; }
        public ICommand SelectResultCommand { get; }
        public ICommand RefreshSchemaCommand { get; }
        public ICommand GoToReceiptCommand { get; }
        public IReadOnlyList<NotebookExample> Examples => NotebookExamples.All;

        public bool IsExecuting { get => _executing; private set { if (Set(ref _executing, value)) { OnPropertyChanged(nameof(RunState)); CommandManager.InvalidateRequerySuggested(); } } }
        private bool CanRun => _main.IsRunning && !_executing && _session != null && !_main.NeedsPassword && !_main.HasSlip;
        private string _runState = "";
        public string RunState { get => _executing ? _runState : ""; private set { _runState = value; OnPropertyChanged(); } }

        private async Task StopAsync()
        {
            if (_session != null) await _session.CancelAsync();
        }

        private List<SqlStatement> StatementsToRun(bool all, string text, int from = -1, int to = -1)
        {
            if (all) return SqlSplitter.Split(text);
            if (from >= 0 && to >= from)
                return SqlSplitter.Split(text).Where(x => x.End > from && x.Start <= to).ToList();
            if (_selLength > 0 && _selStart + _selLength <= text.Length)
            {
                var part = SqlSplitter.Split(text.Substring(_selStart, _selLength));
                return part.Select(p => new SqlStatement(text, p.Start + _selStart, p.Length, p.Delimiter, p.Terminated)).ToList();
            }
            var list = SqlSplitter.Split(text);
            var cur = SqlSplitter.StatementAt(list, _caret);
            return cur == null ? new List<SqlStatement>() : new List<SqlStatement> { cur };
        }

        private async Task RunAsync(bool all, int from = -1, int to = -1)
        {
            if (!_main.IsRunning) { _main.Toast("Start the server first (top right)."); return; }
            if (_main.HasSlip || _executing) return;   // a question is open, or something is already running
            var text = Document.Text;
            var sheet = _sheet;
            var schemaBefore = _schema;
            var dbBefore = _session.CurrentDatabase;
            bool ranAlone = !all && from < 0;
            var statements = StatementsToRun(all, text, from, to);
            if (statements.Count == 0) { _main.Toast("Nothing to run here. Write a command, or pick one from Examples."); return; }

            bool schemaChanged = false, dataChanged = false;
            var tabs = new List<ResultTab>();
            StatementResult last = null;
            try
            {
                // what each command will do, with USE followed through the script
                var infos = new List<StatementInfo>();
                string db = _session.CurrentDatabase;
                foreach (var s in statements)
                {
                    var info = StatementInfo.Analyze(s, db);
                    infos.Add(info);
                    if (info.Verb == "USE" && info.DatabaseName != null) db = info.DatabaseName;
                    else if (info.Verb == "DROP" && info.Object == "DATABASE" && string.Equals(info.DatabaseName, db, StringComparison.OrdinalIgnoreCase)) db = null;
                }

                // ask first about anything destructive, before running any of it
                foreach (var info in infos)
                {
                    var prompt = SafetyGate.Check(info, _schema, SafeUpdates);
                    if (prompt == null) continue;
                    long? rows = null;
                    if (prompt.CountTable != null && prompt.CountTable.ResolvedDatabase != null)
                        rows = await _session.CountRowsAsync(prompt.CountTable.ResolvedDatabase, prompt.CountTable.Name);
                    var choice = await _main.AskAsync(new ChoiceSlip(
                        "CAREFUL", prompt.Title, SafetyGate.WithRows(prompt.Body, rows) + " Undo keeps a copy, so you can take it back.",
                        alarm: true, primary: prompt.RunLabel, secondary: prompt.AddWhere != null ? "Add a WHERE for me" : null,
                        detail: info.Statement.Headline(160)));
                    if (choice == SlipChoice.Secondary && prompt.AddWhere != null)
                    {
                        ApplyFixes(new[] { prompt.AddWhere });
                        // select the "1" so typing replaces it with the real id
                        int at = prompt.AddWhere.Start + prompt.AddWhere.Text.Length - 1;
                        if (at < Document.TextLength) SelectRequested?.Invoke(at, 1);
                        _main.Toast("Added a WHERE. Change the 1 to the row you mean, then run again.");
                        return;
                    }
                    if (choice != SlipChoice.Primary)
                    {
                        AddReceipt(new NotebookReceipt { Headline = info.Statement.Headline(), Outcome = "not run: you cancelled", Kind = "info", Offset = info.Statement.Start, Sheet = sheet });
                        return;
                    }
                }

                // SPEC §5.10: a backup of what a DROP / TRUNCATE / delete-all touches, before it runs
                var toBackUp = SafetyGate.DatabasesToBackUp(infos, _schema);
                if (toBackUp.Count > 0)
                {
                    var risky = infos.First(i => SafetyGate.DatabasesToBackUp(new[] { i }, _schema).Count > 0);
                    _main.Toast("Backing up " + string.Join(", ", toBackUp) + " first…");
                    bool go = await _main.BackUpBeforeAsync(toBackUp, "Before " + risky.Statement.Headline(80));
                    if (!go)
                    {
                        AddReceipt(new NotebookReceipt { Headline = risky.Statement.Headline(), Outcome = "not run: no backup", Kind = "info", Offset = risky.Statement.Start, Sheet = sheet });
                        return;
                    }
                }

                // asked and answered: from here on it runs (Stop takes Run's place)
                IsExecuting = true;
                Error = null;
                ResultMessage = null;

                // one Undo copy for a multi-command run (each single command makes its own)
                UndoPlan runPlan = null;
                bool multi = statements.Count > 1;
                if (multi && infos.Any(i => i.ChangesData))
                {
                    RunState = "Saving an Undo copy…";
                    try { runPlan = await _session.Undo.SnapshotAsync(infos, text.Length > 60000 ? text.Substring(0, 60000) : text, "Ran " + statements.Count + " commands"); }
                    catch (MySqlConnector.MySqlException ex) { runPlan = new UndoPlan { SkippedReason = "Couldn't make the Undo copy: " + ex.Message }; }
                }

                for (int i = 0; i < statements.Count; i++)
                {
                    var s = statements[i];
                    RunState = multi ? "Running " + (i + 1) + " of " + statements.Count + "…" : "Running…";
                    var r = await _session.ExecuteAsync(s, _schema, makeUndo: !multi);
                    last = r;
                    if (multi && i == infos.FindIndex(x => x.ChangesData)) r.Undo = runPlan;
                    var receipt = ReceiptFor(r);
                    receipt.Sheet = sheet;
                    AddReceipt(receipt);
                    foreach (var t in r.Tables) tabs.Add(new ResultTab(tabs.Count + 1, r, t));
                    if (r.Info != null && (r.Info.Kind == StatementKind.Schema || r.Info.Verb == "USE" || r.Info.Object == "DATABASE")) schemaChanged = true;
                    if (r.Info != null && r.Info.ChangesData) dataChanged = true;
                    if (!r.Ok)
                    {
                        // run on its own, a command can fail only because lines above it haven't run yet
                        ScriptNeed need = null;
                        if (ranAlone && !r.Cancelled && r.Explanation?.IsConnectionProblem != true)
                        {
                            var before = new SqlGuard(schemaBefore).Analyze(text.Length > LiveCheckLimit ? text.Substring(0, Math.Min(text.Length, r.Statement.End)) : text, dbBefore);
                            for (int k = 0; k < before.Statements.Count && need == null; k++)
                                if (before.Statements[k].Start >= r.Statement.Start && before.Statements[k].Start <= r.Statement.End) need = before.Needs[k];
                        }
                        ShowError(r, text, sheet, need);
                        break;
                    }
                }

                if (tabs.Count > 0)
                {
                    Results.Clear();
                    foreach (var t in tabs) Results.Add(t);
                    OnPropertyChanged(nameof(HasSeveralResults));
                    SelectedResult = tabs[tabs.Count - 1];
                }
                else if (last != null && last.Ok)
                {
                    Results.Clear();
                    OnPropertyChanged(nameof(HasSeveralResults));
                    SelectedResult = null;
                    ResultMessage = Sentence(last, statements.Count);
                }
                if (Error != null && tabs.Count == 0) SelectedResult = null;
            }
            catch (LuminaException ex)
            {
                _main.Toast(ex.Message);
            }
            finally
            {
                IsExecuting = false;
                if (schemaChanged || dataChanged) await LoadSchemaAsync();
                if (dataChanged || schemaChanged) _main.DataChanged();
            }
        }

        private static string Sentence(StatementResult r, int count)
        {
            var head = count > 1 ? "Ran " + count + " commands. Last: " + r.Outcome + "." : r.Outcome + ".";
            if (r.Warnings.Count > 0) head += " " + r.Warnings.Count + (r.Warnings.Count == 1 ? " warning: " : " warnings, first: ") + r.Warnings[0].Message;
            if (r.Undo?.Covered == true) head += " Undo change can take it back.";
            else if (!string.IsNullOrEmpty(r.Undo?.SkippedReason)) head += " " + r.Undo.SkippedReason;
            return head;
        }

        private NotebookReceipt ReceiptFor(StatementResult r)
        {
            string note = null;
            if (r.Undo?.Covered == true) note = "Undo copy saved";
            else if (!string.IsNullOrEmpty(r.Undo?.SkippedReason)) note = r.Undo.SkippedReason;
            if (r.Warnings.Count > 0) note = (note != null ? note + " · " : "") + r.Warnings.Count + (r.Warnings.Count == 1 ? " warning" : " warnings");
            return new NotebookReceipt
            {
                Time = r.Finished.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                Headline = r.Statement.Headline(),
                Outcome = r.Ok ? r.Outcome : r.Cancelled ? "stopped by you" : r.Explanation?.Title ?? "failed",
                Kind = r.Ok ? "ok" : r.Cancelled ? "info" : "bad",
                Note = note,
                Offset = r.Statement.Start,
            };
        }

        private void ShowError(StatementResult r, string ranText, NotebookSheet ranIn, ScriptNeed need = null)
        {
            var e = r.Explanation;
            if (e == null) return;
            var sheet = new ErrorSheet
            {
                Title = e.Title,
                Body = need != null ? need.Message + " " + e.Body : e.Body,
                ServerText = "#" + r.ErrorCode + " · " + r.ErrorMessage,
                Where = e.Line > 0 ? "line " + e.Line : null,
                FixLabel = e.FixLabel,
                CopyCommand = new RelayCommand(() => _main.CopyText("MariaDB error #" + r.ErrorCode + ": " + r.ErrorMessage + Environment.NewLine + r.Statement.Text, "Copied the error and the command.")),
            };
            if (e.Offset >= 0) sheet.GoToCommand = new RelayCommand(() => GoTo(ranIn, e.Offset, 0));
            if (e.HasFix)
                sheet.FixCommand = new RelayCommand(() =>
                {
                    GoTo(ranIn, e.Offset >= 0 ? e.Offset : 0, 0);
                    if (_sheet != ranIn || Document.Text != ranText) { _main.Toast("The text changed since it ran. Fix it by hand, or run again first."); return; }
                    ApplyFixes(e.Fixes);
                    Error = null;
                });
            if (need != null)
            {
                int from = need.FromOffset, to = r.Statement.Start;
                sheet.RunFromLabel = "Run from line " + ranIn.Document.GetLineByOffset(Math.Min(from, ranIn.Document.TextLength)).LineNumber + " to here";
                sheet.RunFromCommand = new AsyncCommand(async () =>
                {
                    if (_sheet != ranIn) GoTo(ranIn, to, 0);
                    if (_sheet != ranIn || Document.Text != ranText) { _main.Toast("The text changed since it ran. Put the cursor in the command and use Run from line, or Run all."); return; }
                    await RunAsync(false, from, to);
                }, () => CanRun);
            }
            if (r.Explanation.IsConnectionProblem) _session.Reset();
            Results.Clear();
            OnPropertyChanged(nameof(HasSeveralResults));
            SelectedResult = null;
            Error = sheet;
        }

        private async Task PeekAsync(SchemaTableItem t)
        {
            if (t == null || !CanRun) return;
            var sql = "SELECT * FROM " + SqlLexer.Quote(t.Table.Database) + "." + SqlLexer.Quote(t.Name) + " LIMIT 200";
            IsExecuting = true;
            RunState = "Reading " + t.Name + "…";
            try
            {
                var stmt = SqlSplitter.Split(sql)[0];
                var r = await _session.ExecuteAsync(stmt, _schema, makeUndo: false);
                AddReceipt(ReceiptFor(r));
                Error = null;
                ResultMessage = null;
                Results.Clear();
                if (r.Ok && r.Tables.Count > 0)
                {
                    var tab = new ResultTab(1, r, r.Tables[0]);
                    Results.Add(tab);
                    OnPropertyChanged(nameof(HasSeveralResults));
                    SelectedResult = tab;
                }
                else if (!r.Ok) ShowError(r, Document.Text, _sheet);
            }
            finally { IsExecuting = false; }
        }

        // ------------------------------------------------------------------ export

        private bool _exporting;
        private string _lastExport;
        public ICommand ExportResultCommand { get; }
        public ICommand ShowLastExportCommand { get; }
        public string LastExportName => _lastExport == null ? null : Path.GetFileName(_lastExport);
        public bool HasLastExport => _lastExport != null;

        /// <summary>Saves the shown result in exports as CSV, Excel or JSON.</summary>
        private async Task ExportResultAsync(string kind)
        {
            var tab = _selectedResult;
            if (tab == null || _exporting) return;
            var format = kind == "excel" ? ExportFormat.Excel : kind == "json" ? ExportFormat.Json : ExportFormat.Csv;
            _exporting = true;
            CommandManager.InvalidateRequerySuggested();
            try
            {
                var folder = _main.Paths.Exports;
                var file = DataExporter.Unique(Path.Combine(folder, ResultExport.FileName(tab.Name, format, DateTime.Now)));
                long rows = await Task.Run(() =>
                {
                    Directory.CreateDirectory(folder);
                    return ResultExport.Write(tab.Table, format, file);
                });
                _lastExport = file;
                OnPropertyChanged(nameof(LastExportName));
                OnPropertyChanged(nameof(HasLastExport));
                var note = tab.Table.Truncated ? " Only the first " + NotebookSession.DefaultRowCap.ToString("N0", CultureInfo.InvariantCulture) + " rows (the Notebook stops there); Export (F6) saves whole tables."
                         : format == ExportFormat.Excel && rows < tab.Table.RowCount ? " Excel holds " + rows.ToString("N0", CultureInfo.InvariantCulture) + " of them; CSV keeps all." : "";
                _main.Toast("Saved " + Path.GetFileName(file) + " in exports: " + rows.ToString("N0", CultureInfo.InvariantCulture) + (rows == 1 ? " row." : " rows.") + note);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _main.Toast("Couldn't save the result: " + ex.Message);
            }
            finally
            {
                _exporting = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        private void ShowLastExport()
        {
            if (_lastExport == null) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + _lastExport + "\"")?.Dispose(); }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is IOException) { _main.Toast("Couldn't open the folder: " + ex.Message); }
        }

        // ------------------------------------------------------------------ undo

        private async Task UndoAsync()
        {
            if (_main.HasSlip || _executing) return;
            UndoBatch latest;
            try { latest = await _session.Undo.LatestAsync(); }
            catch (Exception ex) when (ex is MySqlConnector.MySqlException || ex is LuminaException) { _main.Toast("Couldn't read the Undo list: " + ex.Message); return; }
            if (latest == null) { _main.Toast("Nothing to undo yet. Changes you run here can be taken back."); return; }

            var headline = new SqlStatement(latest.Statement, 0, latest.Statement.Length, ";", true).Headline(120);
            var choice = await _main.AskAsync(new ChoiceSlip(
                "UNDO", "Take back the last change?",
                "Back to how it was at " + latest.Created.ToString("HH:mm", CultureInfo.InvariantCulture) + ": " + latest.What +
                ". Anything changed in those tables since then is replaced too.",
                alarm: false, primary: "Undo it", secondary: null, detail: headline));
            if (choice != SlipChoice.Primary) return;

            IsExecuting = true;
            RunState = "Putting it back…";
            try
            {
                await _session.Undo.RestoreAsync(latest.Id);
                AddReceipt(new NotebookReceipt { Headline = "UNDO · " + headline, Outcome = "undone: " + latest.What, Kind = "ok" });
                _main.Toast("Done. It's back as it was at " + latest.Created.ToString("HH:mm", CultureInfo.InvariantCulture) + ".");
            }
            catch (Exception ex) when (ex is MySqlConnector.MySqlException || ex is LuminaException)
            {
                AddReceipt(new NotebookReceipt { Headline = "UNDO · " + headline, Outcome = "couldn't undo: " + ex.Message, Kind = "bad" });
                AppLog.Error("undo failed", ex);
            }
            finally
            {
                IsExecuting = false;
                await LoadSchemaAsync();
                _main.DataChanged();
            }
        }

        // ------------------------------------------------------------------ text tools

        private void Tidy()
        {
            var text = Document.Text;
            string tidy;
            try { tidy = SqlFormatter.Format(text); }
            catch (Exception ex) { AppLog.Error("tidy failed", ex); _main.Toast("Couldn't tidy this script."); return; }
            if (tidy == text) { _main.Toast("Already tidy."); return; }
            Document.Replace(0, Document.TextLength, tidy);   // one step on Ctrl+Z
            _main.Toast("Tidied. Ctrl+Z puts it back.");
        }

        private void InsertExample(NotebookExample ex)
        {
            if (ex == null) return;
            var prefix = Document.TextLength == 0 || Document.Text.EndsWith("\n\n", StringComparison.Ordinal) ? "" : Document.Text.EndsWith("\n", StringComparison.Ordinal) ? "\n" : "\n\n";
            int at = Document.TextLength + prefix.Length;
            Document.Insert(Document.TextLength, prefix + "-- Example: " + ex.Title + "\n" + ex.Sql);
            int caret = Math.Min(Document.TextLength, at + ("-- Example: " + ex.Title + "\n").Length);
            SelectRequested?.Invoke(caret, 0);
            _main.Toast(ex.Group == "Mistake" ? "Added at the end: see the note in the margin." : "Added at the end. Put the cursor in it and press Ctrl+Enter.");
        }

        // ------------------------------------------------------------------ persistence

        private void SaveSettings()
        {
            try { Directory.CreateDirectory(_paths.Config); JsonFile.Write(_settingsFile, _settings); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { AppLog.Error("saving notebook settings failed", ex); }
        }

        public void Dispose()
        {
            _analyseTimer.Stop();
            _saveTimer.Stop();
            SaveNow();
            _session?.Dispose();
        }
    }
}
