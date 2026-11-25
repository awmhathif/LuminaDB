using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LuminaDB.Core.Projects;
using LuminaDB.Core.Web;
using MySqlConnector;

namespace LuminaDB.Core
{
    /// <summary>What the Doctor looks at. Anything may be null when that part isn't there.</summary>
    public sealed class DoctorContext
    {
        public AppPaths Paths { get; set; }
        public WindowsInfo Windows { get; set; }
        public EngineSet Engine { get; set; }
        public LuminaException EngineError { get; set; }
        public MariaDbService Server { get; set; }
        public BackupService Backups { get; set; }
        public WebStack Web { get; set; }
        /// <summary>LuminaDB's own web server is up (so web processes from our folder are expected).</summary>
        public bool WebRunning { get; set; }
        public int PreferredPort { get; set; } = 3306;
        /// <summary>Why the last start failed, if it did.</summary>
        public Exception LastStartError { get; set; }
    }

    /// <summary>
    /// The full check-up (SPEC §5.9): everything on Today plus engine files, leftover processes, the server's own
    /// system tables, the clock, and backups. Each finding says what's wrong in plain words and, where LuminaDB can,
    /// offers a fix. A fix that touches data backs up every database first.
    /// </summary>
    public sealed class Doctor
    {
        private readonly DoctorContext _c;

        public Doctor(DoctorContext context) { _c = context ?? throw new ArgumentNullException(nameof(context)); }

        private bool Running => _c.Server != null && _c.Server.State == ServerState.Running && !_c.Server.NeedsPassword;

        public async Task<List<CheckResult>> RunAsync(CancellationToken ct = default)
        {
            var list = new List<CheckResult>();
            list.Add(CheckServer());
            list.Add(CheckEngineFiles());
            if (_c.Server != null) list.Add(StartupChecks.CheckPort(_c.Server, _c.PreferredPort));
            if (Running) list.Add(StartupChecks.CheckLoopbackOnly(_c.Server.Port));
            list.Add(CheckLeftovers());
            if (Running)
            {
                list.Add(await Safe("systables", "System tables", () => CheckSystemTablesAsync(ct)).ConfigureAwait(false));
                list.Add(await Safe("clock", "Clock and time zone", () => CheckClockAsync(ct)).ConfigureAwait(false));
                list.Add(await Safe("backups", "Backups", () => CheckBackupsAsync(ct)).ConfigureAwait(false));
            }
            list.Add(CheckConfig());
            list.Add(CheckOverrides());
            if (_c.Paths != null)
            {
                list.Add(StartupChecks.CheckPath(_c.Paths.Root));
                list.Add(StartupChecks.CheckDisk(_c.Paths.Root));
                list.Add(StartupChecks.CheckWritable(_c.Paths));
            }
            return list.Where(r => r != null).ToList();
        }

        private static async Task<CheckResult> Safe(string id, string title, Func<Task<CheckResult>> check)
        {
            try { return await check().ConfigureAwait(false); }
            catch (Exception ex) when (ex is MySqlException || ex is LuminaException || ex is IOException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                return new CheckResult(id, CheckSeverity.Warn, title, "Couldn't check this just now: " + ex.Message, id);
            }
        }

        // ------------------------------------------------------------------ server

        private CheckResult CheckServer()
        {
            var s = _c.Server;
            if (s == null) return null;   // the engine check says why
            if (s.State == ServerState.Crashed)
                return new CheckResult("server", CheckSeverity.Bad, "Server stopped unexpectedly",
                    "It ended by itself (power cut, antivirus or Task Manager). Committed data is safe: InnoDB replays its log at the next start, and this check-up then looks at the system tables.",
                    Path.GetFileName(s.LogFile)).WithFix("start-server", "Start it again");
            if (s.State != ServerState.Running && _c.LastStartError != null)
            {
                var lex = _c.LastStartError as LuminaException;
                var text = lex?.Message ?? _c.LastStartError.Message;
                if (lex?.Kind == ErrorKind.DataFolderBelongsToOtherEngine)
                    return new CheckResult("server", CheckSeverity.Bad, "Data from another engine set", text + " Back it up from the PC that made it, then import it here.", "data\\");
                return new CheckResult("server", CheckSeverity.Bad, "Server didn't start", text, Path.GetFileName(s.LogFile)).WithFix("start-server", "Try again");
            }
            if (s.State == ServerState.Running)
                return new CheckResult("server", CheckSeverity.Ok, "Server running", $"MariaDB {s.ServerVersion} on port {s.Port}.", "mariadbd");
            return new CheckResult("server", CheckSeverity.Ok, "Server stopped", "Stopped cleanly; everything is on disk. Some checks need it running.", "mariadbd").WithFix("start-server", "Start it");
        }

