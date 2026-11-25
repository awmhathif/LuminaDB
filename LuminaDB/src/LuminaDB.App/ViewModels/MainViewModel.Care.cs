using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using LuminaDB.Core;

namespace LuminaDB.App.ViewModels
{
    /// <summary>
    /// Backups and the Doctor (SPEC §5.9, §5.10): a backup of every changed database every 30 minutes (Settings can change it), before risky
    /// commands and when LuminaDB closes; the Doctor runs after every start and whenever its tab is opened.
    /// </summary>
    public sealed partial class MainViewModel
    {
        private static readonly TimeSpan FirstBackupAfter = TimeSpan.FromMinutes(2);
        private static readonly TimeSpan CloseBackupLimit = TimeSpan.FromSeconds(60);

        private BackupService _backups;
        private DispatcherTimer _backupTimer;
        private bool _autoBackingUp;

        public BackupService BackupStore => _backups;
        public BackupsViewModel Backups { get; private set; }
        public ImportViewModel Import { get; private set; }
        public DiagramViewModel Diagram { get; private set; }

        /// <summary>A file LuminaDB was opened with ("Open with"): shown on the Import page once the server is up.</summary>
        public string OpenOnStart { get; set; }

        public void OpenStartFile()
        {
            var file = OpenOnStart;
            OpenOnStart = null;
            if (string.IsNullOrEmpty(file) || Import == null) return;
            Select("import");
            Import.OpenDropped(file);
        }
        public DoctorViewModel Doctor { get; private set; }
        public AppPaths Paths => _boot.Paths;
        public WindowsInfo Windows => _boot.Windows;
        public DateTime? NextAutoBackup { get; private set; }

        private void InitCare()
        {
            if (_server != null)
            {
                _backups = new BackupService(_boot.Paths, _server);
                _backups.Changed += () => _ui.BeginInvoke(new Action(() => Backups?.OnStoreChanged()));
            }
            _backupTimer = new DispatcherTimer(DispatcherPriority.Background, _ui);
            _backupTimer.Tick += async (s, e) =>
            {
                var every = BackupInterval;
                if (!every.HasValue) { _backupTimer.Stop(); NextAutoBackup = null; return; }
                _backupTimer.Interval = every.Value;
                NextAutoBackup = DateTime.Now + every.Value;
                await AutoBackupAsync("Every " + BackupMinutes + " minutes", BackupKind.Auto);
            };
            Doctor = new DoctorViewModel(this, _ui);
            Backups = new BackupsViewModel(this, _ui);
            Import = new ImportViewModel(this, _ui);
            Diagram = new DiagramViewModel(this, _ui);
        }

        /// <summary>Everything a Doctor run needs, as things stand now.</summary>
        internal LuminaDB.Core.Doctor NewDoctor() => new LuminaDB.Core.Doctor(new DoctorContext
        {
            Paths = _boot.Paths,
            Windows = _boot.Windows,
            Engine = _boot.Engine,
            EngineError = _boot.EngineError,
            Server = _server,
            Backups = _backups,
            Web = Stack,
            WebRunning = WebRunning,
            PreferredPort = _boot.PreferredPort,
            LastStartError = _lastError,
        });

        /// <summary>The server is up (and unlocked): the first automatic backup comes soon, then on the schedule from Settings.</summary>
        private void OnCareServerStarted()
        {
            if (_backups != null && !NeedsPassword && BackupInterval.HasValue)
            {
                _backupTimer.Stop();
                _backupTimer.Interval = FirstBackupAfter;
                NextAutoBackup = DateTime.Now + FirstBackupAfter;
                _backupTimer.Start();
            }
            _ = Doctor.RunAsync();
            Backups.Reload();
        }

        private void OnCareServerStopped()
        {
            _backupTimer?.Stop();
            NextAutoBackup = null;
            if (!_closing)
            {
                _ = Doctor?.RunAsync();
                Backups?.Reload();
            }
        }

        private bool CanBackUp => _backups != null && IsRunning && !NeedsPassword;

