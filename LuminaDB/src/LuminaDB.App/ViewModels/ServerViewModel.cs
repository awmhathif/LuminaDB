using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using LuminaDB.Core;
using LuminaDB.Core.Admin;
using LuminaDB.Core.Web;
using MySqlConnector;

namespace LuminaDB.App.ViewModels
{
    /// <summary>One number on the status page.</summary>
    public sealed class StatusTile
    {
        public string Label { get; set; }
        public string Value { get; set; }
        public string Note { get; set; }
    }

    /// <summary>One log the Logs page can show.</summary>
    public sealed class LogSource
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public string Note { get; set; }
        public override string ToString() => Name;
    }

    /// <summary>A variable in the list, with what is saved for the next start.</summary>
    public sealed class VariableRow
    {
        public VariableRow(ServerVariable v, string saved) { Variable = v; Saved = saved; }
        public ServerVariable Variable { get; }
        public string Saved { get; }
        public string Name => Variable.Name;
        public string Value => Variable.Value != null && Variable.Value.Length > 80 ? Variable.Value.Substring(0, 80) + "…" : Variable.Value;
        public bool HasNote => Variable.Note != null;
        public bool IsSaved => Saved != null;
    }

    /// <summary>
    /// Server (SPEC §5.8): status, connections with Kill, variables (change now or for every start), logs with the
    /// slow and general log switches, and the mariadb console on the right port.
    /// </summary>
    public sealed class ServerViewModel : ObservableObject
    {
        private const int MaxVariablesShown = 400;
        private readonly MainViewModel _main;
        private readonly Dispatcher _ui;
        private readonly DispatcherTimer _timer;
        private string _page = "status";
        private ServerStatus _last;
        private bool _shown, _sampling, _busy;
        private string _problem, _search = "", _edit = "", _editProblem;
        private List<ServerVariable> _variables = new List<ServerVariable>();
        private VariableRow _selectedVariable;
        private LogSource _log;
        private string _logText = "";
        private bool _slowLog, _generalLog;

        public ServerViewModel(MainViewModel main, Dispatcher ui)
        {
            _main = main;
            _ui = ui;
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, async (s, e) => await SampleAsync(), ui);
            _timer.Stop();
            PageCommand = new RelayCommand(p => Page = p as string ?? "status");
            KillQueryCommand = new AsyncCommand(p => KillAsync(p as ServerConnection, true), p => CanAct && (p as ServerConnection)?.CanKill == true);
            KillCommand = new AsyncCommand(p => KillAsync(p as ServerConnection, false), p => CanAct && (p as ServerConnection)?.CanKill == true);
            SetNowCommand = new AsyncCommand(() => SetVariableAsync(keep: false), () => CanAct && _selectedVariable != null && _selectedVariable.Variable.CanSetNow);
            KeepCommand = new AsyncCommand(() => SetVariableAsync(keep: true), () => CanAct && _selectedVariable != null && ServerOverrides.CheckKeep(_selectedVariable.Variable) == null);
            ForgetCommand = new AsyncCommand(ForgetAsync, () => CanAct && _selectedVariable?.IsSaved == true);
            RefreshLogCommand = new RelayCommand(LoadLog);
            OpenLogFolderCommand = new RelayCommand(() => OpenFolder(_main.Paths.Logs));
            OpenLogFileCommand = new RelayCommand(() => OpenFolder(_log?.Path, select: true), () => _log != null && File.Exists(_log.Path));
            ConsoleCommand = new RelayCommand(OpenConsole, () => CanAct && _main.Server?.Engine != null && File.Exists(_main.Server.Engine.ClientExe));
            RestartCommand = new AsyncCommand(async () => { await _main.RestartServerAsync(); await ReloadAsync(); }, () => !_busy && _main.IsRunning);
        }

        public ObservableCollection<StatusTile> Tiles { get; } = new ObservableCollection<StatusTile>();
        public ObservableCollection<ServerConnection> Connections { get; } = new ObservableCollection<ServerConnection>();
        public ObservableCollection<VariableRow> Variables { get; } = new ObservableCollection<VariableRow>();
        public ObservableCollection<LogSource> Logs { get; } = new ObservableCollection<LogSource>();

        public ICommand PageCommand { get; }
        public ICommand KillQueryCommand { get; }
        public ICommand KillCommand { get; }
        public ICommand SetNowCommand { get; }
        public ICommand KeepCommand { get; }
        public ICommand ForgetCommand { get; }
        public ICommand RefreshLogCommand { get; }
        public ICommand OpenLogFolderCommand { get; }
        public ICommand OpenLogFileCommand { get; }
        public ICommand ConsoleCommand { get; }
        public ICommand RestartCommand { get; }

        private bool CanAct => !_busy && _main.IsRunning && !_main.NeedsPassword;
        public bool IsAvailable => _main.IsRunning && !_main.NeedsPassword;
        public string OfflineNote => _main.NeedsPassword ? "Enter the root password first (the slip on Today asks for it)." : "The server is stopped. Start it on Today (F1); the logs below still work.";
        public string Problem { get => _problem; private set { if (Set(ref _problem, value)) OnPropertyChanged(nameof(HasProblem)); } }
        public bool HasProblem => !string.IsNullOrEmpty(_problem);

        public string Page
        {
            get => _page;
            set
            {
                if (!Set(ref _page, value)) return;
                foreach (var n in new[] { nameof(IsStatus), nameof(IsConnections), nameof(IsVariables), nameof(IsLogs) }) OnPropertyChanged(n);
                if (value == "variables" && _variables.Count == 0) _ = LoadVariablesAsync();
                if (value == "logs") LoadLog();
                _ = SampleAsync();
            }
        }
        public bool IsStatus => _page == "status";
        public bool IsConnections => _page == "connections";
        public bool IsVariables => _page == "variables";
        public bool IsLogs => _page == "logs";

        public string Facts
        {
            get
            {
                var s = _main.Server;
                if (s == null) return "";
                var set = EngineSet.FolderName(s.Engine.Kind);
                return "MariaDB " + (s.ServerVersion ?? s.Engine.ServerVersion.ToString()) + " · " + set + " engine set · " +
                       (s.Port > 0 ? "port " + s.Port.ToString(CultureInfo.InvariantCulture) + " on 127.0.0.1 only" : "stopped");
            }
        }
        public string DataDir => _main.Server?.DataDir ?? "";
        public string ConfigFile => _main.Server?.ConfigFile ?? "";
        public string SetAsideNote => _main.Server?.OverridesSetAside == null ? null
            : "The server didn't start with your saved settings, so they were put aside (kept in " + Path.GetFileName(_main.Server.OverridesSetAside) + ") and it started without them.";
        public bool HasSetAside => SetAsideNote != null;

        // ------------------------------------------------------------------ life cycle

        public void OnShown(bool shown)
        {
            _shown = shown;
            if (!shown) { _timer.Stop(); return; }
            Notify();
            BuildLogList();
            if (IsAvailable)
            {
                _timer.Start();
                _ = ReloadAsync();
            }
            else { Tiles.Clear(); Connections.Clear(); }
            if (IsLogs) LoadLog();
        }

        public void OnServerChanged()
        {
            _variables.Clear();
            Variables.Clear();
            _last = null;
            if (_shown) OnShown(true);
            else Notify();
        }

        private void Notify()
        {
            foreach (var n in new[] { nameof(IsAvailable), nameof(OfflineNote), nameof(Facts), nameof(DataDir), nameof(ConfigFile), nameof(SetAsideNote), nameof(HasSetAside) }) OnPropertyChanged(n);
        }

        private async Task ReloadAsync()
        {
            await SampleAsync();
            await LoadLogSwitchesAsync();
            if (IsVariables) await LoadVariablesAsync();
        }

        private async Task SampleAsync()
        {
            if (!_shown || !IsAvailable || _sampling) return;
            if (!IsStatus && !IsConnections) return;
            _sampling = true;
            try
            {
                using (var c = await _main.Server.OpenConnectionAsync())
                {
                    var st = await ServerMonitor.ReadStatusAsync(c);
                    ShowStatus(st, _last);
                    _last = st;
                    if (IsConnections)
                    {
                        var list = await ServerMonitor.ListConnectionsAsync(c);
                        Connections.Clear();
                        foreach (var x in list.OrderBy(x => x.IsSystem).ThenBy(x => x.Id)) Connections.Add(x);
                        OnPropertyChanged(nameof(ConnectionsNote));
                    }
                }
                Problem = null;
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException || ex is InvalidOperationException) { Problem = ex.Message; }
            finally { _sampling = false; }
        }

        private void ShowStatus(ServerStatus st, ServerStatus earlier)
        {
            string N(double v, string f = "0") => v.ToString(f, CultureInfo.InvariantCulture);
            var tiles = new[]
            {
                new StatusTile { Label = "UP FOR", Value = ServerStatus.Duration(st.Uptime), Note = "since the server last started" },
                new StatusTile { Label = "CONNECTIONS", Value = N(st.ThreadsConnected), Note = N(st.ThreadsRunning) + " busy now · at most " + N(st.MaxUsedConnections) + " at once" },
                new StatusTile { Label = "QUERIES / SECOND", Value = N(st.QueriesPerSecond(earlier), "0.0"), Note = N(st.Questions) + " since start (LuminaDB's own included)" },
                new StatusTile { Label = "TRAFFIC", Value = ServerStatus.Bytes(st.BytesSent), Note = "sent · " + ServerStatus.Bytes(st.BytesReceived) + " received" },
                new StatusTile { Label = "BUFFER POOL", Value = N(st.BufferPoolUsed * 100) + "%", Note = "in use · " + N(st.HitRatio * 100, "0.0") + "% of reads from memory" },
                new StatusTile { Label = "SLOW QUERIES", Value = N(st.SlowQueries), Note = _slowLog ? "the slow log is on (Logs)" : "counted even while the slow log is off" },
            };
            if (Tiles.Count != tiles.Length) { Tiles.Clear(); foreach (var t in tiles) Tiles.Add(t); }
            else for (int i = 0; i < tiles.Length; i++) Tiles[i] = tiles[i];
        }

        public string ConnectionsNote
        {
            get
            {
                int mine = Connections.Count(x => x.IsOurs), sys = Connections.Count(x => x.IsSystem);
                int others = Connections.Count - mine - sys;
                return MainViewModel.Capitalise(MainViewModel.NumberWord(others)) + (others == 1 ? " connection" : " connections") +
                       " from programs (your PHP pages, the Notebook, Workbench…), plus the one reading this list" + (sys > 0 ? " and MariaDB's own threads" : "") +
                       ". Stop query ends what a connection is running; Disconnect closes it (PHP just sees \"server has gone away\").";
            }
        }

        // ------------------------------------------------------------------ connections

        private async Task KillAsync(ServerConnection target, bool queryOnly)
        {
            if (target == null) return;
            var what = string.IsNullOrEmpty(target.Query) ? "" : "Running: " + (target.Query.Length > 200 ? target.Query.Substring(0, 200) + "…" : target.Query);
            var choice = await _main.AskAsync(new ChoiceSlip(queryOnly ? "STOP QUERY" : "DISCONNECT",
                queryOnly ? "Stop what #" + target.Id + " is running?" : "Disconnect #" + target.Id + "?",
                target.User + " from " + target.Client + (target.Database != null ? " in " + target.Database : "") + ". " +
                (queryOnly ? "A transaction it had open is rolled back; the connection stays." : "Anything it hadn't committed is rolled back."),
                alarm: true, primary: queryOnly ? "Stop the query" : "Disconnect", secondary: "Leave it", detail: what));
            if (choice != SlipChoice.Primary) return;
            _busy = true;
            try
            {
                using (var c = await _main.Server.OpenConnectionAsync()) await ServerMonitor.KillAsync(c, target, queryOnly);
                _main.Receipt(queryOnly ? "query stopped" : "disconnected", "#" + target.Id + " · " + target.User + "@" + target.Client, "ok");
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { _main.Receipt("kill", ex.Message, "bad"); Problem = ex.Message; }
            finally { _busy = false; CommandManager.InvalidateRequerySuggested(); }
            await SampleAsync();
        }

        // ------------------------------------------------------------------ variables

        public string Search { get => _search; set { if (Set(ref _search, value ?? "")) FilterVariables(); } }
        public string VariablesNote { get; private set; } = "";

        private async Task LoadVariablesAsync()
        {
            if (!IsAvailable) return;
            try
            {
                using (var c = await _main.Server.OpenConnectionAsync()) _variables = await ServerMonitor.ReadVariablesAsync(c);
                FilterVariables();
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { Problem = ex.Message; }
        }

        private ServerOverrides Saved() => ServerOverrides.Load(_main.Paths, _main.Server.Engine.Kind);

        private void FilterVariables()
        {
            var keep = _selectedVariable?.Name;
            var saved = _main.Server == null ? new ServerOverrides() : Saved();
            var words = _search.Trim().ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<ServerVariable> list = _variables;
            if (words.Length > 0)
                list = list.Where(v => words.All(w => v.Name.Contains(w) || (v.Help ?? "").ToLowerInvariant().Contains(w) || (v.Value ?? "").ToLowerInvariant() == w));
            else
                list = list.OrderBy(v => v.Note == null).ThenBy(v => v.Name, StringComparer.Ordinal);   // the explained ones first
            var shown = list.Take(MaxVariablesShown).ToList();
            Variables.Clear();
            foreach (var v in shown) Variables.Add(new VariableRow(v, saved.Get(v.Name)));
            int total = list.Count();
            VariablesNote = total == 0 ? "No variable matches \"" + _search.Trim() + "\"."
                : (words.Length == 0 ? _variables.Count + " variables; the ones with a plain explanation come first. " : total + " match. ")
                  + (total > MaxVariablesShown ? "Showing the first " + MaxVariablesShown + "; type more to narrow it." : "");
            OnPropertyChanged(nameof(VariablesNote));
            SelectedVariable = Variables.FirstOrDefault(r => r.Name == keep) ?? (words.Length > 0 ? Variables.FirstOrDefault() : _selectedVariable == null ? null : Variables.FirstOrDefault(r => r.Name == keep));
        }

        public VariableRow SelectedVariable
        {
            get => _selectedVariable;
            set
            {
                if (!Set(ref _selectedVariable, value)) return;
                _edit = value?.Saved ?? value?.Variable.Value ?? "";
                EditProblem = null;
                foreach (var n in new[] { nameof(HasVariable), nameof(Edit), nameof(VariableHelp), nameof(VariableFacts), nameof(EditSql), nameof(KeepNote), nameof(CanSetNow) }) OnPropertyChanged(n);
                CommandManager.InvalidateRequerySuggested();
            }
        }
        public bool HasVariable => _selectedVariable != null;
        public string Edit { get => _edit; set { if (Set(ref _edit, value ?? "")) { EditProblem = null; OnPropertyChanged(nameof(EditSql)); } } }
        public string EditProblem { get => _editProblem; private set { if (Set(ref _editProblem, value)) OnPropertyChanged(nameof(HasEditProblem)); } }
        public bool HasEditProblem => !string.IsNullOrEmpty(_editProblem);
        public bool CanSetNow => _selectedVariable?.Variable.CanSetNow == true;
        public string VariableHelp => _selectedVariable == null ? "" : _selectedVariable.Variable.Help ?? "No description from the server.";
        public string VariableFacts
        {
            get
            {
                var v = _selectedVariable?.Variable;
                if (v == null) return "";
                var parts = new List<string> { (v.Type ?? "").ToLowerInvariant() };
                if (v.IsNumber && (v.Min != null || v.Max != null)) parts.Add("from " + v.Min + " to " + v.Max);
                if ((v.IsEnum || v.IsSet) && v.Choices.Count > 0) parts.Add("one of " + string.Join(", ", v.Choices));
                if (v.Default != null) parts.Add("default " + (v.Default.Length == 0 ? "empty" : v.Default));
                parts.Add(v.ReadOnly ? "fixed while running" : v.Scope == "SESSION ONLY" ? "per connection only" : "can change while running");
                if (_selectedVariable.IsSaved) parts.Add("saved for every start: " + _selectedVariable.Saved);
                return string.Join(" · ", parts);
            }
        }
        public string KeepNote => _selectedVariable == null ? "" : ServerOverrides.CheckKeep(_selectedVariable.Variable) ?? "Keep for every start writes it to LuminaDB's my.ini; if the server then won't start, LuminaDB puts your settings aside and starts without them.";
        public string EditSql
        {
            get
            {
                var v = _selectedVariable?.Variable;
                if (v == null || !v.CanSetNow) return "";
                try { return ServerMonitor.SetGlobalSql(v, ServerMonitor.Normalize(v, _edit)) + ";"; }
                catch (LuminaException) { return ""; }
            }
        }

        private async Task SetVariableAsync(bool keep)
        {
            var v = _selectedVariable?.Variable;
            if (v == null) return;
            string value;
            try { value = ServerMonitor.Normalize(v, _edit); }
            catch (LuminaException ex) { EditProblem = ex.Message; return; }
            _busy = true;
            try
            {
                if (v.CanSetNow)
                    using (var c = await _main.Server.OpenConnectionAsync()) await ServerMonitor.SetGlobalAsync(c, v, value);
                if (keep)
                {
                    var o = Saved();
                    o.Set(v.Name, value);
                    o.Save(_main.Paths, _main.Server.Engine.Kind);
                }
                _main.Receipt(keep ? "server setting · every start" : "server setting · until restart", v.Name + " = " + value + (keep && !v.CanSetNow ? " (from the next start)" : ""), "ok");
                _main.Toast(keep && !v.CanSetNow ? v.Name + " is saved; it applies when the server restarts." : v.Name + " is now " + value + ".");
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException || ex is IOException)
            {
                EditProblem = ex is MySqlException m ? m.Message : ex.Message;
            }
            finally { _busy = false; CommandManager.InvalidateRequerySuggested(); }
            await LoadVariablesAsync();
            if (v.Name == "slow_query_log" || v.Name == "general_log") await LoadLogSwitchesAsync();
        }

        private async Task ForgetAsync()
        {
            var v = _selectedVariable?.Variable;
            if (v == null) return;
            var o = Saved();
            o.Remove(v.Name);
            o.Save(_main.Paths, _main.Server.Engine.Kind);
            _main.Receipt("server setting · forgotten", v.Name + " is back to LuminaDB's default from the next start", "ok");
            await LoadVariablesAsync();
        }

        // ------------------------------------------------------------------ logs

        private void BuildLogList()
        {
            var keep = _log?.Name;
            var p = _main.Paths;
            Logs.Clear();
            if (_main.Server != null)
            {
                Logs.Add(new LogSource { Name = "MariaDB", Path = _main.Server.LogFile, Note = "What the server says while it starts, stops and runs: errors, crashes, InnoDB recovery." });
                Logs.Add(new LogSource { Name = "Slow queries", Path = _main.Server.SlowLogFile, Note = "Queries slower than long_query_time, while the slow log is on." });
                Logs.Add(new LogSource { Name = "Every query", Path = _main.Server.GeneralLogFile, Note = "Everything any program sends, while the general log is on." });
            }
            Logs.Add(new LogSource { Name = "PHP errors", Path = PhpConfig.ErrorLog(p), Note = "PHP errors outside projects (each project's own log is in Projects)." });
            Logs.Add(new LogSource { Name = "PHP (php-cgi)", Path = Path.Combine(p.Logs, "php-cgi.log"), Note = "The PHP process itself: start-up problems, missing extensions." });
            Logs.Add(new LogSource { Name = "Web server (Caddy)", Path = WebConfig.CaddyLog(p), Note = "Caddy's warnings and errors." });
            Logs.Add(new LogSource { Name = "Apache", Path = WebConfig.ApacheErrorLog(p), Note = "Apache's errors (projects with an .htaccess, and older engine sets)." });
            Logs.Add(new LogSource { Name = "LuminaDB", Path = Path.Combine(p.Logs, "app.log"), Note = "LuminaDB's own log. Include it when you report a problem." });
            _log = Logs.FirstOrDefault(l => l.Name == keep) ?? Logs.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedLog));
            OnPropertyChanged(nameof(LogNote));
        }

        public LogSource SelectedLog { get => _log; set { if (Set(ref _log, value)) { OnPropertyChanged(nameof(LogNote)); LoadLog(); } } }
        public string LogNote => _log == null ? "" : _log.Note + "  " + _log.Path;
        public string LogText { get => _logText; private set => Set(ref _logText, value); }

        /// <summary>The last part of the chosen log (about 256 KB), newest at the bottom.</summary>
        private void LoadLog()
        {
            if (_log == null) { LogText = ""; return; }
            try
            {
                if (!File.Exists(_log.Path)) { LogText = "(nothing yet: this log is made the first time something is written to it)"; return; }
                var lines = new LogTail(_log.Path).ReadNew();
                LogText = lines.Count == 0 ? "(empty)" : string.Join(Environment.NewLine, lines.Skip(Math.Max(0, lines.Count - 3000)));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { LogText = "Couldn't read the log: " + ex.Message; }
            OnPropertyChanged(nameof(LogText));
            CommandManager.InvalidateRequerySuggested();
        }

        public bool SlowLog { get => _slowLog; set { if (_slowLog != value) _ = SetLogSwitchAsync("slow_query_log", value); } }
        public bool GeneralLog { get => _generalLog; set { if (_generalLog != value) _ = SetLogSwitchAsync("general_log", value); } }
        public string LongQueryTime { get; private set; } = "2";

        private async Task LoadLogSwitchesAsync()
        {
            if (!IsAvailable) return;
            try
            {
                using (var c = await _main.Server.OpenConnectionAsync())
                using (var cmd = new MySqlCommand("SELECT @@GLOBAL.slow_query_log, @@GLOBAL.general_log, @@GLOBAL.long_query_time", c))
                using (var r = await cmd.ExecuteReaderAsync())
                {
                    if (await r.ReadAsync())
                    {
                        _slowLog = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture) == 1;
                        _generalLog = Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture) == 1;
                        LongQueryTime = Convert.ToDouble(r.GetValue(2), CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.InvariantCulture);
                    }
                }
                OnPropertyChanged(nameof(SlowLog));
                OnPropertyChanged(nameof(GeneralLog));
                OnPropertyChanged(nameof(LongQueryTime));
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException) { Problem = ex.Message; }
        }

        /// <summary>The switches act now and are remembered for every start (so a restart doesn't silently turn them off).</summary>
        private async Task SetLogSwitchAsync(string name, bool on)
        {
            if (!CanAct) { OnPropertyChanged(name == "slow_query_log" ? nameof(SlowLog) : nameof(GeneralLog)); return; }
            _busy = true;
            try
            {
                using (var c = await _main.Server.OpenConnectionAsync())
                using (var cmd = new MySqlCommand("SET GLOBAL " + name + " = " + (on ? "ON" : "OFF"), c)) await cmd.ExecuteNonQueryAsync();
                var o = Saved();
                if (on) o.Set(name, "ON"); else o.Remove(name);
                o.Save(_main.Paths, _main.Server.Engine.Kind);
                _main.Receipt(name == "slow_query_log" ? "slow query log" : "general log", (on ? "on" : "off") + " · " + (name == "slow_query_log" ? _main.Server.SlowLogFile : _main.Server.GeneralLogFile), "ok");
                if (on && name == "general_log") _main.Toast("Every query is logged now. Turn it off when you're done; the file grows fast.");
            }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException || ex is IOException) { Problem = ex.Message; }
            finally { _busy = false; CommandManager.InvalidateRequerySuggested(); }
            await LoadLogSwitchesAsync();
            if (IsLogs) LoadLog();
        }

        // ------------------------------------------------------------------ console

        /// <summary>
        /// The mariadb command line in its own console window, already connected as root to this server's port. The password
        /// goes through MYSQL_PWD in that process only (never on the command line, which other programs can read).
        /// </summary>
        private void OpenConsole()
        {
            var s = _main.Server;
            try
            {
                var psi = new ProcessStartInfo(s.Engine.ClientExe,
                    "--host=127.0.0.1 --port=" + s.Port.ToString(CultureInfo.InvariantCulture) + " --user=root --default-character-set=utf8mb4 --prompt=\"LuminaDB [\\d]> \"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    WorkingDirectory = _main.Paths.Exports,
                };
                var pwd = s.ConnectionString().Password;
                if (!string.IsNullOrEmpty(pwd)) psi.EnvironmentVariables["MYSQL_PWD"] = pwd;
                Process.Start(psi)?.Dispose();
                _main.Receipt("console", "mariadb on port " + s.Port.ToString(CultureInfo.InvariantCulture) + " · type exit to close it", "info");
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception || ex is InvalidOperationException || ex is LuminaException)
            {
                _main.Toast("Couldn't open the console: " + ex.Message);
            }
        }

        private void OpenFolder(string path, bool select = false)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (select) Process.Start("explorer.exe", "/select,\"" + path + "\"")?.Dispose();
                else { Directory.CreateDirectory(path); Process.Start("explorer.exe", "\"" + path + "\"")?.Dispose(); }
            }
            catch (Exception ex) { _main.Toast("Couldn't open the folder: " + ex.Message); }
        }
    }
}
