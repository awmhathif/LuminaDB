using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using LuminaDB.Core.Mail;
using LuminaDB.Core.Projects;
using LuminaDB.Core.Web;
using Xunit;
using Xunit.Abstractions;

namespace LuminaDB.Core.Tests
{
    /// <summary>Step 5 pieces that need no engine: generated configs, names, MIME, SMTP, QR, archives.</summary>
    public class WebUnitTests : IDisposable
    {
        private readonly ITestOutputHelper _out;
        private readonly string _root;
        private readonly AppPaths _paths;

        public WebUnitTests(ITestOutputHelper output)
        {
            _out = output;
            _root = Path.Combine(Path.GetTempPath(), "Lumina Web " + Guid.NewGuid().ToString("N").Substring(0, 8));
            _paths = new AppPaths(_root);
            _paths.EnsureCreated();
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private WebStack FakeStack(params string[] extensions)
        {
            var php = Path.Combine(_root, "engines", "php");
            Directory.CreateDirectory(Path.Combine(php, "ext"));
            foreach (var e in extensions) File.WriteAllText(Path.Combine(php, "ext", "php_" + e + ".dll"), "");
            File.WriteAllText(Path.Combine(php, "php.exe"), "");
            File.WriteAllText(Path.Combine(php, "php-cgi.exe"), "");
            return WebStack.FromFolders(php, null, null);
        }

        [Fact]
        public void Php_ini_has_absolute_paths_only_existing_extensions_and_our_ports()
        {
            var stack = FakeStack("mysqli", "pdo_mysql", "mbstring", "curl");
            var ini = PhpConfig.Build(_paths, stack, new WebSettings(), new PhpRuntime { MariaDbPort = 3307, MailPort = 2526, TimeZone = "Asia/Colombo" });
            Assert.Contains("extension_dir = \"" + stack.PhpExtDir.Replace('\\', '/') + "\"", ini);
            Assert.Contains("extension=mysqli", ini);
            Assert.Contains("extension=pdo_mysql", ini);
            Assert.DoesNotContain("extension=gd", ini);          // listed in settings, but no DLL
            Assert.Contains("display_errors = Off", ini);
            Assert.Contains("smtp_port = 2526", ini);
            Assert.Contains("mysqli.default_port = 3307", ini);
            Assert.Contains("date.timezone = Asia/Colombo", ini);
            Assert.Contains("auto_prepend_file = \"" + PhpConfig.PrependFile(_paths).Replace('\\', '/') + "\"", ini);
            Assert.Contains("user_ini.cache_ttl = 5", ini);
        }

        [Fact]
        public void User_ini_and_post_size()
        {
            var s = new ProjectPhpSettings { UploadMax = "128M", MemoryLimit = "512M", TimeZone = "Europe/London", MaxExecutionSeconds = 60 };
            var text = PhpConfig.UserIni(s);
            Assert.Contains("upload_max_filesize = 128M", text);
            Assert.Contains("post_max_size = 136M", text);
            Assert.Contains("memory_limit = 512M", text);
            Assert.Contains("date.timezone = Europe/London", text);
            Assert.Contains("E_ALL & ~E_DEPRECATED", text);
            Assert.Null(s.Problem());
            Assert.NotNull(new ProjectPhpSettings { UploadMax = "lots" }.Problem());
            Assert.NotNull(new ProjectPhpSettings { MaxExecutionSeconds = 1 }.Problem());
            Assert.NotNull(new ProjectPhpSettings { TimeZone = "Colombo time" }.Problem());
            Assert.Equal("1032M", PhpConfig.BiggerOf("1G", "8M"));
        }

        [Fact]
        public void Time_zone_maps_to_iana()
        {
            Assert.Equal("Asia/Colombo", PhpConfig.IanaTimeZone(TimeZoneInfo.FindSystemTimeZoneById("Sri Lanka Standard Time")));
            Assert.Equal("UTC", PhpConfig.IanaTimeZone(TimeZoneInfo.Utc));
            var odd = TimeZoneInfo.CreateCustomTimeZone("Test +3", TimeSpan.FromHours(3), "Test", "Test");
            Assert.Equal("Etc/GMT-3", PhpConfig.IanaTimeZone(odd));
        }

        private WebPlan Plan(bool apacheProject = false, DemoSite demo = null) => new WebPlan
        {
            WebPort = 8480, PhpPort = 9010, AdminPort = 2030, ApachePort = apacheProject ? 8810 : 0,
            Sites = new List<SiteInfo>
            {
                new SiteInfo { Name = "shop", DocumentRoot = Path.Combine(_paths.Projects, "shop") },
                new SiteInfo { Name = "blog", DocumentRoot = Path.Combine(_paths.Projects, "blog"), UsesApache = apacheProject },
            },
            Demo = demo,
        };

        [Fact]
        public void Caddyfile_binds_loopback_and_routes_both_url_styles()
        {
            var text = WebConfig.BuildCaddyfile(_paths, Plan(apacheProject: true));
            _out.WriteLine(text);
            Assert.Contains("admin 127.0.0.1:2030", text);
            Assert.Contains("auto_https off", text);
            Assert.Contains("persist_config off", text);
            Assert.Contains("\tbind 127.0.0.1", text);
            Assert.Contains("http://shop.localhost:8480 {", text);
            Assert.Contains("handle_path /shop/* {", text);
            Assert.Contains("php_fastcgi 127.0.0.1:9010", text);
            // the .htaccess project goes to Apache, keeping its /blog/ prefix for path-style URLs
            Assert.Contains("handle /blog/* {", text);
            Assert.Contains("reverse_proxy 127.0.0.1:8810", text);
            Assert.Contains("root * \"" + Path.Combine(_paths.Projects, "shop").Replace('\\', '/') + "\"", text);
            Assert.Contains("request_header -X-Lumina-Demo", text);
            Assert.DoesNotContain("Demo mode", text);
            // dotfiles and vendor\ are never served
            Assert.Contains("path_regexp /\\.", text);
            Assert.Contains("respond /vendor/*", text);
        }

        [Fact]
        public void Demo_site_binds_only_the_lan_address_and_marks_requests()
        {
            var text = WebConfig.BuildCaddyfile(_paths, Plan(demo: new DemoSite { Project = "shop", Address = "192.168.1.20", Port = 8080 }));
            Assert.Contains("http://:8080 {", text);
            Assert.Contains("bind 192.168.1.20", text);
            Assert.Contains("request_header X-Lumina-Demo 1", text);
            Assert.DoesNotContain("bind 0.0.0.0", text);
            Assert.DoesNotContain("3306", text);
            Assert.Equal("http://192.168.1.20:8080/", new DemoSite { Address = "192.168.1.20", Port = 8080 }.Url);
        }

        [Fact]
        public void Httpd_conf_uses_php_cgi_with_the_windows_path_fix()
        {
            var apache = Path.Combine(_root, "engines", "apache");
            Directory.CreateDirectory(Path.Combine(apache, "modules"));
            foreach (var m in new[] { "proxy", "proxy_fcgi", "rewrite", "headers", "dir", "mime" })
                File.WriteAllText(Path.Combine(apache, "modules", "mod_" + m + ".so"), "");
            var stack = WebStack.FromFolders(null, null, apache);
            var text = WebConfig.BuildHttpdConf(_paths, stack, Plan(apacheProject: true));
            _out.WriteLine(text);
            Assert.Contains("Listen 127.0.0.1:8810", text);
            Assert.Contains("SetHandler \"proxy:fcgi://127.0.0.1:9010/\"", text);
            Assert.Contains("ProxyFCGISetEnvIf", text);
            Assert.Contains("AcceptFilter http none", text);
            Assert.Contains("ServerName blog.localhost", text);
            Assert.DoesNotContain("ServerName shop.localhost", text);   // Caddy serves that one
            Assert.Contains("AllowOverride All", text);
            Assert.Contains("LoadModule rewrite_module", text);
            Assert.DoesNotContain("LoadModule deflate_module", text);   // no .so, no line
            // in front (no Caddy) every project is there and Apache writes the access logs
            var front = Plan(apacheProject: true);
            front.ApacheInFront = true;
            var t2 = WebConfig.BuildHttpdConf(_paths, stack, front);
            Assert.Contains("Listen 127.0.0.1:8480", t2);
            Assert.Contains("ServerName shop.localhost", t2);
            Assert.Contains("CustomLog", t2);
        }

        [Fact]
        public void Index_page_lists_projects_with_both_urls()
        {
            var html = WebConfig.BuildIndexPage(Plan(apacheProject: true));
            Assert.Contains("http://shop.localhost:8480/", html);
            Assert.Contains("href=\"/blog/\"", html);
            Assert.Contains("Apache", html);
            Assert.Contains("No projects yet", WebConfig.BuildIndexPage(new WebPlan { WebPort = 80 }));
        }

        [Fact]
        public void Access_log_lines_from_caddy_and_apache()
        {
            var caddy = "{\"level\":\"info\",\"ts\":1764000000.5,\"logger\":\"http.log.access\",\"msg\":\"handled request\",\"request\":{\"remote_ip\":\"127.0.0.1\",\"proto\":\"HTTP/1.1\",\"method\":\"POST\",\"host\":\"shop.localhost:8480\",\"uri\":\"/index.php?a=1\\u0026b=2\",\"headers\":{}},\"bytes_read\":0,\"user_id\":\"\",\"duration\":0.0123,\"size\":5,\"status\":302,\"resp_headers\":{}}";
            var e = AccessEntry.Parse(caddy);
            Assert.NotNull(e);
            Assert.Equal("POST", e.Method);
            Assert.Equal("/index.php?a=1&b=2", e.Uri);
            Assert.Equal(302, e.Status);
            Assert.InRange(e.Milliseconds, 12.2, 12.4);
            var a = AccessEntry.Parse("2026-11-24 10:15:30 GET \"/hello?x=1\" 200 4500");
            Assert.Equal(200, a.Status);
            Assert.Equal("/hello?x=1", a.Uri);
            Assert.Equal(4.5, a.Milliseconds, 3);
            Assert.Null(AccessEntry.Parse("garbage"));
        }

        [Fact]
        public void Project_names()
        {
            Assert.Null(ProjectNames.Problem("my-shop"));
            Assert.Contains("lowercase", ProjectNames.Problem("MyShop"));
            Assert.Contains("hyphens", ProjectNames.Problem("my shop"));
            Assert.Contains("hyphens", ProjectNames.Problem("my_shop"));
            Assert.NotNull(ProjectNames.Problem("-shop"));
            Assert.NotNull(ProjectNames.Problem("phpmyadmin"));
            Assert.NotNull(ProjectNames.Problem("café"));
            Assert.Contains("already", ProjectNames.Problem("shop", new[] { "Shop" }));
            Assert.Equal("my-shop", ProjectNames.Suggest("My Shop!"));
            Assert.Equal("my_shop", ProjectNames.DatabaseFor("my-shop"));
            Assert.Equal("my-project", ProjectNames.Suggest("???"));
        }

        [Fact]
        public void Templates_are_embedded_and_offered()
        {
            var svc = new ProjectService(_paths, FakeStack("mysqli"));
            var t = svc.Templates;
            Assert.Equal(new[] { "empty", "crud", "login", "api", "laravel", "wordpress" }, t.Select(x => x.Id));
            Assert.True(t.Take(4).All(x => x.IsAvailable));
            Assert.False(t.Single(x => x.Id == "laravel").IsAvailable);    // no composer.phar in the fake stack
            Assert.False(t.Single(x => x.Id == "wordpress").IsAvailable);
            Assert.Contains("define('DB_PORT', {{DB_PORT}});", Web.Embedded.Text("templates/_shared/config.php"));
            Assert.Contains("CREATE TABLE IF NOT EXISTS students", Web.Embedded.Text("templates/crud/schema.sql"));
            Assert.Contains("LuminaErrors::install();", ErrorPage.Script(_paths.Projects, _paths.ProjectLogs, 3307));
            Assert.Contains("const DB_PORT = '3307';", ErrorPage.Script(_paths.Projects, _paths.ProjectLogs, 3307));
        }

        [Fact]
        public void Mime_parses_multipart_encoded_words_and_attachments()
        {
            var eml =
                "X-Lumina-Received: 2026-11-24 09:30:00\r\n" +
                "From: =?UTF-8?B?4LeB4LeP4Lax4LeK4Lat4LeS?= <shanthi@x.lk>\r\n" +
                "To: lecturer@x.lk\r\n" +
                "Subject: =?UTF-8?Q?Marks_=E2=9C=93?= =?UTF-8?Q?_ready?=\r\n" +
                "MIME-Version: 1.0\r\n" +
                "Content-Type: multipart/mixed; boundary=\"outer\"\r\n\r\n" +
                "--outer\r\n" +
                "Content-Type: multipart/alternative;\r\n boundary=inner\r\n\r\n" +
                "--inner\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\n" +
                "Hello =E2=9C=93 soft=\r\nbreak\r\n" +
                "--inner\r\nContent-Type: text/html; charset=utf-8\r\n\r\n<p>Hello <img src=\"cid:logo1\"></p>\r\n" +
                "--inner--\r\n" +
                "--outer\r\nContent-Type: image/png\r\nContent-ID: <logo1>\r\nContent-Disposition: inline\r\nContent-Transfer-Encoding: base64\r\n\r\niVBORw0KGgo=\r\n" +
                "--outer\r\nContent-Type: application/pdf; name=\"x.pdf\"\r\nContent-Disposition: attachment; filename*=UTF-8''r%C3%A9sum%C3%A9.pdf\r\nContent-Transfer-Encoding: base64\r\n\r\nJVBERi0x\r\n" +
                "--outer--\r\n";
            var m = MimeMessage.Parse(Encoding.UTF8.GetBytes(eml));
            Assert.Equal("Marks ✓ ready", m.Subject);
            Assert.Equal("ශාන්ති <shanthi@x.lk>", m.From);
            Assert.Equal("Hello ✓ softbreak", m.TextBody.Trim());
            Assert.Contains("<p>Hello", m.HtmlBody);
            Assert.Equal(new DateTime(2026, 11, 24, 9, 30, 0), m.Received);
            Assert.Equal(2, m.Attachments.Count);
            var pdf = m.Attachments.Single(a => a.ContentType == "application/pdf");
            Assert.Equal("résumé.pdf", pdf.FileName);
            Assert.Equal("%PDF-1", Encoding.ASCII.GetString(pdf.Data));
            Assert.Contains("data:image/png;base64,iVBORw0KGgo=", m.DisplayHtml());
        }

        [Fact]
        public async Task Mail_catcher_speaks_smtp_and_saves_eml()
        {
            using (var catcher = new MailCatcher(_paths))
            {
                string saved = null;
                var got = new TaskCompletionSource<string>();
                catcher.MessageReceived += f => got.TrySetResult(f);
                catcher.Start(2591);
                Assert.True(catcher.IsRunning);
                using (var c = new TcpClient())
                {
                    await c.ConnectAsync("127.0.0.1", catcher.Port);
                    var s = c.GetStream();
                    var r = new StreamReader(s, Encoding.ASCII);
                    var w = new StreamWriter(s, new UTF8Encoding(false)) { NewLine = "\r\n", AutoFlush = true };
                    Assert.StartsWith("220", await r.ReadLineAsync());
                    await w.WriteLineAsync("EHLO test");
                    string line;
                    do { line = await r.ReadLineAsync(); } while (line.StartsWith("250-"));
                    Assert.StartsWith("250 ", line);
                    await w.WriteLineAsync("MAIL FROM:<php@lumina.localhost> SIZE=100");
                    Assert.StartsWith("250", await r.ReadLineAsync());
                    await w.WriteLineAsync("DATA");
                    Assert.StartsWith("503", await r.ReadLineAsync());   // no recipient yet
                    await w.WriteLineAsync("RCPT TO:<student@x.lk>");
                    Assert.StartsWith("250", await r.ReadLineAsync());
                    await w.WriteLineAsync("DATA");
                    Assert.StartsWith("354", await r.ReadLineAsync());
                    await w.WriteLineAsync("Subject: Test ✓");
                    await w.WriteLineAsync("");
                    await w.WriteLineAsync("..starts with a dot");
                    await w.WriteLineAsync("ශාන්ති");
                    await w.WriteLineAsync(".");
                    Assert.StartsWith("250", await r.ReadLineAsync());
                    await w.WriteLineAsync("QUIT");
                    Assert.StartsWith("221", await r.ReadLineAsync());
                }
                saved = await Task.WhenAny(got.Task, Task.Delay(5000)) == got.Task ? got.Task.Result : null;
                Assert.NotNull(saved);
                var m = MimeMessage.Load(saved);
                Assert.Equal("Test ✓", m.Subject);
                Assert.Equal(".starts with a dot\r\nශාන්ති\r\n", m.TextBody);
                Assert.Equal("student@x.lk", m.Header("X-Lumina-Envelope-To"));
                Assert.Single(MailStore.List(_paths));
                Assert.Equal(1, MailStore.DeleteAll(_paths));
                Assert.Equal(catcher.Port, LuminaState.Load(_paths).MailPort);
            }
        }

        [Fact]
        public void Smtp_address_parsing()
        {
            Assert.Equal("a@b.lk", MailCatcher.Address("MAIL FROM:<a@b.lk> SIZE=12"));
            Assert.Equal("a@b.lk", MailCatcher.Address("RCPT TO: a@b.lk"));
            Assert.Equal("", MailCatcher.Address("MAIL FROM:<>"));
        }

        [Fact]
        public void Qr_code_versions_and_structure()
        {
            var url = "http://192.168.100.200:8080/";
            var qr = QrCode.EncodeText(url);
            Assert.InRange(qr.Version, 2, 3);
            Assert.Equal(qr.Version * 4 + 17, qr.Size);
            // finder pattern corners are dark, separators light
            Assert.True(qr[0, 0] && qr[6, 6] && qr[qr.Size - 1, 0] && qr[0, qr.Size - 1]);
            Assert.False(qr[7, 7]);
            Assert.True(qr[8, qr.Size - 8]);    // the dark module
            var big = QrCode.EncodeText(new string('x', 300));
            Assert.True(big.Version >= 10);
            var dir = Environment.GetEnvironmentVariable("LUMINA_QR_OUT");
            if (!string.IsNullOrEmpty(dir))
            {
                // Written for decoding by hand with OpenCV (see PROGRESS.md).
                qr.WritePgm(Path.Combine(dir, "qr-small.pgm"));
                big.WritePgm(Path.Combine(dir, "qr-big.pgm"), 4);
                QrCode.EncodeText("http://shop.localhost/ ශාන්ති ✓", QrCode.Ecc.High).WritePgm(Path.Combine(dir, "qr-utf8.pgm"));
            }
        }

        [Fact]
        public void Composer_console_arguments()
        {
            Assert.Equal(new[] { "require", "monolog/monolog" }, Composer.SplitCommandLine("composer require monolog/monolog"));
            Assert.Equal(new[] { "require", "a b", "--dev" }, Composer.SplitCommandLine("require \"a b\"  --dev"));
            Assert.Empty(Composer.SplitCommandLine("   "));
        }

        [Fact]
        public void Pack_names_and_env_files()
        {
            Assert.Equal("HNDIT-2024-F-001_my-shop_2026-11-24.zip", ProjectArchive.PackName("HNDIT/2024/F/001", "my-shop", new DateTime(2026, 11, 24)));
            Assert.Equal("my-shop_2026-11-24.zip", ProjectArchive.PackName("  ", "my-shop", new DateTime(2026, 11, 24)));
            var env = Path.Combine(_root, ".env");
            File.WriteAllLines(env, new[] { "APP_NAME=Laravel", "DB_HOST=localhost", "# DB_PORT=3306" });
            EnvFile.Set(env, new Dictionary<string, string> { ["DB_HOST"] = "127.0.0.1", ["DB_PORT"] = "3307", ["DB_PASSWORD"] = "p#ss word" });
            var lines = File.ReadAllLines(env);
            Assert.Equal(new[] { "APP_NAME=Laravel", "DB_HOST=127.0.0.1", "DB_PORT=3307", "DB_PASSWORD=\"p#ss word\"" }, lines);
        }

        [Fact]
        public void Unusable_folders_get_suggestions()
        {
            Directory.CreateDirectory(Path.Combine(_paths.Projects, "My Shop"));
            Directory.CreateDirectory(Path.Combine(_paths.Projects, "blog"));
            var svc = new ProjectService(_paths, null);
            Assert.Equal(new[] { "blog" }, svc.List().Select(p => p.Name));
            var bad = svc.UnusableFolders();
            Assert.Single(bad);
            Assert.Equal("My Shop", bad[0].Key);
            Assert.Equal("my-shop", bad[0].Value);
            svc.RenameFolder("My Shop", "my-shop");
            Assert.Equal(new[] { "blog", "my-shop" }, svc.List().Select(p => p.Name));
        }

        [Fact]
        public void Laravel_layout_and_htaccess_detection()
        {
            var dir = Path.Combine(_paths.Projects, "app");
            Directory.CreateDirectory(Path.Combine(dir, "public"));
            File.WriteAllText(Path.Combine(dir, "artisan"), "");
            File.WriteAllText(Path.Combine(dir, "public", "index.php"), "");
            var p = new Project("app", dir, null);
            Assert.Equal(Path.Combine(dir, "public"), p.DocumentRoot);
            Assert.False(p.NeedsApache);
            File.WriteAllText(Path.Combine(dir, "public", ".htaccess"), "RewriteEngine On");
            Assert.True(p.NeedsApache);
        }
    }
}