        // ------------------------------------------------------------------ engine files

        /// <summary>The engine's programs: present and not emptied (antivirus quarantine often leaves a 0-byte file or nothing).</summary>
        private CheckResult CheckEngineFiles()
        {
            if (_c.Engine == null)
                return new CheckResult("engine", CheckSeverity.Bad, "Database engine missing",
                    (_c.EngineError?.Message ?? "The engine files for this PC are not in the LuminaDB folder.") + " Run the LuminaDB installer again to put them back (your data, projects and backups stay).", "engines\\");
            var problems = EngineFileProblems(_c.Engine);
            var folder = _c.Engine.MariaDbBin;
            if (problems.Count == 0)
            {
                string v;
                try { v = _c.Engine.ServerVersion.ToString(); } catch (LuminaException) { v = "?"; }
                return new CheckResult("engine", CheckSeverity.Ok, "Engine files complete",
                    $"MariaDB {v} ({EngineSet.FolderName(_c.Engine.Kind)} set) for {_c.Windows?.FriendlyName ?? "this Windows"}: every program is there and the Visual C++ runtime ships beside it.", "engines\\");
            }
            return new CheckResult("engine", CheckSeverity.Bad, "Engine files damaged",
                string.Join(", ", problems.Take(4)) + (problems.Count > 4 ? $" and {problems.Count - 4} more" : "") +
                ". That is almost always antivirus. In your antivirus, add an exclusion for " + folder +
                ", then run the LuminaDB installer again (your data, projects and backups stay).", folder)
                .WithFix("open-engine-folder", "Open the engine folder");
        }

