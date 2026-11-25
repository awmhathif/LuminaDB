using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using LuminaDB.Core;
using LuminaDB.Core.Admin;
using LuminaDB.Core.Web;

namespace LuminaDB.App.ViewModels
{
    /// <summary>One choice in a row of buttons (backup schedule, text size).</summary>
    public sealed class SettingChoice : ObservableObject
    {
        private bool _on;
        public SettingChoice(string label, object value) { Label = label; Value = value; }
        public string Label { get; }
        public object Value { get; }
        public bool IsOn { get => _on; set => Set(ref _on, value); }
    }

    /// <summary>
    /// Settings (SPEC §5.13): ports, start with Windows, root password, backup schedule, text size, phpMyAdmin, moving the
    /// data folder, and what this copy of LuminaDB runs. Everything has a sensible default; most people never come here.
    /// </summary>
    public sealed class SettingsViewModel : ObservableObject
    {
        private readonly MainViewModel _main;
        private readonly Dispatcher _ui;
        private readonly WindowsStartup _startup = new WindowsStartup();
        private string _dbPort, _webPort, _phpPort, _mailPort, _portProblem, _moveTo = "", _busyText = "";
        private bool _busy;

        public SettingsViewModel(MainViewModel main, Dispatcher ui)
        {
            _main = main;
            _ui = ui;
            foreach (var m in AppSettings.BackupChoices) BackupChoices.Add(new SettingChoice(m == 0 ? "Off" : "Every " + m + " min", m));
            foreach (var t in AppSettings.TextSizeChoices) TextChoices.Add(new SettingChoice((t * 100).ToString("0", CultureInfo.InvariantCulture) + "%", t));
            SavePortsCommand = new AsyncCommand(SavePortsAsync, () => !_busy && PortsChanged);
            ResetPortsCommand = new RelayCommand(LoadPorts, () => PortsChanged);
            BackupCommand = new RelayCommand(p => { if (p is SettingChoice c) SetBackup((int)c.Value); });
            TextCommand = new RelayCommand(p => { if (p is SettingChoice c) SetText((double)c.Value); });
            RootPasswordCommand = new RelayCommand(() => _main.OpenRootPasswordSlip(), () => _main.IsRunning && !_main.NeedsPassword && !_busy);
            OpenPhpMyAdminCommand = new RelayCommand(() => _main.OpenLink(_main.Web.PhpMyAdminUrl), () => _main.WebRunning && _main.Web.PhpMyAdminOn);
            ChooseFolderCommand = new RelayCommand(ChooseFolder, () => !_busy);
            MoveCommand = new AsyncCommand(MoveAsync, () => !_busy && _main.Server != null && MoveProblem == null);
            OpenDataCommand = new RelayCommand(() => OpenFolder(_main.Paths.Data));
            LoadPorts();
            Refresh();
        }

        public List<SettingChoice> BackupChoices { get; } = new List<SettingChoice>();
        public List<SettingChoice> TextChoices { get; } = new List<SettingChoice>();
        public ICommand SavePortsCommand { get; }
        public ICommand ResetPortsCommand { get; }
        public ICommand BackupCommand { get; }
        public ICommand TextCommand { get; }
        public ICommand RootPasswordCommand { get; }
        public ICommand OpenPhpMyAdminCommand { get; }
        public ICommand ChooseFolderCommand { get; }
        public ICommand MoveCommand { get; }
        public ICommand OpenDataCommand { get; }

