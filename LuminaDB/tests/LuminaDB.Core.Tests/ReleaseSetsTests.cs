using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using LuminaDB.Core.Import;
using LuminaDB.Core.Projects;
using LuminaDB.Core.Web;
using Xunit;
using Xunit.Abstractions;

namespace LuminaDB.Core.Tests
{
    /// <summary>
    /// Step 11: the three engine sets exactly as the installers ship them (packaging\out\stage, made by
    /// packaging\build.ps1), laid out as an installed copy: engines\&lt;set&gt;\ plus the shared phpMyAdmin, Composer and
    /// templates. Each set: a fresh database (the installer's first start), a MariaDB 11.8 dump imported, PHP projects
    /// served by the set's own web server and reading the database, phpMyAdmin, and the expected versions and bitness.
    /// The x86 set runs here as 32-bit programs on 64-bit Windows. Skipped when the stage hasn't been built.
    /// </summary>
    [Collection("RealMariaDb")]
    public class ReleaseSetsTests
    {
        private readonly ITestOutputHelper _out;
        public ReleaseSetsTests(ITestOutputHelper output) { _out = output; }

        public static IEnumerable<object[]> Sets => new[]
        {
            new object[] { EngineSetKind.Modern, "11.8.10", "8.5.", 64, true },
            new object[] { EngineSetKind.Legacy, "10.4.34", "8.2.", 64, false },
            new object[] { EngineSetKind.X86, "10.4.22", "8.2.", 32, false },
        };

        internal static string FindStage()
        {
            var env = Environment.GetEnvironmentVariable("LUMINA_STAGE");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            for (var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
            {
                var c = Path.Combine(d.FullName, "packaging", "out", "stage");
                if (Directory.Exists(Path.Combine(c, "engines"))) return c;
            }
            return null;
        }

        private static void CopyTree(string from, string to)
        {
            var psi = new ProcessStartInfo("robocopy.exe", $"\"{from}\" \"{to}\" /E /NFL /NDL /NJH /NJS /NP /R:1 /W:1")
            { UseShellExecute = false, CreateNoWindow = true };
            using (var p = Process.Start(psi))
            {
                p.WaitForExit();
                if (p.ExitCode >= 8) throw new IOException($"robocopy {from} -> {to} failed ({p.ExitCode})");
            }
        }

        private static (int Status, string Body) Get(int port, string host, string path)
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
                return ((int)resp.StatusCode, sr.ReadToEnd());
        }

