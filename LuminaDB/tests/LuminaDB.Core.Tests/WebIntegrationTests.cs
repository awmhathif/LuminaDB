using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using LuminaDB.Core.Mail;
using LuminaDB.Core.Projects;
using LuminaDB.Core.Web;
using MySqlConnector;
using Xunit;
using Xunit.Abstractions;

namespace LuminaDB.Core.Tests
{
    /// <summary>
    /// Step 5 end to end on the real bundle (ParityTests\web\bundle: Caddy 2.11.9, PHP 8.5.13, Apache 2.4.71) and
    /// MariaDB 11.8: templates served and used, the error page, .htaccess through Apache, mail() into the catcher,
    /// per-project PHP settings, snapshots and the submission pack.
    /// </summary>
    [Collection("RealMariaDb")]
    public class WebIntegrationTests
    {
        private readonly ITestOutputHelper _out;
        public WebIntegrationTests(ITestOutputHelper output) { _out = output; }

        internal static string FindWebBundle()
        {
            var env = Environment.GetEnvironmentVariable("LUMINA_WEB_DIR");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            for (var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
            {
                var c = Path.Combine(d.FullName, "ParityTests", "web", "bundle");
                if (Directory.Exists(c)) return c;
            }
            return null;
        }

        private sealed class Http
        {
            public readonly CookieContainer Cookies = new CookieContainer();
            public int Port;

            public (int Status, string Body, WebHeaderCollection Headers) Send(string host, string path, string method = "GET", string body = null,
                string contentType = "application/x-www-form-urlencoded", string accept = "text/html")
            {
                var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + Port + path);
                req.Host = host + ":" + Port;
                req.Method = method;
                req.Proxy = null;
                req.AllowAutoRedirect = false;
                req.CookieContainer = Cookies;
                req.Accept = accept;
                req.Timeout = 30000;
                if (body != null)
                {
                    var b = Encoding.UTF8.GetBytes(body);
                    req.ContentType = contentType;
                    req.ContentLength = b.Length;
                    using (var s = req.GetRequestStream()) s.Write(b, 0, b.Length);
                }
                HttpWebResponse resp;
                try { resp = (HttpWebResponse)req.GetResponse(); }
                catch (WebException ex) when (ex.Response is HttpWebResponse r) { resp = r; }
                using (resp)
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    // Cookies are set for 127.0.0.1; replay them for the project host too.
                    foreach (Cookie c in Cookies.GetCookies(new Uri("http://127.0.0.1:" + Port + "/"))) c.Domain = "127.0.0.1";
                    return ((int)resp.StatusCode, sr.ReadToEnd(), resp.Headers);
                }
            }
        }

        private static string Form(params string[] kv)
        {
            var parts = new List<string>();
            for (int i = 0; i < kv.Length; i += 2) parts.Add(Uri.EscapeDataString(kv[i]) + "=" + Uri.EscapeDataString(kv[i + 1]));
            return string.Join("&", parts);
        }

        private static string Csrf(string html) => Regex.Match(html, "name=\"csrf\" value=\"([0-9a-f]+)\"").Groups[1].Value;

