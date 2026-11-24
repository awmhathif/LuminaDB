using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace LuminaDB.Core
{
    public enum ServerState { Stopped, Starting, Running, Stopping, Crashed }

    public sealed class MariaDbServiceOptions
    {
        public int PreferredPort { get; set; } = 3306;
        public TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(120);
        public TimeSpan InitTimeout { get; set; } = TimeSpan.FromSeconds(240);
        public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(60);
        /// <summary>0 = read from Windows.</summary>
        public ulong TotalRamBytes { get; set; }
    }

    /// <summary>
    /// Owns one MariaDB server: creates its data folder, writes my.ini, starts it on a free port
    /// bound to 127.0.0.1, waits until it answers, stops it cleanly, and adopts a server left
    /// running by an earlier LuminaDB run. Logic ported from EngineSpike, which proved it on this PC.
    /// </summary>
    public sealed class MariaDbService : IDisposable
    {
        private readonly AppPaths _paths;
        private readonly EngineSet _engine;
        private readonly PortManager _ports;
        private readonly MariaDbServiceOptions _options;
        private readonly RootCredentialStore _credentials;
        private readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);
        private ManagedProcess _process;
        private ServerState _state = ServerState.Stopped;
        private bool _stopRequested;

        public MariaDbService(AppPaths paths, EngineSet engine, PortManager ports = null, MariaDbServiceOptions options = null)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _ports = ports ?? new PortManager();
            _options = options ?? new MariaDbServiceOptions();
            _credentials = RootCredentialStore.For(paths, engine.Kind);
        }

        public event Action<ServerState> StateChanged;
        public event Action<string> OutputLine;

        public EngineSet Engine => _engine;
        public ServerState State => _state;
        public int Port { get; private set; }
        /// <summary>e.g. "11.8.10-MariaDB" (the "5.5.5-" compatibility prefix is removed).</summary>
        public string ServerVersion { get; private set; }
        public int? ProcessId => _process?.Id;
        /// <summary>True when the server runs but the stored root password can't be read on this PC.</summary>
        public bool NeedsPassword { get; private set; }
        public string DataDir => _paths.MariaDbData(_engine.Kind);
        public string ConfigFile => _paths.MariaDbConfigFile(_engine.Kind);
        public string LogFile => _paths.MariaDbLog(_engine.Kind);
        private string PidFile => _paths.MariaDbPidFile(_engine.Kind);

        // ------------------------------------------------------------------ connections

        public MySqlConnectionStringBuilder ConnectionString(string database = null, string password = null)
        {
            if (Port == 0) throw new LuminaException(ErrorKind.ServerNotRunning, "The database server is not running. Press Start first.");
            return new MySqlConnectionStringBuilder
            {
                Server = "127.0.0.1",
                Port = (uint)Port,
                UserID = "root",
                Password = password ?? _credentials.Load() ?? "",
                Database = database ?? "",
                CharacterSet = "utf8mb4",
                SslMode = MySqlSslMode.None,
                AllowPublicKeyRetrieval = true,
                AllowUserVariables = true,      // student SQL uses @variables
                AllowLoadLocalInfile = true,    // CSV import
                ConvertZeroDateTime = true,     // '0000-00-00' must not crash the grid
                ConnectionTimeout = 10,
                DefaultCommandTimeout = 0,
                Pooling = true,
            };
        }

        public async Task<MySqlConnection> OpenConnectionAsync(string database = null, CancellationToken ct = default)
        {
            var c = new MySqlConnection(ConnectionString(database).ConnectionString);
            try { await c.OpenAsync(ct).ConfigureAwait(false); return c; }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.AccessDenied)
            {
                c.Dispose();
                throw new LuminaException(ErrorKind.WrongRootPassword, "The saved root password no longer matches. Enter it once in Settings > Users.", ex);
            }
            catch { c.Dispose(); throw; }
        }

        // ------------------------------------------------------------------ start

        public async Task StartAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_state == ServerState.Running && _process != null && !_process.HasExited) return;
                SetState(ServerState.Starting);
                _stopRequested = false;
                try
                {
                    _paths.EnsureCreated();
                    if (await TryAdoptAsync(ct).ConfigureAwait(false)) { SetState(ServerState.Running); return; }
                    await EnsureDataFolderAsync(ct).ConfigureAwait(false);

                    var state = LuminaState.Load(_paths);
                    var tried = new List<int>();
                    for (int attempt = 0; ; attempt++)
                    {
                        int port = _ports.Pick(_options.PreferredPort, state.GetMariaDbPort(_engine.Kind), tried);
                        var result = await LaunchAsync(port, ct).ConfigureAwait(false);
                        if (result == LaunchResult.Ready)
                        {
                            state.SetMariaDbPort(_engine.Kind, port);
                            state.Save(_paths);
                            SetState(ServerState.Running);
                            return;
                        }
                        // Another program grabbed the port between our check and the server's bind: try the next one.
                        tried.Add(port);
                        if (attempt >= 2) throw StartFailure("The server could not get a free port.");
                    }
                }
                catch
                {
                    if (_state == ServerState.Starting) SetState(ServerState.Stopped);
                    throw;
                }
            }
            finally { _lock.Release(); }
        }

        private enum LaunchResult { Ready, PortTaken }

        private async Task<LaunchResult> LaunchAsync(int port, CancellationToken ct)
        {
            MariaDbConfigWriter.Write(ConfigFile, new MariaDbConfig
            {
                DataDir = DataDir,
                Port = port,
                SecureFilePriv = _paths.Exports,
                PluginDir = _engine.PluginDir,
                TempDir = _paths.Temp,
                TotalRamBytes = _options.TotalRamBytes != 0 ? _options.TotalRamBytes : SystemMemory.TotalBytes(),
                SupportsCharacterSetCollations = _engine.SupportsCharacterSetCollations,
            });

            // --defaults-file must be the first argument. Never pass SQL on the command line (SPEC §8).
            var proc = ManagedProcess.Start(_engine.ServerExe, new[] { "--defaults-file=" + ConfigFile, "--console" }, LogFile, _engine.MariaDbBin);
            AttachProcess(proc, port);

            var ready = await WaitReadyAsync(port, proc, _options.StartTimeout, ct).ConfigureAwait(false);
            if (ready) { WritePidFile(); return LaunchResult.Ready; }

            var output = proc.RecentOutput(40);
            DetachProcess();
            if (!proc.HasExited)
            {
                proc.Kill();
                proc.Dispose();
                throw new LuminaException(ErrorKind.ServerStartTimeout,
                    $"The database server did not answer within {_options.StartTimeout.TotalSeconds:0} seconds. The PC may be very busy; try again.", detail: output);
            }
            proc.Dispose();
            if (Contains(output, "Bind on TCP/IP port", "Do you already have another", "Address already in use"))
                return LaunchResult.PortTaken;
            if (Contains(output, "Unable to lock", "Check that you do not already have another"))
                throw new LuminaException(ErrorKind.DataFolderInUse,
                    "Another database server is already using LuminaDB's data folder. Close other copies of LuminaDB (or restart the PC) and try again.", detail: output);
            throw StartFailure(null, output);
        }

        private LuminaException StartFailure(string reason, string output = null) =>
            new LuminaException(ErrorKind.ServerFailedToStart,
                (reason ?? "The database server stopped while starting.") + " The Doctor can look at the log and suggest a fix.",
                detail: output ?? _process?.RecentOutput(40));

        private async Task<bool> WaitReadyAsync(int port, ManagedProcess proc, TimeSpan timeout, CancellationToken ct)
        {
            var until = DateTime.UtcNow + timeout;
            var pwd = _credentials.Load();
            while (DateTime.UtcNow < until)
            {
                ct.ThrowIfCancellationRequested();
                if (proc != null && proc.HasExited) return false;
                var probe = await ProbeAsync(port, pwd, ct).ConfigureAwait(false);
                if (probe.Answered)
                {
                    Port = port;
                    ServerVersion = probe.Version;
                    NeedsPassword = probe.AccessDenied;
                    return true;
                }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            return false;
        }

        private struct ProbeResult { public bool Answered, AccessDenied; public string Version; }

        /// <summary>Is a MariaDB server answering on this port? "Access denied" still means yes.</summary>
        private static async Task<ProbeResult> ProbeAsync(int port, string password, CancellationToken ct)
        {
            var cs = new MySqlConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = (uint)port, UserID = "root", Password = password ?? "",
                ConnectionTimeout = 2, Pooling = false, SslMode = MySqlSslMode.None, AllowPublicKeyRetrieval = true,
            };
            try
            {
                using (var c = new MySqlConnection(cs.ConnectionString))
                {
                    await c.OpenAsync(ct).ConfigureAwait(false);
                    using (var cmd = new MySqlCommand("SELECT VERSION()", c))
                        return new ProbeResult { Answered = true, Version = StripCompatPrefix(Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))) };
                }
            }
            catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.AccessDenied)
            {
                return new ProbeResult { Answered = true, AccessDenied = true, Version = null };
            }
            catch (MySqlException) { return default; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return default; }
            catch (TimeoutException) { return default; }
            catch (IOException) { return default; }
        }

        /// <summary>MariaDB 10.x sends "5.5.5-10.4.34-MariaDB" to old clients; show the real version.</summary>
        public static string StripCompatPrefix(string version)
        {
            if (string.IsNullOrEmpty(version)) return version;
            return version.StartsWith("5.5.5-", StringComparison.Ordinal) ? version.Substring(6) : version;
        }

        // ------------------------------------------------------------------ adopt

        private async Task<bool> TryAdoptAsync(CancellationToken ct)
        {
            var exe = _engine.ServerExe;
            var record = JsonFile.Read<ServerRecord>(PidFile);
            if (record != null)
            {
                if (await AdoptAsync(record.ProcessId, record.Port, ct).ConfigureAwait(false)) return true;
                JsonFile.Delete(PidFile);
            }

            // A server from this LuminaDB copy without a PID file (e.g. the PID file was deleted): adopt it if it
            // answers on the last port, otherwise stop it. A hard stop is safe: InnoDB recovers on next start.
            // Same exe is not enough: another LuminaDB folder may share the engine, and its server is not ours to touch.
            var lastPort = LuminaState.Load(_paths).GetMariaDbPort(_engine.Kind);
            foreach (var pid in ManagedProcess.FindRunning(exe))
            {
                if (!ManagedProcess.StartedFrom(pid, _paths.Root)) continue;
                if (lastPort.HasValue && await AdoptAsync(pid, lastPort.Value, ct).ConfigureAwait(false)) return true;
                var orphan = ManagedProcess.TryAdopt(pid, exe, LogFile);
                if (orphan != null)
                {
                    orphan.Log("--- stopping a leftover server that did not answer");
                    orphan.Kill();
                    orphan.Dispose();
                }
            }
            return false;
        }

        private async Task<bool> AdoptAsync(int pid, int port, CancellationToken ct)
        {
            if (!ManagedProcess.StartedFrom(pid, _paths.Root)) return false;   // a reused PID, or another copy's server
            var proc = ManagedProcess.TryAdopt(pid, _engine.ServerExe, LogFile);
            if (proc == null) return false;
            if (!await WaitReadyAsync(port, proc, TimeSpan.FromSeconds(3), ct).ConfigureAwait(false))
            {
                proc.Dispose();
                return false;
            }
            AttachProcess(proc, port);
            WritePidFile();
            return true;
        }

        private void AttachProcess(ManagedProcess proc, int port)
        {
            _process = proc;
            Port = port;
            proc.OutputLine += ForwardOutput;
            proc.Exited.ContinueWith(_ => OnProcessExited(proc), TaskScheduler.Default);
        }

        private void DetachProcess()
        {
            if (_process != null) _process.OutputLine -= ForwardOutput;
            _process = null;
            Port = 0;
        }

        private void ForwardOutput(string line) => OutputLine?.Invoke(line);

        private void OnProcessExited(ManagedProcess proc)
        {
            if (!ReferenceEquals(proc, _process)) return;
            if (_stopRequested || _state != ServerState.Running) return;
            JsonFile.Delete(PidFile);
            SetState(ServerState.Crashed);
        }

        private void WritePidFile()
        {
            JsonFile.Write(PidFile, new ServerRecord
            {
                ProcessId = _process.Id,
                ExePath = _engine.ServerExe,
                Port = Port,
                DataDir = DataDir,
                StartedUtcTicks = DateTime.UtcNow.Ticks,
            });
        }

        // ------------------------------------------------------------------ data folder

        private async Task EnsureDataFolderAsync(CancellationToken ct)
        {
            var dir = DataDir;
            var stampPath = Path.Combine(dir, DataFolderStamp.FileName);
            var setName = EngineSet.FolderName(_engine.Kind);

            if (Directory.Exists(Path.Combine(dir, "mysql")))
            {
                var stamp = JsonFile.Read<DataFolderStamp>(stampPath);
                if (stamp != null && !string.Equals(stamp.EngineSet, setName, StringComparison.OrdinalIgnoreCase))
                    throw new LuminaException(ErrorKind.DataFolderBelongsToOtherEngine,
                        $"This data folder was made by the \"{stamp.EngineSet}\" engine, but this PC uses \"{setName}\". " +
                        "Use Backups > Move to this PC to bring the databases over safely.");
                if (stamp == null) WriteStamp(stampPath, setName);
                return;
            }

            if (Directory.Exists(dir))
            {
                if (Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    // Half-made folder from an interrupted first start. Keep it aside rather than delete anything.
                    var aside = dir + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    Directory.Move(dir, aside);
                    OutputLine?.Invoke($"--- an unfinished data folder was moved to {aside}");
                }
                else Directory.Delete(dir);
            }

            var args = new[] { "--datadir=" + dir, "--port=" + _options.PreferredPort.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var tool = ManagedProcess.Start(_engine.InstallDbExe, args, LogFile, _engine.MariaDbBin);
            tool.OutputLine += ForwardOutput;
            try
            {
                if (!await tool.WaitForExitAsync(_options.InitTimeout).ConfigureAwait(false))
                {
                    tool.Kill();
                    throw new LuminaException(ErrorKind.DataFolderInitFailed,
                        "Creating the database folder took too long. Antivirus may be scanning it; try again.", detail: tool.RecentOutput(30));
                }
                int code = await tool.Exited.ConfigureAwait(false);
                if (code != 0 || !Directory.Exists(Path.Combine(dir, "mysql")))
                {
                    var output = tool.RecentOutput(30);
                    TryDeleteNewFolder(dir);
                    throw new LuminaException(ErrorKind.DataFolderInitFailed,
                        "LuminaDB could not create its database folder. The Doctor can look at the log.", detail: output);
                }
            }
            finally
            {
                tool.OutputLine -= ForwardOutput;
                tool.Dispose();
            }
            ct.ThrowIfCancellationRequested();
            WriteStamp(stampPath, setName);
        }

        private void WriteStamp(string path, string setName) =>
            JsonFile.Write(path, new DataFolderStamp
            {
                EngineSet = setName,
                CreatedByVersion = _engine.ServerVersion.ToString(),
                CreatedUtcTicks = DateTime.UtcNow.Ticks,
            });

        private static void TryDeleteNewFolder(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ------------------------------------------------------------------ stop

        public async Task StopAsync(CancellationToken ct = default)
        {
            await _lock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var proc = _process;
                if (proc == null || proc.HasExited)
                {
                    JsonFile.Delete(PidFile);
                    DetachProcess();
                    proc?.Dispose();
                    SetState(ServerState.Stopped);
                    return;
                }
                _stopRequested = true;
                SetState(ServerState.Stopping);
                bool sent = false;
                try
                {
                    var cs = ConnectionString();
                    cs.Pooling = false;
                    using (var c = new MySqlConnection(cs.ConnectionString))
                    {
                        await c.OpenAsync(ct).ConfigureAwait(false);
                        using (var cmd = new MySqlCommand("SHUTDOWN", c)) await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        sent = true;
                    }
                }
                catch (MySqlException ex) when (ex.ErrorCode != MySqlErrorCode.AccessDenied) { sent = true; /* connection drops while shutting down */ }
                catch (MySqlException) { /* wrong password: fall back to a hard stop below */ }
                catch (IOException) { sent = true; }

                if (!sent || !await proc.WaitForExitAsync(_options.StopTimeout).ConfigureAwait(false))
                {
                    proc.Log("--- clean shutdown not possible, stopping the server process (InnoDB will recover on next start)");
                    proc.Kill();
                }
                JsonFile.Delete(PidFile);
                DetachProcess();
                proc.Dispose();
                MySqlConnection.ClearAllPools();
                SetState(ServerState.Stopped);
            }
            finally { _lock.Release(); }
        }

        public async Task RestartAsync(CancellationToken ct = default)
        {
            await StopAsync(ct).ConfigureAwait(false);
            await StartAsync(ct).ConfigureAwait(false);
        }

        // ------------------------------------------------------------------ root password

        /// <summary>Sets the root password on EVERY root account (localhost, 127.0.0.1, ::1, host name), so no login path is left open.</summary>
        public async Task SetRootPasswordAsync(string newPassword, CancellationToken ct = default)
        {
            if (newPassword == null) throw new ArgumentNullException(nameof(newPassword));
            if (newPassword.Length > 0 && newPassword.Trim().Length == 0)
                throw new LuminaException(ErrorKind.InvalidInput, "A password made only of spaces is easy to get wrong. Use letters or numbers.");

            using (var c = await OpenConnectionAsync(null, ct).ConfigureAwait(false))
            {
                var hosts = new List<string>();
                using (var cmd = new MySqlCommand("SELECT Host FROM mysql.user WHERE User = 'root'", c))
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    while (await r.ReadAsync(ct).ConfigureAwait(false)) hosts.Add(r.GetString(0));

                foreach (var host in hosts)
                {
                    using (var cmd = new MySqlCommand("ALTER USER 'root'@" + SqlLiteral(host) + " IDENTIFIED BY @pwd", c))
                    {
                        cmd.Parameters.AddWithValue("@pwd", newPassword);
                        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }
            }
            _credentials.Save(newPassword);
            NeedsPassword = false;
            MySqlConnection.ClearAllPools();
        }

        /// <summary>When <see cref="NeedsPassword"/> is true: checks the password the user typed and remembers it.</summary>
        public async Task<bool> UnlockAsync(string password, CancellationToken ct = default)
        {
            var probe = await ProbeAsync(Port, password, ct).ConfigureAwait(false);
            if (!probe.Answered || probe.AccessDenied) return false;
            _credentials.Save(password);
            ServerVersion = probe.Version;
            NeedsPassword = false;
            return true;
        }

        internal static string SqlLiteral(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'";

        // ------------------------------------------------------------------ misc

        private static bool Contains(string text, params string[] needles) =>
            text != null && needles.Any(n => text.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0);

        private void SetState(ServerState s)
        {
            if (_state == s) return;
            _state = s;
            try { StateChanged?.Invoke(s); } catch { /* UI handler errors must not break the service */ }
        }

        /// <summary>Releases our handles. The server keeps running (call StopAsync first to stop it).</summary>
        public void Dispose()
        {
            var p = _process;
            if (p != null) p.OutputLine -= ForwardOutput;
            p?.Dispose();
            _process = null;
            _lock.Dispose();
        }
    }
}
