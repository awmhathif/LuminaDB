using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using LuminaDB.Core;
using LuminaDB.Core.Mail;
using LuminaDB.Core.Web;

namespace LuminaDB.App.ViewModels
{
    public enum StatusTone { Ok, Off, Busy, Bad }

    /// <summary>Everything found at launch: where we live, which Windows, which engine.</summary>
    public sealed class Bootstrap
    {
        public AppPaths Paths { get; set; }
        public WindowsInfo Windows { get; set; }
        public EngineSetKind Kind { get; set; }
        public EngineSet Engine { get; set; }
        public LuminaException EngineError { get; set; }
        public int PreferredPort { get; set; } = 3306;
        /// <summary>PHP, Caddy and Apache (engines\&lt;set&gt;\ or LUMINA_WEB_DIR).</summary>
        public WebStack Web { get; set; }
    }

    public sealed partial class MainViewModel : ObservableObject
    {
        public const int ActivitySamples = 60;
        private const int MaxReceipts = 60;

        private readonly Bootstrap _boot;
        private readonly MariaDbService _server;
        private readonly Dispatcher _ui;
        private readonly ActivityMeter _meter = new ActivityMeter();
        private readonly DispatcherTimer _activityTimer;
        private bool _windowHidden;

        /// <summary>
        /// The window tells us how it is shown: minimised, nobody sees the activity line, so it stops asking the server;
        /// behind other windows it asks every 3 seconds instead of every second.
        /// </summary>
        /// <summary>Notebook and Tables both need every database's tables when the server starts: read them once.</summary>
        private async Task<LuminaDB.Core.Sql.SchemaSnapshot> ReadSchemaOnceAsync()
        {
            if (!IsRunning || _server == null) return null;
            try { return await LuminaDB.Core.Sql.SchemaReader.ReadAsync(_server); }
            catch (Exception ex) when (ex is LuminaException || ex is MySqlConnector.MySqlException) { AppLog.Error("reading schema failed", ex); return null; }
        }

        public void SetWindowShown(bool minimised, bool active)
        {
            _windowHidden = minimised;
            var every = TimeSpan.FromSeconds(active ? 1 : 3);
            if (_activityTimer.Interval != every) _activityTimer.Interval = every;
        }
        private readonly DispatcherTimer _refreshTimer;
        private readonly DispatcherTimer _toastTimer;
        private readonly double[] _samples = new double[ActivitySamples];
        private ReceiptSlip _currentSlip;
        private Exception _lastError;
        private bool _sampling;
        private bool _refreshing;
        private bool _closing;

        public MainViewModel(Bootstrap boot, Dispatcher ui)
        {
            _boot = boot;
            _ui = ui;
            Sections = Section.All();
            SelectedSection = Sections[0];
            SelectedSection.IsSelected = true;
            SelectSection = new SelectSectionCommand(Select);

            if (boot.Engine != null)
            {
                _server = new MariaDbService(boot.Paths, boot.Engine, null, new MariaDbServiceOptions { PreferredPort = boot.PreferredPort });
                _server.StateChanged += s => _ui.BeginInvoke(new Action(() => OnServerState(s)));
                _server.OutputLine += line => _ui.BeginInvoke(DispatcherPriority.Background, new Action(() => _currentSlip?.AddLine(line)));
            }

            Notebook = new NotebookViewModel(this, _server, boot.Paths, ui);
            Notebook.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(NotebookViewModel.IsFocus)) OnPropertyChanged(nameof(IsCompactChrome)); };
            Tables = new TablesViewModel(this, _server, boot.Paths, ui);
            InitWeb();
            InitCare();
            InitAdmin();

            ToggleServerCommand = new AsyncCommand(ToggleServerAsync, () => _server != null && !IsBusy && !_closing);
            RefreshCommand = new AsyncCommand(RefreshAsync, () => IsRunning);
            NewDatabaseCommand = new RelayCommand(OpenNewDatabase, () => IsRunning);
            OpenDatabaseCommand = new AsyncCommand(async p => { Select("notebook"); if (p is string db) await Notebook.OpenDatabaseAsync(db); }, p => IsRunning);
            CopyPhpCommand = new RelayCommand(() => Copy(PhpSnippet, "Copied the PHP connection line."), () => IsRunning);
            CopyUrlCommand = new RelayCommand(() => Copy(ConnectionUrl, "Copied " + ConnectionUrl), () => IsRunning);
            ShowErrorCommand = new RelayCommand(ShowLastError, () => _lastError != null);
            UnlockCommand = new RelayCommand(OpenPasswordSlip, () => IsRunning && NeedsPassword);
            OpenLogsCommand = new RelayCommand(OpenLogsFolder);

