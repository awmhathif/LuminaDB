using System;
using System.Threading.Tasks;
using System.Windows.Threading;
using LuminaDB.Core;
using LuminaDB.Core.Admin;

namespace LuminaDB.App.ViewModels
{
    /// <summary>Users, Server and Settings (SPEC §5.7, §5.8, §5.13) and the things they change app-wide.</summary>
    public sealed partial class MainViewModel
    {
        private AppSettings _settings;

        public UsersViewModel Users { get; private set; }
        public ServerViewModel ServerPage { get; private set; }
        public SettingsViewModel SettingsPage { get; private set; }

        /// <summary>config\settings.json (port, backup schedule, text size).</summary>
        public AppSettings AppSettings => _settings;

        private void InitAdmin()
        {
            _settings = AppSettings.Load(_boot.Paths);
            Users = new UsersViewModel(this, _ui);
            ServerPage = new ServerViewModel(this, _ui);
            SettingsPage = new SettingsViewModel(this, _ui);
        }

        /// <summary>Everything is drawn this much larger (Settings › Text size).</summary>
        public double TextSize => _settings?.TextSize ?? 1.0;

        /// <summary>Minutes between automatic backups; 0 = off.</summary>
        public int BackupMinutes => _settings?.BackupMinutes ?? 30;
        public TimeSpan? BackupInterval => BackupMinutes <= 0 ? (TimeSpan?)null : TimeSpan.FromMinutes(BackupMinutes);

        internal void SaveSettings()
        {
            _settings.Save(_boot.Paths);
            OnPropertyChanged(nameof(TextSize));
            OnPropertyChanged(nameof(BackupMinutes));
        }

        /// <summary>Settings › Backups: takes effect at once (the next look for changes is one interval from now).</summary>
        internal void ApplyBackupSchedule()
        {
            if (_backupTimer == null) return;
            _backupTimer.Stop();
            NextAutoBackup = null;
            var every = BackupInterval;
            if (every.HasValue && _backups != null && IsRunning && !NeedsPassword)
            {
                _backupTimer.Interval = every.Value;
                NextAutoBackup = DateTime.Now + every.Value;
                _backupTimer.Start();
            }
            Backups?.Reload();
        }

        // ------------------------------------------------------------------ root password

        /// <summary>Users and Settings: the slip that sets or removes the root password on every root account.</summary>
        public void OpenRootPasswordSlip()
        {
            if (_server == null || !IsRunning) return;
            Slip = new RootPasswordSlip(CloseSlip, _server.RootHasPassword, SetRootPasswordAsync);
        }

        private async Task SetRootPasswordAsync(string password)
        {
            await _server.SetRootPasswordAsync(password);
            AddSlip("root password", password.Length == 0 ? "removed from every root account" : "changed on every root account · saved encrypted for your Windows account", "ok");
            Toast(password.Length == 0 ? "root has no password now." : "root password changed.");
            // phpMyAdmin opens straight in without a password, and asks for it with one.
            if (_web != null && WebRunning && _web.Settings?.PhpMyAdmin == true)
            {
                try { await _web.SetPhpMyAdminAsync(true, PhpRuntime()); }
                catch (LuminaException ex) { AppLog.Error("phpMyAdmin config after password change", ex); }
            }
            UpdateStatus();
            await Users.ReloadAsync();
            SettingsPage.Refresh();
        }

        // ------------------------------------------------------------------ text questions

        /// <summary>Shows a one-line question and waits for the answer (null = cancelled).</summary>
        public Task<string> AskTextAsync(TextSlip q)
        {
            q.Dismiss = () => { if (ReferenceEquals(_slip, q)) Slip = null; };
            Slip = q;
            return q.Result;
        }

        internal void OpenLink(string url) => OpenUrl(url);

        internal void OnAdminServerChanged()
        {
            if (SelectedSection?.Key == "users") Users?.OnShown();
            ServerPage?.OnServerChanged();
            SettingsPage?.Refresh();
        }

        internal Dispatcher Ui => _ui;
    }
}
