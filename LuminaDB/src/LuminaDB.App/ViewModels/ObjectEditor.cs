using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using LuminaDB.Core;
using LuminaDB.Core.Sql;
using LuminaDB.Core.Tables;
using MySqlConnector;

namespace LuminaDB.App.ViewModels
{
    /// <summary>Editor for views, procedures, functions, triggers and events: templates, live corrections, save, put back.</summary>
    public sealed class ObjectEditor : ObservableObject
    {
        private readonly TablesViewModel _owner;
        private readonly DispatcherTimer _timer;
        private DbObjectInfo _info;
        private DbObjectKind _kind = DbObjectKind.View;
        private string _saved = "";
        private string _previous;
        private bool _isNew;
        private SqlAnalysis _analysis;
        private bool _loading;

        public ObjectEditor(TablesViewModel owner)
        {
            _owner = owner;
            Document = new TextDocument();
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(280), DispatcherPriority.Background, (s, e) => { _timer.Stop(); Analyse(); }, owner.Ui);
            _timer.Stop();
            Document.TextChanged += (s, e) =>
            {
                if (_loading) return;
                OnPropertyChanged(nameof(IsDirty));
                OnPropertyChanged(nameof(StateText));
                _timer.Stop(); _timer.Start();
            };
            SaveCommand = new AsyncCommand(SaveAsync, () => _owner.CanAct && (IsDirty || _isNew) && Document.TextLength > 0);
            PutBackCommand = new RelayCommand(PutBack, () => _previous != null || IsDirty);
            DropCommand = new AsyncCommand(DropAsync, () => _owner.CanAct && _info != null && !_isNew);
            TidyCommand = new RelayCommand(Tidy, () => Document.TextLength > 0 && _kind == DbObjectKind.View);
        }

        public TextDocument Document { get; }
        public event Action<int, int> SelectRequested;
        public event Action AnalysisChanged;

        public bool IsNew => _isNew;
        public string Name => _info?.Name;
        public string KindWord => _kind.ToString().ToUpperInvariant();
        public bool IsDirty => !_loading && Document.Text != _saved;
        public string StateText => _isNew ? "not saved yet" : IsDirty ? "changed · not saved" : "saved";
        public string PutBackLabel => IsDirty ? "Put back the saved version" : "Put back the version before my last save";

        public ICommand SaveCommand { get; }
        public ICommand PutBackCommand { get; }
        public ICommand DropCommand { get; }
        public ICommand TidyCommand { get; }

        public IReadOnlyList<SqlIssue> Issues => _analysis?.Issues ?? (IReadOnlyList<SqlIssue>)Array.Empty<SqlIssue>();
        public ObservableCollection<MarginNote> Notes { get; } = new ObservableCollection<MarginNote>();
        public bool HasNotes => Notes.Count > 0;

        private string _verdict = "", _verdictKind = "info", _verdictMark = "—";
        public string VerdictText { get => _verdict; private set => Set(ref _verdict, value); }
        public string VerdictKind { get => _verdictKind; private set => Set(ref _verdictKind, value); }
        public string VerdictMark { get => _verdictMark; private set => Set(ref _verdictMark, value); }

        public void Clear()
        {
            _info = null;
            _isNew = false;
            _previous = null;
            SetText("");
            Notify();
        }

        private void SetText(string text)
        {
            _loading = true;
            try
            {
                Document.Text = text ?? "";
                Document.UndoStack.ClearAll();
                _saved = Document.Text;
            }
            finally { _loading = false; }
            Analyse();
        }

