using System;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using LuminaDB.Core;
using LuminaDB.Core.Mail;
using LuminaDB.Core.Web;

namespace LuminaDB.App.ViewModels
{
    /// <summary>The web server (Caddy/Apache + PHP) and the mail catcher, owned by the main window (SPEC §5.2, §5.11).</summary>
    public sealed partial class MainViewModel
    {
        private WebStackService _web;
        private MailCatcher _mail;
        private bool _webBusy;
        private ReceiptSlip _webSlip;
        private int _phpDbPort;

        public ProjectsViewModel Projects { get; private set; }
        public MailViewModel Mail { get; private set; }

        public EngineSetKind Kind => _boot.Kind;
        public MariaDbService Server => _server;
        public WebStack Stack { get; private set; }
        public WebStackService Web => _web;
        public MailCatcher MailCatcher => _mail;

        public bool CanServeWeb => _web != null;
        public bool WebRunning => _web != null && _web.State == WebState.Running;
        public bool WebBusy { get => _webBusy; private set { if (Set(ref _webBusy, value)) { OnWebChanged(); } } }

        public ICommand ToggleWebCommand { get; private set; }
        public ICommand StartEverythingCommand { get; private set; }
        public ICommand OpenWebIndexCommand { get; private set; }
        public ICommand OpenProjectsCommand { get; private set; }

        private void InitWeb()
        {
            Stack = _boot.Web ?? WebStack.FromFolders(null, null, null);
            if (Stack.CanServe)
            {
                _web = new WebStackService(_boot.Paths, _boot.Kind, Stack);
                _web.StateChanged += s => _ui.BeginInvoke(new Action(OnWebChanged));
                _web.DemoChanged += () => _ui.BeginInvoke(new Action(() => { Projects?.OnDemoChanged(); OnWebChanged(); }));
                _web.OutputLine += (source, line) =>
                {
                    if (source != "lumina") return;
                    _ui.BeginInvoke(DispatcherPriority.Background, new Action(() => AddSlip("web", line, "info")));
                };
            }

            _mail = new MailCatcher(_boot.Paths);
            try { _mail.Start(WebSettings.Load(_boot.Paths).PreferredMailPort); }
            catch (SocketException ex) { AppLog.Error("mail catcher could not listen", ex); }
            catch (LuminaException ex) { AppLog.Error("mail catcher: " + ex.Message); }

            Projects = new ProjectsViewModel(this, _boot.Paths, _ui);
            Mail = new MailViewModel(this, _boot.Paths, _ui, _mail);

            WatchProjects();

            ToggleWebCommand = new AsyncCommand(() => WebRunning ? StopWebAsync() : StartWebAsync(), () => CanServeWeb && !WebBusy && !_closing);
            StartEverythingCommand = new AsyncCommand(StartEverythingAsync, () => !_closing && !IsBusy && !WebBusy && (!IsRunning && _server != null || CanServeWeb && !WebRunning));
            OpenWebIndexCommand = new RelayCommand(() => OpenUrl(_web.IndexUrl), () => WebRunning);
            OpenProjectsCommand = new RelayCommand(() => Select("projects"));
        }

        /// <summary>The database server just started: bring the web server up with it (Settings: on by default).</summary>
        private async Task OnDatabaseStartedAsync()
        {
            if (_web == null || !IsRunning) return;
            if (WebRunning)
            {
                if (_phpDbPort != _server.Port) await RestartPhpForDatabaseAsync();
                return;
            }
            if (WebSettings.Load(_boot.Paths).StartWithDatabase) await StartWebAsync();
        }

        private async Task RestartPhpForDatabaseAsync()
        {
            try
            {
                var rt = PhpRuntime();
                await _web.RestartPhpAsync(rt);
                _phpDbPort = rt.MariaDbPort;
            }
            catch (LuminaException ex) { AddSlip("php restart", ex.Message, "bad"); }
        }

        private async Task StartEverythingAsync()
        {
            if (_server != null && !IsRunning) await StartAsync();       // starts the web server too when that's switched on
            if (CanServeWeb && !WebRunning) await StartWebAsync();
            if (IsRunning && WebRunning) Toast("Everything is running.");
        }