        [Fact]
        public async Task Projects_are_served_end_to_end()
        {
            var bundle = FindWebBundle();
            if (bundle == null) { _out.WriteLine("SKIPPED: ParityTests\\web\\bundle not found (set LUMINA_WEB_DIR)."); return; }
            using (var rig = MariaDbServiceIntegrationTests.NewRig("mariadb-11.8.10-winx64", EngineSetKind.Modern, _out))
            {
                if (rig == null) return;
                var stack = WebStack.FromFolder(bundle);
                Assert.True(stack.CanServe, stack.Missing);
                Assert.True(stack.UsesCaddy);
                rig.Paths.EnsureCreated();
                // Not 80: keep clear of anything on the PC, and of the dev app.
                new WebSettings { PreferredWebPort = 8480, PreferredPhpPort = 9480 }.Save(rig.Paths);

                var server = rig.NewService(3466);
                await server.StartAsync();
                using (var mail = new MailCatcher(rig.Paths))
                using (var web = new WebStackService(rig.Paths, EngineSetKind.Modern, stack))
                {
                    var lines = new List<string>();
                    web.OutputLine += (s, l) => { lock (lines) lines.Add(s + ": " + l); };
                    mail.Start(2580, new[] { server.Port });
                    var projects = new ProjectService(rig.Paths, stack);
                    var progress = new Progress<string>(m => _out.WriteLine("  " + m));
                    foreach (var t in new[] { "empty", "crud", "login", "api" })
                        await projects.CreateAsync("t-" + t, t, server, progress);
                    var bad = await Assert.ThrowsAsync<LuminaException>(() => projects.CreateAsync("t-crud", "crud", server));
                    Assert.Contains("already", bad.Message);

                    var rt = new PhpRuntime { MariaDbPort = server.Port, MailPort = mail.Port, TimeZone = "Asia/Colombo" };
                    var t0 = DateTime.UtcNow;
                    try { await web.StartAsync(projects.Sites(), rt); }
                    catch { lock (lines) _out.WriteLine(string.Join("\n", lines)); throw; }
                    _out.WriteLine($"web up in {(DateTime.UtcNow - t0).TotalMilliseconds:0} ms on {web.WebPort} (php {web.PhpPort})");
                    Assert.Equal(WebState.Running, web.State);
                    Assert.False(web.ApacheRunning);
                    var http = new Http { Port = web.WebPort };

                    // ---- index + empty template ----
                    var idx = http.Send("localhost", "/");
                    Assert.Equal(200, idx.Status);
                    Assert.Contains("t-crud.localhost", idx.Body);
                    var empty = http.Send("t-empty.localhost", "/");
                    Assert.Equal(200, empty.Status);
                    Assert.Contains("connected to t_empty", empty.Body);
                    Assert.Contains("11.8.10-MariaDB", empty.Body);
                    // path-style URL for old browsers
                    Assert.Contains("connected to t_empty", http.Send("localhost", "/t-empty/").Body);
                    Assert.Equal(308, http.Send("localhost", "/t-empty").Status);
                    // private files stay private
                    Assert.Equal(403, http.Send("t-empty.localhost", "/.user.ini").Status);
                    // and so do database dumps left beside index.php (an imported project's .sql)
                    var dumpPath = Path.Combine(projects.Find("t-empty").DocumentRoot, "t_empty.sql");
                    File.WriteAllText(dumpPath, "INSERT INTO users VALUES ('secret');");
                    var dump = http.Send("t-empty.localhost", "/t_empty.sql");
                    Assert.Equal(403, dump.Status);
                    Assert.DoesNotContain("secret", dump.Body);
                    File.Delete(dumpPath);

                    // ---- CRUD: list, add (with CSRF), duplicate email, delete ----
                    var crud = http.Send("t-crud.localhost", "/");
                    Assert.Contains("Nimali Perera", crud.Body);
                    var token = Csrf(crud.Body);
                    Assert.NotEmpty(token);
                    var add = http.Send("t-crud.localhost", "/index.php", "POST", Form("csrf", token, "action", "create", "id", "0", "name", "ශාන්ති", "email", "shanthi@x.lk", "course", "HNDIT"));
                    Assert.Equal(302, add.Status);
                    var after = http.Send("t-crud.localhost", "/");
                    Assert.Contains("ශාන්ති", after.Body);
                    Assert.Contains("Student added.", after.Body);
                    var dup = http.Send("t-crud.localhost", "/index.php", "POST", Form("csrf", token, "action", "create", "id", "0", "name", "X", "email", "shanthi@x.lk", "course", ""));
                    Assert.Contains("Another student already uses that email.", dup.Body);
                    Assert.Equal(400, http.Send("t-crud.localhost", "/index.php", "POST", Form("csrf", "forged", "action", "delete", "id", "1")).Status);

                    // ---- login: register, dashboard, sign out, wrong password ----
                    var reg = http.Send("t-login.localhost", "/register.php");
                    var rt1 = http.Send("t-login.localhost", "/register.php", "POST", Form("csrf", Csrf(reg.Body), "name", "Kavin", "email", "kavin@x.lk", "password", "correct horse", "confirm", "correct horse"));
                    Assert.Equal(302, rt1.Status);
                    var dash = http.Send("t-login.localhost", "/index.php");
                    Assert.Contains("Hello, Kavin", dash.Body);
                    var outp = http.Send("t-login.localhost", "/logout.php", "POST", Form("csrf", Csrf(dash.Body)));
                    Assert.Equal(302, outp.Status);
                    Assert.Equal(302, http.Send("t-login.localhost", "/index.php").Status);   // back to login
                    var lg = http.Send("t-login.localhost", "/login.php");
                    var wrong = http.Send("t-login.localhost", "/login.php", "POST", Form("csrf", Csrf(lg.Body), "email", "kavin@x.lk", "password", "nope"));
                    Assert.Contains("don&#039;t match", wrong.Body);
                    using (var c = await server.OpenConnectionAsync("t_login"))
                    using (var cmd = new MySqlCommand("SELECT password_hash FROM users", c))
                        Assert.StartsWith("$2y$", (string)await cmd.ExecuteScalarAsync());

                    // ---- REST API ----
                    var list = http.Send("t-api.localhost", "/items", accept: "application/json");
                    Assert.Equal(200, list.Status);
                    Assert.Contains("\"name\": \"Notebook\"", list.Body);
                    var created = http.Send("t-api.localhost", "/items", "POST", "{\"name\":\"Ruler\",\"price\":60}", "application/json", "application/json");
                    Assert.Equal(201, created.Status);
                    var id = Regex.Match(created.Body, "\"id\": (\\d+)").Groups[1].Value;
                    Assert.Equal(200, http.Send("t-api.localhost", "/items/" + id, "PUT", "{\"name\":\"Long ruler\",\"price\":75.5}", "application/json", "application/json").Status);
                    Assert.Contains("Long ruler", http.Send("t-api.localhost", "/items/" + id, accept: "application/json").Body);
                    Assert.Equal(422, http.Send("t-api.localhost", "/items", "POST", "{\"price\":-1}", "application/json", "application/json").Status);
                    Assert.Equal(204, http.Send("t-api.localhost", "/items/" + id, "DELETE").Status);
                    Assert.Equal(404, http.Send("t-api.localhost", "/items/" + id, accept: "application/json").Status);
                    Assert.Equal(405, http.Send("t-api.localhost", "/items", "PATCH", "{}", "application/json", "application/json").Status);

                    // ---- error page ----
                    var root = Path.Combine(rig.Paths.Projects, "t-empty");
                    File.WriteAllText(Path.Combine(root, "fatal.php"), "<?php\necho 'before';\nmysql_connect('localhost');\n");
                    File.WriteAllText(Path.Combine(root, "exception.php"), "<?php\nrequire __DIR__ . '/config.php';\n$pdo = new PDO('mysql:host=127.0.0.1;port=' . DB_PORT . ';dbname=nope_db', 'root', '');\n");
                    File.WriteAllText(Path.Combine(root, "warning.php"), "<?php\necho '<p>page ran</p>';\necho $user;\n$a = [];\necho $a['name'] ?? '';\necho $a['email'];\n");
                    File.WriteAllText(Path.Combine(root, "json.php"), "<?php\nheader('Content-Type: application/json');\nnull_fn();\n");
                    File.WriteAllText(Path.Combine(root, "syntax.php"), "<?php\n$x = 1\necho $x;\n");
                    var fatal = http.Send("t-empty.localhost", "/fatal.php");
                    Assert.Equal(500, fatal.Status);
                    Assert.Contains("Call to undefined function mysql_connect()", fatal.Body);
                    Assert.Contains("removed in PHP 7", fatal.Body);
                    Assert.Contains("t-empty/fatal.php", fatal.Body);
                    Assert.Contains("ln hit", fatal.Body);
                    Assert.DoesNotContain("before<!doctype", fatal.Body);     // the half-made page was replaced
                    var ex = http.Send("t-empty.localhost", "/exception.php");
                    Assert.Equal(500, ex.Status);
                    Assert.Contains("Uncaught PDOException", ex.Body);
                    Assert.Contains("There is no database called nope_db", ex.Body);
                    var warn = http.Send("t-empty.localhost", "/warning.php");
                    Assert.Equal(200, warn.Status);
                    Assert.Contains("<p>page ran</p>", warn.Body);
                    Assert.Contains("lumina-warnings", warn.Body);
                    Assert.Contains("PHP reported 2 problems", warn.Body);
                    Assert.Contains("$user is used before it was given a value", warn.Body);
                    Assert.Contains("no item called &quot;email&quot;", warn.Body);
                    var json = http.Send("t-empty.localhost", "/json.php", accept: "application/json");
                    Assert.Equal(500, json.Status);
                    Assert.StartsWith("application/json", json.Headers["Content-Type"]);
                    Assert.Contains("\"message\": \"Call to undefined function null_fn()\"", json.Body);
                    var syn = http.Send("t-empty.localhost", "/syntax.php");
                    Assert.Contains("Syntax error", syn.Body);
                    Assert.Contains("missing ;", syn.Body);
                    // demo visitors never see code
                    var demoReq = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + web.WebPort + "/fatal.php");
                    demoReq.Host = "t-empty.localhost:" + web.WebPort;
                    demoReq.Proxy = null;
                    demoReq.Headers[WebConfig.DemoHeader] = "1";   // stripped by the local site: still the full page
                    using (var r = (HttpWebResponse)(await Assert.ThrowsAsync<WebException>(() => Task.Run(() => demoReq.GetResponse()))).Response)
                    using (var sr = new StreamReader(r.GetResponseStream())) Assert.Contains("ln hit", sr.ReadToEnd());
                    var phpLog = string.Join("\n", LogTail.ReadAll(WebConfig.PhpLog(rig.Paths, "t-empty")));
                    Assert.Contains("Uncaught Error: Call to undefined function mysql_connect() in t-empty/fatal.php:3", phpLog);
                    Assert.Contains("Warning: Undefined variable $user", phpLog);
                    await Task.Delay(1500);   // Caddy flushes the access log
                    var access = LogTail.ReadAll(WebConfig.AccessLog(rig.Paths, "t-empty")).Select(AccessEntry.Parse).Where(a => a != null).ToList();
                    Assert.Contains(access, a => a.Uri == "/fatal.php" && a.Status == 500);

                    // ---- per-project PHP settings through .user.ini ----
                    var p = projects.Find("t-empty");
                    projects.SavePhpSettings(p, new ProjectPhpSettings { UploadMax = "8M", MemoryLimit = "128M", TimeZone = "Europe/London", MaxExecutionSeconds = 30 });
                    File.WriteAllText(Path.Combine(root, "ini.php"), "<?php echo ini_get('upload_max_filesize'), '|', ini_get('memory_limit'), '|', date_default_timezone_get(), '|', ini_get('mysqli.default_port');");
                    await Task.Delay(5500);   // user_ini.cache_ttl
                    Assert.Equal("8M|128M|Europe/London|" + server.Port, http.Send("t-empty.localhost", "/ini.php").Body);

                    // ---- mail() into the catcher ----
                    var got = new TaskCompletionSource<string>();
                    mail.MessageReceived += f => got.TrySetResult(f);
                    File.WriteAllText(Path.Combine(root, "mail.php"), "<?php\n$ok = mail('student@x.lk', '=?UTF-8?B?' . base64_encode('Results ✓') . '?=', \"Hello ශාන්ති\", \"From: shop@t-empty.localhost\\r\\nContent-Type: text/plain; charset=UTF-8\");\necho $ok ? 'sent' : 'failed';\n");
                    Assert.Equal("sent", http.Send("t-empty.localhost", "/mail.php").Body);
                    Assert.Same(got.Task, await Task.WhenAny(got.Task, Task.Delay(10000)));
                    var msg = MimeMessage.Load(await got.Task);
                    Assert.Equal("Results ✓", msg.Subject);
                    Assert.Equal("student@x.lk", msg.To);
                    Assert.Contains("Hello ශාන්ති", msg.TextBody);

                    // ---- .htaccess: Apache starts on demand behind Caddy ----
                    await projects.CreateAsync("t-rewrite", "empty", server);
                    var rwRoot = Path.Combine(rig.Paths.Projects, "t-rewrite");
                    File.WriteAllText(Path.Combine(rwRoot, ".htaccess"), "RewriteEngine On\nRewriteRule ^hello/([a-z]+)$ hello.php?who=$1 [L,QSA]\n");
                    File.WriteAllText(Path.Combine(rwRoot, "hello.php"), "<?php echo 'via apache: ', htmlspecialchars($_GET['who'] ?? ''), ' ', $_SERVER['SERVER_SOFTWARE'] ?? '';");
                    await web.ApplySitesAsync(projects.Sites());
                    Assert.True(web.ApacheRunning);
                    var hello = http.Send("t-rewrite.localhost", "/hello/kamala");
                    Assert.Equal(200, hello.Status);
                    Assert.StartsWith("via apache: kamala Apache", hello.Body);
                    Assert.Contains("connected to t_rewrite", http.Send("t-rewrite.localhost", "/").Body);
                    Assert.Contains("connected to t_empty", http.Send("t-empty.localhost", "/").Body);   // Caddy projects unaffected
                    var apFatal = http.Send("t-rewrite.localhost", "/fatal.php");
                    Assert.Equal(404, apFatal.Status);
                    File.Delete(Path.Combine(rwRoot, ".htaccess"));
                    await web.ApplySitesAsync(projects.Sites());
                    Assert.False(web.ApacheRunning);
                    // Back on Caddy: no rewrite rule any more, so the front-controller fallback serves index.php.
                    var back = http.Send("t-rewrite.localhost", "/hello/kamala");
                    Assert.DoesNotContain("via apache", back.Body);
                    Assert.Contains("connected to t_rewrite", back.Body);

                    // ---- snapshot and restore ----
                    var archive = new ProjectArchive(rig.Paths);
                    var crudProject = projects.Find("t-crud");
                    var snap = await archive.TakeSnapshotAsync(crudProject, server, "before changes");
                    Assert.True(snap.HasDatabase);
                    Assert.Single(archive.ListSnapshots("t-crud"));
                    using (var c = await server.OpenConnectionAsync("t_crud"))
                    using (var cmd = new MySqlCommand("DELETE FROM students", c)) await cmd.ExecuteNonQueryAsync();
                    File.WriteAllText(Path.Combine(crudProject.Folder, "index.php"), "<?php echo 'broken';");
                    File.WriteAllText(Path.Combine(crudProject.Folder, "extra.txt"), "new file");
                    Assert.Equal("broken", http.Send("t-crud.localhost", "/").Body);
                    var safety = await archive.RestoreAsync(crudProject, archive.ListSnapshots("t-crud").Single(), true, true, server);
                    Assert.StartsWith("Before restoring", safety.Note);
                    Assert.Equal(2, archive.ListSnapshots("t-crud").Count);
                    Assert.False(File.Exists(Path.Combine(crudProject.Folder, "extra.txt")));
                    var restored = http.Send("t-crud.localhost", "/");
                    Assert.Contains("ශාන්ති", restored.Body);
                    Assert.Contains("Nimali Perera", restored.Body);

                    // ---- submission pack ----
                    Directory.CreateDirectory(Path.Combine(crudProject.Folder, "vendor", "x"));
                    File.WriteAllText(Path.Combine(crudProject.Folder, "vendor", "x", "big.php"), "<?php");
                    Directory.CreateDirectory(Path.Combine(crudProject.Folder, "uploads"));
                    File.WriteAllText(Path.Combine(crudProject.Folder, ".env"), "DB_PASSWORD=hunter2");
                    File.WriteAllText(Path.Combine(crudProject.Folder, ".env.example"), "DB_PASSWORD=");
                    var pack = await archive.BuildSubmissionPackAsync(crudProject, server, "HNDIT/24/001", includeVendor: false);
                    Assert.EndsWith("HNDIT-24-001_t-crud_" + DateTime.Now.ToString("yyyy-MM-dd") + ".zip", pack);
                    using (var z = ZipFile.OpenRead(pack))
                    {
                        var names = z.Entries.Select(e => e.FullName).ToList();
                        _out.WriteLine(string.Join("\n", names));
                        Assert.Contains("t-crud/index.php", names);
                        Assert.Contains("t-crud/config.php", names);
                        Assert.Contains("t-crud/uploads/", names);
                        Assert.Contains("database/t_crud.sql", names);
                        Assert.Contains("README.txt", names);
                        Assert.DoesNotContain(names, n => n.Contains("vendor/"));
                        Assert.DoesNotContain("t-crud/.env", names);          // passwords stay on this PC
                        Assert.Contains("t-crud/.env.example", names);
                        using (var rr = new StreamReader(z.GetEntry("README.txt").Open()))
                            Assert.Contains("t-crud/.env", rr.ReadToEnd());   // and the README says so
                        using (var sr = new StreamReader(z.GetEntry("database/t_crud.sql").Open()))
                        {
                            var sql = sr.ReadToEnd();
                            Assert.Contains("CREATE DATABASE", sql);
                            Assert.Contains("ශාන්ති", sql);
                            Assert.DoesNotContain("uca1400", sql);   // importable into XAMPP's MariaDB 10.4
                        }
                    }

                    // ---- delete goes to the Recycle Bin ----
                    await projects.DeleteAsync(projects.Find("t-rewrite"), dropDatabase: true, server);
                    Assert.Null(projects.Find("t-rewrite"));
                    using (var c = await server.OpenConnectionAsync())
                    using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME='t_rewrite'", c))
                        Assert.Equal(0L, Convert.ToInt64(await cmd.ExecuteScalarAsync()));

                    // ---- restart PHP (extension change) keeps serving; then stop frees every port ----
                    await web.RestartPhpAsync(rt);
                    Assert.Contains("connected to t_empty", http.Send("t-empty.localhost", "/").Body);
                    int webPort = web.WebPort, phpPort = web.PhpPort;
                    await web.StopAsync();
                    Assert.Equal(WebState.Stopped, web.State);
                    Assert.False(await WebStackService.CanConnectAsync(webPort));
                    Assert.False(await WebStackService.CanConnectAsync(phpPort));
                    Assert.Empty(MariaDbServiceIntegrationTests.Ours(stack.PhpCgiExe, rig.Paths.Root));
                    Assert.Empty(MariaDbServiceIntegrationTests.Ours(stack.CaddyExe, rig.Paths.Root));
                    Assert.Empty(MariaDbServiceIntegrationTests.Ours(stack.HttpdExe, rig.Paths.Root));
                }
            }
        }
    }
}
