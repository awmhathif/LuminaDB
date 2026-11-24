using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using LuminaDB.Core;
using LuminaDB.Core.Projects;

namespace LuminaDB.App.ViewModels
{
    // ====================================================================== Doctor (F9)

    /// <summary>One check on the Doctor page, with its fix button and the FIXED stamp once a fix worked.</summary>
    public sealed class DoctorItem : ObservableObject
    {
        private bool _fixing;
        private string _progress, _fixed, _problem;

        public DoctorItem(CheckResult result, ICommand fix)
        {
            Result = result;
            FixCommand = fix;
        }

        public CheckResult Result { get; }
        public string Id => Result.Id;
        public CheckSeverity Severity => Result.Severity;
        public string Title => Result.Title;
        public string Text => Result.Text;
        public string Code => Result.Code;
        public bool IsOk => Result.Severity == CheckSeverity.Ok;
        public string FixLabel => Result.FixLabel;
        public bool HasFix => Result.FixId != null && _fixed == null;
        public string FixNote => Result.FixBacksUp ? "backs up every database first" : null;
        public bool HasFixNote => HasFix && Result.FixBacksUp;
        public ICommand FixCommand { get; }

        public bool IsFixing { get => _fixing; set { if (Set(ref _fixing, value)) OnPropertyChanged(nameof(HasProgress)); } }
        public string Progress { get => _progress; set { if (Set(ref _progress, value)) OnPropertyChanged(nameof(HasProgress)); } }
        public bool HasProgress => _fixing && !string.IsNullOrEmpty(_progress);
        /// <summary>What the fix did, shown under the FIXED stamp.</summary>
        public string Fixed
        {
            get => _fixed;
            set { if (Set(ref _fixed, value)) { OnPropertyChanged(nameof(IsFixed)); OnPropertyChanged(nameof(HasFix)); OnPropertyChanged(nameof(HasFixNote)); } }
        }
        public bool IsFixed => _fixed != null;
        public string Problem { get => _problem; set { if (Set(ref _problem, value)) OnPropertyChanged(nameof(HasProblem)); } }
        public bool HasProblem => !string.IsNullOrEmpty(_problem);
    }

    public sealed class DoctorViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private readonly Dispatcher _ui;
        /// <summary>Check id → what the fix did; kept across re-runs while the check stays healthy.</summary>
        private readonly Dictionary<string, string> _fixedNotes = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool _checking, _fixing, _again;
        private DateTime? _lastRun;

        public DoctorViewModel(MainViewModel main, Dispatcher ui)
        {
            _main = main;
            _ui = ui;
            RunCommand = new AsyncCommand(RunAsync, () => !_checking && !_fixing);
            FixCommand = new AsyncCommand(p => FixAsync(p as DoctorItem), p => p is DoctorItem d && d.HasFix && !_fixing);
            OpenLogsCommand = new RelayCommand(() => OpenFolder(_main.Paths.Logs));
        }

        public ObservableCollection<DoctorItem> Problems { get; } = new ObservableCollection<DoctorItem>();
        public ObservableCollection<DoctorItem> Healthy { get; } = new ObservableCollection<DoctorItem>();
        public ICommand RunCommand { get; }
        public ICommand FixCommand { get; }
        public ICommand OpenLogsCommand { get; }

        public bool IsChecking { get => _checking; private set { if (Set(ref _checking, value)) { OnPropertyChanged(nameof(RunLabel)); CommandManager.InvalidateRequerySuggested(); } } }
        public string RunLabel => _checking ? "Checking…" : "Check again";
        public int IssueCount => Problems.Count(p => !p.IsFixed);
        public bool HasProblems => Problems.Count > 0;
        public bool HasHealthy => Healthy.Count > 0;
        public bool HasRun => _lastRun.HasValue;

