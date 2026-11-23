using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using LuminaDB.Core;
using MySqlConnector;
using Xunit;
using Xunit.Abstractions;

namespace LuminaDB.Core.Tests
{
    [CollectionDefinition("RealMariaDb", DisableParallelization = true)]
    public class RealMariaDbCollection { }

    /// <summary>
    /// Runs the real MariaDB 11.8 (Modern set) and 10.4 (Legacy set) engines from EngineSpike\engines.
    /// Each test gets its own app folder (with a space in the path, like "C:\Users\Lab User\...").
    /// </summary>
    [Collection("RealMariaDb")]
    public class MariaDbServiceIntegrationTests
    {
        private readonly ITestOutputHelper _out;
        public MariaDbServiceIntegrationTests(ITestOutputHelper output) { _out = output; }

        public static IEnumerable<object[]> Engines => new[]
        {
            new object[] { "mariadb-11.8.10-winx64", EngineSetKind.Modern, "11.8.10" },
            new object[] { "mariadb-10.4.34-winx64", EngineSetKind.Legacy, "10.4.34" },
        };

        internal sealed class Rig : IDisposable
        {
            public AppPaths Paths;
            public EngineSet Engine;
            public readonly List<MariaDbService> Services = new List<MariaDbService>();

            public MariaDbService NewService(int preferredPort = 3406)
            {
                var s = new MariaDbService(Paths, Engine, null, new MariaDbServiceOptions { PreferredPort = preferredPort });
                Services.Add(s);
                return s;
            }

            public void Dispose()
            {
                foreach (var s in Services)
                {
                    try { if (s.State == ServerState.Running) s.StopAsync().GetAwaiter().GetResult(); } catch { }
                    s.Dispose();
                }
                // Make sure no server from this rig survives the test.
                foreach (var pid in ManagedProcess.FindRunning(Engine.ServerExe))
                    try { Process.GetProcessById(pid).Kill(); } catch { }
                MySqlConnection.ClearAllPools();
                for (int i = 0; i < 10 && Directory.Exists(Paths.Root); i++)
                {
                    try { Directory.Delete(Paths.Root, true); break; }
                    catch (IOException) { System.Threading.Thread.Sleep(500); }
                    catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(500); }
                }
            }
        }

        private Rig NewRig(string engineFolder, EngineSetKind kind) => NewRig(engineFolder, kind, _out);

        internal static Rig NewRig(string engineFolder, EngineSetKind kind, ITestOutputHelper output)
        {
            var _out = output;
            var engines = FindEnginesFolder();
            var dir = engines == null ? null : Path.Combine(engines, engineFolder);
            if (dir == null || !Directory.Exists(dir))
            {
                _out.WriteLine($"SKIPPED: engine {engineFolder} not found (set LUMINA_ENGINES).");
                return null;
            }
            var root = Path.Combine(Path.GetTempPath(), "Lumina Test " + Guid.NewGuid().ToString("N").Substring(0, 8));
            return new Rig { Paths = new AppPaths(root), Engine = EngineSet.FromMariaDbFolder(kind, dir) };
        }

