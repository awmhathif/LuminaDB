using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;
using MySqlConnector;

namespace EngineSpike
{
    /// <summary>
    /// Engine spike: proves (or disproves) that a portable MariaDB can be run by our app
    /// on this PC with no installer, no admin, no service, and survive a crash.
    ///
    /// Usage:
    ///   EngineSpike.exe                 test every engine found in .\engines (folders or .zip)
    ///   EngineSpike.exe &lt;folder|zip&gt;    test one engine
    ///   EngineSpike.exe --scan-only     only check Windows 7 / VC++ compatibility, don't start anything
    /// Writes report-&lt;PC name&gt;-&lt;time&gt;.md next to the exe.
    /// </summary>
    internal static class Program
    {
        static readonly StringBuilder Report = new StringBuilder();
        static readonly List<Process> Started = new List<Process>();
        static string BaseDir => AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');

        static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            AppDomain.CurrentDomain.ProcessExit += (s, e) => KillLeftovers();
            Console.CancelKeyPress += (s, e) => KillLeftovers();

            bool scanOnly = args.Any(a => a.Equals("--scan-only", StringComparison.OrdinalIgnoreCase));
            var targets = args.Where(a => !a.StartsWith("--")).ToList();

            H1($"LuminaDB engine test — {Environment.MachineName} — {DateTime.Now:yyyy-MM-dd HH:mm}");
            DescribeMachine();

            var engines = targets.Count > 0 ? targets : FindEngines();
            if (engines.Count == 0)
            {
                Line("No engines found. Put mariadb-*-winx64.zip (or its extracted folder) in the 'engines' folder next to this exe.");
                return Save(2);
            }

            int failures = 0;
            foreach (var e in engines)
            {
                try
                {
                    var folder = PrepareEngine(e);
                    if (folder == null) { failures++; continue; }
                    if (!TestEngine(folder, scanOnly)) failures++;
                }
                catch (Exception ex)
                {
                    failures++;
                    Fail($"Unexpected error while testing {Path.GetFileName(e)}: {ex.GetType().Name}: {ex.Message}");
                }
                finally { KillLeftovers(); }
            }

            H2("Overall");
            Line(failures == 0 ? "PASS: every engine tested worked on this PC." : $"FAIL: {failures} engine(s) had problems. See the sections above.");
            return Save(failures == 0 ? 0 : 1);
        }