        public string Heading => !_lastRun.HasValue ? "Checking LuminaDB…"
            : IssueCount == 0 ? "Everything checks out."
            : IssueCount == 1 ? "One thing needs a look." : MainViewModel.Capitalise(MainViewModel.NumberWord(IssueCount)) + " things need a look.";

        public string RunNote => !_lastRun.HasValue ? "The usual reasons XAMPP breaks are checked here, with a safe fix for each."
            : "Checked at " + _lastRun.Value.ToString("HH:mm", CultureInfo.InvariantCulture) + " · " + (Problems.Count + Healthy.Count) + " checks" +
              (_main.IsRunning ? "" : " · some need the server running");

        public void OnShown()
        {
            // a recent run (after start, after a fix) is still true; older ones are repeated
            if (!_lastRun.HasValue || DateTime.Now - _lastRun.Value > TimeSpan.FromSeconds(20)) _ = RunAsync();
        }

        public async Task RunAsync()
        {
            if (_checking) { _again = true; return; }
            IsChecking = true;
            try
            {
                do
                {
                    _again = false;
                    List<CheckResult> results;
                    try
                    {
                        var doctor = _main.NewDoctor();
                        results = await Task.Run(() => doctor.RunAsync());
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error("doctor failed", ex);
                        results = new List<CheckResult> { new CheckResult("doctor", CheckSeverity.Warn, "The check-up stopped", ex.Message, "doctor") };
                    }
                    Show(results);
                } while (_again);
            }
            finally { IsChecking = false; }
        }

        private void Show(List<CheckResult> results)
        {
            Problems.Clear();
            Healthy.Clear();
            foreach (var r in results.OrderByDescending(r => r.Severity))
            {
                var item = new DoctorItem(r, FixCommand);
                if (r.Severity == CheckSeverity.Ok)
                {
                    if (_fixedNotes.TryGetValue(r.Id, out var note)) item.Fixed = note;
                    Healthy.Add(item);
                }
                else
                {
                    _fixedNotes.Remove(r.Id);   // it came back: the old stamp no longer holds
                    Problems.Add(item);
                }
            }
            // just-fixed checks come first among the healthy ones, so the stamp is seen
            var stamped = Healthy.Where(h => h.IsFixed).ToList();
            foreach (var s in stamped) { Healthy.Remove(s); Healthy.Insert(0, s); }
            _lastRun = DateTime.Now;
            Changed();
        }

        private void Changed()
        {
            OnPropertyChanged(nameof(IssueCount));
            OnPropertyChanged(nameof(HasProblems));
            OnPropertyChanged(nameof(HasHealthy));
            OnPropertyChanged(nameof(HasRun));
            OnPropertyChanged(nameof(Heading));
            OnPropertyChanged(nameof(RunNote));
            _main.UpdateDoctorAlert();
        }

        private async Task FixAsync(DoctorItem item)
        {
            if (item == null || !item.HasFix || _fixing) return;
            _fixing = true;
            item.IsFixing = true;
            item.Problem = null;
            CommandManager.InvalidateRequerySuggested();
            var slip = _main.Receipt("doctor · " + item.FixLabel.ToLowerInvariant(), null, "run");
            try
            {
                string done;
                switch (item.Result.FixId)
                {
                    case "start-server":
                        await _main.StartAsync();
                        done = _main.IsRunning ? "Started." : null;
                        if (done == null) throw new LuminaException(ErrorKind.Unknown, (_main.LastStartError as LuminaException)?.Message ?? _main.LastStartError?.Message ?? "It didn't start.");
                        break;
                    case "restart-server":
                        await _main.RestartServerAsync();
                        if (!_main.IsRunning) throw new LuminaException(ErrorKind.Unknown, _main.LastStartError?.Message ?? "It didn't start again.");
                        done = "Restarted; the settings were written again from the template.";
                        break;
                    case "open-engine-folder":
                        OpenFolder(_main.Paths.Engines);
                        done = null;   // nothing is fixed until the files are back
                        break;
                    default:
                        var doctor = _main.NewDoctor();
                        var progress = new Progress<string>(t => item.Progress = t);
                        done = await Task.Run(() => doctor.FixAsync(item.Result.FixId, progress));
                        break;
                }
                slip.Kind = done == null ? "info" : "ok";
                slip.Message = done ?? "opened " + _main.Paths.Engines;
                if (done != null)
                {
                    _fixedNotes[item.Id] = done;
                    item.Fixed = done;
                    _main.Toast(done);
                }
            }
            catch (Exception ex) when (ex is LuminaException || ex is MySqlConnector.MySqlException || ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                item.Problem = ex.Message;
                slip.Kind = "bad";
                slip.Message = ex.Message;
                AppLog.Error("doctor fix " + item.Result.FixId + " failed", ex);
            }
            finally
            {
                item.IsFixing = false;
                _fixing = false;
                CommandManager.InvalidateRequerySuggested();
            }
            if (item.IsFixed) await RunAsync();
            else Changed();
        }