            _activityTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (s, e) => await SampleActivityAsync(), ui);
            _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(15), DispatcherPriority.Background, async (s, e) => { if (SelectedSection.Key == "today") await RefreshAsync(); }, ui);
            _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(2.8), DispatcherPriority.Normal, (s, e) => { IsToastVisible = false; _toastTimer.Stop(); }, ui);
            _toastTimer.Stop();

            RunChecks();
            UpdateStatus();
        }

        // ------------------------------------------------------------------ sections

        public List<Section> Sections { get; }
        public NotebookViewModel Notebook { get; }

        /// <summary>The Notebook is in focus mode: the window's header becomes one slim line and the tabs a thin rail.</summary>
        public bool IsCompactChrome => SelectedSection?.Key == "notebook" && Notebook != null && Notebook.IsFocus;
        public TablesViewModel Tables { get; }
        public ICommand SelectSection { get; }

        private Section _selected;
        public Section SelectedSection { get => _selected; private set => Set(ref _selected, value); }

        public void Select(string key)
        {
            var s = Sections.FirstOrDefault(x => x.Key == key);
            if (s == null || s == SelectedSection) return;
            // Dev/test only (LUMINA_TIMING=1): logs how long a page switch takes until the page is drawn and idle.
            if (TimingOn)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                _ui.BeginInvoke(new Action(() => AppLog.Info("timing: page " + key + " " + sw.ElapsedMilliseconds + " ms")), DispatcherPriority.ApplicationIdle);
            }
            SelectedSection.IsSelected = false;
            s.IsSelected = true;
            SelectedSection = s;
            OnSectionShown(key);
            OnPropertyChanged(nameof(IsCompactChrome));
            if (key == "tables" && TablesStale && IsRunning) { TablesStale = false; _ = Tables.ReloadAsync(keepPage: true); }
            if (key == "notebook" && NotebookStale && IsRunning) { NotebookStale = false; _ = Notebook.LoadSchemaAsync(); }
        }

        private static readonly bool TimingOn = Environment.GetEnvironmentVariable("LUMINA_TIMING") == "1";

        public void SelectByKey(Key fKey)
        {
            var s = Sections.FirstOrDefault(x => x.FKey == fKey);
            if (s != null) Select(s.Key);
        }

        // ------------------------------------------------------------------ status sentence (masthead)

        private string _statusWord = "", _statusTail = "", _portText = "", _mastNote = "", _serverButtonText = "Start server";
        private StatusTone _tone = StatusTone.Off;
        private bool _busy;

        public string StatusWord { get => _statusWord; private set => Set(ref _statusWord, value); }
        public string StatusTail { get => _statusTail; private set => Set(ref _statusTail, value); }
        public string PortText { get => _portText; private set { if (Set(ref _portText, value)) OnPropertyChanged(nameof(HasPort)); } }
        public bool HasPort => !string.IsNullOrEmpty(_portText);
        public StatusTone Tone { get => _tone; private set => Set(ref _tone, value); }
        public string MastNote { get => _mastNote; private set => Set(ref _mastNote, value); }
        public string ServerButtonText { get => _serverButtonText; private set => Set(ref _serverButtonText, value); }
        public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
        public bool IsRunning => _server != null && _server.State == ServerState.Running;
        public bool NeedsPassword => _server != null && _server.NeedsPassword;
        public bool HasError => _lastError != null;
        public string EngineLabel => _boot.Engine == null ? "engine missing" : $"MariaDB {_boot.Engine.ServerVersion} · {EngineSet.FolderName(_boot.Kind)} set · {_boot.Windows.FriendlyName}";

        private void UpdateStatus()
        {
            var state = _server?.State ?? ServerState.Stopped;
            bool movedPort = IsRunning && _server.Port != _boot.PreferredPort;
            switch (state)
            {
                case ServerState.Running:
                    Tone = StatusTone.Ok; StatusWord = "running"; StatusTail = " on "; PortText = ":" + _server.Port;
                    MastNote = NeedsPassword ? "Enter the root password once to use it on this PC."
                        : movedPort && new SystemPortProbe().IsInUse(_boot.PreferredPort) ? $"{_boot.PreferredPort} was taken by {PortOwner.Describe(_boot.PreferredPort)}, so I moved it."
                        : movedPort ? $"Kept :{_server.Port} from last time, so your PHP code still connects."
                        : "Only this PC can connect. No firewall pop-ups.";
                    ServerButtonText = "Stop server";
                    break;
                case ServerState.Starting:
                    Tone = StatusTone.Busy; StatusWord = "starting"; StatusTail = "…"; PortText = "";
                    MastNote = "The first start creates the data folder; that takes a few seconds.";
                    ServerButtonText = "Starting…";
                    break;
                case ServerState.Stopping:
                    Tone = StatusTone.Busy; StatusWord = "stopping"; StatusTail = ", safely…"; PortText = "";
                    MastNote = "Writing everything to disk first.";
                    ServerButtonText = "Stopping…";
                    break;
                case ServerState.Crashed:
                    Tone = StatusTone.Bad; StatusWord = "stopped unexpectedly"; StatusTail = "."; PortText = "";
                    MastNote = "Saved data is safe: InnoDB recovers on the next start.";
                    ServerButtonText = "Start again";
                    break;
                default:
                    if (_boot.Engine == null)
                    {
                        Tone = StatusTone.Bad; StatusWord = "missing"; StatusTail = " from this folder."; PortText = "";
                        MastNote = _boot.EngineError?.Message ?? "Re-extract LuminaDB.";
                        ServerButtonText = "Start server";
                    }
                    else if (_lastError != null)
                    {
                        Tone = StatusTone.Bad; StatusWord = "not running"; StatusTail = "."; PortText = "";
                        MastNote = (_lastError as LuminaException)?.Message ?? "Something unexpected stopped it. See the details.";
                        ServerButtonText = "Try again";
                    }
                    else
                    {
                        Tone = StatusTone.Off; StatusWord = "stopped"; StatusTail = ", safely."; PortText = "";
                        MastNote = "Everything is on disk. Safe to switch off the PC.";
                        ServerButtonText = "Start server";
                    }
                    break;
            }
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(NeedsPassword));
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(ConnectionPort));
            OnPropertyChanged(nameof(ConnectionPassword));
            OnPropertyChanged(nameof(PhpSnippet));
            OnPropertyChanged(nameof(ConnectionUrl));
            CommandManager.InvalidateRequerySuggested();
        }

        // ------------------------------------------------------------------ server

        public ICommand ToggleServerCommand { get; }
        public ICommand ShowErrorCommand { get; }
        public ICommand UnlockCommand { get; }
        public ICommand OpenLogsCommand { get; }

        public async Task StartAsync()
        {
            if (_server == null || IsRunning) return;
            IsBusy = true;
            _lastError = null;
            var slip = AddSlip("server start", null, "run");
            try
            {
                await _server.StartAsync();
                slip.Kind = "ok";
                slip.Message = $"ready on 127.0.0.1:{_server.Port} · {_server.ServerVersion}";
                _meter.Reset();
                _activityTimer.Start();
                _refreshTimer.Start();
                await RefreshAsync();
                if (_server.NeedsPassword) OpenPasswordSlip();
                else { var schema = await ReadSchemaOnceAsync(); await Notebook.OnServerStartedAsync(schema); await Tables.OnServerStartedAsync(schema); }
                await OnDatabaseStartedAsync();
                OnCareServerStarted();
                OnAdminServerChanged();
            }
            catch (Exception ex)
            {
                _lastError = ex;
                slip.Kind = "bad";
                slip.Message = ex is LuminaException ? ex.Message : "Unexpected: " + ex.Message;
                AppLog.Error("start failed", ex);
            }
            finally
            {
                IsBusy = false;
                RunChecks();
                UpdateStatus();
            }
        }

        public async Task StopAsync()
        {
            if (_server == null || _server.State == ServerState.Stopped) return;
            IsBusy = true;
            _activityTimer.Stop();
            _refreshTimer.Stop();
            var slip = AddSlip("server stop", null, "run");
            try
            {
                await _server.StopAsync();
                slip.Kind = "ok";
                slip.Message = "clean shutdown, data flushed";
            }
            catch (Exception ex)
            {
                _lastError = ex;
                slip.Kind = "bad";
                slip.Message = ex.Message;
                AppLog.Error("stop failed", ex);
            }
            finally
            {
                IsBusy = false;
                ResetLiveData();
                Notebook.OnServerStopped();
                Tables.OnServerStopped();
                OnCareServerStopped();
                RunChecks();
                UpdateStatus();
            }
        }

        /// <summary>Nothing on screen may pretend the server is still answering.</summary>
        private void ResetLiveData()
        {
            Array.Clear(_samples, 0, _samples.Length);
            Samples = (double[])_samples.Clone();
            QpsText = "idle";
            Databases.Clear();
            OnPropertyChanged(nameof(DatabasesHeading));
            OnPropertyChanged(nameof(HasDatabases));
            OnPropertyChanged(nameof(PhpSnippet));
        }

        private Task ToggleServerAsync() => IsRunning ? StopAsync() : StartAsync();

        private void OnServerState(ServerState s)
        {
            if (s == ServerState.Crashed)
            {
                _activityTimer.Stop();
                _refreshTimer.Stop();
                AddSlip("server crashed", "The server process ended by itself. Start it again; nothing committed is lost.", "bad");
                ResetLiveData();
                Notebook.OnServerStopped();
                Tables.OnServerStopped();
                AppLog.Error("server crashed");
                OnCareServerStopped();
                RunChecks();
            }
            UpdateStatus();
            if (s == ServerState.Running || s == ServerState.Stopped || s == ServerState.Crashed) OnAdminServerChanged();
        }

        /// <summary>Called when the window closes: always a clean shutdown (SPEC §5.13).</summary>
        public async Task ShutdownAsync()
        {
            _closing = true;
            CommandManager.InvalidateRequerySuggested();
            _activityTimer.Stop();
            _refreshTimer.Stop();
            _backupTimer?.Stop();
            await BackUpOnCloseAsync();
            Notebook.Dispose();
            Tables.Dispose();
            ServerPage?.Dispose();
            await ShutdownWebAsync();
            if (_server != null && _server.State != ServerState.Stopped && _server.State != ServerState.Crashed)
                await StopAsync();
            _server?.Dispose();
        }

        /// <summary>Windows is logging off / shutting down: stop the server synchronously, bounded.</summary>
        public void EmergencyStop()
        {
            try
            {
                EmergencyStopWeb();
                if (_server != null && _server.State == ServerState.Running)
                    Task.Run(() => _server.StopAsync()).Wait(TimeSpan.FromSeconds(20));
            }
            catch (Exception ex) { AppLog.Error("stop at Windows shutdown failed", ex); }
        }

        private void ShowLastError()
        {
            if (_lastError == null) return;
            var lex = _lastError as LuminaException;
            var detail = lex?.Detail ?? _lastError.ToString();
            Slip = new MessageSlip(CloseSlip, "WHY IT DIDN'T START", "Not running.",
                lex?.Message ?? _lastError.Message, detail, true,
                () => Copy((lex?.Message ?? _lastError.Message) + Environment.NewLine + Environment.NewLine + detail, "Copied the details."),
                OpenLogsFolder);
        }

        private void OpenLogsFolder()
        {
            try { Process.Start("explorer.exe", "\"" + _boot.Paths.Logs + "\""); }
            catch (Exception ex) { Toast("Couldn't open the folder: " + ex.Message); }
        }

        // ------------------------------------------------------------------ activity line

        private double[] _samplesView = new double[ActivitySamples];
        private string _qpsText = "idle";
        public double[] Samples { get => _samplesView; private set => Set(ref _samplesView, value); }
        public string QpsText { get => _qpsText; private set => Set(ref _qpsText, value); }

        private async Task SampleActivityAsync()
        {
            if (_sampling || !IsRunning || NeedsPassword || _windowHidden) return;
            _sampling = true;
            try
            {
                var q = await _meter.SampleAsync(_server);
                if (q.HasValue)
                {
                    Array.Copy(_samples, 1, _samples, 0, _samples.Length - 1);
                    _samples[_samples.Length - 1] = q.Value;
                    Samples = (double[])_samples.Clone();
                    QpsText = q.Value < 10 ? $"{q.Value:0.#} q/s" : $"{q.Value:0} q/s";
                }
            }
            catch (Exception) { QpsText = "—"; }
            finally { _sampling = false; }
        }

        // ------------------------------------------------------------------ Today: databases

        public ObservableCollection<DatabaseSummary> Databases { get; } = new ObservableCollection<DatabaseSummary>();
        public bool HasDatabases => Databases.Count > 0;
        public ICommand RefreshCommand { get; }
        public ICommand NewDatabaseCommand { get; }
        public ICommand OpenDatabaseCommand { get; }

        public string DatabasesHeading
        {
            get
            {
                if (!IsRunning) return "Start the server to see your databases.";
                int n = Databases.Count;
                if (n == 0) return "No databases yet. Make your first one.";
                return $"{Capitalise(NumberWord(n))} {(n == 1 ? "ledger" : "ledgers")} on this machine.";
            }
        }

        /// <summary>Refreshes the database list now (Diagram awaits it after creating a database).</summary>
        internal Task RefreshNowAsync() => RefreshAsync();

        private async Task RefreshAsync()
        {
            if (!IsRunning || NeedsPassword || _refreshing) { OnPropertyChanged(nameof(DatabasesHeading)); return; }
            _refreshing = true;
            try
            {
                var list = await DatabaseCatalog.ListAsync(_server);
                Databases.Clear();
                foreach (var d in list) Databases.Add(d);
            }
            catch (LuminaException ex) when (ex.Kind == ErrorKind.WrongRootPassword) { OpenPasswordSlip(); }
            catch (Exception ex) { AppLog.Error("listing databases failed", ex); }
            finally
            {
                _refreshing = false;
                OnPropertyChanged(nameof(DatabasesHeading));
                OnPropertyChanged(nameof(HasDatabases));
                OnPropertyChanged(nameof(PhpSnippet));
            }
        }

        private void OpenNewDatabase()
        {
            var existing = Databases.Select(d => d.Name).ToList();
            Slip = new NewDatabaseSlip(CloseSlip, existing, async name =>
            {
                await DatabaseCatalog.CreateAsync(_server, name);
                AddSlip("CREATE DATABASE " + name, "utf8mb4 · utf8mb4_unicode_ci", "ok");
                await RefreshAsync();
                TablesStale = true;
                NotebookStale = true;
                Toast($"Created {name}.");
            });
        }

        // ------------------------------------------------------------------ Today: connection sheet

        public ICommand CopyPhpCommand { get; }
        public ICommand CopyUrlCommand { get; }
        public string ConnectionHost => "127.0.0.1";
        public string ConnectionPort => IsRunning ? _server.Port.ToString() : "—";
        public string ConnectionUser => "root";
        private bool HasRootPassword => _boot.Engine != null && RootCredentialStore.For(_boot.Paths, _boot.Kind).HasStoredPassword;
        public string ConnectionPassword => HasRootPassword ? "set · saved for you" : "none · local only";
        private string SampleDb => Databases.FirstOrDefault()?.Name ?? "your_db";
        public string PhpSnippet => !IsRunning ? "Start the server to get a ready-to-paste PHP line." : $"$db = new mysqli(\"127.0.0.1\", \"root\", \"{(HasRootPassword ? "YOUR_PASSWORD" : "")}\", \"{SampleDb}\", {ConnectionPort});";
        public string ConnectionUrl => $"mysql://root@127.0.0.1:{ConnectionPort}/{SampleDb}";

        // ------------------------------------------------------------------ Today: checks

        public ObservableCollection<CheckResult> Checks { get; } = new ObservableCollection<CheckResult>();
        public int IssueCount => Checks.Count(c => c.Severity != CheckSeverity.Ok);
        public string IssueCaption => IssueCount == 0 ? "all\nhealthy" : IssueCount == 1 ? "thing needs\na look" : "things need\na look";

        public void RunChecks()
        {
            List<CheckResult> results;
            try { results = StartupChecks.Run(_boot.Paths, _boot.Windows, _boot.Engine, _server, _boot.PreferredPort); }
            catch (Exception ex) { AppLog.Error("checks failed", ex); return; }
            if (_boot.EngineError != null)
                results[0] = new CheckResult("engine", CheckSeverity.Bad, "Database engine missing", _boot.EngineError.Message, "engines\\");
            if (_server?.State == ServerState.Crashed)
                results.Insert(0, new CheckResult("crash", CheckSeverity.Bad, "Server stopped unexpectedly",
                    "It ended by itself (power cut, antivirus or Task Manager). Committed data is safe; start it again.", System.IO.Path.GetFileName(_server.LogFile)));
            Checks.Clear();
            foreach (var r in results.OrderBy(r => r.Severity == CheckSeverity.Ok ? 1 : 0)) Checks.Add(r);
            OnPropertyChanged(nameof(IssueCount));
            OnPropertyChanged(nameof(IssueCaption));
            UpdateDoctorAlert();
        }


        // ------------------------------------------------------------------ receipt tape

        public ObservableCollection<ReceiptSlip> Receipts { get; } = new ObservableCollection<ReceiptSlip>();

        private ReceiptSlip AddSlip(string title, string message, string kind)
        {
            var s = new ReceiptSlip(title, message, kind);
            Receipts.Insert(0, s);
            while (Receipts.Count > MaxReceipts) Receipts.RemoveAt(Receipts.Count - 1);
            _currentSlip = s;
            return s;
        }

        // ------------------------------------------------------------------ slips (overlay) and toast

        private SlipViewModel _slip;
        public SlipViewModel Slip
        {
            get => _slip;
            set
            {
                var old = _slip;
                if (!Set(ref _slip, value)) return;
                OnPropertyChanged(nameof(HasSlip));
                // a question that is replaced or closed without an answer counts as "cancel"
                if (old is ChoiceSlip q) q.Resolve(SlipChoice.Cancel);
            }
        }
        public bool HasSlip => _slip != null;
        public void CloseSlip()
        {
            if (_slip is ChoiceSlip q) q.Resolve(SlipChoice.Cancel);
            else Slip = null;
        }

        /// <summary>Shows a question slip and waits for the answer.</summary>
        public Task<SlipChoice> AskAsync(ChoiceSlip q)
        {
            q.Dismiss = () => { if (ReferenceEquals(_slip, q)) Slip = null; };
            Slip = q;
            return q.Result;
        }

        public void CopyText(string text, string done) => Copy(text, done);

        /// <summary>Something changed the schema or data; Tables reloads when it is shown next.</summary>
        public bool TablesStale { get; set; }
        /// <summary>Databases or tables changed outside the Notebook (Import, Tables, Diagram, Projects): its index reloads when shown.</summary>
        public bool NotebookStale { get; set; }

        /// <summary>The Notebook changed data or schema: keep Today's numbers honest.</summary>
        /// <summary>Shows a database on Notebook, Tables or Diagram, with that database picked.</summary>
        public async Task OpenDatabaseOnAsync(string page, string db)
        {
            if (!IsRunning || string.IsNullOrEmpty(db)) return;
            switch (page)
            {
                case "notebook":
                    NotebookStale = false;
                    Select("notebook");
                    await Notebook.LoadSchemaAsync();
                    await Notebook.OpenDatabaseAsync(db);
                    break;
                case "tables":
                    TablesStale = false;
                    Select("tables");
                    await Tables.OpenDatabaseAsync(db);
                    break;
                case "diagram":
                    if (!Databases.Any(d => string.Equals(d.Name, db, StringComparison.OrdinalIgnoreCase))) await RefreshAsync();
                    Select("diagram");
                    Diagram.Database = db;
                    break;
            }
        }

        public void DataChanged()
        {
            if (IsRunning) _ = RefreshAsync();
            TablesStale = true;
            NotebookStale = SelectedSection?.Key != "notebook";   // the Notebook reloads after its own runs
        }

        private void OpenPasswordSlip()
        {
            if (Slip is PasswordSlip) return;
            Slip = new PasswordSlip(CloseSlip, async pwd =>
            {
                bool ok = await _server.UnlockAsync(pwd);
                if (ok) { UpdateStatus(); await RefreshAsync(); RunChecks(); var schema = await ReadSchemaOnceAsync(); await Notebook.OnServerStartedAsync(schema); await Tables.OnServerStartedAsync(schema); OnCareServerStarted(); OnAdminServerChanged(); }
                return ok;
            });
        }

        private string _toastText;
        private bool _toastVisible;
        public string ToastText { get => _toastText; private set => Set(ref _toastText, value); }
        public bool IsToastVisible { get => _toastVisible; private set => Set(ref _toastVisible, value); }

        public void Toast(string text)
        {
            ToastText = text;
            IsToastVisible = true;
            _toastTimer.Stop();
            _toastTimer.Start();
        }

        private void Copy(string text, string done)
        {
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetText(text); Toast(done); return; }
                catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(40); }   // clipboard busy
            }
            Toast("The clipboard is busy. Try again.");
        }

        // ------------------------------------------------------------------ words

        private static readonly string[] Words = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve" };
        internal static string NumberWord(int n) => n >= 0 && n < Words.Length ? Words[n] : n.ToString();
        internal static string Capitalise(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
    }
}