        private static string ParityDump()
        {
            for (var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
            {
                var p = Path.Combine(d.FullName, "ParityTests", "database", "dumps", "dump_118.sql");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        [Theory]
        [MemberData(nameof(Sets))]
        public async Task Shipped_set_runs_as_installed(EngineSetKind kind, string mariaDb, string php, int bits, bool caddy)
        {
            var stage = FindStage();
            if (stage == null) { _out.WriteLine("SKIPPED: packaging\\out\\stage not built (run packaging\\build.ps1)."); return; }
            var set = EngineSet.FolderName(kind);
            var root = Path.Combine(Path.GetTempPath(), "Lumina Release " + set + " " + Guid.NewGuid().ToString("N").Substring(0, 6));
            var paths = new AppPaths(root);
            try
            {
                // The installer's layout: the set, with the shared files inside it.
                var t0 = DateTime.UtcNow;
                CopyTree(Path.Combine(stage, "engines", set), Path.Combine(paths.Engines, set));
                CopyTree(Path.Combine(stage, "shared"), Path.Combine(paths.Engines, set));
                _out.WriteLine($"{set}: laid out in {(DateTime.UtcNow - t0).TotalSeconds:0.0} s");
                paths.EnsureCreated();

                var engine = EngineSet.FromAppRoot(paths, kind);
                Assert.Empty(Doctor.EngineFileProblems(engine));
                Assert.Equal(mariaDb, engine.ServerVersion.ToString(3));
                var stack = WebStack.FromAppRoot(paths, kind);
                Assert.True(stack.CanServe, stack.Missing);
                Assert.Equal(caddy, stack.UsesCaddy);
                Assert.True(stack.HasApache);
                Assert.True(stack.HasPhpMyAdmin);
                Assert.True(stack.HasComposer);
                Assert.True(stack.HasWordPress);

                var port = 3480 + (int)kind;
                using (var server = new MariaDbService(paths, engine, null, new MariaDbServiceOptions { PreferredPort = port }))
                {
                    try
                    {
                        // First start: the data folder is made by the set's own install-db.
                        await server.StartAsync();
                        Assert.Equal(ServerState.Running, server.State);

                        // A dump from MariaDB 11.8 imports cleanly (rewritten for 10.4 where needed).
                        var dump = ParityDump();
                        if (dump != null)
                        {
                            var preview = SqlImporter.Preview(dump);
                            var r = await SqlImporter.ImportAsync(server, new ImportOptions { File = dump, Database = preview.NeedsDatabase ? "restored" : null, Target = ImportRewriter.TargetFor(kind) });
                            _out.WriteLine($"{set}: dump_118 {r.Statements} commands, {r.Problems.Count} problems");
                            foreach (var p in r.Problems) _out.WriteLine($"   line {p.Line}: #{p.Code} {p.Message}");
                            Assert.True(r.Clean);
                        }

                        new WebSettings { PreferredWebPort = 8580 + (int)kind, PreferredPhpPort = 9580 + (int)kind, PhpMyAdmin = true }.Save(paths);
                        var projects = new ProjectService(paths, stack);
                        await projects.CreateAsync("t-crud", "crud", server);
                        var probe = Path.Combine(paths.Projects, "probe");
                        Directory.CreateDirectory(probe);
                        File.WriteAllText(Path.Combine(probe, "index.php"),
                            "<?php $db = new mysqli('127.0.0.1', 'root', '', '', " + server.Port + ");" +
                            "echo 'php=', PHP_VERSION, ';bits=', PHP_INT_SIZE * 8, ';db=', $db->server_info, ';sw=', $_SERVER['SERVER_SOFTWARE'] ?? '', ';missing=';" +
                            "echo implode(',', array_filter(['mysqli','pdo_mysql','mbstring','intl','gd','zip','openssl','curl','fileinfo','sodium'], function ($e) { return !extension_loaded($e); }));");

                        using (var web = new WebStackService(paths, kind, stack))
                        {
                            var rt = new PhpRuntime { MariaDbPort = server.Port, MailPort = 2595, TimeZone = "UTC" };
                            await web.StartAsync(projects.Sites(), rt);
                            try
                            {
                                Assert.Equal(WebState.Running, web.State);
                                var body = Get(web.WebPort, "probe.localhost", "/").Body;
                                _out.WriteLine($"{set}: {body}");
                                Assert.Contains("php=" + php, body);
                                Assert.Contains(";bits=" + bits + ";", body);
                                Assert.Contains(";db=" + mariaDb, body);
                                Assert.Contains(";sw=" + (caddy ? "Caddy" : "Apache"), body);
                                Assert.EndsWith(";missing=", body);

                                var crud = Get(web.WebPort, "t-crud.localhost", "/");
                                Assert.Equal(200, crud.Status);
                                Assert.Contains("Nimali Perera", crud.Body);

                                var pma = Get(web.WebPort, "phpmyadmin.localhost", "/index.php?route=/server/databases");
                                Assert.Equal(200, pma.Status);
                                Assert.Contains("information_schema", pma.Body);
                                Assert.Contains("t_crud", pma.Body);
                            }
                            finally { await web.StopAsync(); }
                        }

                        // Composer runs on the set's PHP.
                        var composer = Process.Start(new ProcessStartInfo(stack.PhpExe, $"-n \"{stack.ComposerPhar}\" --version")
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
                        var cv = composer.StandardOutput.ReadToEnd();
                        composer.WaitForExit();
                        Assert.Contains("Composer version", cv);
                    }
                    finally
                    {
                        if (server.State == ServerState.Running) await server.StopAsync();
                    }
                }
            }
            finally
            {
                for (int i = 0; i < 10 && Directory.Exists(root); i++)
                {
                    try { Directory.Delete(root, true); break; }
                    catch (IOException) { await Task.Delay(500); }
                    catch (UnauthorizedAccessException) { await Task.Delay(500); }
                }
            }
        }
    }
}