        /// <summary>"mariadbd.exe is missing", "libssl.dll is empty".</summary>
        public static List<string> EngineFileProblems(EngineSet engine)
        {
            var p = new List<string>();
            foreach (var get in new Func<string>[] { () => engine.ServerExe, () => engine.ClientExe, () => engine.DumpExe, () => engine.CheckExe })
            {
                try { get(); }
                catch (LuminaException ex) { p.Add(ex.Message.Split(new[] { " is missing" }, StringSplitOptions.None)[0] + " is missing"); }
            }
            try
            {
                if (Directory.Exists(engine.MariaDbBin))
                    foreach (var f in Directory.GetFiles(engine.MariaDbBin).Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
                        if (new FileInfo(f).Length == 0) p.Add(Path.GetFileName(f) + " is empty");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { p.Add("the engine folder can't be read (" + ex.Message + ")"); }
            return p;
        }

        // ------------------------------------------------------------------ leftover processes

        /// <summary>Servers from this LuminaDB folder that no part of the app is using (e.g. after the app was killed).</summary>
        public List<(int Pid, string Name)> FindLeftovers()
        {
            var result = new List<(int, string)>();
            if (_c.Paths == null) return result;
            if (_c.Engine != null && _c.Server != null && _c.Server.State != ServerState.Starting && _c.Server.State != ServerState.Stopping)
            {
                string exe = null;
                try { exe = _c.Engine.ServerExe; } catch (LuminaException) { }
                if (exe != null)
                    foreach (var pid in ManagedProcess.FindRunning(exe))
                        if (pid != _c.Server.ProcessId && ManagedProcess.StartedFrom(pid, _c.Paths.Root)) result.Add((pid, Path.GetFileName(exe)));
            }
            if (_c.Web != null && !_c.WebRunning)
            {
                foreach (var exe in new[] { _c.Web.CaddyExe, _c.Web.HttpdExe, _c.Web.PhpCgiExe })
                {
                    if (exe == null || !File.Exists(exe)) continue;
                    foreach (var pid in ManagedProcess.FindRunning(exe))
                        if (ManagedProcess.StartedFrom(pid, _c.Paths.Root)) result.Add((pid, Path.GetFileName(exe)));
                }
            }
            return result;
        }

        private CheckResult CheckLeftovers()
        {
            var left = FindLeftovers();
            if (left.Count == 0)
                return new CheckResult("leftovers", CheckSeverity.Ok, "No leftover servers", "Nothing from an earlier run is still holding ports or files.", "processes");
            return new CheckResult("leftovers", CheckSeverity.Warn, "Leftover servers running",
                string.Join(", ", left.Select(l => $"{l.Name} (PID {l.Pid})")) + " from an earlier run of this LuminaDB folder " +
                (left.Count == 1 ? "is" : "are") + " still running. They hold ports and files. Stopping them is safe: the database recovers by itself.", "processes")
                .WithFix("stop-leftovers", left.Count == 1 ? "Stop it" : "Stop them");
        }

        // ------------------------------------------------------------------ system tables

        private async Task<CheckResult> CheckSystemTablesAsync(CancellationToken ct)
        {
            var (code, output, errors) = await DatabaseDumper.RunToolAsync(_c.Server, _c.Engine.CheckExe, new[] { "--check", "--databases", "mysql" }, ct).ConfigureAwait(false);
            var (bad, warn) = ParseCheckOutput(output + "\n" + errors);
            if (code == 0 && bad.Count == 0 && warn.Count == 0)
                return new CheckResult("systables", CheckSeverity.Ok, "System tables healthy",
                    "The server's own tables (users, privileges, time zones) passed a check. They're the ones an unclean shutdown can damage.", "mariadb-check mysql");
            var names = bad.Concat(warn).Distinct().ToList();
            return new CheckResult("systables", bad.Count > 0 || code != 0 ? CheckSeverity.Bad : CheckSeverity.Warn,
                bad.Count > 0 ? "System tables damaged" : "System tables not closed properly",
                (names.Count > 0 ? string.Join(", ", names.Take(5)) + (names.Count > 5 ? " and more" : "") : "Some system tables") +
                " need a repair, usually after a power cut or a forced stop. Logins or privileges may misbehave until then.",
                "mariadb-check mysql")
                .WithFix("repair-system-tables", "Repair them", backsUp: true);
        }

        /// <summary>
        /// Reads mariadb-check output: a "db.table" line, then "error :" / "warning :" / "note :" / "status :" lines, or
        /// "db.table   OK" on one line. Errors and warnings name tables to repair; notes don't.
        /// </summary>
        public static (List<string> Errors, List<string> Warnings) ParseCheckOutput(string output)
        {
            var errors = new List<string>();
            var warnings = new List<string>();
            string current = null;
            foreach (var raw in (output ?? "").Split('\n'))
            {
                var line = raw.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                string head = colon > 0 ? line.Substring(0, colon).Trim().ToLowerInvariant() : null;
                if (head == "error" || head == "warning" || head == "note" || head == "status" || head == "info")
                {
                    var target = current ?? "?";
                    if (head == "error" && !errors.Contains(target)) errors.Add(target);
                    else if (head == "warning" && !warnings.Contains(target)) warnings.Add(target);
                    continue;
                }
                // "mysql.db      OK" or a bare "mysql.db"
                var first = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
                if (first.Contains(".")) current = first;
                else if (line.StartsWith("mariadb-check:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("mysqlcheck:", StringComparison.OrdinalIgnoreCase))
                {
                    if (line.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 && !errors.Contains("?")) errors.Add("?");
                }
            }
            return (errors, warnings);
        }

        // ------------------------------------------------------------------ clock

        private async Task<CheckResult> CheckClockAsync(CancellationToken ct)
        {
            int serverMinutes;
            string zone;
            using (var c = await _c.Server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
            using (var cmd = new MySqlCommand("SELECT TIMESTAMPDIFF(MINUTE, UTC_TIMESTAMP(), NOW()), @@time_zone, @@system_time_zone", c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                await r.ReadAsync(ct).ConfigureAwait(false);
                serverMinutes = Convert.ToInt32(r.GetValue(0), CultureInfo.InvariantCulture);
                zone = r.GetValue(1) + " / " + r.GetValue(2);
            }
            int windowsMinutes = (int)Math.Round(TimeZoneInfo.Local.GetUtcOffset(DateTime.Now).TotalMinutes);
            string Offset(int m) => "UTC" + (m < 0 ? "−" : "+") + (Math.Abs(m) / 60).ToString("00", CultureInfo.InvariantCulture) + ":" + (Math.Abs(m) % 60).ToString("00", CultureInfo.InvariantCulture);
            if (Math.Abs(serverMinutes - windowsMinutes) <= 1)
                return new CheckResult("clock", CheckSeverity.Ok, "Clock and time zone",
                    $"NOW() in SQL matches Windows ({Offset(windowsMinutes)}), and PHP uses the same zone, so times in your pages agree.", "time_zone");
            return new CheckResult("clock", CheckSeverity.Warn, "Database clock is off",
                $"NOW() in SQL is {Offset(serverMinutes)} but Windows is {Offset(windowsMinutes)} (server zone {zone}). Restart the server so it picks up the Windows time zone.", "time_zone")
                .WithFix("restart-server", "Restart the server");
        }

        // ------------------------------------------------------------------ backups

        private async Task<CheckResult> CheckBackupsAsync(CancellationToken ct)
        {
            if (_c.Backups == null) return null;
            var prints = await _c.Backups.FingerprintsAsync(ct).ConfigureAwait(false);
            if (prints.Count == 0)
                return new CheckResult("backups", CheckSeverity.Ok, "Backups", "No databases yet, so nothing to back up. Backups start by themselves once you make one.", "backups\\");
            var changed = await _c.Backups.ChangedDatabasesAsync(ct).ConfigureAwait(false);
            var never = changed.Where(db => _c.Backups.Latest(db) == null).ToList();
            var latest = _c.Backups.Latest();
            if (never.Count > 0)
                return new CheckResult("backups", CheckSeverity.Warn, "Not backed up yet",
                    string.Join(", ", never.Take(4)) + (never.Count > 4 ? $" and {never.Count - 4} more" : "") +
                    (never.Count == 1 ? " has" : " have") + " no backup yet. The automatic one runs within 30 minutes, or take one now.", "backups\\")
                    .WithFix("backup-now", "Back up now");
            var age = latest == null ? "" : " The newest is from " + Ago(latest.Taken) + " (" + latest.Database + ").";
            return new CheckResult("backups", CheckSeverity.Ok, "Backups",
                $"Every database has a backup.{age} {(changed.Count == 0 ? "Nothing changed since." : changed.Count + " changed since; the next automatic backup takes care of " + (changed.Count == 1 ? "it." : "them."))}", "backups\\");
        }

        public static string Ago(DateTime when)
        {
            var d = DateTime.Now - when;
            if (d.TotalMinutes < 1) return "just now";
            if (d.TotalMinutes < 60) return (int)d.TotalMinutes + (d.TotalMinutes < 2 ? " minute ago" : " minutes ago");
            if (d.TotalHours < 24) return (int)d.TotalHours + ((int)d.TotalHours == 1 ? " hour ago" : " hours ago");
            return when.ToString("d MMM, HH:mm", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ config

        private CheckResult CheckConfig()
        {
            if (_c.Paths == null || _c.Engine == null) return null;
            var file = _c.Paths.MariaDbConfigFile(_c.Engine.Kind);
            if (!File.Exists(file))
                return new CheckResult("config", CheckSeverity.Ok, "Server settings", "Written fresh from LuminaDB's template at the next start.", Path.GetFileName(file));
            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new CheckResult("config", CheckSeverity.Warn, "Server settings", "Couldn't read " + file + ": " + ex.Message, Path.GetFileName(file));
            }
            bool loopback = text.IndexOf("bind-address=127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("bind-address = 127.0.0.1", StringComparison.OrdinalIgnoreCase) >= 0;
            bool utf8 = text.IndexOf("utf8mb4", StringComparison.OrdinalIgnoreCase) >= 0;
            if (loopback && utf8)
                return new CheckResult("config", CheckSeverity.Ok, "Server settings",
                    "Written from LuminaDB's template at every start (local only, utf8mb4 for Sinhala and Tamil), so a hand edit can't break it for long.", Path.GetFileName(file));
            return new CheckResult("config", CheckSeverity.Warn, "Server settings were edited",
                Path.GetFileName(file) + " is missing " + (loopback ? "utf8mb4" : "the local-only setting") + ". LuminaDB writes it again from its template at the next start.", Path.GetFileName(file))
                .WithFix("restart-server", "Restart to rewrite it");
        }

        /// <summary>Server variables kept from the Server page (SPEC §5.9 "overrides kept aside and reported").</summary>
        private CheckResult CheckOverrides()
        {
            if (_c.Paths == null || _c.Engine == null) return null;
            var aside = _c.Server?.OverridesSetAside;
            if (aside != null)
                return new CheckResult("overrides", CheckSeverity.Warn, "Kept server settings were put aside",
                    "The server wouldn't start with the settings kept on the Server page, so LuminaDB started it without them. They are saved in " +
                    Path.GetFileName(aside) + " (config folder); keep them again one by one on the Server page.", Path.GetFileName(aside));
            Admin.ServerOverrides kept;
            try { kept = Admin.ServerOverrides.Load(_c.Paths, _c.Engine.Kind); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is LuminaException)
            {
                return new CheckResult("overrides", CheckSeverity.Warn, "Kept server settings", "Couldn't read them: " + ex.Message, "overrides");
            }
            if (kept.Items.Count == 0) return null;
            return new CheckResult("overrides", CheckSeverity.Ok, "Kept server settings",
                kept.Items.Count + " setting" + (kept.Items.Count == 1 ? "" : "s") + " from the Server page (" +
                string.Join(", ", kept.Items.Take(4).Select(i => i.Name)) + (kept.Items.Count > 4 ? ", …" : "") +
                "). If one stops the server starting, they are put aside automatically.", Path.GetFileName(Admin.ServerOverrides.FileFor(_c.Paths, _c.Engine.Kind)));
        }

        // ------------------------------------------------------------------ fixes

        /// <summary>Runs a Core fix and says what it did. Fixes that touch data back up every database first.</summary>
        public async Task<string> FixAsync(string fixId, IProgress<string> progress = null, CancellationToken ct = default)
        {
            switch (fixId)
            {
                case "stop-leftovers":
                {
                    var left = FindLeftovers();
                    int stopped = 0;
                    foreach (var (pid, _) in left)
                    {
                        try
                        {
                            using (var p = Process.GetProcessById(pid))
                            {
                                if (!ManagedProcess.StartedFrom(pid, _c.Paths.Root)) continue;   // re-check: PIDs get reused
                                p.Kill();
                                p.WaitForExit(10000);
                                stopped++;
                            }
                        }
                        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                    }
                    return stopped == 0 ? "They had already stopped." : $"Stopped {stopped} leftover {(stopped == 1 ? "server" : "servers")}.";
                }
                case "repair-system-tables":
                {
                    RequireRunning();
                    if (_c.Backups != null)
                    {
                        progress?.Report("Backing up every database first…");
                        await _c.Backups.BackUpAllAsync("Before repairing the server's system tables", BackupKind.BeforeRisky, ct).ConfigureAwait(false);
                    }
                    progress?.Report("Repairing the system tables…");
                    var (code, output, errors) = await DatabaseDumper.RunToolAsync(_c.Server, _c.Engine.CheckExe, new[] { "--auto-repair", "--check", "--databases", "mysql" }, ct).ConfigureAwait(false);
                    var after = await DatabaseDumper.RunToolAsync(_c.Server, _c.Engine.CheckExe, new[] { "--check", "--databases", "mysql" }, ct).ConfigureAwait(false);
                    var (bad, warn) = ParseCheckOutput(after.Output + "\n" + after.Errors);
                    if (after.Code != 0 || bad.Count > 0)
                        throw new LuminaException(ErrorKind.Unknown, "The repair didn't fix everything: " + string.Join(", ", bad.DefaultIfEmpty("see the details")), detail: output + errors + after.Output + after.Errors);
                    return "Repaired. Every system table passes its check" + (warn.Count > 0 ? " (with notes)." : ".");
                }
                case "backup-now":
                {
                    RequireRunning();
                    var made = await _c.Backups.BackUpChangedAsync("Taken from the Doctor", BackupKind.Manual, ct).ConfigureAwait(false);
                    return made.Count == 0 ? "Everything was already backed up." : "Backed up " + string.Join(", ", made.Select(m => m.Database)) + ".";
                }
                default:
                    throw new LuminaException(ErrorKind.InvalidInput, "Unknown fix: " + fixId);
            }
        }

        private void RequireRunning()
        {
            if (!Running) throw new LuminaException(ErrorKind.ServerNotRunning, "Start the database server first.");
        }
    }
}