        private void OpenFolder(string folder)
        {
            try { Directory.CreateDirectory(folder); Process.Start("explorer.exe", "\"" + folder + "\"")?.Dispose(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception) { _main.Toast("Couldn't open the folder: " + ex.Message); }
        }
    }

    // ====================================================================== Backups (F10)

    /// <summary>A database in the Backups index: one that exists now, or one that is gone but still has backups.</summary>
    public sealed class BackupDatabase : ObservableObject
    {
        private bool _selected;
        public string Name { get; set; }
        public int Count { get; set; }
        public DateTime? Latest { get; set; }
        public bool Exists { get; set; }
        public string CountText => Count == 0 ? "none yet" : Count == 1 ? "1 backup" : Count + " backups";
        public string LatestText => Latest.HasValue ? Doctor.Ago(Latest.Value) : Exists ? "not backed up yet" : "";
        public string GoneText => Exists ? null : "deleted · can be brought back";
        public bool IsGone => !Exists;
        public bool IsSelected { get => _selected; set => Set(ref _selected, value); }
    }

    public sealed class BackupRow
    {
        public BackupRow(BackupInfo info) { Info = info; }
        public BackupInfo Info { get; }
        public string When => Info.Taken.ToString("ddd d MMM · HH:mm", CultureInfo.InvariantCulture);
        public string Ago => Doctor.Ago(Info.Taken);
        public string Reason => string.IsNullOrWhiteSpace(Info.Reason) ? "—" : Info.Reason;
        public string Size => Info.SizeText;
        public string KindText => KindWord(Info.Kind);
        /// <summary>Palette key for the kind stamp.</summary>
        public string Tone => Info.Kind == BackupKind.Manual ? "ok" : Info.Kind == BackupKind.BeforeRisky || Info.Kind == BackupKind.BeforeRestore ? "warn" : "info";
        public string KeepText => Info.IsManual ? "kept until you delete it" : null;

        public static string KindWord(BackupKind k)
        {
            switch (k)
            {
                case BackupKind.Manual: return "BY YOU";
                case BackupKind.BeforeRisky: return "BEFORE CHANGE";
                case BackupKind.OnClose: return "ON CLOSE";
                case BackupKind.BeforeRestore: return "BEFORE RESTORE";
                case BackupKind.First: return "FIRST";
                default: return "AUTO";
            }
        }
    }

    public sealed class SnapshotListRow
    {
        public SnapshotListRow(SnapshotInfo info) { Info = info; }
        public SnapshotInfo Info { get; }
        public string Project => Info.Project;
        public string When => Info.Taken.ToString("ddd d MMM · HH:mm", CultureInfo.InvariantCulture);
        public string Note => string.IsNullOrWhiteSpace(Info.Note) ? "—" : Info.Note;
        public string Holds => Info.HasDatabase ? "code + " + Info.Database : "code only";
        public string Size => Info.SizeText;
    }