        public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) { OnPropertyChanged(nameof(BusyText)); CommandManager.InvalidateRequerySuggested(); } } }
        public string BusyText => _busy ? _busyText : "";

        /// <summary>Reads everything again (the page is shown, the server or the web server changed).</summary>
        public void Refresh()
        {
            foreach (var c in BackupChoices) c.IsOn = (int)c.Value == _main.BackupMinutes;
            foreach (var c in TextChoices) c.IsOn = Math.Abs((double)c.Value - _main.TextSize) < 0.001;
            foreach (var n in new[] { nameof(StartWithWindows), nameof(StartupNote), nameof(RootNote), nameof(PhpMyAdmin), nameof(PhpMyAdminNote), nameof(HasPhpMyAdmin),
                                      nameof(DataFolder), nameof(DataNote), nameof(MoveProblem), nameof(EngineFacts), nameof(EngineNote), nameof(PortsNote), nameof(BackupNote) })
                OnPropertyChanged(n);
            CommandManager.InvalidateRequerySuggested();
        }

        // ------------------------------------------------------------------ ports

        public string DbPort { get => _dbPort; set { if (Set(ref _dbPort, value)) PortsEdited(); } }
        public string WebPort { get => _webPort; set { if (Set(ref _webPort, value)) PortsEdited(); } }
        public string PhpPort { get => _phpPort; set { if (Set(ref _phpPort, value)) PortsEdited(); } }
        public string MailPort { get => _mailPort; set { if (Set(ref _mailPort, value)) PortsEdited(); } }
        public string PortProblem { get => _portProblem; private set { if (Set(ref _portProblem, value)) OnPropertyChanged(nameof(HasPortProblem)); } }
        public bool HasPortProblem => !string.IsNullOrEmpty(_portProblem);

        public string PortsNote
        {
            get
            {
                var s = _main.Server;
                var parts = new List<string>();
                if (s != null && s.Port > 0) parts.Add("Database on " + s.Port.ToString(CultureInfo.InvariantCulture));
                if (_main.WebRunning) parts.Add("web on " + _main.Web.WebPort + ", PHP on " + _main.Web.PhpPort);
                return (parts.Count == 0 ? "" : "Now: " + string.Join(", ", parts) + ". ") +
                       "These are the ports LuminaDB tries first; if another program has one, it takes the next free port and tells you. 3306 and 80 are what XAMPP uses.";
            }
        }

        private void LoadPorts()
        {
            var web = WebSettings.Load(_main.Paths);
            _dbPort = _main.AppSettings.DatabasePort.ToString(CultureInfo.InvariantCulture);
            _webPort = web.PreferredWebPort.ToString(CultureInfo.InvariantCulture);
            _phpPort = web.PreferredPhpPort.ToString(CultureInfo.InvariantCulture);
            _mailPort = web.PreferredMailPort.ToString(CultureInfo.InvariantCulture);
            foreach (var n in new[] { nameof(DbPort), nameof(WebPort), nameof(PhpPort), nameof(MailPort) }) OnPropertyChanged(n);
            PortProblem = null;
            PortsEdited();
        }

        private bool PortsChanged
        {
            get
            {
                var web = WebSettings.Load(_main.Paths);
                return _dbPort != _main.AppSettings.DatabasePort.ToString(CultureInfo.InvariantCulture) || _webPort != web.PreferredWebPort.ToString(CultureInfo.InvariantCulture)
                       || _phpPort != web.PreferredPhpPort.ToString(CultureInfo.InvariantCulture) || _mailPort != web.PreferredMailPort.ToString(CultureInfo.InvariantCulture);
            }
        }

        private void PortsEdited() { PortProblem = null; CommandManager.InvalidateRequerySuggested(); }

        private static int? ParsePort(string s) => int.TryParse((s ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?)null;

        private async Task SavePortsAsync()
        {
            var ports = new[] { ("Database", ParsePort(_dbPort), 1024), ("Web", ParsePort(_webPort), 1), ("PHP", ParsePort(_phpPort), 1024), ("Mail", ParsePort(_mailPort), 1) };
            foreach (var (name, port, min) in ports)
            {
                if (port == null || port < min || port > 65535) { PortProblem = name + ": choose a port from " + min + " to 65535."; return; }
            }
            var all = ports.Select(p => p.Item2.Value).ToList();
            if (all.Distinct().Count() != all.Count) { PortProblem = "Each service needs its own port."; return; }

            int db = all[0];
            bool dbChanged = db != _main.AppSettings.DatabasePort;
            var web = WebSettings.Load(_main.Paths);
            bool webChanged = web.PreferredWebPort != all[1] || web.PreferredPhpPort != all[2] || web.PreferredMailPort != all[3];

            _main.AppSettings.DatabasePort = db;
            _main.SaveSettings();
            if (dbChanged) _main.Server?.SetPreferredPort(db);
            if (webChanged)
            {
                web.PreferredWebPort = all[1]; web.PreferredPhpPort = all[2]; web.PreferredMailPort = all[3];
                if (_main.Web != null) _main.Web.SaveSettings(web); else web.Save(_main.Paths);
                // Forget the remembered ports, so the new ones are tried first next time.
                var st = LuminaState.Load(_main.Paths);
                st.WebPort = null; st.PhpPort = null; st.MailPort = null;
                st.Save(_main.Paths);
            }
            _main.Receipt("settings · ports", "database " + db + " · web " + all[1] + " · PHP " + all[2] + " · mail " + all[3], "ok");
            PortsEdited();

            bool restartDb = dbChanged && _main.IsRunning, restartWeb = webChanged && _main.WebRunning;
            if (!restartDb && !restartWeb) { _main.Toast("Saved. The new ports are used at the next start."); return; }
            var choice = await _main.AskAsync(new ChoiceSlip("PORTS", "Restart now?",
                (restartDb ? "The database server" : "The web server") + (restartDb && restartWeb ? " and the web server" : "") +
                " start again on the new ports. PHP code that names a port (config.php) needs the new one.",
                alarm: false, primary: "Restart now", secondary: "Later", detail: null));
            if (choice != SlipChoice.Primary) return;
            _busyText = "Restarting…";
            IsBusy = true;
            try
            {
                // The web server is only brought back if it was running: PHP needs the database's new port, so it
                // restarts with it, but a web server the user had stopped stays stopped.
                bool webWasRunning = _main.WebRunning;
                if (webWasRunning) await _main.StopWebAsync();
                if (restartDb) await _main.RestartServerAsync();
                if (webWasRunning) await _main.StartWebAsync();
            }
            finally { IsBusy = false; Refresh(); }
        }

        // ------------------------------------------------------------------ start with Windows

        private static string Exe => System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "";

        public bool StartWithWindows
        {
            get { try { return _startup.IsOn(Exe); } catch (Exception) { return false; } }
            set
            {
                try
                {
                    _startup.Set(value, Exe);
                    _main.Receipt("settings · start with Windows", value ? "on · LuminaDB opens minimised when you sign in" : "off", "ok");
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is IOException)
                {
                    _main.Toast("Windows didn't allow it: " + ex.Message);
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(StartupNote));
            }
        }

        public string StartupNote => "Opens LuminaDB, minimised, when you sign in to Windows, and starts the database. It's a normal start-up entry for your account only, " +
                                     "not a Windows service; turn it off here or in Task Manager › Startup apps." +
                                     (_main.Paths.Root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) ? " (This copy runs from a temporary folder.)" : "");

        // ------------------------------------------------------------------ root password

        public string RootNote
        {
            get
            {
                var s = _main.Server;
                if (s == null) return "No database engine in this copy.";
                if (!_main.IsRunning) return "Start the server to set or change it.";
                if (_main.NeedsPassword) return "This PC doesn't know the root password yet; enter it on Today first.";
                return s.RootHasPassword
                    ? "root has a password. LuminaDB keeps it encrypted for your Windows account, so you never type it here."
                    : "root has no password, like XAMPP. Fine on your own PC; set one if others use it or your lecturer asks.";
            }
        }

        // ------------------------------------------------------------------ backups and text size

        public string BackupNote => _main.BackupInterval.HasValue
            ? "Databases that changed are backed up every " + _main.BackupMinutes + " minutes while the server runs."
            : "Off: only the backups before risky commands, before repairs and on closing are taken.";

        private void SetBackup(int minutes)
        {
            if (minutes == _main.BackupMinutes) return;
            _main.AppSettings.BackupMinutes = minutes;
            _main.SaveSettings();
            _main.ApplyBackupSchedule();
            _main.Receipt("settings · backups", minutes == 0 ? "automatic backups off" : "every " + minutes + " minutes", "ok");
            Refresh();
        }

        private void SetText(double size)
        {
            if (Math.Abs(size - _main.TextSize) < 0.001) return;
            _main.AppSettings.TextSize = size;
            _main.SaveSettings();
            Refresh();
        }

        // ------------------------------------------------------------------ phpMyAdmin

        public bool HasPhpMyAdmin => _main.Stack != null && _main.Stack.HasPhpMyAdmin;
        public bool PhpMyAdmin
        {
            get => WebSettings.Load(_main.Paths).PhpMyAdmin;
            set => _ = SetPhpMyAdminAsync(value);
        }

        public string PhpMyAdminNote
        {
            get
            {
                if (!HasPhpMyAdmin) return "phpMyAdmin isn't included in this copy of LuminaDB.";
                var on = WebSettings.Load(_main.Paths).PhpMyAdmin;
                if (!on) return "Off. LuminaDB does what phpMyAdmin does; turn it on if your lecturer asks for phpMyAdmin.";
                var url = _main.WebRunning ? _main.Web.PhpMyAdminUrl : "http://phpmyadmin.localhost/";
                var login = _main.Server != null && _main.Server.RootHasPassword ? " It asks for root and the root password." : " It opens straight in, as root.";
                return "On at " + url + (_main.WebRunning ? "." : " (when the web server runs).") + login + " Only this PC can reach it.";
            }
        }

        private async Task SetPhpMyAdminAsync(bool on)
        {
            if (!HasPhpMyAdmin) { OnPropertyChanged(nameof(PhpMyAdmin)); return; }
            _busyText = on ? "Turning phpMyAdmin on…" : "Turning phpMyAdmin off…";
            IsBusy = true;
            try
            {
                if (_main.Web != null) await _main.Web.SetPhpMyAdminAsync(on, _main.PhpRuntime());
                else { var w = WebSettings.Load(_main.Paths); w.PhpMyAdmin = on; w.Save(_main.Paths); }
                _main.Receipt("phpMyAdmin", on ? (_main.WebRunning ? "on · " + _main.Web.PhpMyAdminUrl : "on · when the web server starts") : "off", "ok");
            }
            catch (LuminaException ex) { _main.Receipt("phpMyAdmin", ex.Message, "bad"); _main.Toast(ex.Message); }
            finally { IsBusy = false; Refresh(); }
        }

        // ------------------------------------------------------------------ data folder

        public string DataFolder => _main.Paths.Data;
        public string DataNote
        {
            get
            {
                long bytes = 0;
                try
                {
                    if (Directory.Exists(_main.Paths.Data))
                        bytes = new DirectoryInfo(_main.Paths.Data).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
                return DatabaseSummary.FormatSize(bytes) + (_main.Paths.DataMoved ? " · moved here from the LuminaDB folder" : " · inside the LuminaDB folder") +
                       ". Moving stops the server, copies everything, checks every file and every database arrived, then starts from the new place. " +
                       "The old folder is kept (renamed …old-date) until you delete it.";
            }
        }

        public string MoveTo { get => _moveTo; set { if (Set(ref _moveTo, value ?? "")) { OnPropertyChanged(nameof(MoveProblem)); OnPropertyChanged(nameof(HasMoveProblem)); CommandManager.InvalidateRequerySuggested(); } } }
        public string MoveProblem => string.IsNullOrWhiteSpace(_moveTo) ? "" : DataFolderMover.Check(_main.Paths, _moveTo);
        public bool HasMoveProblem => !string.IsNullOrEmpty(MoveProblem);

        private void ChooseFolder()
        {
            using (var d = new System.Windows.Forms.FolderBrowserDialog { Description = "Choose an empty folder for LuminaDB's databases", ShowNewFolderButton = true })
            {
                if (d.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var path = d.SelectedPath;
                // An empty folder is used as it is; a folder with things in it gets a LuminaDB data folder inside.
                if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) path = Path.Combine(path, "LuminaDB data");
                MoveTo = path;
            }
        }

        private async Task MoveAsync()
        {
            var target = Path.GetFullPath(_moveTo.Trim());
            var choice = await _main.AskAsync(new ChoiceSlip("MOVE THE DATA FOLDER", "Move every database?",
                "From " + _main.Paths.Data + " to " + target + ". The database server stops while the files are copied (PHP pages can't reach it for that time), " +
                "then starts from the new place. If any check fails, everything goes back the way it was.",
                alarm: false, primary: "Move it", secondary: "Not now", detail: null));
            if (choice != SlipChoice.Primary) return;

            _busyText = "Moving the data folder…";
            IsBusy = true;
            var slip = _main.Receipt("data folder · move", "starting…", "run");
            bool wasRunning = _main.IsRunning;
            var expected = wasRunning ? _main.Databases.Select(d => d.Name).ToList() : null;
            try
            {
                if (_main.WebRunning) await _main.StopWebAsync();
                if (wasRunning) await _main.StopAsync();
                var r = await DataFolderMover.MoveAsync(_main.Server, target, new Progress<string>(m => slip.Message = m), expected);
                slip.Kind = "ok";
                slip.Message = r.Files + " files · " + DatabaseSummary.FormatSize(r.Bytes) + " · now in " + r.To + " · old copy kept in " + r.OldCopy;
                MoveTo = "";
                _main.Toast("The data folder moved. Every database is there.");
            }
            catch (Exception ex) when (ex is LuminaException || ex is IOException || ex is UnauthorizedAccessException || ex is MySqlConnector.MySqlException)
            {
                slip.Kind = "bad";
                slip.Message = ex.Message;
                AppLog.Error("data folder move failed", ex);
                await _main.AskAsync(new ChoiceSlip("MOVE THE DATA FOLDER", "It stayed where it was.", ex.Message + " Nothing was changed.",
                    alarm: true, primary: "OK", secondary: null, detail: (ex as LuminaException)?.Detail));
            }
            finally
            {
                if (wasRunning && !_main.IsRunning) await _main.StartAsync();
                if (wasRunning && _main.IsRunning && _main.CanServeWeb && !_main.WebRunning && WebSettings.Load(_main.Paths).StartWithDatabase) await _main.StartWebAsync();
                IsBusy = false;
                Refresh();
            }
        }

        // ------------------------------------------------------------------ engine set

        public string EngineFacts
        {
            get
            {
                var s = _main.Server;
                var parts = new List<string>();
                if (s != null) parts.Add(EngineSet.FolderName(s.Engine.Kind) + " engine set · MariaDB " + s.Engine.ServerVersion);
                var php = _main.Stack?.PhpVersion;
                if (php != null) parts.Add("PHP " + php);
                if (_main.Stack != null) parts.Add(_main.Stack.UsesCaddy ? "Caddy (+ Apache for .htaccess)" : _main.Stack.HasApache ? "Apache" : "no web server");
                parts.Add("folder " + _main.Paths.Root);
                return string.Join(" · ", parts);
            }
        }

        public string EngineNote
        {
            get
            {
                var k = _main.Server?.Engine.Kind;
                if (k == EngineSetKind.Modern) return "The current versions, chosen for this PC's Windows.";
                if (k == null) return "The database engine is missing; the Doctor (F9) can explain.";
                return "This PC's Windows is too old for the current versions, so LuminaDB uses older ones that still run on it. They don't get security fixes any more: " +
                       "fine for coursework on this PC, but don't put anything you need to keep private on it.";
            }
        }

        private void OpenFolder(string path)
        {
            try { Directory.CreateDirectory(path); System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"")?.Dispose(); }
            catch (Exception ex) { _main.Toast("Couldn't open the folder: " + ex.Message); }
        }
    }
}