        public async Task StartWebAsync()
        {
            if (_web == null || WebRunning || WebBusy) return;
            WebBusy = true;
            _webSlip = AddSlip("web server start", null, "run");
            try
            {
                var rt = PhpRuntime();
                var sites = Projects.Service.Sites();
                _sitesSignature = Signature(sites);
                await _web.StartAsync(sites, rt);
                _phpDbPort = rt.MariaDbPort;
                int n = _web.Sites.Count;
                _webSlip.Kind = "ok";
                _webSlip.Message = "PHP " + Stack.PhpVersion + " on " + _web.IndexUrl + " · " + NumberWord(n) + (n == 1 ? " project" : " projects") + (_web.ApacheRunning ? " · Apache for .htaccess" : "");
            }
            catch (LuminaException ex) { _webSlip.Kind = "bad"; _webSlip.Message = ex.Message; }
            catch (Exception ex) { _webSlip.Kind = "bad"; _webSlip.Message = "Unexpected: " + ex.Message; AppLog.Error("web start failed", ex); }
            finally { WebBusy = false; }
        }

        public async Task StopWebAsync()
        {
            if (_web == null || _web.State == WebState.Stopped) return;
            WebBusy = true;
            var slip = AddSlip("web server stop", null, "run");
            try
            {
                await _web.StopAsync();
                slip.Kind = "ok";
                slip.Message = "PHP, Caddy and Apache stopped";
            }
            catch (Exception ex) { slip.Kind = "bad"; slip.Message = ex.Message; AppLog.Error("web stop failed", ex); }
            finally { WebBusy = false; }
        }

        /// <summary>A project was added, removed, renamed or gained/lost an .htaccess: tell the running web server.</summary>
        public async Task ProjectsChangedAsync()
        {
            if (!WebRunning) return;
            var sites = Projects.Service.Sites();
            _sitesSignature = Signature(sites);
            try { await _web.ApplySitesAsync(sites); }
            catch (LuminaException ex) { AddSlip("web reload", ex.Message, "bad"); }
            catch (Exception ex) { AddSlip("web reload", ex.Message, "bad"); AppLog.Error("apply sites failed", ex); }
            OnWebChanged();
        }

        // ------------------------------------------------------------------ projects folder watcher

        private System.IO.FileSystemWatcher _watcher;
        private DispatcherTimer _watchDebounce;
        private string _sitesSignature;