    public sealed class BackupsViewModel : ObservableObject
    {
        private const int MaxSnapshotsShown = 12;

        private readonly MainViewModel _main;
        private readonly Dispatcher _ui;
        private BackupDatabase _selected;
        private bool _busy;
        private string _busyText = "";

        public BackupsViewModel(MainViewModel main, Dispatcher ui)
        {
            _main = main;
            _ui = ui;
            SelectDatabaseCommand = new RelayCommand(p => Select(p as BackupDatabase));
            BackUpAllCommand = new AsyncCommand(BackUpAllAsync, () => CanAct);
            BackUpSelectedCommand = new AsyncCommand(BackUpSelectedAsync, () => CanAct && _selected != null && _selected.Exists);
            RestoreCommand = new AsyncCommand(p => RestoreAsync(p as BackupRow), p => CanAct && p is BackupRow);
            DeleteCommand = new AsyncCommand(p => DeleteAsync(p as BackupRow), p => !_busy && p is BackupRow);
            ShowFileCommand = new RelayCommand(p => ShowFile((p as BackupRow)?.Info.SqlFile), p => p is BackupRow);
            OpenFolderCommand = new RelayCommand(() => OpenFolder(_main.Paths.Backups));
            OpenProjectCommand = new RelayCommand(p =>
            {
                if (!(p is SnapshotListRow s)) return;
                _main.Select("projects");
                _main.Projects?.Reload(s.Project);
                if (_main.Projects != null) _main.Projects.Page = "snapshots";
            });
        }

        public ObservableCollection<BackupDatabase> Databases { get; } = new ObservableCollection<BackupDatabase>();
        public ObservableCollection<BackupRow> Rows { get; } = new ObservableCollection<BackupRow>();
        public ObservableCollection<SnapshotListRow> Snapshots { get; } = new ObservableCollection<SnapshotListRow>();

        public ICommand SelectDatabaseCommand { get; }
        public ICommand BackUpAllCommand { get; }
        public ICommand BackUpSelectedCommand { get; }
        public ICommand RestoreCommand { get; }
        public ICommand DeleteCommand { get; }
        public ICommand ShowFileCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand OpenProjectCommand { get; }

