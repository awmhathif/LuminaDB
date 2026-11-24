using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LuminaDB.Core.Web
{
    public enum WebState { Stopped, Starting, Running, Stopping, Failed }

    /// <summary>
    /// Runs the web half (SPEC §4): a php-cgi FastCGI pool, Caddy in front, and Apache only while a project has an
    /// .htaccess (or as the front server on sets without Caddy). Every process is hidden, logged to logs\ and kept in
    /// a Job Object so it dies with LuminaDB. All of it listens on 127.0.0.1 only, except the demo-mode site.
    /// </summary>
    public sealed class WebStackService : IDisposable
    {
        public const int PhpChildren = 4;
        public const int PhpMaxRequests = 500;
        private static readonly object StateFileLock = new object();

        private readonly AppPaths _paths;
        private readonly EngineSetKind _kind;
        private readonly WebStack _stack;
        private readonly PortManager _ports;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private readonly JobObject _job;
        private ManagedProcess _php, _caddy, _apache;
        private WebPlan _plan;
        private PhpRuntime _runtime;
        private WebState _state = WebState.Stopped;
        private bool _stopping;
        private readonly Queue<DateTime> _phpRestarts = new Queue<DateTime>();
        private Timer _demoTimer;

        public WebStackService(AppPaths paths, EngineSetKind kind, WebStack stack, PortManager ports = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _kind = kind;
            _stack = stack ?? throw new ArgumentNullException(nameof(stack));
            _ports = ports ?? new PortManager();
            try { _job = new JobObject(); }
            catch (System.ComponentModel.Win32Exception) { _job = null; }   // still works, children just outlive a crash
            Settings = WebSettings.Load(paths);
        }

        public event Action<WebState> StateChanged;
        /// <summary>(source, line): "php", "caddy", "apache" or "lumina".</summary>
        public event Action<string, string> OutputLine;
        public event Action DemoChanged;

        public WebStack Stack => _stack;
        public WebSettings Settings { get; private set; }
        public WebState State => _state;
        /// <summary>Why the web server isn't running, in plain words (null when fine).</summary>
        public string Problem { get; private set; }
        public int WebPort => _plan?.WebPort ?? 0;
        public int PhpPort => _plan?.PhpPort ?? 0;
        public int ApachePort => _plan?.ApachePort ?? 0;
        public bool ApacheRunning => _apache != null && !_apache.HasExited;
        public DemoSite Demo => _plan?.Demo;
        public DateTime? DemoEndsAt { get; private set; }
        public IReadOnlyList<SiteInfo> Sites => _plan?.Sites ?? new List<SiteInfo>();

        /// <summary>http://name.localhost/ (port left out when it's 80).</summary>
        public string HostUrl(string project) => "http://" + project + ".localhost" + PortSuffix + "/";
        /// <summary>http://localhost/name/ for browsers that don't resolve *.localhost.</summary>
        public string PathUrl(string project) => "http://localhost" + PortSuffix + "/" + project + "/";
        public string IndexUrl => "http://localhost" + PortSuffix + "/";
        private string PortSuffix => WebPort == 80 || WebPort == 0 ? "" : ":" + WebPort;

        // ------------------------------------------------------------------ start / stop

        public async Task StartAsync(IEnumerable<SiteInfo> sites, PhpRuntime runtime, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_state == WebState.Running) return;
                var missing = _stack.Missing;
                if (missing != null) { Fail(missing); throw new LuminaException(ErrorKind.EngineFileMissing, missing); }
                _stopping = false;
                Problem = null;
                SetState(WebState.Starting);
                Settings = WebSettings.Load(_paths);
                _runtime = runtime;
                KillOrphans();
                try
                {
                    _plan = PlanPorts(sites.ToList());
                    WriteConfigs();
                    await StartPhpAsync(ct).ConfigureAwait(false);
                    if (_plan.NeedsApache) await StartApacheAsync(ct).ConfigureAwait(false);
                    if (_stack.UsesCaddy) await StartCaddyAsync(ct).ConfigureAwait(false);
                    SaveState();
                    SetState(WebState.Running);
                }
                catch (Exception ex)
                {
                    StopAll();
                    Fail(ex is LuminaException ? ex.Message : "The web server couldn't start: " + ex.Message);
                    throw;
                }
            }
            finally { _lock.Release(); }
        }

        public async Task StopAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_state == WebState.Stopped) return;
                SetState(WebState.Stopping);
                _stopping = true;
                StopDemoTimer();
                if (_plan != null) _plan.Demo = null;
                if (_caddy != null && !_caddy.HasExited)
                {
                    // Graceful: lets open requests finish (grace_period 3s).
                    if (await PostAdminAsync("/stop", null).ConfigureAwait(false) == null)
                        await _caddy.WaitForExitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                StopAll();
                Problem = null;
                SetState(WebState.Stopped);
            }
            finally { _lock.Release(); }
        }

        /// <summary>Applies a changed project list (new, deleted, gained or lost an .htaccess) without a full restart.</summary>
        public async Task ApplySitesAsync(IEnumerable<SiteInfo> sites, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_state != WebState.Running || _plan == null) return;
                var list = sites.ToList();
                var demo = _plan.Demo;
                if (demo != null && !list.Any(s => s.Name == demo.Project)) { demo = null; StopDemoTimer(); }
                bool apacheWasNeeded = _plan.NeedsApache;
                _plan.Sites = list;
                _plan.Demo = demo;
                if (_plan.NeedsApache && _plan.ApachePort == 0 && !_plan.ApacheInFront)
                    _plan.ApachePort = _ports.Pick(8800, LoadState().ApachePort, Taken());
                WriteConfigs();

                if (_plan.NeedsApache)
                {
                    // Apache reads its config only at start; it starts in about a second.
                    StopApache();
                    await StartApacheAsync(ct).ConfigureAwait(false);
                }
                else if (apacheWasNeeded) StopApache();

                if (_stack.UsesCaddy) await ReloadCaddyAsync().ConfigureAwait(false);
                SaveState();
            }
            finally { _lock.Release(); }
        }

        /// <summary>Restarts the PHP pool (after changing extensions, or when the database moved to another port).</summary>
        public async Task RestartPhpAsync(PhpRuntime runtime, CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (runtime != null) _runtime = runtime;
                Settings = WebSettings.Load(_paths);
                if (_state != WebState.Running) return;
                WriteConfigs();
                StopPhp();
                await StartPhpAsync(ct).ConfigureAwait(false);
            }
            finally { _lock.Release(); }
        }

        public void SaveSettings(WebSettings settings)
        {
            settings.Save(_paths);
            Settings = settings;
        }

        // ------------------------------------------------------------------ demo mode

        /// <summary>
        /// Serves one project on the local network for <paramref name="minutes"/> (SPEC §5.2). Windows may show a
        /// firewall prompt for Caddy/Apache the first time; the UI warns before calling this.
        /// </summary>
        public async Task<DemoSite> StartDemoAsync(string project, string address, int minutes, CancellationToken ct = default)
        {
            if (minutes < 1 || minutes > 240) throw new LuminaException(ErrorKind.InvalidInput, "Choose between 1 and 240 minutes.");
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_state != WebState.Running) throw new LuminaException(ErrorKind.ServerNotRunning, "Start the web server first.");
                if (!_plan.Sites.Any(s => s.Name == project)) throw new LuminaException(ErrorKind.InvalidInput, "There's no project called " + project + ".");
                if (!IPAddress.TryParse(address, out var ip) || IPAddress.IsLoopback(ip))
                    throw new LuminaException(ErrorKind.InvalidInput, "Demo mode needs this PC's network address, for example 192.168.1.20.");
                int port = _plan.Demo?.Port ?? _ports.Pick(8080, null, Taken());
                _plan.Demo = new DemoSite { Project = project, Address = address, Port = port };
                WriteConfigs();
                if (_stack.UsesCaddy) await ReloadCaddyAsync().ConfigureAwait(false);
                else { StopApache(); await StartApacheAsync(ct).ConfigureAwait(false); }
                StopDemoTimer();
                DemoEndsAt = DateTime.Now.AddMinutes(minutes);
                _demoTimer = new Timer(_ => { var _1 = StopDemoAsync(); }, null, TimeSpan.FromMinutes(minutes), Timeout.InfiniteTimeSpan);
                Emit("lumina", $"Demo mode on: {project} at {_plan.Demo.Url} until {DemoEndsAt:HH:mm}");
                var demo = _plan.Demo;
                RaiseDemoChanged();
                return demo;
            }
            finally { _lock.Release(); }
        }

        public async Task StopDemoAsync()
        {
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                StopDemoTimer();
                if (_plan?.Demo == null) return;
                _plan.Demo = null;
                DemoEndsAt = null;
                if (_state == WebState.Running)
                {
                    WriteConfigs();
                    if (_stack.UsesCaddy) await ReloadCaddyAsync().ConfigureAwait(false);
                    else { StopApache(); await StartApacheAsync(CancellationToken.None).ConfigureAwait(false); }
                }
                Emit("lumina", "Demo mode off. Nothing is served to the network now.");
                RaiseDemoChanged();
            }
            finally { _lock.Release(); }
        }

        private void StopDemoTimer()
        {
            _demoTimer?.Dispose();
            _demoTimer = null;
            DemoEndsAt = null;
        }

        private void RaiseDemoChanged() { try { DemoChanged?.Invoke(); } catch { } }

        // ------------------------------------------------------------------ pieces

        private WebPlan PlanPorts(List<SiteInfo> sites)
        {
            var st = LoadState();
            var plan = new WebPlan { Sites = sites, ApacheInFront = !_stack.UsesCaddy };
            var taken = new List<int>();
            if (_runtime != null) { taken.Add(_runtime.MariaDbPort); taken.Add(_runtime.MailPort); }
            plan.WebPort = _ports.Pick(Settings.PreferredWebPort, st.WebPort, taken); taken.Add(plan.WebPort);
            plan.PhpPort = _ports.Pick(Settings.PreferredPhpPort, st.PhpPort, taken); taken.Add(plan.PhpPort);
            if (_stack.UsesCaddy) { plan.AdminPort = _ports.Pick(2019, st.CaddyAdminPort, taken); taken.Add(plan.AdminPort); }
            if (plan.NeedsApache && !plan.ApacheInFront) plan.ApachePort = _ports.Pick(8800, st.ApachePort, taken);
            return plan;
        }

        private List<int> Taken()
        {
            var t = new List<int> { _plan.WebPort, _plan.PhpPort, _plan.AdminPort, _plan.ApachePort };
            if (_plan.Demo != null) t.Add(_plan.Demo.Port);
            if (_runtime != null) { t.Add(_runtime.MariaDbPort); t.Add(_runtime.MailPort); }
            return t.Where(p => p > 0).ToList();
        }

        private void WriteConfigs()
        {
            PhpConfig.Write(_paths, _kind, _stack, Settings, _runtime ?? new PhpRuntime { MariaDbPort = 3306, MailPort = 25, TimeZone = PhpConfig.IanaTimeZone() });
            WebConfig.WriteIndex(_paths, _plan);
            Directory.CreateDirectory(Path.Combine(_paths.Run, "caddy"));
            Directory.CreateDirectory(_paths.ProjectLogs);
            if (_stack.UsesCaddy) File.WriteAllText(WebConfig.CaddyFile(_paths), WebConfig.BuildCaddyfile(_paths, _plan), new UTF8Encoding(false));
            if (_plan.NeedsApache) File.WriteAllText(WebConfig.ApacheFile(_paths), WebConfig.BuildHttpdConf(_paths, _stack, _plan), new UTF8Encoding(false));
        }

        private async Task StartPhpAsync(CancellationToken ct)
        {
            var env = new Dictionary<string, string>
            {
                ["PHP_FCGI_CHILDREN"] = PhpChildren.ToString(),
                // php-cgi exits after this many requests per child; with children the parent replaces them (ParityTests 4d).
                ["PHP_FCGI_MAX_REQUESTS"] = PhpMaxRequests.ToString(),
                ["PHPRC"] = _paths.Config,
                ["TMP"] = _paths.Temp,
                ["TEMP"] = _paths.Temp,
            };
            var args = new[] { "-b", "127.0.0.1:" + _plan.PhpPort, "-c", PhpConfig.IniFile(_paths, _kind) };
            var p = ManagedProcess.Start(_stack.PhpCgiExe, args, Path.Combine(_paths.Logs, "php-cgi.log"), _stack.PhpDir, env);
            Hook(p, "php");
            _php = p;
            await WaitForPortAsync(p, _plan.PhpPort, "PHP", ct).ConfigureAwait(false);
            _job?.AddWithChildren(p.Id);
            _ = p.Exited.ContinueWith(_ => OnPhpExited(p), TaskScheduler.Default);
        }

        private void OnPhpExited(ManagedProcess p)
        {
            if (_stopping || p != _php) return;
            // The pool died (antivirus, a crash in an extension). Restart it a few times before giving up.
            var now = DateTime.UtcNow;
            lock (_phpRestarts)
            {
                while (_phpRestarts.Count > 0 && now - _phpRestarts.Peek() > TimeSpan.FromMinutes(1)) _phpRestarts.Dequeue();
                if (_phpRestarts.Count >= 3)
                {
                    Fail("PHP keeps stopping (3 times in a minute). The last lines of its log: " + p.RecentOutput(5));
                    return;
                }
                _phpRestarts.Enqueue(now);
            }
            Emit("lumina", "PHP stopped unexpectedly; starting it again.");
            _ = Task.Run(async () =>
            {
                await _lock.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_stopping || p != _php) return;
                    _php.Dispose();
                    _php = null;
                    await StartPhpAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex) { Fail("PHP couldn't be restarted: " + ex.Message); }
                finally { _lock.Release(); }
            });
        }

        private async Task StartCaddyAsync(CancellationToken ct)
        {
            var caddyHome = Path.Combine(_paths.Run, "caddy");
            var env = new Dictionary<string, string>
            {
                // Keep Caddy's own files inside LuminaDB's folder instead of %AppData% (portable copy, SPEC §3).
                ["XDG_DATA_HOME"] = caddyHome,
                ["XDG_CONFIG_HOME"] = caddyHome,
                ["APPDATA"] = caddyHome,
            };
            var args = new[] { "run", "--config", WebConfig.CaddyFile(_paths), "--adapter", "caddyfile" };
            var p = ManagedProcess.Start(_stack.CaddyExe, args, Path.Combine(_paths.Logs, "caddy-console.log"), caddyHome, env);
            Hook(p, "caddy");
            _caddy = p;
            _job?.Add(p.Id);
            await WaitForPortAsync(p, _plan.WebPort, "The web server (Caddy)", ct).ConfigureAwait(false);
            _ = p.Exited.ContinueWith(_ => { if (!_stopping && p == _caddy) Fail("The web server (Caddy) stopped unexpectedly. " + LastUsefulLine(p)); }, TaskScheduler.Default);
        }

        private async Task StartApacheAsync(CancellationToken ct)
        {
            int port = _plan.ApacheInFront ? _plan.WebPort : _plan.ApachePort;
            var args = new[] { "-d", _stack.ApacheDir, "-f", WebConfig.ApacheFile(_paths) };
            var p = ManagedProcess.Start(_stack.HttpdExe, args, Path.Combine(_paths.Logs, "apache-console.log"), Path.Combine(_stack.ApacheDir, "bin"));
            Hook(p, "apache");
            _apache = p;
            _job?.Add(p.Id);
            await WaitForPortAsync(p, port, "Apache", ct).ConfigureAwait(false);
            // Apache on Windows runs the work in a child process; make sure it is in the job too.
            _job?.AddWithChildren(p.Id);
            _ = p.Exited.ContinueWith(_ =>
            {
                if (_stopping || p != _apache) return;
                if (_plan.ApacheInFront) Fail("The web server (Apache) stopped unexpectedly. " + LastUsefulLine(p));
                else Emit("lumina", "Apache stopped unexpectedly; projects with .htaccess show an error until the next start.");
            }, TaskScheduler.Default);
        }

        private async Task ReloadCaddyAsync()
        {
            var text = File.ReadAllText(WebConfig.CaddyFile(_paths));
            var error = await PostAdminAsync("/load", text).ConfigureAwait(false);
            if (error != null)
                throw new LuminaException(ErrorKind.ServerFailedToStart, "The web server refused the new settings: " + error);
        }

        /// <summary>Calls Caddy's admin API. Returns null on success, or the error text.</summary>
        private async Task<string> PostAdminAsync(string path, string caddyfile)
        {
            if (_plan == null || _plan.AdminPort == 0) return "no admin port";
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + _plan.AdminPort + path);
                req.Method = "POST";
                req.Proxy = null;
                req.Timeout = 15000;
                req.ContentType = caddyfile == null ? "application/json" : "text/caddyfile";
                var body = Encoding.UTF8.GetBytes(caddyfile ?? "");
                req.ContentLength = body.Length;
                using (var s = await req.GetRequestStreamAsync().ConfigureAwait(false)) await s.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
                using (var resp = (HttpWebResponse)await req.GetResponseAsync().ConfigureAwait(false)) return null;
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse r)
            {
                using (r)
                using (var sr = new StreamReader(r.GetResponseStream() ?? Stream.Null)) return sr.ReadToEnd().Trim();
            }
            catch (WebException ex) { return ex.Message; }
        }

        private async Task WaitForPortAsync(ManagedProcess p, int port, string what, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (p.HasExited)
                {
                    await p.WaitForExitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
                    throw new LuminaException(ErrorKind.ServerFailedToStart, what + " stopped while starting. " + LastUsefulLine(p), detail: p.RecentOutput());
                }
                if (await CanConnectAsync(port).ConfigureAwait(false)) return;
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
            throw new LuminaException(ErrorKind.ServerStartTimeout, what + " didn't answer on port " + port + " within 15 seconds.", detail: p.RecentOutput());
        }

        internal static async Task<bool> CanConnectAsync(int port)
        {
            using (var c = new TcpClient())
            {
                try
                {
                    var t = c.ConnectAsync(IPAddress.Loopback, port);
                    if (await Task.WhenAny(t, Task.Delay(500)).ConfigureAwait(false) != t)
                    {
                        // Disposing the client fails the pending connect; observe that so it isn't logged as unhandled.
                        _ = t.ContinueWith(x => x.Exception, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
                        return false;
                    }
                    await t.ConfigureAwait(false);
                    return c.Connected;
                }
                catch (SocketException) { return false; }
            }
        }

        private static string LastUsefulLine(ManagedProcess p)
        {
            var lines = p.RecentOutput(30).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("---")).ToList();
            var line = lines.LastOrDefault(l => l.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0) ?? lines.LastOrDefault();
            if (line == null) return "";
            if (line.Contains("Only one usage of each socket address") || line.Contains("10048"))
                return "Another program took its port. Start again and LuminaDB will pick a free one.";
            return "It said: " + (line.Length > 300 ? line.Substring(0, 300) + "…" : line);
        }

        private void Hook(ManagedProcess p, string source) => p.OutputLine += line => Emit(source, line);

        private void Emit(string source, string line) { try { OutputLine?.Invoke(source, line); } catch { } }

        // ------------------------------------------------------------------ stopping

        private void StopAll()
        {
            _stopping = true;
            StopDemoTimer();
            if (_caddy != null) { KillTree(_caddy, _stack.CaddyExe); _caddy.Dispose(); _caddy = null; }
            StopApache();
            StopPhp();
        }

        private void StopPhp()
        {
            var p = _php;
            _php = null;
            if (p == null) return;
            KillTree(p, _stack.PhpCgiExe);
            p.Dispose();
        }

        private void StopApache()
        {
            var p = _apache;
            _apache = null;
            if (p == null) return;
            KillTree(p, _stack.HttpdExe);
            p.Dispose();
        }

        /// <summary>Kills the process and its direct children (php-cgi workers, Apache's worker process).</summary>
        private static void KillTree(ManagedProcess p, string exe)
        {
            var children = JobObject.ChildrenOf(p.Id);
            p.Kill();
            foreach (var c in children) KillIfOurs(c, exe);
        }

        private static void KillIfOurs(int pid, string exe)
        {
            try
            {
                using (var proc = Process.GetProcessById(pid))
                {
                    string path;
                    try { path = proc.MainModule?.FileName; } catch (System.ComponentModel.Win32Exception) { return; }
                    if (path == null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase)) return;
                    proc.Kill();
                    proc.WaitForExit(5000);
                }
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        /// <summary>Web processes from an earlier run that the Job Object didn't catch (same exe path only).</summary>
        private void KillOrphans()
        {
            foreach (var exe in new[] { _stack.CaddyExe, _stack.HttpdExe, _stack.PhpCgiExe })
            {
                if (exe == null || !File.Exists(exe)) continue;
                foreach (var pid in ManagedProcess.FindRunning(exe))
                {
                    if (IsOurs(pid)) continue;
                    Emit("lumina", $"Stopping a leftover {Path.GetFileName(exe)} (PID {pid}) from an earlier run.");
                    KillIfOurs(pid, exe);
                }
            }
        }

        private bool IsOurs(int pid) =>
            (_php != null && _php.Id == pid) || (_caddy != null && _caddy.Id == pid) || (_apache != null && _apache.Id == pid);

        // ------------------------------------------------------------------ state

        private void SetState(WebState s)
        {
            _state = s;
            try { StateChanged?.Invoke(s); } catch { }
        }

        private void Fail(string problem)
        {
            Problem = problem;
            Emit("lumina", problem);
            if (_state != WebState.Stopped || problem != null) SetState(WebState.Failed);
        }

        private LuminaState LoadState() { lock (StateFileLock) return LuminaState.Load(_paths); }

        private void SaveState()
        {
            lock (StateFileLock)
            {
                var st = LuminaState.Load(_paths);
                st.WebPort = _plan.WebPort;
                st.PhpPort = _plan.PhpPort;
                if (_plan.AdminPort > 0) st.CaddyAdminPort = _plan.AdminPort;
                if (_plan.ApachePort > 0) st.ApachePort = _plan.ApachePort;
                st.Save(_paths);
            }
        }

        public void Dispose()
        {
            StopAll();
            _job?.Dispose();
            _lock.Dispose();
        }
    }
}