        // ------------------------------------------------------------------ machine
        static void DescribeMachine()
        {
            H2("This PC");
            string product = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName") ?? "?";
            string build = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuild") ?? "?";
            string display = Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion") ?? Reg(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CSDVersion") ?? "";
            if (int.TryParse(build, out var b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            Kv("Windows", $"{product} {display} (build {build}), {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")}");
            Kv("CPU cores", Environment.ProcessorCount.ToString());
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize, FreePhysicalMemory FROM Win32_OperatingSystem"))
                    foreach (ManagementObject o in s.Get())
                        Kv("RAM", $"{Convert.ToInt64(o["TotalVisibleMemorySize"]) / 1024} MB total, {Convert.ToInt64(o["FreePhysicalMemory"]) / 1024} MB free");
            }
            catch { Kv("RAM", "could not read"); }
            Kv(".NET Framework", NetFxVersion());
            Kv("Running as admin", IsAdmin() ? "yes (the real app must NOT need this; re-run as a normal user too)" : "no (good: this is how students will run it)");
            Kv("UCRT (ucrtbase.dll) in Windows", File.Exists(Path.Combine(Environment.SystemDirectory, "ucrtbase.dll")) ? "present" : "MISSING (needs KB2999226 on Windows 7, unless the engine ships it)");
            Kv("VC++ 2015-2022 x64 installed", Reg(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64", "Version") ?? "not installed (fine if the engine ships its own DLLs)");
            Kv("Antivirus", Antivirus());
            Kv("Test folder", BaseDir + (BaseDir.Contains(" ") ? "  ⚠ contains spaces" : ""));
            Kv("Port 3306", PortInUse(3306) ? $"IN USE by {PortOwner(3306)}" : "free");
        }

        static string NetFxVersion()
        {
            var rel = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full")?.GetValue("Release");
            if (!(rel is int r)) return "4.x (unknown)";
            return r >= 533320 ? "4.8.1" : r >= 528040 ? "4.8" : r >= 461808 ? "4.7.2" : r >= 460798 ? "4.7" : "older than 4.7";
        }

        static string Antivirus()
        {
            try
            {
                using (var s = new ManagementObjectSearcher(@"root\SecurityCenter2", "SELECT displayName FROM AntiVirusProduct"))
                {
                    var names = s.Get().Cast<ManagementObject>().Select(o => o["displayName"]?.ToString()).Where(n => !string.IsNullOrEmpty(n)).Distinct().ToList();
                    return names.Count > 0 ? string.Join(", ", names) : "none reported";
                }
            }
            catch { return "could not read"; }
        }

        static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        // ------------------------------------------------------------------ engines
        static List<string> FindEngines()
        {
            var dir = Path.Combine(BaseDir, "engines");
            if (!Directory.Exists(dir)) return new List<string>();
            var folders = Directory.GetDirectories(dir, "mariadb-*").ToList();
            var zips = Directory.GetFiles(dir, "mariadb-*.zip")
                .Where(z => !folders.Any(f => Path.GetFileName(f).Equals(Path.GetFileNameWithoutExtension(z), StringComparison.OrdinalIgnoreCase)));
            return folders.Concat(zips).OrderBy(x => x).ToList();
        }

        static string PrepareEngine(string path)
        {
            if (Directory.Exists(path)) return path;
            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                var target = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path));
                if (!Directory.Exists(target))
                {
                    Console.WriteLine($"Extracting {Path.GetFileName(path)} ...");
                    ZipFile.ExtractToDirectory(path, Path.GetDirectoryName(path));
                }
                return target;
            }
            Fail($"Not found: {path}");
            return null;
        }

        static bool TestEngine(string folder, bool scanOnly)
        {
            string name = Path.GetFileName(folder);
            H2(name);
            string bin = Path.Combine(folder, "bin");
            string server = new[] { "mariadbd.exe", "mysqld.exe" }.Select(x => Path.Combine(bin, x)).FirstOrDefault(File.Exists);
            string installer = new[] { "mariadb-install-db.exe", "mysql_install_db.exe" }.Select(x => Path.Combine(bin, x)).FirstOrDefault(File.Exists);
            if (server == null || installer == null) { Fail("bin\\mariadbd.exe / mysqld.exe or the install-db tool is missing."); return false; }

            bool ok = true;
            ok &= SizeReport(folder);
            ok &= CompatibilityReport(server, bin);
            if (scanOnly) return ok;
            ok &= Lifecycle(name, server, installer);
            return ok;
        }

        // ------------------------------------------------------------------ size
        static readonly string[] TrimDirs = { "include", "lib\\pkgconfig", "share\\mysql-test", "mysql-test", "sql-bench", "data" };
        static bool IsTrimmed(string rel)
        {
            if (rel.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) return true;
            if (rel.EndsWith(".pl", StringComparison.OrdinalIgnoreCase)) return true;
            if (rel.EndsWith(".lib", StringComparison.OrdinalIgnoreCase)) return true;
            return TrimDirs.Any(d => rel.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase));
        }

        static bool SizeReport(string folder)
        {
            var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
                .Select(f => new { Rel = f.Substring(folder.Length + 1), Len = new FileInfo(f).Length }).ToList();
            long full = files.Sum(f => f.Len);
            var keep = files.Where(f => !IsTrimmed(f.Rel)).ToList();
            long trimmed = keep.Sum(f => f.Len);

            long zipped = 0;
            var tmp = Path.Combine(Path.GetTempPath(), "lumina-size-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var z = ZipFile.Open(tmp, ZipArchiveMode.Create))
                    foreach (var f in keep) z.CreateEntryFromFile(Path.Combine(folder, f.Rel), f.Rel, CompressionLevel.Optimal);
                zipped = new FileInfo(tmp).Length;
            }
            finally { try { File.Delete(tmp); } catch { } }

            H3("Size");
            Kv("Extracted, as downloaded", Mb(full));
            Kv("After removing debug symbols (.pdb), headers, .lib, perl scripts", Mb(trimmed));
            Kv("Trimmed and zipped (≈ download size)", Mb(zipped));
            return true;
        }

        // ------------------------------------------------------------------ Win7 / VC++
        static bool CompatibilityReport(string server, string bin)
        {
            H3("Will it start on Windows 7? (read from the binaries, no Windows 7 PC needed)");
            var localDlls = new HashSet<string>(Directory.GetFiles(bin, "*.dll").Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);

            // The server plus every DLL in bin it loads at startup, recursively.
            var toScan = new Queue<string>(new[] { server });
            var scanned = new Dictionary<string, PeInfo>(StringComparer.OrdinalIgnoreCase);
            while (toScan.Count > 0)
            {
                var p = toScan.Dequeue();
                if (scanned.ContainsKey(Path.GetFileName(p))) continue;
                PeInfo pe;
                try { pe = PeInfo.Read(p); } catch (Exception ex) { Line($"- could not read {Path.GetFileName(p)}: {ex.Message}"); continue; }
                scanned[Path.GetFileName(p)] = pe;
                foreach (var dll in pe.Imports.Select(i => i.Key).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (localDlls.Contains(dll) && !dll.StartsWith("api-ms-win-", StringComparison.OrdinalIgnoreCase))
                        toScan.Enqueue(Path.Combine(bin, dll));
            }

            var srv = scanned[Path.GetFileName(server)];
            Kv("Server binary", $"{Path.GetFileName(server)} (header says it needs Windows {srv.SubsystemVersion} or newer; 6.1 = Windows 7, 6.2 = Windows 8, 10.0 = Windows 10)");

            var blockers = new List<string>();
            var warnings = new List<string>();
            foreach (var kv in scanned)
            {
                var pe = kv.Value;
                if (pe.SubsystemVersion > new Version(6, 1))
                    blockers.Add($"{kv.Key}: PE header requires Windows {pe.SubsystemVersion} (Windows 7 is 6.1)");
                foreach (var imp in pe.Imports)
                {
                    if (Win7Check.NewerThanWin7.TryGetValue(imp.Value, out var since))
                        blockers.Add($"{kv.Key} imports {imp.Value} from {imp.Key} (only exists on {since}+)");
                    else if (Win7Check.DllsNewerThanWin7.Contains(imp.Key) && !localDlls.Contains(imp.Key))
                        blockers.Add($"{kv.Key} needs {imp.Key}, which Windows 7 does not have and the engine does not ship");
                }
                foreach (var imp in pe.DelayImports)
                    if (Win7Check.NewerThanWin7.TryGetValue(imp.Value, out var since))
                        warnings.Add($"{kv.Key} delay-loads {imp.Value} ({since}+). Only fails if that code path runs.");
            }
            blockers = blockers.Distinct().ToList();

            Kv("Files checked", string.Join(", ", scanned.Keys));
            if (blockers.Count == 0) Line("- **Windows 7: no blockers found** in the server's startup imports.");
            else
            {
                Line($"- **Windows 7: WILL NOT START** ({blockers.Count} blocker{(blockers.Count == 1 ? "" : "s")}):");
                foreach (var x in blockers.Take(12)) Line($"  - {x}");
                if (blockers.Count > 12) Line($"  - … and {blockers.Count - 12} more");
            }
            foreach (var w in warnings.Distinct().Take(6)) Line($"  - note: {w}");

            H3("Does it need anything installed? (VC++ runtime)");
            var vcDlls = scanned.Values.SelectMany(p => p.Imports.Select(i => i.Key))
                .Where(d => d.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase) || d.StartsWith("msvcp", StringComparison.OrdinalIgnoreCase)
                         || d.StartsWith("concrt", StringComparison.OrdinalIgnoreCase) || d.Equals("ucrtbase.dll", StringComparison.OrdinalIgnoreCase)
                         || d.StartsWith("api-ms-win-crt-", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d).ToList();
            var missing = vcDlls.Where(d => !localDlls.Contains(d)).ToList();
            var vcMissing = missing.Where(d => !d.StartsWith("api-ms-win-crt-", StringComparison.OrdinalIgnoreCase) && !d.Equals("ucrtbase.dll", StringComparison.OrdinalIgnoreCase)).ToList();
            var ucrtMissing = missing.Except(vcMissing).ToList();
            Line(vcMissing.Count == 0
                ? "- VC++ runtime: **shipped inside the engine** (vcruntime/msvcp found in bin). Users install nothing."
                : $"- VC++ runtime: **NOT shipped**: {string.Join(", ", vcMissing)}. Users would need the VC++ redistributable.");
            Line(ucrtMissing.Count == 0
                ? "- Universal C Runtime: shipped inside the engine (works even on Windows 7 without updates)."
                : "- Universal C Runtime: not shipped. Built into Windows 10/11; Windows 7/8.1 need update KB2999226 (or we copy ucrtbase.dll + api-ms-win-crt-*.dll next to the server).");
            return true;
        }

        // ------------------------------------------------------------------ lifecycle
        static bool Lifecycle(string name, string server, string installer)
        {
            H3("Run test (no installer, no service, normal user)");
            var root = Path.Combine(BaseDir, "run", name);
            var data = Path.Combine(root, "data");
            var log = Path.Combine(root, "server.log");
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (Exception ex) { Fail($"Could not clean old test folder {root}: {ex.Message}"); return false; }
            Directory.CreateDirectory(root);
            bool ok = true;

            // 1. simulate Skype/another MySQL holding 3306, then pick a port
            TcpListener blocker = null;
            if (!PortInUse(3306))
            {
                try { blocker = new TcpListener(IPAddress.Loopback, 3306); blocker.Start(); } catch { blocker = null; }
            }
            int port = PickPort(3306);
            Step(port != 3306, $"Port pick: 3306 is busy{(blocker != null ? " (simulated)" : "")} → chose {port}");
            blocker?.Stop();

            // 2. create the data folder
            var sw = Stopwatch.StartNew();
            var init = RunTool(installer, $"--datadir=\"{data}\" --port={port}", 180_000, out string initOut);
            Step(init == 0 && Directory.Exists(data), $"Create data folder with {Path.GetFileName(installer)}: exit {init}, {sw.Elapsed.TotalSeconds:0.0}s");
            if (init != 0) { Line("  ```\n  " + Tail(initOut, 15).Replace("\n", "\n  ") + "\n  ```"); return false; }

            // 3. start, query, clean shutdown
            Process p = StartServer(server, data, port, log);
            sw.Restart();
            ok &= Step(WaitReady(port, 90, p, out string ver), $"Start server: ready in {sw.Elapsed.TotalSeconds:0.0}s → {ver}");
            if (!ok) { DumpLog(log); return false; }
            ok &= Step(!PortListeningOn(port, IPAddress.Any), "Listens on 127.0.0.1 only (no firewall prompt, no outside access)");
            p.Refresh();
            Kv("  Server memory after start", Mb(SafeWorkingSet(p)));

            try
            {
                using (var c = Open(port))
                {
                    Exec(c, "CREATE DATABASE spike");
                    Exec(c, "CREATE TABLE spike.students (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(60) NOT NULL, gpa DECIMAL(3,2)) ENGINE=InnoDB");
                    sw.Restart();
                    using (var tx = c.BeginTransaction())
                    {
                        var cmd = new MySqlCommand("INSERT INTO spike.students (name, gpa) VALUES (@n, @g)", c, tx);
                        cmd.Parameters.Add("@n", MySqlDbType.VarChar); cmd.Parameters.Add("@g", MySqlDbType.Decimal);
                        for (int i = 0; i < 1000; i++) { cmd.Parameters["@n"].Value = "Student " + i; cmd.Parameters["@g"].Value = 2 + (i % 200) / 100m; cmd.ExecuteNonQuery(); }
                        tx.Commit();
                    }
                    long n = Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM spike.students"));
                    ok &= Step(n == 1000, $"Create table, insert 1000 rows, read back: {n} rows in {sw.Elapsed.TotalMilliseconds:0} ms");
                    // MySQL-style features students use
                    Exec(c, "CREATE TABLE spike.courses (id INT AUTO_INCREMENT PRIMARY KEY, title VARCHAR(60)) ENGINE=InnoDB");
                    Exec(c, "ALTER TABLE spike.students ADD COLUMN course_id INT, ADD FOREIGN KEY (course_id) REFERENCES spike.courses(id)");
                    bool fkWorks = false;
                    try { Exec(c, "UPDATE spike.students SET course_id = 999 WHERE id = 1"); } catch (MySqlException ex) when (ex.Number == 1452) { fkWorks = true; }
                    ok &= Step(fkWorks, "Foreign keys enforced (error 1452 on a bad course_id)");
                }
            }
            catch (Exception ex) { ok = Fail($"Query step failed: {ex.Message}"); }

            sw.Restart();
            ok &= Step(CleanShutdown(port, p, 60), $"Clean shutdown with SHUTDOWN command: {sw.Elapsed.TotalSeconds:0.0}s");

            // 4. crash test: committed rows must survive a hard kill (= power cut)
            p = StartServer(server, data, port, log);
            if (!WaitReady(port, 90, p, out _)) { DumpLog(log); return Fail("Server did not restart after clean shutdown."); }
            try
            {
                using (var c = Open(port)) for (int i = 0; i < 500; i++) Exec(c, $"INSERT INTO spike.students (name, gpa) VALUES ('After {i}', 3.00)");
            }
            catch (Exception ex) { return Fail($"Insert before crash failed: {ex.Message}"); }
            long logBefore = new FileInfo(log).Length;
            p.Kill(); p.WaitForExit(15000);
            Step(true, "Hard kill of the server (simulated power cut) after 500 committed inserts");

            sw.Restart();
            p = StartServer(server, data, port, log);
            bool back = WaitReady(port, 120, p, out _);
            ok &= Step(back, $"Restart after crash: {(back ? $"ready in {sw.Elapsed.TotalSeconds:0.0}s" : "FAILED")}");
            if (back)
            {
                try
                {
                    using (var c = Open(port))
                    {
                        long n = Convert.ToInt64(Scalar(c, "SELECT COUNT(*) FROM spike.students"));
                        ok &= Step(n == 1500, $"No committed data lost: {n}/1500 rows");
                        using (var r = new MySqlCommand("CHECK TABLE spike.students", c).ExecuteReader())
                        {
                            string status = "";
                            while (r.Read()) status = r.GetString(3);
                            ok &= Step(status.Equals("OK", StringComparison.OrdinalIgnoreCase), $"CHECK TABLE after crash: {status}");
                        }
                    }
                }
                catch (Exception ex) { ok = Fail($"Verify after crash failed: {ex.Message}"); }
                string after = ReadFrom(log, logBefore);
                bool recovered = after.IndexOf("recovery", StringComparison.OrdinalIgnoreCase) >= 0 || after.IndexOf("Starting crash", StringComparison.OrdinalIgnoreCase) >= 0;
                Kv("  Server log mentions recovery", recovered ? "yes (InnoDB replayed its log, as designed)" : "no (nothing needed replaying)");
                ok &= Step(CleanShutdown(port, p, 60), "Final clean shutdown");
            }
            else DumpLog(log);

            try { Directory.Delete(root, true); } catch { Line($"  (test data left in {root})"); }
            return ok;
        }

        // ------------------------------------------------------------------ server helpers
        static Process StartServer(string server, string data, int port, string log)
        {
            var args = $"--defaults-file=\"{Path.Combine(data, "my.ini")}\" --datadir=\"{data}\" --port={port} --bind-address=127.0.0.1 --console --skip-name-resolve";
            var psi = new ProcessStartInfo(server, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                WorkingDirectory = Path.GetDirectoryName(server)
            };
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var writer = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            object gate = new object();
            DataReceivedEventHandler h = (s, e) => { if (e.Data != null) lock (gate) writer.WriteLine(e.Data); };
            p.OutputDataReceived += h; p.ErrorDataReceived += h;
            p.Exited += (s, e) => { Thread.Sleep(300); lock (gate) writer.Dispose(); };
            p.Start();
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            Started.Add(p);
            return p;
        }

        static bool WaitReady(int port, int seconds, Process p, out string version)
        {
            version = "";
            var until = DateTime.Now.AddSeconds(seconds);
            while (DateTime.Now < until)
            {
                if (p.HasExited) { version = $"server exited with code {p.ExitCode}"; return false; }
                try
                {
                    using (var c = Open(port, 2)) { version = Scalar(c, "SELECT VERSION()")?.ToString(); return true; }
                }
                catch { Thread.Sleep(250); }
            }
            version = "timed out";
            return false;
        }

        static bool CleanShutdown(int port, Process p, int seconds)
        {
            try { using (var c = Open(port)) Exec(c, "SHUTDOWN"); }
            catch (MySqlException) { /* connection drops while shutting down: expected */ }
            catch (Exception) { return false; }
            return p.WaitForExit(seconds * 1000) && p.ExitCode == 0;
        }

        static MySqlConnection Open(int port, int timeout = 10)
        {
            var cs = new MySqlConnectionStringBuilder
            {
                Server = "127.0.0.1", Port = (uint)port, UserID = "root", Password = "",
                ConnectionTimeout = (uint)timeout, Pooling = false, SslMode = MySqlSslMode.None, AllowPublicKeyRetrieval = true
            };
            var c = new MySqlConnection(cs.ConnectionString);
            c.Open();
            return c;
        }
        static void Exec(MySqlConnection c, string sql) { using (var cmd = new MySqlCommand(sql, c)) cmd.ExecuteNonQuery(); }
        static object Scalar(MySqlConnection c, string sql) { using (var cmd = new MySqlCommand(sql, c)) return cmd.ExecuteScalar(); }

        static int RunTool(string exe, string args, int timeoutMs, out string output)
        {
            var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = Path.GetDirectoryName(exe) };
            using (var p = Process.Start(psi))
            {
                var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } output = "timed out"; return -1; }
                output = o.Result + e.Result;
                return p.ExitCode;
            }
        }

        static int PickPort(int preferred)
        {
            for (int p = preferred; p < preferred + 50; p++) if (!PortInUse(p)) return p;
            var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port;
        }
        static bool PortInUse(int port) => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
        static bool PortListeningOn(int port, IPAddress addr) => IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port && e.Address.Equals(addr));

        static string PortOwner(int port)
        {
            try
            {
                var o = RunTool("netstat.exe", "-ano -p TCP", 10000, out string text);
                var line = text.Split('\n').FirstOrDefault(l => l.Contains($":{port} ") && l.Contains("LISTENING"));
                var pid = line?.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (int.TryParse(pid, out var id)) return $"{Process.GetProcessById(id).ProcessName}.exe (PID {id})";
            }
            catch { }
            return "another program";
        }

        static long SafeWorkingSet(Process p) { try { return p.WorkingSet64; } catch { return 0; } }

        static void KillLeftovers()
        {
            foreach (var p in Started) { try { if (!p.HasExited) p.Kill(); } catch { } }
            Started.Clear();
        }

        static void DumpLog(string log)
        {
            try { Line("  Last server log lines:\n  ```\n  " + Tail(File.ReadAllText(log), 20).Replace("\n", "\n  ") + "\n  ```"); } catch { }
        }
        static string ReadFrom(string path, long offset)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                { fs.Seek(offset, SeekOrigin.Begin); return new StreamReader(fs).ReadToEnd(); }
            }
            catch { return ""; }
        }
        static string Tail(string s, int lines) => string.Join("\n", (s ?? "").Replace("\r", "").Split('\n').Where(l => l.Trim().Length > 0).Reverse().Take(lines).Reverse());

        // ------------------------------------------------------------------ report
        static string Reg(string key, string val) { try { return Registry.LocalMachine.OpenSubKey(key)?.GetValue(val)?.ToString(); } catch { return null; } }
        static string Mb(long b) => $"{b / 1048576.0:0.0} MB";
        static void H1(string t) { Line("# " + t); Line(""); }
        static void H2(string t) { Line(""); Line("## " + t); }
        static void H3(string t) { Line(""); Line("### " + t); }
        static void Kv(string k, string v) => Line($"- {k}: {v}");
        static bool Step(bool ok, string text) { Line($"- {(ok ? "PASS" : "FAIL")} — {text}"); return ok; }
        static bool Fail(string text) { Line($"- FAIL — {text}"); return false; }
        static void Line(string t)
        {
            Report.AppendLine(t);
            var color = Console.ForegroundColor;
            if (t.Contains("FAIL") || t.Contains("WILL NOT START")) Console.ForegroundColor = ConsoleColor.Red;
            else if (t.Contains("PASS")) Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(t);
            Console.ForegroundColor = color;
        }
        static int Save(int code)
        {
            var file = Path.Combine(BaseDir, $"report-{Environment.MachineName}-{DateTime.Now:yyyyMMdd-HHmm}.md");
            try { File.WriteAllText(file, Report.ToString(), new UTF8Encoding(false)); Console.WriteLine($"\nReport saved: {file}"); }
            catch (Exception ex) { Console.WriteLine($"\nCould not save report: {ex.Message}"); }
            return code;
        }
    }
}