        private bool CanAct => !_busy && _main.IsRunning && !_main.NeedsPassword && _main.BackupStore != null;
        public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { OnPropertyChanged(nameof(BusyText)); CommandManager.InvalidateRequerySuggested(); } } }
        public string BusyText { get => _busy ? _busyText : ""; private set { _busyText = value ?? ""; OnPropertyChanged(); } }

        public BackupDatabase Selected => _selected;
        public bool HasSelection => _selected != null;
        public bool HasDatabases => Databases.Count > 0;
        public bool HasRows => Rows.Count > 0;
        public bool HasSnapshots => Snapshots.Count > 0;
        public string SelectedName => _selected?.Name ?? "";
        public string SelectedNote => _selected == null ? ""
            : !_selected.Exists ? _selected.Name + " was deleted. Restore a backup to bring it back exactly as it was."
            : Rows.Count == 0 ? "No backup yet. The automatic one comes within 30 minutes of a change, or press Back up now."
            : "Restore puts the whole database back to that moment. What's there now is backed up first, so a restore can be undone too.";

        public string Heading
        {
            get
            {
                int total = Databases.Sum(d => d.Count);
                if (total == 0) return _main.IsRunning ? "No backups yet." : "Start the server to back up.";
                var latest = Databases.Where(d => d.Latest.HasValue).Max(d => d.Latest.Value);
                return MainViewModel.Capitalise(MainViewModel.NumberWord(total))
                       + (total == 1 ? " backup" : " backups") + ", the newest " + Doctor.Ago(latest) + ".";
            }
        }

        public string ScheduleNote
        {
            get
            {
                if (_main.BackupStore == null) return "Backups need the database engine.";
                if (!_main.IsRunning) return "Automatic backups run while the server is running.";
                var next = _main.NextAutoBackup;
                string when = next.HasValue ? " Next look for changes " + (next.Value <= DateTime.Now.AddSeconds(30) ? "any moment" : "at " + next.Value.ToString("HH:mm", CultureInfo.InvariantCulture)) + "." : "";
                return "Every 30 minutes for databases that changed, before every DROP, TRUNCATE and delete-all, before repairs, and when LuminaDB closes." + when;
            }
        }

        public string KeepNote => $"Keeps the newest {BackupService.KeepLatest} of each database and one a day for {BackupService.KeepDays} days. Ones you take yourself stay until you delete them. Files are plain .sql, readable by XAMPP and Workbench too.";

        public string FolderText => _main.Paths.Backups;

        // ------------------------------------------------------------------ loading

        public void OnStoreChanged()
        {
            if (_main.SelectedSection?.Key == "backups") Reload();   // otherwise it reloads when shown
        }

        public void Reload()
        {
            var keep = _selected?.Name;
            List<BackupInfo> all;
            try { all = _main.BackupStore?.List() ?? new List<BackupInfo>(); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { AppLog.Error("listing backups failed", ex); all = new List<BackupInfo>(); }

            var live = _main.IsRunning ? _main.Databases.Select(d => d.Name).ToList() : new List<string>();
            var names = all.Select(b => b.Database).Concat(live).Distinct(StringComparer.OrdinalIgnoreCase)
                           .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            Databases.Clear();
            foreach (var n in names)
            {
                var mine = all.Where(b => string.Equals(b.Database, n, StringComparison.OrdinalIgnoreCase)).ToList();
                Databases.Add(new BackupDatabase
                {
                    Name = n,
                    Count = mine.Count,
                    Latest = mine.Count == 0 ? (DateTime?)null : mine.Max(b => b.Taken),
                    // while the server is stopped nothing is known to be gone
                    Exists = !_main.IsRunning || live.Contains(n, StringComparer.OrdinalIgnoreCase),
                });
            }
            _selected = null;
            Select(Databases.FirstOrDefault(d => string.Equals(d.Name, keep, StringComparison.OrdinalIgnoreCase)) ?? Databases.FirstOrDefault(), all);
            LoadSnapshots();
            OnPropertyChanged(nameof(HasDatabases));
            OnPropertyChanged(nameof(Heading));
            OnPropertyChanged(nameof(ScheduleNote));
        }

        private void Select(BackupDatabase d, List<BackupInfo> all = null)
        {
            if (_selected != null) _selected.IsSelected = false;
            _selected = d;
            if (d != null) d.IsSelected = true;
            Rows.Clear();
            if (d != null)
            {
                List<BackupInfo> list;
                try { list = all?.Where(b => string.Equals(b.Database, d.Name, StringComparison.OrdinalIgnoreCase)).ToList() ?? _main.BackupStore?.List(d.Name) ?? new List<BackupInfo>(); }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { list = new List<BackupInfo>(); }
                foreach (var b in list.OrderByDescending(b => b.Taken)) Rows.Add(new BackupRow(b));
            }
            OnPropertyChanged(nameof(Selected));
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(SelectedName));
            OnPropertyChanged(nameof(SelectedNote));
            OnPropertyChanged(nameof(HasRows));
            CommandManager.InvalidateRequerySuggested();
        }

        private void LoadSnapshots()
        {
            Snapshots.Clear();
            var archive = _main.Projects?.Archive;
            if (archive != null)
            {
                try
                {
                    var list = new List<SnapshotInfo>();
                    foreach (var p in _main.Projects.Service.List()) list.AddRange(archive.ListSnapshots(p.Name));
                    foreach (var s in list.OrderByDescending(s => s.Taken).Take(MaxSnapshotsShown)) Snapshots.Add(new SnapshotListRow(s));
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { AppLog.Error("listing snapshots failed", ex); }
            }
            OnPropertyChanged(nameof(HasSnapshots));
        }

        // ------------------------------------------------------------------ actions

        private async Task Busy(string text, Func<Task> work)
        {
            BusyText = text;
            IsBusy = true;
            try { await work(); }
            catch (Exception ex) when (ex is LuminaException || ex is MySqlConnector.MySqlException || ex is IOException || ex is UnauthorizedAccessException)
            {
                _main.Receipt("backups", ex.Message, "bad");
                await _main.AskAsync(new ChoiceSlip("BACKUPS", "That didn't work.", ex.Message, alarm: true, primary: "OK", secondary: null, detail: (ex as LuminaException)?.Detail));
                AppLog.Error("backups action failed", ex);
            }
            finally { IsBusy = false; Reload(); }
        }

        private Task BackUpAllAsync() => Busy("Backing up every database…", async () =>
        {
            var made = await _main.BackupStore.BackUpAllAsync("Taken by you", BackupKind.Manual);
            _main.Receipt("backup · by you", made.Count == 0 ? "no databases to back up" : string.Join(", ", made.Select(m => m.Database)) + " · " + DatabaseSummary.FormatSize(made.Sum(m => m.Bytes)), "ok");
            _main.Toast(made.Count == 0 ? "There are no databases to back up yet." : "Backed up " + (made.Count == 1 ? made[0].Database : made.Count + " databases") + ".");
        });

        private Task BackUpSelectedAsync()
        {
            var name = _selected?.Name;
            if (name == null) return Task.CompletedTask;
            return Busy("Backing up " + name + "…", async () =>
            {
                var made = await _main.BackupStore.BackUpAsync(new[] { name }, "Taken by you", BackupKind.Manual);
                _main.Receipt("backup · by you", name + " · " + DatabaseSummary.FormatSize(made.Sum(m => m.Bytes)), "ok");
                _main.Toast("Backed up " + name + ".");
            });
        }

        private async Task RestoreAsync(BackupRow row)
        {
            if (row == null) return;
            var b = row.Info;
            bool exists = _main.Databases.Any(d => string.Equals(d.Name, b.Database, StringComparison.OrdinalIgnoreCase));
            var choice = await _main.AskAsync(new ChoiceSlip("RESTORE", "Put " + b.Database + " back to " + row.When + "?",
                exists
                    ? "Every table in " + b.Database + " goes back to how it was then; tables made later are removed. What's there now is backed up first, so you can undo this from here."
                    : b.Database + " is brought back exactly as it was then.",
                alarm: false, primary: "Restore " + b.Database, secondary: null, detail: row.Reason + " · " + row.Size));
            if (choice != SlipChoice.Primary) return;
            await Busy("Restoring " + b.Database + "…", async () =>
            {
                var safety = await _main.BackupStore.RestoreAsync(b);
                _main.Receipt("restore " + b.Database, "back to " + row.When + (safety != null ? " · the state before is saved as a backup" : ""), "ok");
                _main.Toast("Restored " + b.Database + (safety != null ? ". The previous state is saved too." : "."));
                _main.DataChanged();
                await _main.Notebook.OnServerStartedAsync();   // fresh schema for the margin checks
            });
        }

        private async Task DeleteAsync(BackupRow row)
        {
            if (row == null) return;
            var choice = await _main.AskAsync(new ChoiceSlip("DELETE BACKUP", "Delete this backup of " + row.Info.Database + "?",
                "The " + row.When + " backup (" + row.Size + ") is removed from the disk. Other backups stay.",
                alarm: true, primary: "Delete it", secondary: null, detail: row.Reason));
            if (choice != SlipChoice.Primary) return;
            try { _main.BackupStore.Delete(row.Info); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { _main.Toast("Couldn't delete it: " + ex.Message); }
            Reload();
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
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception) { _main.Toast("Couldn't open the folder: " + ex.Message); }
        }
    }
}
