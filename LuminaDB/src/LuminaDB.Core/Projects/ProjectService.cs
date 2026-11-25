using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LuminaDB.Core.Sql;
using LuminaDB.Core.Web;
using Microsoft.Win32;
using MySqlConnector;

namespace LuminaDB.Core.Projects
{
    /// <summary>A starter the New project form offers (SPEC §5.2).</summary>
    public sealed class ProjectTemplate
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        /// <summary>Why it can't be used on this copy, or null.</summary>
        public string Unavailable { get; set; }
        public bool IsAvailable => Unavailable == null;
    }

    /// <summary>Lists, creates and deletes projects in projects\ (SPEC §5.2). Metadata stays in config\projects\.</summary>
    public sealed class ProjectService
    {
        private readonly AppPaths _paths;
        private readonly WebStack _stack;

        public ProjectService(AppPaths paths, WebStack stack)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _stack = stack;
        }

        public string Root => _paths.Projects;

        public IReadOnlyList<ProjectTemplate> Templates => new List<ProjectTemplate>
        {
            new ProjectTemplate { Id = "empty", Title = "Empty PHP", Description = "One index.php and a database connection, ready for your own code." },
            new ProjectTemplate { Id = "crud", Title = "CRUD starter", Description = "List, add, edit and delete rows of a students table, with prepared statements." },
            new ProjectTemplate { Id = "login", Title = "Login and registration", Description = "Sign up, sign in and sign out with sessions and password_hash()." },
            new ProjectTemplate { Id = "api", Title = "REST API", Description = "JSON endpoints for GET, POST, PUT and DELETE on an items table." },
            new ProjectTemplate
            {
                Id = "laravel", Title = "Laravel", Description = "A new Laravel app made with the bundled Composer. Needs the internet once to download Laravel.",
                Unavailable = _stack != null && _stack.HasComposer && _stack.HasPhp ? null : "Composer isn't included in this copy of LuminaDB.",
            },
            new ProjectTemplate
            {
                Id = "wordpress", Title = "WordPress", Description = "WordPress with its database ready; finish the five-minute setup in the browser.",
                Unavailable = _stack != null && _stack.HasWordPress ? null : "WordPress isn't included in this copy of LuminaDB.",
            },
        };

        // ------------------------------------------------------------------ listing

        public List<Project> List()
        {
            Directory.CreateDirectory(_paths.Projects);
            var result = new List<Project>();
            foreach (var dir in Directory.GetDirectories(_paths.Projects).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                // Folders copied in by hand with names the web server can't use are skipped; the UI offers to rename them.
                if (ProjectNames.Problem(name) != null && ProjectNames.Problem(name.ToLowerInvariant()) != null) continue;
                result.Add(new Project(name, dir, LoadMeta(name)));
            }
            return result;
        }

        /// <summary>Folders in projects\ that can't be served as they are (spaces, capitals...), with a suggested name.</summary>
        public List<KeyValuePair<string, string>> UnusableFolders()
        {
            if (!Directory.Exists(_paths.Projects)) return new List<KeyValuePair<string, string>>();
            var taken = Directory.GetDirectories(_paths.Projects).Select(Path.GetFileName).ToList();
            return taken.Where(n => ProjectNames.Problem(n) != null)
                .Select(n => new KeyValuePair<string, string>(n, Unique(ProjectNames.Suggest(n), taken))).ToList();
        }

        public Project Find(string name) => List().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        public List<SiteInfo> Sites() => List().Select(p => new SiteInfo { Name = p.Name.ToLowerInvariant(), DocumentRoot = p.DocumentRoot, UsesApache = p.NeedsApache }).ToList();

        private string MetaFile(string name) => Path.Combine(_paths.ProjectMetaDir, name.ToLowerInvariant() + ".json");

        public ProjectMeta LoadMeta(string name) => JsonFile.Read<ProjectMeta>(MetaFile(name));

        public void SaveMeta(ProjectMeta meta) => JsonFile.Write(MetaFile(meta.Name), meta);

        // ------------------------------------------------------------------ create

        /// <summary>
        /// Creates projects\&lt;name&gt; from a template plus its database, and writes config.php (or .env) with the
        /// connection details. If anything fails the half-made folder is removed again.
        /// </summary>
        public async Task<Project> CreateAsync(string name, string templateId, MariaDbService server, IProgress<string> progress = null, CancellationToken ct = default)
        {
            var existing = Directory.Exists(_paths.Projects) ? Directory.GetDirectories(_paths.Projects).Select(Path.GetFileName) : Enumerable.Empty<string>();
            var problem = ProjectNames.Problem(name, existing);
            if (problem != null) throw new LuminaException(ErrorKind.InvalidInput, problem);
            var template = Templates.FirstOrDefault(t => t.Id == templateId) ?? throw new LuminaException(ErrorKind.InvalidInput, "Pick a template.");
            if (!template.IsAvailable) throw new LuminaException(ErrorKind.InvalidInput, template.Unavailable);
            if (server == null || server.State != ServerState.Running)
                throw new LuminaException(ErrorKind.ServerNotRunning, "Start the database server first: a new project gets its own database.");

            var folder = Path.Combine(_paths.Projects, name);
            var dbName = ProjectNames.DatabaseFor(name);
            bool createdDb = false;
            Directory.CreateDirectory(folder);
            try
            {
                progress?.Report("Creating the database " + dbName + "…");
                using (var c = await server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @n";
                    cmd.Parameters.AddWithValue("@n", dbName);
                    bool exists = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
                    if (!exists)
                    {
                        await DatabaseCatalog.CreateAsync(server, dbName, ct).ConfigureAwait(false);
                        createdDb = true;
                    }
                }

                var cs = server.ConnectionString();
                var values = new Dictionary<string, string>
                {
                    ["{{PROJECT}}"] = name,
                    ["{{DB_NAME}}"] = dbName,
                    ["{{DB_PORT}}"] = server.Port.ToString(CultureInfo.InvariantCulture),
                    ["{{DB_USER}}"] = cs.UserID,
                    ["{{DB_PASS}}"] = PhpEscape(cs.Password ?? ""),
                    ["{{WEB_PORT}}"] = (LuminaState.Load(_paths).WebPort ?? 80).ToString(CultureInfo.InvariantCulture),
                    ["{{WEB_SUFFIX}}"] = (LuminaState.Load(_paths).WebPort ?? 80) == 80 ? "" : ":" + (LuminaState.Load(_paths).WebPort ?? 80).ToString(CultureInfo.InvariantCulture),
                    ["{{TOOLS}}"] = CommandLineTools.Folder(_paths),
                };

                var meta = new ProjectMeta { Name = name, Template = templateId, Database = dbName, Created = DateTime.Now, Php = new ProjectPhpSettings() };
                switch (templateId)
                {
                    case "laravel":
                        await CreateLaravelAsync(folder, server, dbName, progress, ct).ConfigureAwait(false);
                        meta.WebRoot = "public";
                        break;
                    case "wordpress":
                        progress?.Report("Unpacking WordPress…");
                        CreateWordPress(folder, server, dbName);
                        break;
                    default:
                        progress?.Report("Writing the starter files…");
                        WriteTemplate(templateId, folder, values);
                        var schema = Embedded.Exists("templates/" + templateId + "/schema.sql") ? Embedded.Text("templates/" + templateId + "/schema.sql") : null;
                        if (schema != null)
                        {
                            progress?.Report("Creating the starter tables…");
                            await RunSqlAsync(server, dbName, schema, ct).ConfigureAwait(false);
                        }
                        break;
                }
                SaveMeta(meta);
                WriteUserIni(new Project(name, folder, meta));
                progress?.Report("Done.");
                return new Project(name, folder, meta);
            }
            catch
            {
                TryDeleteFolder(folder);
                if (createdDb)
                {
                    try
                    {
                        using (var c = await server.OpenConnectionAsync(null, CancellationToken.None).ConfigureAwait(false))
                        using (var cmd = c.CreateCommand())
                        {
                            cmd.CommandText = "DROP DATABASE IF EXISTS " + SqlLexer.Quote(dbName);
                            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                        }
                    }
                    catch (Exception) { /* leave the empty database; it does no harm */ }
                }
                throw;
            }
        }

        private static void WriteTemplate(string templateId, string folder, IDictionary<string, string> values)
        {
            void Write(string resource, string relative)
            {
                var text = Embedded.Text(resource);
                foreach (var kv in values) text = text.Replace(kv.Key, kv.Value);
                var target = Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllText(target, text, new UTF8Encoding(false));
            }

            foreach (var res in Embedded.Names.Where(n => n.StartsWith("templates/_shared/", StringComparison.Ordinal)))
                Write(res, res.Substring("templates/_shared/".Length));
            var prefix = "templates/" + templateId + "/";
            foreach (var res in Embedded.Names.Where(n => n.StartsWith(prefix, StringComparison.Ordinal) && !n.EndsWith("/schema.sql", StringComparison.Ordinal)))
                Write(res, res.Substring(prefix.Length));
        }

        /// <summary>Runs a template's schema.sql (trusted, ours) statement by statement.</summary>
        private static async Task RunSqlAsync(MariaDbService server, string database, string sql, CancellationToken ct)
        {
            using (var c = await server.OpenConnectionAsync(database, ct).ConfigureAwait(false))
            {
                foreach (var stmt in SqlSplitter.Split(sql))
                {
                    if (string.IsNullOrWhiteSpace(stmt.Text)) continue;
                    using (var cmd = c.CreateCommand()) { cmd.CommandText = stmt.Text; await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
                }
            }
        }

        private async Task CreateLaravelAsync(string folder, MariaDbService server, string dbName, IProgress<string> progress, CancellationToken ct)
        {
            progress?.Report("Downloading Laravel with Composer (this takes a minute)…");
            // Composer refuses a non-empty folder, and ours exists already (it is empty).
            var code = await Composer.RunAsync(_paths, _stack, Path.GetDirectoryName(folder),
                new[] { "create-project", "laravel/laravel", Path.GetFileName(folder), "--prefer-dist", "--no-interaction", "--no-progress" },
                line => progress?.Report(line), ct).ConfigureAwait(false);
            if (code != 0 || !File.Exists(Path.Combine(folder, "artisan")))
                throw new LuminaException(ErrorKind.Unknown, "Composer couldn't create the Laravel project (exit code " + code + "). Check the internet connection; the Composer log is in the Logs tab.");
            var env = Path.Combine(folder, ".env");
            if (!File.Exists(env) && File.Exists(Path.Combine(folder, ".env.example"))) File.Copy(Path.Combine(folder, ".env.example"), env);
            var cs = server.ConnectionString();
            int port = LuminaState.Load(_paths).WebPort ?? 80;
            EnvFile.Set(env, new Dictionary<string, string>
            {
                ["DB_CONNECTION"] = "mariadb",
                ["DB_HOST"] = "127.0.0.1",
                ["DB_PORT"] = server.Port.ToString(CultureInfo.InvariantCulture),
                ["DB_DATABASE"] = dbName,
                ["DB_USERNAME"] = cs.UserID,
                ["DB_PASSWORD"] = cs.Password ?? "",
                ["MAIL_MAILER"] = "smtp",
                ["MAIL_HOST"] = "127.0.0.1",
                ["MAIL_PORT"] = (LuminaState.Load(_paths).MailPort ?? 2525).ToString(CultureInfo.InvariantCulture),
                ["APP_URL"] = "http://" + Path.GetFileName(folder).ToLowerInvariant() + ".localhost" + (port == 80 ? "" : ":" + port.ToString(CultureInfo.InvariantCulture)),
            });

            // Composer's post-install script migrated an SQLite file before .env pointed at MariaDB, so Laravel's own
            // tables (sessions, cache, jobs, users) don't exist in the project's database yet: make them there now.
            progress?.Report("Creating Laravel's tables in " + dbName + "…");
            code = await Composer.RunScriptAsync(_paths, _stack, folder, new[] { "artisan", "migrate", "--force", "--no-interaction", "--no-ansi" },
                line => progress?.Report(line), ct).ConfigureAwait(false);
            if (code != 0)
                throw new LuminaException(ErrorKind.Unknown, "Laravel was downloaded, but `php artisan migrate` failed (exit code " + code + "). The details are in logs\\composer.log.");
            var sqlite = Path.Combine(folder, "database", "database.sqlite");
            if (File.Exists(sqlite) && new FileInfo(sqlite).Length == 0) File.Delete(sqlite);   // the unused empty file Laravel made
        }

        private void CreateWordPress(string folder, MariaDbService server, string dbName)
        {
            using (var zip = ZipFile.OpenRead(_stack.WordPressZip))
            {
                foreach (var e in zip.Entries)
                {
                    // The official zip has everything under wordpress/.
                    var rel = e.FullName.StartsWith("wordpress/", StringComparison.Ordinal) ? e.FullName.Substring(10) : e.FullName;
                    if (rel.Length == 0) continue;
                    var target = Path.GetFullPath(Path.Combine(folder, rel));
                    if (!target.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    if (rel.EndsWith("/", StringComparison.Ordinal)) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                }
            }
            var sample = Path.Combine(folder, "wp-config-sample.php");
            if (!File.Exists(sample)) throw new LuminaException(ErrorKind.Unknown, "The bundled WordPress zip looks damaged (no wp-config-sample.php).");
            var cs = server.ConnectionString();
            var text = File.ReadAllText(sample)
                .Replace("'database_name_here'", ErrorPage.PhpString(dbName))
                .Replace("'username_here'", ErrorPage.PhpString(cs.UserID))
                .Replace("'password_here'", ErrorPage.PhpString(cs.Password ?? ""))
                .Replace("'localhost'", ErrorPage.PhpString("127.0.0.1:" + server.Port.ToString(CultureInfo.InvariantCulture)));
            // Fresh salts instead of "put your unique phrase here".
            var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
            while (text.Contains("'put your unique phrase here'"))
            {
                var bytes = new byte[48];
                rng.GetBytes(bytes);
                var salt = Convert.ToBase64String(bytes);
                int i = text.IndexOf("'put your unique phrase here'", StringComparison.Ordinal);
                text = text.Substring(0, i) + ErrorPage.PhpString(salt) + text.Substring(i + "'put your unique phrase here'".Length);
            }
            File.WriteAllText(Path.Combine(folder, "wp-config.php"), text, new UTF8Encoding(false));
        }

        private static string PhpEscape(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        // ------------------------------------------------------------------ settings, delete, rename

        /// <summary>Writes the project's .user.ini from its PHP settings (applies within 5 seconds, no restart).</summary>
        public void WriteUserIni(Project p)
        {
            var s = p.Meta.Php ?? new ProjectPhpSettings();
            File.WriteAllText(Path.Combine(p.DocumentRoot, ".user.ini"), PhpConfig.UserIni(s), new UTF8Encoding(false));
        }

        public void SavePhpSettings(Project p, ProjectPhpSettings settings)
        {
            var problem = settings.Problem();
            if (problem != null) throw new LuminaException(ErrorKind.InvalidInput, problem);
            p.Meta.Php = settings.Clone();
            if (string.IsNullOrEmpty(p.Meta.Name)) p.Meta.Name = p.Name;
            SaveMeta(p.Meta);
            WriteUserIni(p);
        }

        /// <summary>Moves the folder to the Recycle Bin (never a permanent delete) and optionally drops the database.</summary>
        public async Task DeleteAsync(Project p, bool dropDatabase, MariaDbService server, CancellationToken ct = default)
        {
            if (dropDatabase && (server == null || server.State != ServerState.Running))
                throw new LuminaException(ErrorKind.ServerNotRunning, "Start the database server to delete the database too, or keep the database.");
            RecycleBin.Send(p.Folder);
            JsonFile.Delete(MetaFile(p.Name));
            if (dropDatabase)
            {
                using (var c = await server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
                using (var cmd = c.CreateCommand())
                {
                    cmd.CommandText = "DROP DATABASE IF EXISTS " + SqlLexer.Quote(p.Database);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
        }

        /// <summary>
        /// Copies an existing folder (an old XAMPP htdocs project, a classmate's zip extracted somewhere) into projects\
        /// under a usable name. The original stays where it was.
        /// </summary>
        public async Task<Project> ImportFolderAsync(string source, string name, CancellationToken ct = default)
        {
            if (!Directory.Exists(source)) throw new LuminaException(ErrorKind.InvalidInput, "That folder doesn't exist any more.");
            var full = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar);
            var root = Path.GetFullPath(_paths.Projects).TrimEnd(Path.DirectorySeparatorChar);
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
                throw new LuminaException(ErrorKind.InvalidInput, "That folder is already inside LuminaDB's projects folder.");
            Directory.CreateDirectory(_paths.Projects);
            var problem = ProjectNames.Problem(name, Directory.GetDirectories(_paths.Projects).Select(Path.GetFileName));
            if (problem != null) throw new LuminaException(ErrorKind.InvalidInput, problem);
            var target = Path.Combine(_paths.Projects, name);
            try
            {
                await Task.Run(() => CopyTree(full, target, ct), ct).ConfigureAwait(false);
            }
            catch
            {
                TryDeleteFolder(target);
                throw;
            }
            // the database its PHP connects to (feedback_db), not one guessed from the folder name
            var found = await Task.Run(() => ProjectDatabaseScan.Scan(target), ct).ConfigureAwait(false);
            var meta = new ProjectMeta { Name = name, Template = "imported", Database = found.Database ?? ProjectNames.DatabaseFor(name), Created = DateTime.Now, Php = new ProjectPhpSettings() };
            SaveMeta(meta);
            var p = new Project(name, target, meta);
            if (!File.Exists(Path.Combine(p.DocumentRoot, ".user.ini"))) WriteUserIni(p);
            return p;
        }

        private static void CopyTree(string from, string to, CancellationToken ct)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
            }
            foreach (var dir in Directory.GetDirectories(from))
            {
                if ((new DirectoryInfo(dir).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)), ct);
            }
        }

        /// <summary>Renames a hand-copied folder to a usable project name.</summary>
        public string RenameFolder(string folderName, string newName)
        {
            var existing = Directory.GetDirectories(_paths.Projects).Select(Path.GetFileName).Where(n => !string.Equals(n, folderName, StringComparison.OrdinalIgnoreCase));
            var problem = ProjectNames.Problem(newName, existing);
            if (problem != null) throw new LuminaException(ErrorKind.InvalidInput, problem);
            var from = Path.Combine(_paths.Projects, folderName);
            var to = Path.Combine(_paths.Projects, newName);
            if (string.Equals(folderName, newName, StringComparison.OrdinalIgnoreCase))
            {
                var tmp = to + ".lumina-rename";
                Directory.Move(from, tmp);
                Directory.Move(tmp, to);
            }
            else Directory.Move(from, to);
            return to;
        }

        private static string Unique(string name, ICollection<string> taken)
        {
            var n = name;
            for (int i = 2; taken.Contains(n, StringComparer.OrdinalIgnoreCase); i++) n = name + "-" + i.ToString(CultureInfo.InvariantCulture);
            return n;
        }

        private static void TryDeleteFolder(string folder)
        {
            for (int i = 0; i < 3; i++)
            {
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); return; }
                catch (IOException) { Thread.Sleep(200); }
                catch (UnauthorizedAccessException) { Thread.Sleep(200); }
            }
        }

        // ------------------------------------------------------------------ editors

        /// <summary>Editors that can open a folder, best first (SPEC §5.2: VS Code / Notepad++ / Explorer).</summary>
        public static List<KeyValuePair<string, string>> Editors()
        {
            var result = new List<KeyValuePair<string, string>>();
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string First(params string[] candidates) => candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(c));

            var code = First(Path.Combine(local, "Programs", "Microsoft VS Code", "Code.exe"), Path.Combine(pf, "Microsoft VS Code", "Code.exe"), AppPathFromRegistry("Code.exe"));
            if (code != null) result.Add(new KeyValuePair<string, string>("VS Code", code));
            var npp = First(Path.Combine(pf, "Notepad++", "notepad++.exe"), Path.Combine(pf86, "Notepad++", "notepad++.exe"), AppPathFromRegistry("notepad++.exe"));
            if (npp != null) result.Add(new KeyValuePair<string, string>("Notepad++", npp));
            result.Add(new KeyValuePair<string, string>("Explorer", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")));
            return result;
        }

        private static string AppPathFromRegistry(string exe)
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var k = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                        if (k?.GetValue(null) is string s && File.Exists(s.Trim('"'))) return s.Trim('"');
                }
                catch (System.Security.SecurityException) { }
            }
            return null;
        }

        /// <summary>Opens the project folder in an editor (Notepad++ gets the main file, since it can't open folders).</summary>
        public static void OpenInEditor(Project p, string editorExe)
        {
            string arg = p.Folder;
            if (Path.GetFileName(editorExe).Equals("notepad++.exe", StringComparison.OrdinalIgnoreCase))
            {
                var index = new[] { "index.php", Path.Combine("public", "index.php"), "index.html" }.Select(f => Path.Combine(p.Folder, f)).FirstOrDefault(File.Exists);
                arg = index ?? p.Folder;
            }
            Process.Start(new ProcessStartInfo(editorExe, ManagedProcess.Quote(arg)) { UseShellExecute = false })?.Dispose();
        }
    }

    /// <summary>Minimal .env editing: replaces KEY=value lines, adds missing keys, keeps everything else.</summary>
    public static class EnvFile
    {
        public static void Set(string path, IDictionary<string, string> values)
        {
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            foreach (var kv in values)
            {
                var v = Quote(kv.Value);
                int i = lines.FindIndex(l => l.TrimStart().StartsWith(kv.Key + "=", StringComparison.Ordinal) || l.TrimStart().StartsWith("# " + kv.Key + "=", StringComparison.Ordinal));
                if (i >= 0) lines[i] = kv.Key + "=" + v;
                else lines.Add(kv.Key + "=" + v);
            }
            File.WriteAllLines(path, lines, new UTF8Encoding(false));
        }

        private static string Quote(string v)
        {
            if (string.IsNullOrEmpty(v)) return "";
            return v.IndexOfAny(new[] { ' ', '#', '"', '\'', '=', '$' }) >= 0 ? "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : v;
        }
    }
}