        private static string FindEnginesFolder()
        {
            var env = Environment.GetEnvironmentVariable("LUMINA_ENGINES");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            for (var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "EngineSpike", "engines");
                if (Directory.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static async Task<object> ScalarAsync(MariaDbService s, string sql, string db = null)
        {
            using (var c = await s.OpenConnectionAsync(db))
            using (var cmd = new MySqlCommand(sql, c))
                return await cmd.ExecuteScalarAsync();
        }

        private static async Task ExecAsync(MariaDbService s, string sql, string db = null)
        {
            using (var c = await s.OpenConnectionAsync(db))
            using (var cmd = new MySqlCommand(sql, c))
                await cmd.ExecuteNonQueryAsync();
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task First_start_creates_data_folder_with_verified_settings_and_data_survives_restart(string folder, EngineSetKind kind, string version)
        {
            using (var rig = NewRig(folder, kind))
            {
                if (rig == null) return;
                var s = rig.NewService();
                var sw = Stopwatch.StartNew();
                await s.StartAsync();
                _out.WriteLine($"{folder}: first start (incl. init) {sw.Elapsed.TotalSeconds:0.0}s on port {s.Port}, {s.ServerVersion}");

                Assert.Equal(ServerState.Running, s.State);
                Assert.StartsWith(version, s.ServerVersion);
                Assert.True(File.Exists(Path.Combine(s.DataDir, DataFolderStamp.FileName)));
                Assert.Equal(rig.Paths.MariaDbData(kind), s.DataDir);

                // Bound to 127.0.0.1 only: no firewall prompt, nothing reachable from the network.
                var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Where(e => e.Port == s.Port).ToList();
                Assert.NotEmpty(listeners);
                Assert.All(listeners, e => Assert.True(IPAddress.IsLoopback(e.Address), e.ToString()));

                Assert.Equal("utf8mb4", await ScalarAsync(s, "SELECT @@character_set_server"));
                Assert.Equal("utf8mb4_unicode_ci", await ScalarAsync(s, "SELECT @@collation_server"));
                Assert.Contains("STRICT_TRANS_TABLES", (string)await ScalarAsync(s, "SELECT @@sql_mode"));
                Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(s, "SELECT @@local_infile")));
                Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(s, "SELECT @@lower_case_table_names")));
                Assert.Equal("ON", await ScalarAsync(s, "SELECT @@event_scheduler"));
                Assert.Equal(256L * 1024 * 1024, Convert.ToInt64(await ScalarAsync(s, "SELECT @@max_allowed_packet")));

                await ExecAsync(s, "CREATE DATABASE school");
                await ExecAsync(s, "CREATE TABLE students (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(60) NOT NULL)", "school");
                await ExecAsync(s, "INSERT INTO students (name) VALUES ('Nimal'), ('ශ්‍රී ලංකා'), ('emoji 🎓')", "school");

                await s.StopAsync();
                Assert.Equal(ServerState.Stopped, s.State);
                Assert.False(File.Exists(rig.Paths.MariaDbPidFile(kind)));
                Assert.Empty(ManagedProcess.FindRunning(rig.Engine.ServerExe));

                await s.StartAsync();
                Assert.Equal(3L, Convert.ToInt64(await ScalarAsync(s, "SELECT COUNT(*) FROM students", "school")));
                Assert.Equal("emoji 🎓", await ScalarAsync(s, "SELECT name FROM students WHERE id = 3", "school"));
                Assert.Equal("ශ්‍රී ලංකා", await ScalarAsync(s, "SELECT name FROM students WHERE id = 2", "school"));
                await s.StopAsync();
            }
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Busy_port_is_skipped_and_the_port_is_remembered(string folder, EngineSetKind kind, string version)
        {
            using (var rig = NewRig(folder, kind))
            {
                if (rig == null) return;
                var blocker = new TcpListener(IPAddress.Any, 0);   // "Skype" holding the port on 0.0.0.0
                blocker.Start();
                int busy = ((IPEndPoint)blocker.LocalEndpoint).Port;
                try
                {
                    var s = rig.NewService(busy);
                    await s.StartAsync();
                    Assert.NotEqual(busy, s.Port);
                    Assert.StartsWith(version, s.ServerVersion);
                    int chosen = s.Port;
                    await s.StopAsync();
                    Assert.Equal(chosen, LuminaState.Load(rig.Paths).GetMariaDbPort(kind));

                    await s.StartAsync();   // same port again so project URLs/connection strings stay valid
                    Assert.Equal(chosen, s.Port);
                    await s.StopAsync();
                }
                finally { blocker.Stop(); }
            }
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Reopened_app_adopts_the_running_server_instead_of_starting_another(string folder, EngineSetKind kind, string version)
        {
            using (var rig = NewRig(folder, kind))
            {
                if (rig == null) return;
                var first = rig.NewService();
                await first.StartAsync();
                int pid = first.ProcessId.Value, port = first.Port;
                first.Dispose();                  // the UI closed (or crashed) without stopping the server
                rig.Services.Remove(first);

                var second = rig.NewService();
                await second.StartAsync();
                Assert.Equal(pid, second.ProcessId);
                Assert.Equal(port, second.Port);
                Assert.StartsWith(version, second.ServerVersion);
                Assert.Single(ManagedProcess.FindRunning(rig.Engine.ServerExe));

                // Same again, but the PID file is gone too: still adopted via the remembered port.
                second.Dispose();
                rig.Services.Remove(second);
                File.Delete(rig.Paths.MariaDbPidFile(kind));
                var third = rig.NewService();
                await third.StartAsync();
                Assert.Equal(pid, third.ProcessId);
                await third.StopAsync();
                Assert.Empty(ManagedProcess.FindRunning(rig.Engine.ServerExe));
            }
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Crash_is_reported_and_committed_data_survives(string folder, EngineSetKind kind, string version)
        {
            using (var rig = NewRig(folder, kind))
            {
                if (rig == null) return;
                var s = rig.NewService();
                var crashed = new TaskCompletionSource<bool>();
                s.StateChanged += st => { if (st == ServerState.Crashed) crashed.TrySetResult(true); };
                await s.StartAsync();
                Assert.StartsWith(version, s.ServerVersion);
                await ExecAsync(s, "CREATE DATABASE shop");
                await ExecAsync(s, "CREATE TABLE orders (id INT AUTO_INCREMENT PRIMARY KEY, total DECIMAL(8,2)) ENGINE=InnoDB", "shop");
                for (int i = 0; i < 200; i++) await ExecAsync(s, $"INSERT INTO orders (total) VALUES ({i}.50)", "shop");

                Process.GetProcessById(s.ProcessId.Value).Kill();   // power cut / Task Manager
                Assert.True(await Task.WhenAny(crashed.Task, Task.Delay(15000)) == crashed.Task, "crash was not reported");
                Assert.Equal(ServerState.Crashed, s.State);

                await s.StartAsync();
                Assert.Equal(ServerState.Running, s.State);
                Assert.Equal(200L, Convert.ToInt64(await ScalarAsync(s, "SELECT COUNT(*) FROM orders", "shop")));
                using (var c = await s.OpenConnectionAsync("shop"))
                using (var r = await new MySqlCommand("CHECK TABLE orders", c).ExecuteReaderAsync())
                {
                    string status = "";
                    while (await r.ReadAsync()) status = r.GetString(3);
                    Assert.Equal("OK", status);
                }
                await s.StopAsync();
            }
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Root_password_is_set_on_every_root_account_and_remembered(string folder, EngineSetKind kind, string version)
        {
            using (var rig = NewRig(folder, kind))
            {
                if (rig == null) return;
                var s = rig.NewService();
                await s.StartAsync();
                Assert.StartsWith(version, s.ServerVersion);
                const string pwd = "Lab'Pc\\2026 ✓";
                await s.SetRootPasswordAsync(pwd);

                // Every root account now needs the password (no forgotten root@127.0.0.1 / root@::1 left open).
                using (var c = await s.OpenConnectionAsync())
                using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM mysql.user WHERE User='root' AND (authentication_string = '' OR authentication_string IS NULL) AND (Password = '' OR Password IS NULL)", c))
                    Assert.Equal(0L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));

                var empty = s.ConnectionString(password: "");
                empty.Pooling = false;
                var ex = await Assert.ThrowsAsync<MySqlException>(async () => { using (var c = new MySqlConnection(empty.ConnectionString)) await c.OpenAsync(); });
                Assert.Equal(MySqlErrorCode.AccessDenied, ex.ErrorCode);

                // The password is stored encrypted and used for stop/start without asking.
                Assert.True(RootCredentialStore.For(rig.Paths, kind).HasStoredPassword);
                Assert.DoesNotContain("Lab", File.ReadAllText(Path.Combine(rig.Paths.Config, "root-" + EngineSet.FolderName(kind) + ".secret")));
                await s.StopAsync();
                Assert.Empty(ManagedProcess.FindRunning(rig.Engine.ServerExe));   // SHUTDOWN worked with the stored password
                await s.StartAsync();
                Assert.False(s.NeedsPassword);
                Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(s, "SELECT 1")));

                // Lost secret (USB stick on another PC): server still starts, UI asks once, then works.
                File.Delete(Path.Combine(rig.Paths.Config, "root-" + EngineSet.FolderName(kind) + ".secret"));
                await s.RestartAsync();
                Assert.True(s.NeedsPassword);
                Assert.False(await s.UnlockAsync("wrong"));
                Assert.True(await s.UnlockAsync(pwd));
                Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(s, "SELECT 1")));
                await s.StopAsync();
            }
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Half_made_data_folder_is_moved_aside_not_deleted(string folder, EngineSetKind kind, string version)
        {
            using (var rig = NewRig(folder, kind))
            {
                if (rig == null) return;
                var data = rig.Paths.MariaDbData(kind);
                Directory.CreateDirectory(data);
                File.WriteAllText(Path.Combine(data, "leftover.txt"), "from an interrupted first start");

                var s = rig.NewService();
                await s.StartAsync();
                Assert.StartsWith(version, s.ServerVersion);
                Assert.Equal(ServerState.Running, s.State);
                var aside = Directory.GetDirectories(rig.Paths.Data, "mariadb-" + EngineSet.FolderName(kind) + ".broken-*").Single();
                Assert.True(File.Exists(Path.Combine(aside, "leftover.txt")));
                await s.StopAsync();
            }
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Today_data_databases_create_activity_and_checks(string folder, EngineSetKind kind, string version)
        {
            using (var rig = NewRig(folder, kind))
            {
                if (rig == null) return;
                var s = rig.NewService();
                await s.StartAsync();
                Assert.StartsWith(version, s.ServerVersion);
                // System schemas are hidden; the install creates an empty "test" database (like XAMPP).
                Assert.Equal(new[] { "test" }, (await DatabaseCatalog.ListAsync(s)).Select(d => d.Name).ToArray());

                await DatabaseCatalog.CreateAsync(s, "college");
                await ExecAsync(s, "CREATE TABLE students (id INT PRIMARY KEY, name VARCHAR(40))", "college");
                await ExecAsync(s, "INSERT INTO students VALUES (1,'a'),(2,'b')", "college");
                await DatabaseCatalog.CreateAsync(s, "shop");
                var dup = await Assert.ThrowsAsync<LuminaException>(() => DatabaseCatalog.CreateAsync(s, "college"));
                Assert.Equal(ErrorKind.InvalidInput, dup.Kind);
                await Assert.ThrowsAsync<LuminaException>(() => DatabaseCatalog.CreateAsync(s, "bad name"));

                var dbs = await DatabaseCatalog.ListAsync(s);
                Assert.Equal(new[] { "college", "shop", "test" }, dbs.Select(d => d.Name).ToArray());
                Assert.Equal(1, dbs[0].Tables);
                Assert.True(dbs[0].Bytes > 0);
                Assert.Equal(0, dbs[1].Tables);
                Assert.Equal("utf8mb4", await ScalarAsync(s, "SELECT DEFAULT_CHARACTER_SET_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME='shop'"));

                var meter = new ActivityMeter();
                Assert.Null(await meter.SampleAsync(s));   // first sample has nothing to compare with
                for (int i = 0; i < 20; i++) await ScalarAsync(s, "SELECT 1");
                var qps = await meter.SampleAsync(s);
                Assert.True(qps.HasValue && qps.Value > 0, "qps=" + qps);

                var checks = StartupChecks.Run(rig.Paths, WindowsInfo.Current(), rig.Engine, s, 3406);
                Assert.Contains(checks, c => c.Id == "firewall" && c.Severity == CheckSeverity.Ok);
                Assert.Contains(checks, c => c.Id == "engine" && c.Severity == CheckSeverity.Ok && c.Text.Contains(version));
                Assert.Contains(checks, c => c.Id == "write" && c.Severity == CheckSeverity.Ok);
                await s.StopAsync();
            }
        }

        [Fact]
        public async Task Data_folder_from_another_engine_set_is_refused()
        {
            using (var rig = NewRig("mariadb-11.8.10-winx64", EngineSetKind.Modern))
            {
                if (rig == null) return;
                var data = rig.Paths.MariaDbData(EngineSetKind.Modern);
                Directory.CreateDirectory(Path.Combine(data, "mysql"));
                File.WriteAllText(Path.Combine(data, DataFolderStamp.FileName), "{\"CreatedByVersion\":\"10.4.34\",\"CreatedUtcTicks\":0,\"EngineSet\":\"legacy\"}");

                var s = rig.NewService();
                var ex = await Assert.ThrowsAsync<LuminaException>(() => s.StartAsync());
                Assert.Equal(ErrorKind.DataFolderBelongsToOtherEngine, ex.Kind);
                Assert.Equal(ServerState.Stopped, s.State);
            }
        }

        [Fact]
        public void Engine_versions_are_read_from_the_exe()
        {
            using (var modern = NewRig("mariadb-11.8.10-winx64", EngineSetKind.Modern))
            using (var legacy = NewRig("mariadb-10.4.34-winx64", EngineSetKind.Legacy))
            {
                if (modern == null || legacy == null) return;
                Assert.Equal(new Version(11, 8, 10), modern.Engine.ServerVersion);
                Assert.True(modern.Engine.SupportsCharacterSetCollations);
                Assert.Equal(new Version(10, 4, 34), legacy.Engine.ServerVersion);
                Assert.False(legacy.Engine.SupportsCharacterSetCollations);
                Assert.EndsWith("mysqld.exe", legacy.Engine.ServerExe);
                Assert.EndsWith("mariadbd.exe", modern.Engine.ServerExe);
            }
        }
    }

    public class SmallHelperTests
    {
        [Theory]
        [InlineData("5.5.5-10.4.34-MariaDB", "10.4.34-MariaDB")]
        [InlineData("11.8.10-MariaDB", "11.8.10-MariaDB")]
        [InlineData("", "")]
        public void Strips_compat_prefix(string raw, string expected) => Assert.Equal(expected, MariaDbService.StripCompatPrefix(raw));

        [Theory]
        [InlineData("plain", "plain")]
        [InlineData(@"C:\Lumina DB\x", "\"C:\\Lumina DB\\x\"")]
        [InlineData(@"--datadir=C:\a b\", "\"--datadir=C:\\a b\\\\\"")]
        [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
        [InlineData("", "\"\"")]
        public void Quotes_command_line_args(string arg, string expected) => Assert.Equal(expected, ManagedProcess.Quote(arg));

        [Fact]
        public void Sql_literal_escapes_quotes_and_backslashes() => Assert.Equal("'a''b\\\\c'", MariaDbService.SqlLiteral("a'b\\c"));

        [Fact]
        public void Credential_store_round_trips_and_empty_means_no_file()
        {
            var path = Path.Combine(Path.GetTempPath(), "lumina-secret-" + Guid.NewGuid().ToString("N"), "root.secret");
            try
            {
                var store = new RootCredentialStore(path);
                Assert.Equal("", store.Load());
                store.Save("pässwörd ✓");
                Assert.Equal("pässwörd ✓", store.Load());
                File.WriteAllBytes(path, new byte[] { 1, 2, 3 });   // from another PC / corrupted
                Assert.Null(store.Load());
                store.Save("");
                Assert.False(store.HasStoredPassword);
            }
            finally { try { Directory.Delete(Path.GetDirectoryName(path), true); } catch { } }
        }

        [Fact]
        public void State_file_survives_corruption()
        {
            var p = new AppPaths(Path.Combine(Path.GetTempPath(), "lumina-state-" + Guid.NewGuid().ToString("N")));
            try
            {
                var s = new LuminaState();
                s.SetMariaDbPort(EngineSetKind.Legacy, 3310);
                s.Save(p);
                Assert.Equal(3310, LuminaState.Load(p).GetMariaDbPort(EngineSetKind.Legacy));
                Assert.Null(LuminaState.Load(p).GetMariaDbPort(EngineSetKind.Modern));
                File.WriteAllText(p.StateFile, "{not json");
                Assert.Null(LuminaState.Load(p).GetMariaDbPort(EngineSetKind.Legacy));
            }
            finally { try { Directory.Delete(p.Root, true); } catch { } }
        }
    }
}