        /// <summary>The automatic backup: only databases that changed since their last backup. Quiet when nothing did.</summary>
        internal async Task AutoBackupAsync(string reason, BackupKind kind, CancellationToken ct = default)
        {
            if (!CanBackUp || _autoBackingUp) return;
            _autoBackingUp = true;
            try
            {
                var made = await _backups.BackUpChangedAsync(reason, kind, ct);
                if (made.Count > 0)
                    AddSlip("backup · " + reason.ToLowerInvariant(), Names(made) + " · " + DatabaseSummary.FormatSize(made.Sum(m => m.Bytes)), "ok");
            }
            catch (OperationCanceledException) { AddSlip("backup", "stopped: closing took too long", "info"); }
            catch (Exception ex) when (ex is LuminaException || ex is MySqlConnector.MySqlException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                AddSlip("backup · " + reason.ToLowerInvariant(), ex.Message, "bad");
                AppLog.Error("automatic backup failed", ex);
            }
            finally { _autoBackingUp = false; }
        }

        /// <summary>
        /// Backs up <paramref name="databases"/> before a risky command (SPEC §5.10). Returns false when the backup failed and
        /// the person chose not to go on without it.
        /// </summary>
        public async Task<bool> BackUpBeforeAsync(IEnumerable<string> databases, string why)
        {
            var list = (databases ?? Enumerable.Empty<string>()).ToList();
            if (list.Count == 0 || _backups == null) return true;
            try
            {
                var made = await _backups.BackUpAsync(list, why, BackupKind.BeforeRisky);
                if (made.Count > 0)
                    AddSlip("backup · before a risky change", Names(made) + " · " + DatabaseSummary.FormatSize(made.Sum(m => m.Bytes)) + " · Backups (F10) can put it back", "ok");
                return true;
            }
            catch (Exception ex) when (ex is LuminaException || ex is MySqlConnector.MySqlException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                AppLog.Error("backup before a risky change failed", ex);
                AddSlip("backup · before a risky change", ex.Message, "bad");
                var choice = await AskAsync(new ChoiceSlip("NO BACKUP", "The backup didn't work.",
                    "LuminaDB backs up " + string.Join(", ", list) + " before a command like this, but it failed: " + ex.Message +
                    " Without it, only Undo can take the change back.", alarm: true, primary: "Run anyway", secondary: null, detail: why));
                return choice == SlipChoice.Primary;
            }
        }

        /// <summary>When LuminaDB closes: back up what changed since the last backup, for at most a minute.</summary>
        private async Task BackUpOnCloseAsync()
        {
            if (!CanBackUp) return;
            var slip = AddSlip("backup · on close", "backing up what changed…", "run");
            using (var cts = new CancellationTokenSource(CloseBackupLimit))
            {
                try
                {
                    var made = await _backups.BackUpChangedAsync("When LuminaDB closed", BackupKind.OnClose, cts.Token);
                    slip.Kind = "ok";
                    slip.Message = made.Count == 0 ? "nothing changed since the last backup" : Names(made);
                }
                catch (OperationCanceledException) { slip.Kind = "info"; slip.Message = "skipped: it took longer than a minute"; }
                catch (Exception ex) { slip.Kind = "bad"; slip.Message = ex.Message; AppLog.Error("backup on close failed", ex); }
            }
        }

        /// <summary>Today's quick checks or the full Doctor found something: a red dot on the Doctor tab.</summary>
        internal void UpdateDoctorAlert()
        {
            var tab = Sections.First(s => s.Key == "doctor");
            tab.HasAlert = IssueCount > 0 || (Doctor?.IssueCount ?? 0) > 0;
        }

        /// <summary>Lets the Doctor and Backups pages add to the receipt tape.</summary>
        internal ReceiptSlip Receipt(string title, string message, string kind) => AddSlip(title, message, kind);

        /// <summary>The Doctor's "Start it" / "Restart to rewrite it" fixes.</summary>
        internal async Task RestartServerAsync()
        {
            if (IsRunning) await StopAsync();
            await StartAsync();
        }

        internal Exception LastStartError => _lastError;

        private static string Names(IEnumerable<BackupInfo> made) => string.Join(", ", made.Select(m => m.Database));
    }
}