        /// <summary>
        /// A folder pasted into projects\, or an .htaccess added or removed, is served without pressing anything:
        /// changes are gathered for a second, then the web server gets the new list (Apache starts or stops by itself).
        /// </summary>
        private void WatchProjects()
        {
            _watchDebounce = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, async (s, e) =>
            {
                _watchDebounce.Stop();
                await RescanProjectsAsync();
            }, _ui);
            _watchDebounce.Stop();
            try
            {
                System.IO.Directory.CreateDirectory(_boot.Paths.Projects);
                _watcher = new System.IO.FileSystemWatcher(_boot.Paths.Projects)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.DirectoryName,
                };
                System.IO.FileSystemEventHandler changed = (s, e) => { if (Matters(e.FullPath)) Poke(); };
                _watcher.Created += changed;
                _watcher.Deleted += changed;
                _watcher.Renamed += (s, e) => { if (Matters(e.FullPath) || Matters(e.OldFullPath)) Poke(); };
                _watcher.Error += (s, e) => Poke();
                _watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is System.IO.IOException || ex is UnauthorizedAccessException)
            {
                AppLog.Error("can't watch the projects folder", ex);
            }
        }

        /// <summary>Only top-level folders and .htaccess files change what the web server serves.</summary>
        private bool Matters(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (string.Equals(System.IO.Path.GetFileName(path), ".htaccess", StringComparison.OrdinalIgnoreCase)) return true;
            var parent = System.IO.Path.GetDirectoryName(path);
            return string.Equals(parent?.TrimEnd('\\'), _boot.Paths.Projects.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        private void Poke() => _ui.BeginInvoke(new Action(() => { _watchDebounce?.Stop(); _watchDebounce?.Start(); }));

        private static string Signature(System.Collections.Generic.IEnumerable<SiteInfo> sites) =>
            string.Join("|", sites.OrderBy(x => x.Name, StringComparer.Ordinal).Select(x => x.Name + ">" + x.DocumentRoot + ">" + x.UsesApache));

        private async Task RescanProjectsAsync()
        {
            if (_closing || Projects == null) return;
            Projects.Reload();
            if (!WebRunning) return;
            var sig = Signature(Projects.Service.Sites());
            if (sig == (_sitesSignature ?? Signature(_web.Sites))) return;
            await ProjectsChangedAsync();
        }

        /// <summary>What php.ini needs to know: where the database and the mail catcher listen.</summary>
        public PhpRuntime PhpRuntime() => new PhpRuntime
        {
            MariaDbPort = IsRunning ? _server.Port : LuminaState.Load(_boot.Paths).GetMariaDbPort(_boot.Kind) ?? _boot.PreferredPort,
            MailPort = _mail != null && _mail.IsRunning ? _mail.Port : 0,
            TimeZone = PhpConfig.IanaTimeZone(),
        };

        // ------------------------------------------------------------------ the web status sentence

        public string WebStatusWord
        {
            get
            {
                if (_web == null) return "missing";
                if (WebBusy && _web.State != WebState.Stopping) return "starting";
                switch (_web.State)
                {
                    case WebState.Running: return "running";
                    case WebState.Starting: return "starting";
                    case WebState.Stopping: return "stopping";
                    case WebState.Failed: return "not running";
                    default: return "stopped";
                }
            }
        }

        public string WebStatusTail
        {
            get
            {
                if (_web == null) return " from this folder.";
                if (WebRunning) return " at " + _web.IndexUrl.TrimEnd('/') + ".";
                return _web.State == WebState.Starting || _web.State == WebState.Stopping || WebBusy ? "…" : ".";
            }
        }

        public StatusTone WebTone
        {
            get
            {
                if (_web == null || _web.State == WebState.Failed) return StatusTone.Bad;
                if (WebRunning) return StatusTone.Ok;
                if (WebBusy || _web.State == WebState.Starting || _web.State == WebState.Stopping) return StatusTone.Busy;
                return StatusTone.Off;
            }
        }

        public string WebButtonText => WebRunning ? "Stop web" : WebBusy ? "Starting…" : "Start web";

        public string WebNote
        {
            get
            {
                if (_web == null) return Stack.Missing ?? "The web server files are missing.";
                if (_web.State == WebState.Failed) return _web.Problem;
                if (!WebRunning) return "PHP " + (Stack.PhpVersion ?? "") + " is ready. Starting it serves every project in the projects folder.";
                int n = _web.Sites.Count;
                var demo = _web.Demo != null ? " Demo mode is on for " + _web.Demo.Project + "." : "";
                return Capitalise(NumberWord(n)) + (n == 1 ? " project" : " projects") + " served, only to this PC." + demo;
            }
        }

        public string MailNote => _mail != null && _mail.IsRunning
            ? "PHP's mail() is caught on 127.0.0.1:" + _mail.Port.ToString(CultureInfo.InvariantCulture) + ". Nothing leaves this PC."
            : "The mail catcher couldn't start; mail() will fail.";

        private void OnWebChanged()
        {
            OnPropertyChanged(nameof(WebRunning));
            OnPropertyChanged(nameof(WebStatusWord));
            OnPropertyChanged(nameof(WebStatusTail));
            OnPropertyChanged(nameof(WebTone));
            OnPropertyChanged(nameof(WebButtonText));
            OnPropertyChanged(nameof(WebNote));
            Projects?.OnWebChanged();
            CommandManager.InvalidateRequerySuggested();
        }

        private void OpenUrl(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Toast("Couldn't open the browser: " + ex.Message); }
        }

        private void OnSectionShown(string key)
        {
            Projects?.OnShown(key == "projects");
            Mail?.OnShown(key == "mail");
        }

        // ------------------------------------------------------------------ shutdown

        private async Task ShutdownWebAsync()
        {
            _watchDebounce?.Stop();
            _watcher?.Dispose();
            _watcher = null;
            Projects?.Dispose();
            Mail?.Dispose();
            try { if (_web != null) await _web.StopAsync(); }
            catch (Exception ex) { AppLog.Error("web stop at exit failed", ex); }
            _web?.Dispose();
            _mail?.Dispose();
        }

        private void EmergencyStopWeb()
        {
            try { if (_web != null && _web.State != WebState.Stopped) Task.Run(() => _web.StopAsync()).Wait(TimeSpan.FromSeconds(8)); }
            catch (Exception ex) { AppLog.Error("web stop at Windows shutdown failed", ex); }
            try { _mail?.Stop(); } catch (Exception ex) { AppLog.Error("mail stop failed", ex); }
        }
    }
}
