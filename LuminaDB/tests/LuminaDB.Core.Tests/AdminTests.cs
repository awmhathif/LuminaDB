using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using LuminaDB.Core.Admin;
using LuminaDB.Core.Web;
using Microsoft.Win32;
using MySqlConnector;
using Xunit;
using Xunit.Abstractions;

namespace LuminaDB.Core.Tests
{
    /// <summary>Step 9 pieces that need no engine: SQL for accounts, value checks, overrides, settings, phpMyAdmin config.</summary>
    public class AdminUnitTests : IDisposable
    {
        private readonly string _root;
        private readonly AppPaths _paths;

        public AdminUnitTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "Lumina Admin " + Guid.NewGuid().ToString("N").Substring(0, 8));
            _paths = new AppPaths(_root);
            _paths.EnsureCreated();
        }

        public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

        private static Account User(string u, string h = "localhost") => new Account { User = u, Host = h };

        [Fact]
        public void Account_sql_quotes_names_and_marks_system_accounts()
        {
            Assert.Equal("'o''brien'@'localhost'", User("o'brien").Sql);
            Assert.Equal("'a\\\\b'@'%'", User("a\\b", "%").Sql);
            Assert.Equal("'teachers'", new Account { User = "teachers", IsRole = true }.Sql);
            Assert.True(User("root").IsSystem);
            Assert.True(User("mariadb.sys").IsSystem);
            Assert.True(new Account { User = "PUBLIC", IsRole = true }.IsSystem);
            Assert.False(User("shop").IsSystem);
        }

        [Fact]
        public void Names_and_passwords_are_checked_in_plain_words()
        {
            var existing = new List<Account> { User("root"), User("shop"), new Account { User = "staff", IsRole = true } };
            Assert.Null(Accounts.CheckName("shop", existing, "%"));
            Assert.Contains("already", Accounts.CheckName("shop", existing, "localhost"));
            Assert.Contains("MariaDB itself", Accounts.CheckName("root", existing, "%"));
            Assert.Contains("MariaDB itself", Accounts.CheckName("mysql.session", existing, "localhost"));
            Assert.Contains("space", Accounts.CheckName(" shop2", existing, "localhost"));
            Assert.Contains("longer", Accounts.CheckName(new string('x', 81), existing, "localhost"));
            Assert.Contains("role", Accounts.CheckName("staff", existing, "localhost"));
            Assert.Contains("already", Accounts.CheckName("staff", existing, null, role: true));
            Assert.Null(Accounts.CheckPassword("", allowEmpty: true));
            Assert.NotNull(Accounts.CheckPassword("", allowEmpty: false));
            Assert.Contains("spaces", Accounts.CheckPassword("   ", true));
            Assert.Null(Accounts.CheckPassword("s3cret", false));
        }

        [Fact]
        public void Diff_groups_grants_per_place_in_a_stable_order()
        {
            var a = User("shop_app");
            var before = new[]
            {
                new Grant(PrivilegeLevel.Database, "SELECT", "shop"),
                new Grant(PrivilegeLevel.Database, "DELETE", "shop"),
                new Grant(PrivilegeLevel.Global, "PROCESS"),
            };
            var after = new[]
            {
                new Grant(PrivilegeLevel.Database, "select", "SHOP"),   // same grant, other case
                new Grant(PrivilegeLevel.Database, "UPDATE", "shop"),
                new Grant(PrivilegeLevel.Database, "INSERT", "shop"),
                new Grant(PrivilegeLevel.Column, "UPDATE", "shop", "products", "price"),
                new Grant(PrivilegeLevel.Column, "UPDATE", "shop", "products", "name"),
                new Grant(PrivilegeLevel.Table, "SELECT", "shop", "orders"),
                new Grant(PrivilegeLevel.Global, "PROCESS"),
            };
            var sql = Accounts.Diff(a, before, after);
            Assert.Equal(new[]
            {
                "REVOKE DELETE ON `shop`.* FROM 'shop_app'@'localhost'",
                "GRANT INSERT, UPDATE ON `shop`.* TO 'shop_app'@'localhost'",
                "GRANT SELECT ON `shop`.`orders` TO 'shop_app'@'localhost'",
                "GRANT UPDATE (`name`, `price`) ON `shop`.`products` TO 'shop_app'@'localhost'",
            }, sql);
            Assert.Empty(Accounts.Diff(a, after, after));
        }

        [Fact]
        public void Passwords_never_appear_in_the_script()
        {
            var create = Accounts.CreateUser("shop_app", "localhost", withPassword: true);
            Assert.Equal("CREATE USER 'shop_app'@'localhost' IDENTIFIED BY @pwd", create[0]);
            var script = Accounts.Script(create.Concat(new[] { Accounts.SetPassword(User("shop_app"), clear: false) }));
            Assert.DoesNotContain("@pwd", script);
            Assert.Contains("'••••••'", script);
            Assert.Equal("ALTER USER 'x'@'%' IDENTIFIED BY ''", Accounts.SetPassword(User("x", "%"), clear: true));
            Assert.Equal("DROP ROLE 'r'", Accounts.Drop(new Account { User = "r", IsRole = true }));
            Assert.Equal("SET DEFAULT ROLE 'r' FOR 'x'@'%'", Accounts.DefaultRole("r", User("x", "%")));
        }

        private static ServerVariable Var(string name, string type, string min = null, string max = null, params string[] choices) =>
            new ServerVariable { Name = name, Type = type, Min = min, Max = max, Choices = choices.ToList(), Scope = "GLOBAL", InConfig = true };

        [Fact]
        public void Values_are_checked_against_the_variable_type()
        {
            Assert.Equal("ON", ServerMonitor.Normalize(Var("general_log", "BOOLEAN"), " on "));
            Assert.Equal("OFF", ServerMonitor.Normalize(Var("general_log", "BOOLEAN"), "0"));
            Assert.Throws<LuminaException>(() => ServerMonitor.Normalize(Var("general_log", "BOOLEAN"), "maybe"));
            var packet = Var("max_allowed_packet", "BIGINT UNSIGNED", "1024", "1073741824");
            Assert.Equal("67108864", ServerMonitor.Normalize(packet, "64M"));
            Assert.Equal("1073741824", ServerMonitor.Normalize(packet, "1G"));
            Assert.Contains("more than", Assert.Throws<LuminaException>(() => ServerMonitor.Normalize(packet, "2G")).Message);
            Assert.Contains("less than", Assert.Throws<LuminaException>(() => ServerMonitor.Normalize(packet, "10")).Message);
            Assert.Throws<LuminaException>(() => ServerMonitor.Normalize(packet, "lots"));
            Assert.Equal("2.5", ServerMonitor.Normalize(Var("long_query_time", "DOUBLE", "0", "31536000"), "2.5"));
            Assert.Equal("ROW", ServerMonitor.Normalize(Var("binlog_format", "ENUM", null, null, "MIXED", "STATEMENT", "ROW"), "row"));
            Assert.Equal("STRICT_TRANS_TABLES,NO_ZERO_DATE",
                ServerMonitor.Normalize(Var("sql_mode", "SET", null, null, "STRICT_TRANS_TABLES", "NO_ZERO_DATE", "ANSI_QUOTES"), "strict_trans_tables, no_zero_date"));
            Assert.Throws<LuminaException>(() => ServerMonitor.Normalize(Var("sql_mode", "SET", null, null, "ANSI_QUOTES"), "BOGUS"));

            Assert.Equal("SET GLOBAL max_allowed_packet = 67108864", ServerMonitor.SetGlobalSql(packet, "67108864"));
            Assert.Equal("SET GLOBAL time_zone = '+05:30'", ServerMonitor.SetGlobalSql(Var("time_zone", "VARCHAR"), "+05:30"));
            var ro = Var("version", "VARCHAR"); ro.ReadOnly = true;
            Assert.Throws<LuminaException>(() => ServerMonitor.SetGlobalSql(ro, "x"));
        }

        [Fact]
        public void Status_numbers_and_qps()
        {
            var t0 = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            var a = ServerMonitor.FromStatus(new Dictionary<string, string> { ["Uptime"] = "100", ["Questions"] = "500", ["Innodb_buffer_pool_pages_total"] = "1000", ["Innodb_buffer_pool_pages_free"] = "250",
                                                                             ["Innodb_buffer_pool_read_requests"] = "1000", ["Innodb_buffer_pool_reads"] = "10" }, t0);
            Assert.Equal(5, a.QueriesPerSecond(null), 3);
            Assert.Equal(0.75, a.BufferPoolUsed, 3);
            Assert.Equal(0.99, a.HitRatio, 3);
            var b = ServerMonitor.FromStatus(new Dictionary<string, string> { ["Uptime"] = "102", ["Questions"] = "540" }, t0.AddSeconds(2));
            Assert.Equal(20, b.QueriesPerSecond(a), 3);
            Assert.Equal("1.5 KB", ServerStatus.Bytes(1536));
            Assert.Equal("2 h 5 min", ServerStatus.Duration(TimeSpan.FromMinutes(125)));
            Assert.Equal("localhost", new ServerConnection { Host = "localhost:52011" }.Client);
            Assert.False(new ServerConnection { User = "system user" }.CanKill);
            Assert.Throws<LuminaException>(() => ServerMonitor.KillSql(new ServerConnection { Id = 5, IsOurs = true }, true));
            Assert.Equal("KILL QUERY 7", ServerMonitor.KillSql(new ServerConnection { Id = 7, User = "root", Command = "Query" }, true));
        }

        [Fact]
        public void Overrides_refuse_what_LuminaDB_controls_and_write_safe_ini_lines()
        {
            Assert.NotNull(ServerOverrides.CheckKeep(Var("port", "INT")));
            Assert.NotNull(ServerOverrides.CheckKeep(Var("secure_file_priv", "VARCHAR")));
            var notInConfig = Var("timestamp", "DOUBLE"); notInConfig.InConfig = false;
            Assert.NotNull(ServerOverrides.CheckKeep(notInConfig));
            Assert.Null(ServerOverrides.CheckKeep(Var("max_connections", "INT")));

            var o = new ServerOverrides();
            o.Set("max_connections", "77");
            o.Set("sql_mode", "STRICT_TRANS_TABLES,NO_ZERO_DATE");
            o.Set("max-connections", "88");   // same setting, other spelling: replaced
            o.Save(_paths, EngineSetKind.Modern);
            // A hand-edited file can't add a forbidden or broken line.
            var file = ServerOverrides.FileFor(_paths, EngineSetKind.Modern);
            File.WriteAllText(file, File.ReadAllText(file).Replace("]}", ",{\"Name\":\"datadir\",\"Value\":\"C:/x\"},{\"Name\":\"evil\",\"Value\":\"1\\nskip-grant-tables\"}]}"));
            var back = ServerOverrides.Load(_paths, EngineSetKind.Modern);
            Assert.Equal(new[] { "max_connections=88", "sql_mode=STRICT_TRANS_TABLES,NO_ZERO_DATE" }, back.IniLines().Select(kv => kv.Key + "=" + kv.Value));

            var ini = MariaDbConfigWriter.Render(new MariaDbConfig
            {
                DataDir = @"C:\d", Port = 3306, SecureFilePriv = @"C:\e", PluginDir = @"C:\p", TempDir = @"C:\t",
                SlowLogFile = @"C:\l\slow.log", GeneralLogFile = @"C:\l\general.log", Overrides = back.IniLines().ToList(),
            });
            Assert.Contains("slow_query_log_file=\"C:/l/slow.log\"", ini);
            var mysqld = ini.Substring(0, ini.IndexOf("[client]", StringComparison.Ordinal));
            Assert.EndsWith("max_connections=88\r\nsql_mode=STRICT_TRANS_TABLES,NO_ZERO_DATE\r\n\r\n", mysqld);

            var aside = ServerOverrides.SetAside(_paths, EngineSetKind.Modern);
            Assert.True(File.Exists(aside));
            Assert.Empty(ServerOverrides.Load(_paths, EngineSetKind.Modern).Items);
        }

        [Fact]
        public void Settings_load_with_safe_defaults()
        {
            File.WriteAllText(AppSettings.FileFor(_paths), "{\"DatabasePort\":12,\"BackupMinutes\":7,\"TextSize\":3}");
            var s = AppSettings.Load(_paths);
            Assert.Equal(3306, s.DatabasePort);
            Assert.Equal(30, s.BackupMinutes);
            Assert.Equal(1.0, s.TextSize);
            s.DatabasePort = 3320; s.BackupMinutes = 0; s.TextSize = 1.25;
            s.Save(_paths);
            var t = AppSettings.Load(_paths);
            Assert.Equal(3320, t.DatabasePort);
            Assert.Equal(0, t.BackupMinutes);
            Assert.Equal(1.25, t.TextSize);
            Assert.NotNull(AppSettings.CheckPort(80));
            Assert.NotNull(AppSettings.CheckPort(8080, new[] { 8080 }));
            Assert.Null(AppSettings.CheckPort(3320));
        }

        [Fact]
        public void Data_folder_choice_is_checked_and_remembered()
        {
            Assert.Contains("now", DataFolderMover.Check(_paths, _paths.Data));
            Assert.Contains("inside", DataFolderMover.Check(_paths, Path.Combine(_paths.Data, "sub")));
            Assert.Contains("outside", DataFolderMover.Check(_paths, Path.Combine(_paths.Backups, "x")));
            Assert.Contains("drive", DataFolderMover.Check(_paths, "C:\\"));
            var busy = Path.Combine(_root, "busy");
            Directory.CreateDirectory(busy);
            File.WriteAllText(Path.Combine(busy, "a.txt"), "x");
            Assert.Contains("isn't empty", DataFolderMover.Check(_paths, busy));
            Assert.Null(DataFolderMover.Check(_paths, Path.Combine(_root, "new data")));

            Assert.False(_paths.DataMoved);
            _paths.UseDataFolder(Path.Combine(_root, "new data"));
            Assert.True(_paths.DataMoved);
            Assert.Equal(Path.Combine(_root, "new data"), new AppPaths(_root).Data);   // a new start reads it back
            _paths.UseDataFolder(null);
            Assert.Equal(Path.Combine(_root, "data"), new AppPaths(_root).Data);
        }

        [Fact]
        public void Start_with_Windows_writes_and_removes_one_value()
        {
            const string key = @"Software\LuminaDB.Tests\Run";
            var name = "LuminaDB-" + Guid.NewGuid().ToString("N").Substring(0, 6);
            var ws = new WindowsStartup(name, key);
            try
            {
                Assert.False(ws.IsOn(@"C:\Lumina\LuminaDB.exe"));
                ws.Set(true, @"C:\Lumina\LuminaDB.exe");
                Assert.True(ws.IsOn(@"C:\Lumina\LuminaDB.exe"));
                Assert.False(ws.IsOn(@"D:\Other\LuminaDB.exe"));
                using (var k = Registry.CurrentUser.OpenSubKey(key)) Assert.Equal("\"C:\\Lumina\\LuminaDB.exe\" --minimized", k.GetValue(name));
                ws.Set(false, @"C:\Lumina\LuminaDB.exe");
                Assert.False(ws.IsOn(@"C:\Lumina\LuminaDB.exe"));
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\LuminaDB.Tests", false); }
        }

        [Fact]
        public void PhpMyAdmin_config_logs_in_only_without_a_root_password()
        {
            var open = PhpMyAdminConfig.Build(_paths, 3307, rootHasPassword: false);
            Assert.Contains("'auth_type'] = 'config'", open);
            Assert.Contains("'port'] = '3307'", open);
            var locked = PhpMyAdminConfig.Build(_paths, 3307, rootHasPassword: true);
            Assert.Contains("'auth_type'] = 'cookie'", locked);
            Assert.DoesNotContain("'password']", locked);
            var secret = System.Text.RegularExpressions.Regex.Match(open, "blowfish_secret'\\] = '([A-Za-z0-9]{32})'").Groups[1].Value;
            Assert.Equal(32, secret.Length);
            Assert.Contains(secret, locked);   // kept between writes, so logins survive

            var plan = new WebPlan { WebPort = 8090, PhpPort = 9090, AdminPort = 2019, PhpMyAdminDir = @"C:\engines\phpmyadmin" };
            var caddy = WebConfig.BuildCaddyfile(_paths, plan);
            Assert.Contains("http://phpmyadmin.localhost:8090 {", caddy);
            Assert.Contains("redir /phpmyadmin* http://phpmyadmin.localhost:8090/ 302", caddy);
            Assert.Contains("libraries/", caddy);
            plan.PhpMyAdminDir = null;
            Assert.DoesNotContain("phpmyadmin", WebConfig.BuildCaddyfile(_paths, plan));
        }
    }

    /// <summary>Step 9 against real servers: accounts, privileges, monitor, saved settings, moving the data folder, phpMyAdmin.</summary>
    [Collection("RealMariaDb")]
    public class AdminIntegrationTests
    {
        private readonly ITestOutputHelper _out;
        public AdminIntegrationTests(ITestOutputHelper output) { _out = output; }

        public static IEnumerable<object[]> Engines => MariaDbServiceIntegrationTests.Engines;

        private static async Task Exec(MySqlConnection c, string sql)
        {
            using (var cmd = new MySqlCommand(sql, c)) await cmd.ExecuteNonQueryAsync();
        }

        private static MySqlConnection Login(MariaDbService s, string user, string password, string db = null)
        {
            var cs = s.ConnectionString(db);
            cs.UserID = user;
            cs.Password = password;
            cs.Pooling = false;
            var c = new MySqlConnection(cs.ConnectionString);
            c.Open();
            return c;
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Users_get_exactly_the_privileges_the_page_shows(string folder, EngineSetKind kind, string version)
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig(folder, kind, _out))
            {
                if (rig == null) return;
                var s = rig.NewService(3496);
                await s.StartAsync();
                Assert.StartsWith(version, s.ServerVersion);
                using (var c = await s.OpenConnectionAsync())
                {
                    await Exec(c, "CREATE DATABASE shop");
                    await Exec(c, "CREATE TABLE shop.products (id INT PRIMARY KEY, name VARCHAR(40), price DECIMAL(8,2))");
                    await Exec(c, "INSERT INTO shop.products VALUES (1, 'Pen', 50)");

                    var list = await Accounts.ListAsync(c);
                    Assert.Contains(list, a => a.User == "root" && a.IsSystem);
                    Assert.Null(Accounts.CheckName("shop_app", list, "localhost"));

                    // create with a password (passed as a parameter), give read/write on shop and UPDATE on one column
                    var create = Accounts.CreateUser("shop_app", "localhost", withPassword: true);
                    await Accounts.RunAsync(c, create, "pa'ss w0rd");
                    var app = (await Accounts.ListAsync(c)).Single(a => a.User == "shop_app");
                    Assert.True(app.HasPassword);
                    var want = Privileges.ReadOnly.Select(p => new Grant(PrivilegeLevel.Database, p, "shop")).ToList();
                    want.Add(new Grant(PrivilegeLevel.Column, "UPDATE", "shop", "products", "price"));
                    var sql = Accounts.Diff(app, await Accounts.ReadGrantsAsync(c, app), want);
                    _out.WriteLine(Accounts.Script(sql));
                    await Accounts.RunAsync(c, sql);

                    var have = await Accounts.ReadGrantsAsync(c, app);
                    Assert.Empty(Accounts.Diff(app, have, want));              // what we read back is what we set
                    Assert.Contains((await Accounts.ShowGrantsAsync(c, app)), g => g.Contains("UPDATE (`price`)") || g.Contains("UPDATE (price)"));

                    using (var u = Login(s, "shop_app", "pa'ss w0rd", "shop"))
                    {
                        using (var cmd = new MySqlCommand("SELECT name FROM products", u)) Assert.Equal("Pen", await cmd.ExecuteScalarAsync());
                        await Exec(u, "UPDATE products SET price = 55 WHERE id = 1");
                        var denied = await Assert.ThrowsAsync<MySqlException>(() => Exec(u, "UPDATE products SET name = 'x'"));
                        Assert.Equal(1143, denied.Number);   // column command denied
                        await Assert.ThrowsAsync<MySqlException>(() => Exec(u, "DROP TABLE products"));
                    }

                    // a role holds privileges; given as the default role it works at once
                    await Accounts.RunAsync(c, Accounts.CreateRole("shop_writers"));
                    var role = (await Accounts.ListAsync(c)).Single(a => a.User == "shop_writers");
                    Assert.True(role.IsRole);
                    await Accounts.RunAsync(c, Accounts.Diff(role, null, new[] { new Grant(PrivilegeLevel.Database, "INSERT", "shop") }));
                    await Accounts.RunAsync(c, new[] { Accounts.GrantRole("shop_writers", app), Accounts.DefaultRole("shop_writers", app) });
                    Assert.Contains("shop_writers", (await Accounts.ListAsync(c)).Single(a => a.User == "shop_app").Roles);
                    using (var u = Login(s, "shop_app", "pa'ss w0rd", "shop")) await Exec(u, "INSERT INTO products VALUES (2, 'Ink', 20)");

                    // new password, lock, unlock, drop
                    await Accounts.RunAsync(c, new[] { Accounts.SetPassword(app, clear: false) }, "second");
                    Assert.Throws<MySqlException>(() => Login(s, "shop_app", "pa'ss w0rd").Dispose());
                    Login(s, "shop_app", "second").Dispose();
                    await Accounts.RunAsync(c, new[] { Accounts.Lock(app, true) });
                    Assert.True((await Accounts.ListAsync(c)).Single(a => a.User == "shop_app").IsLocked);
                    Assert.Throws<MySqlException>(() => Login(s, "shop_app", "second").Dispose());
                    await Accounts.RunAsync(c, new[] { Accounts.Drop(app), Accounts.Drop(role) });
                    Assert.DoesNotContain(await Accounts.ListAsync(c), a => a.User == "shop_app" || a.User == "shop_writers");
                }
            }
        }

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Monitor_reads_status_connections_variables_and_logs(string folder, EngineSetKind kind, string version)
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig(folder, kind, _out))
            {
                if (rig == null) return;
                var s = rig.NewService(3496);
                await s.StartAsync();
                using (var c = await s.OpenConnectionAsync())
                {
                    var st = await ServerMonitor.ReadStatusAsync(c);
                    Assert.StartsWith(version, s.ServerVersion);
                    Assert.True(st.Questions > 0);
                    Assert.True(st.ThreadsConnected >= 1);
                    Assert.True(st.BufferPoolPagesTotal > 0);

                    var vars = await ServerMonitor.ReadVariablesAsync(c);
                    var packet = vars.Single(v => v.Name == "max_allowed_packet");
                    Assert.Equal("268435456", packet.Value);
                    Assert.True(packet.IsNumber && packet.CanSetNow && packet.InConfig);
                    Assert.NotNull(packet.Note);
                    Assert.True(vars.Single(v => v.Name == "port").ReadOnly);
                    var slow = vars.Single(v => v.Name == "slow_query_log");
                    Assert.True(slow.IsBoolean);
                    Assert.Equal(s.SlowLogFile.Replace('\\', '/'), vars.Single(v => v.Name == "slow_query_log_file").Value.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);

                    // turn the slow log on and catch a slow query
                    await ServerMonitor.SetGlobalAsync(c, vars.Single(v => v.Name == "long_query_time"), ServerMonitor.Normalize(vars.Single(v => v.Name == "long_query_time"), "0.1"));
                    await ServerMonitor.SetGlobalAsync(c, slow, ServerMonitor.Normalize(slow, "on"));
                    using (var other = await s.OpenConnectionAsync())
                    using (var cmd = new MySqlCommand("SELECT SLEEP(0.4) AS lumina_slow_probe", other)) await cmd.ExecuteScalarAsync();
                    await ServerMonitor.SetGlobalAsync(c, slow, "OFF");
                    Assert.Contains("lumina_slow_probe", ReadShared(s.SlowLogFile));

                    // a busy connection can be stopped; ours can't
                    using (var busy = await s.OpenConnectionAsync())
                    {
                        var running = Task.Run(async () =>
                        {
                            try { using (var cmd = new MySqlCommand("SELECT SLEEP(30)", busy)) await cmd.ExecuteScalarAsync(); return "finished"; }
                            catch (MySqlException ex) { return ex.Message; }
                        });
                        ServerConnection target = null;
                        for (int i = 0; i < 40 && target == null; i++)
                        {
                            await Task.Delay(100);
                            target = (await ServerMonitor.ListConnectionsAsync(c)).FirstOrDefault(x => x.Query != null && x.Query.Contains("SLEEP(30)"));
                        }
                        Assert.NotNull(target);
                        Assert.True(target.CanKill && target.IsBusy);
                        var all = await ServerMonitor.ListConnectionsAsync(c);
                        Assert.Single(all, x => x.IsOurs);
                        await ServerMonitor.KillAsync(c, target, queryOnly: true);
                        var t0 = DateTime.UtcNow;
                        Assert.Contains("interrupted", await running);
                        Assert.True((DateTime.UtcNow - t0).TotalSeconds < 10);
                        using (var cmd = new MySqlCommand("SELECT 1", busy)) Assert.Equal(1, Convert.ToInt32(await cmd.ExecuteScalarAsync()));   // connection kept
                    }
                }
            }
        }

        private static async Task<CheckResult> OverridesCheck(AppPaths paths, MariaDbService s) =>
            (await new Doctor(new DoctorContext { Paths = paths, Engine = s.Engine, Server = s }).RunAsync()).Single(r => r.Id == "overrides");

        [Theory]
        [MemberData(nameof(Engines))]
        public async Task Console_runs_sql_in_utf8_and_keeps_going_after_an_error(string folder, EngineSetKind kind, string version)
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig(folder, kind, _out))
            {
                if (rig == null) return;
                var s = rig.NewService(3536);
                await s.StartAsync();
                var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
                int? exited = null;
                using (var con = new MariaDbConsole())
                {
                    con.Line += (l, err) => lines.Enqueue((err ? "! " : "") + l);
                    con.Exited += code => exited = code;
                    con.Start(s.Engine.ClientExe, s.Port, s.ConnectionString().Password, rig.Paths.Root);
                    Assert.True(con.IsRunning);
                    Assert.DoesNotContain("PWD", MariaDbConsole.Arguments(s.Port), StringComparison.OrdinalIgnoreCase);

                    async Task<string> Wait(string needle)
                    {
                        for (int i = 0; i < 100; i++)
                        {
                            var all = string.Join("\n", lines);
                            if (all.Contains(needle)) return all;
                            await Task.Delay(100);
                        }
                        Assert.Fail("never saw " + needle + " in:\n" + string.Join("\n", lines));
                        return null;
                    }

                    con.Send("SELECT 1 + 1 AS two;");
                    var o = await Wait("| two |");
                    Assert.Contains("|   2 |", o);

                    con.Send("SELECT VERSION() AS v;");               // connected to this engine set's server
                    await Wait(version);

                    con.Send("SELEC oops;");                      // an error doesn't end the session (--force)
                    Assert.Contains("ERROR 1064", await Wait("ERROR 1064"));
                    Assert.True(con.IsRunning);

                    con.Send("SELECT 'ශ්‍රී ලංකා' AS sinhala,");   // a statement over two lines
                    con.Send("  'தமிழ்' AS tamil;");
                    var u = await Wait("தமிழ்");
                    Assert.Contains("ශ්‍රී ලංකා", u);
                    Assert.DoesNotContain("ssl-verify-server-cert", u);

                    con.Send("exit");
                    for (int i = 0; i < 50 && con.IsRunning; i++) await Task.Delay(100);
                    Assert.False(con.IsRunning);
                    for (int i = 0; i < 20 && exited == null; i++) await Task.Delay(100);
                    Assert.Equal(0, exited);
                    Assert.Throws<InvalidOperationException>(() => con.Send("SELECT 1;"));
                }
            }
        }

        private static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var r = new StreamReader(fs)) return r.ReadToEnd();
        }

        [Fact]
        public async Task Saved_settings_apply_at_start_and_a_bad_one_is_put_aside()
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig("mariadb-11.8.10-winx64", EngineSetKind.Modern, _out))
            {
                if (rig == null) return;
                rig.Paths.EnsureCreated();
                var s = rig.NewService(3506);
                await s.StartAsync();
                var o = new ServerOverrides();
                o.Set("max_connections", "77");
                o.Save(rig.Paths, EngineSetKind.Modern);
                await s.RestartAsync();
                using (var c = await s.OpenConnectionAsync())
                using (var cmd = new MySqlCommand("SELECT @@max_connections", c)) Assert.Equal(77L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
                Assert.Null(s.OverridesSetAside);
                var kept = await OverridesCheck(rig.Paths, s);
                Assert.Equal(CheckSeverity.Ok, kept.Severity);
                Assert.Contains("max_connections", kept.Text);

                o.Set("innodb_buffer_pool_size", "lots");   // the server refuses to start with this
                o.Save(rig.Paths, EngineSetKind.Modern);
                await s.RestartAsync();
                Assert.Equal(ServerState.Running, s.State);
                Assert.NotNull(s.OverridesSetAside);
                Assert.True(File.Exists(s.OverridesSetAside));
                Assert.False(File.Exists(ServerOverrides.FileFor(rig.Paths, EngineSetKind.Modern)));
                using (var c = await s.OpenConnectionAsync())
                using (var cmd = new MySqlCommand("SELECT @@max_connections", c)) Assert.NotEqual(77L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));
                var aside = await OverridesCheck(rig.Paths, s);   // Doctor reports it (SPEC §5.9)
                Assert.Equal(CheckSeverity.Warn, aside.Severity);
                Assert.Contains(Path.GetFileName(s.OverridesSetAside), aside.Text);

                // a new preferred port is used at the next start
                s.SetPreferredPort(3509);
                await s.RestartAsync();
                Assert.Equal(3509, s.Port);
            }
        }

        [Fact]
        public async Task Data_folder_moves_with_every_database_and_the_old_copy_is_kept()
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig("mariadb-11.8.10-winx64", EngineSetKind.Modern, _out))
            {
                if (rig == null) return;
                var s = rig.NewService(3516);
                await s.StartAsync();
                using (var c = await s.OpenConnectionAsync())
                {
                    await Exec(c, "CREATE DATABASE moved_db");
                    await Exec(c, "CREATE TABLE moved_db.t (id INT PRIMARY KEY, v VARCHAR(10)) ENGINE=InnoDB");
                    await Exec(c, "INSERT INTO moved_db.t VALUES (1, 'kept')");
                }
                var oldData = rig.Paths.Data;
                var target = Path.Combine(rig.Paths.Root, "elsewhere", "Lumina data");
                var steps = new List<string>();
                var r = await DataFolderMover.MoveAsync(s, target, new SyncProgress(steps.Add));
                _out.WriteLine(string.Join(" | ", steps));
                Assert.Equal(target, rig.Paths.Data);
                Assert.Equal(ServerState.Running, s.State);
                Assert.True(r.Files > 10);
                Assert.True(Directory.Exists(r.OldCopy));
                Assert.False(Directory.Exists(oldData));       // renamed to data.old-…
                Assert.StartsWith(target, s.DataDir);
                using (var c = await s.OpenConnectionAsync())
                using (var cmd = new MySqlCommand("SELECT v FROM moved_db.t", c)) Assert.Equal("kept", await cmd.ExecuteScalarAsync());
                Assert.Equal(target, new AppPaths(rig.Paths.Root).Data);   // remembered for the next start

                // a bad target changes nothing
                await Assert.ThrowsAsync<LuminaException>(() => DataFolderMover.MoveAsync(s, Path.Combine(target, "inner")));
                Assert.Equal(target, rig.Paths.Data);
                Assert.Equal(ServerState.Running, s.State);
            }
        }

        private sealed class SyncProgress : IProgress<string>
        {
            private readonly Action<string> _a;
            public SyncProgress(Action<string> a) { _a = a; }
            public void Report(string value) => _a(value);
        }

        private static (int Status, string Body, string Location) Get(int port, string host, string path)
        {
            var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
            req.Host = host + ":" + port;
            req.Proxy = null;
            req.AllowAutoRedirect = false;
            req.Timeout = 30000;
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex) when (ex.Response is HttpWebResponse r) { resp = r; }
            using (resp)
            using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                return ((int)resp.StatusCode, sr.ReadToEnd(), resp.Headers["Location"]);
        }

        [Fact]
        public async Task PhpMyAdmin_switch_serves_it_logged_in_then_asks_for_the_password()
        {
            var bundle = WebIntegrationTests.FindWebBundle();
            if (bundle == null) { _out.WriteLine("SKIPPED: ParityTests\\web\\bundle not found (set LUMINA_WEB_DIR)."); return; }
            var stack = WebStack.FromFolder(bundle);
            if (!stack.HasPhpMyAdmin) { _out.WriteLine("SKIPPED: no phpmyadmin in the bundle."); return; }
            using (var rig = MariaDbServiceIntegrationTests.NewRig("mariadb-11.8.10-winx64", EngineSetKind.Modern, _out))
            {
                if (rig == null) return;
                rig.Paths.EnsureCreated();
                new WebSettings { PreferredWebPort = 8490, PreferredPhpPort = 9490, PhpMyAdmin = false }.Save(rig.Paths);
                var s = rig.NewService(3526);
                await s.StartAsync();
                using (var web = new WebStackService(rig.Paths, EngineSetKind.Modern, stack))
                {
                    var rt = new PhpRuntime { MariaDbPort = s.Port, MailPort = 2590, TimeZone = "UTC" };
                    await web.StartAsync(new List<SiteInfo>(), rt);
                    Assert.False(web.PhpMyAdminOn);
                    Assert.DoesNotContain("phpMyAdmin", Get(web.WebPort, "phpmyadmin.localhost", "/").Body);

                    await web.SetPhpMyAdminAsync(true, rt);
                    Assert.True(web.PhpMyAdminOn);
                    Assert.True(WebSettings.Load(rig.Paths).PhpMyAdmin);
                    var home = Get(web.WebPort, "phpmyadmin.localhost", "/");
                    Assert.Equal(200, home.Status);
                    Assert.Contains("phpMyAdmin", home.Body);
                    Assert.DoesNotContain("pma_username", home.Body);            // opened straight in (no root password)
                    Assert.Contains("information_schema", Get(web.WebPort, "phpmyadmin.localhost", "/index.php?route=/server/databases").Body);
                    Assert.Equal(403, Get(web.WebPort, "phpmyadmin.localhost", "/libraries/constants.php").Status);
                    Assert.Equal(403, Get(web.WebPort, "phpmyadmin.localhost", "/config.inc.php").Status);
                    Assert.Equal(403, Get(web.WebPort, "phpmyadmin.localhost", "/setup/index.php").Status);
                    var redirect = Get(web.WebPort, "localhost", "/phpmyadmin/");
                    Assert.Equal(302, redirect.Status);
                    Assert.StartsWith("http://phpmyadmin.localhost:" + web.WebPort, redirect.Location);
                    Assert.Contains(PhpMyAdminConfig.StubMarker, File.ReadAllText(Path.Combine(stack.PhpMyAdminDir, "config.inc.php")));

                    // with a root password it asks for it (the password is never written to a file)
                    await s.SetRootPasswordAsync("t0p secret");
                    rt.RootHasPassword = true;
                    await web.SetPhpMyAdminAsync(true, rt);
                    Assert.Contains("pma_username", Get(web.WebPort, "phpmyadmin.localhost", "/").Body);
                    Assert.DoesNotContain("t0p secret", File.ReadAllText(PhpMyAdminConfig.ConfigFile(rig.Paths)));

                    await web.SetPhpMyAdminAsync(false, rt);
                    Assert.DoesNotContain("pma_username", Get(web.WebPort, "phpmyadmin.localhost", "/").Body);
                    await web.StopAsync();
                }
                await s.SetRootPasswordAsync("");
            }
        }
    }
}