        private void Notify()
        {
            OnPropertyChanged(nameof(IsNew));
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(KindWord));
            OnPropertyChanged(nameof(IsDirty));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(PutBackLabel));
            CommandManager.InvalidateRequerySuggested();
        }

        public async Task LoadAsync(DbObjectInfo info)
        {
            _info = info;
            _kind = info.Kind;
            _isNew = false;
            _previous = null;
            try
            {
                using (var c = await _owner.OpenConnectionAsync())
                    SetText(await DbObjects.DefinitionAsync(c, info));
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException)
            {
                SetText("-- Couldn't read the definition: " + ex.Message.Replace("\n", " ") + "\n");
            }
            Notify();
        }

        public void StartNew(DbObjectKind kind, TableSchema table)
        {
            _info = null;
            _kind = kind;
            _isNew = true;
            _previous = null;
            SetText(DbObjects.Template(kind, table));
            _saved = "";
            Notify();
        }

        // ------------------------------------------------------------------ live checks

        internal void Analyse()
        {
            var text = Document.Text;
            _analysis = new SqlGuard(_owner.Schema).Analyze(text, _owner.Database);
            _analysis.Script = text;
            Notes.Clear();
            var seen = new HashSet<int>();
            foreach (var i in _analysis.Issues.Where(i => i.Severity != IssueSeverity.Info && i.Start <= Document.TextLength).OrderBy(i => i.Start))
            {
                int line = Document.GetLineByOffset(Math.Min(i.Start, Document.TextLength)).LineNumber;
                if (!seen.Add(line)) continue;
                var issue = i;
                Notes.Add(new MarginNote
                {
                    Issue = issue,
                    FixCommand = issue.HasFix ? new RelayCommand(() => ApplyFix(issue)) : null,
                    GoToCommand = new RelayCommand(() => SelectRequested?.Invoke(issue.Start, issue.Length)),
                });
            }
            var bad = _analysis.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Bad);
            var warn = _analysis.Issues.FirstOrDefault(i => i.Severity == IssueSeverity.Warn);
            if (text.Trim().Length == 0) { VerdictMark = "—"; VerdictKind = "info"; VerdictText = "Write the definition, then Save (Ctrl+S)."; }
            else if (bad != null) { VerdictMark = "FIX"; VerdictKind = "bad"; VerdictText = bad.Message; }
            else if (warn != null) { VerdictMark = "CAREFUL"; VerdictKind = "run"; VerdictText = warn.Message; }
            else { VerdictMark = "OK"; VerdictKind = "ok"; VerdictText = "Looks fine. Save runs it; the old version is kept so you can put it back."; }
            OnPropertyChanged(nameof(HasNotes));
            AnalysisChanged?.Invoke();
        }

        private void ApplyFix(SqlIssue issue)
        {
            if (_analysis == null || _analysis.Script != Document.Text) { Analyse(); return; }
            Document.BeginUpdate();
            try
            {
                foreach (var f in issue.Fixes.OrderByDescending(f => f.Start))
                    if (f.Start >= 0 && f.Start + f.Length <= Document.TextLength) Document.Replace(f.Start, f.Length, f.Text);
            }
            finally { Document.EndUpdate(); }
            Analyse();
        }

        // ------------------------------------------------------------------ actions

        private async Task SaveAsync()
        {
            Analyse();
            if (_analysis.HasErrors)
            {
                var bad = _analysis.Issues.First(i => i.Severity == IssueSeverity.Bad);
                SelectRequested?.Invoke(bad.Start, bad.Length);
                _owner.Toast("Fix the red notes first: " + bad.Message);
                return;
            }
            var text = Document.Text;
            var kind = _kind;
            var before = new HashSet<string>(ExistingNames(kind), StringComparer.OrdinalIgnoreCase);
            var r = await _owner.RunAsync(text, (_isNew ? "Created " : "Saved ") + kind.ToString().ToLowerInvariant() + (_info != null ? " " + _info.Name : ""), useDatabase: true);
            if (r == null || !r.Ok) return;
            _previous = _isNew ? null : _saved;
            _saved = text;
            Notify();
            if (_isNew)
            {
                await _owner.LoadSchemaAsync();
                var made = ExistingNames(kind).FirstOrDefault(n => !before.Contains(n));
                if (made != null) await _owner.CreatedAsync(kind, made, "definition");
                else _owner.Toast("Saved. It isn't in the list yet; press Ctrl+R if it should be.");
            }
            else
            {
                var keep = _previous;
                await _owner.ReloadAsync(keepPage: true);
                _previous = keep;
                Notify();
                _owner.Toast("Saved. Put back can return the earlier version.");
            }
        }

        private IEnumerable<string> ExistingNames(DbObjectKind kind) =>
            _owner.Groups.First(g => g.Kind == kind).Items.Select(i => i.Name).ToList();

        private void PutBack()
        {
            if (IsDirty) { Document.Text = _saved; _owner.Toast("Back to the saved version."); return; }
            if (_previous == null) return;
            Document.Text = _previous;
            _owner.Toast("The earlier version is in the editor. Save to make it the real one again.");
        }

        private async Task DropAsync()
        {
            var info = _info;
            var choice = await _owner.AskAsync(new ChoiceSlip("CAREFUL", "Drop " + info.Kind.ToString().ToLowerInvariant() + " " + info.Name + "?",
                "It stops working at once. The definition stays here in the editor, so Save makes it again.",
                alarm: true, primary: "Drop it", secondary: null, detail: DbObjects.DropSql(info)));
            if (choice != SlipChoice.Primary) return;
            var text = _saved;
            var r = await _owner.RunAsync(DbObjects.DropSql(info), "Dropped " + info.Kind.ToString().ToLowerInvariant() + " " + info.Name, useDatabase: true);
            if (r == null || !r.Ok) return;
            await _owner.ReopenAsync(info.Kind, null, null);
            _info = null;
            _isNew = true;
            SetText(text);
            _saved = "";
            Notify();
            _owner.ShowUnsavedDefinition();
            _owner.Toast("Dropped. The definition is still here: Save makes it again.");
        }

        private void Tidy()
        {
            string tidy;
            try { tidy = SqlFormatter.Format(Document.Text); }
            catch (Exception ex) { AppLog.Error("tidy failed", ex); _owner.Toast("Couldn't tidy this."); return; }
            if (tidy == Document.Text) { _owner.Toast("Already tidy."); return; }
            Document.Replace(0, Document.TextLength, tidy);
            _owner.Toast("Tidied. Ctrl+Z puts it back.");
        }
    }
}
