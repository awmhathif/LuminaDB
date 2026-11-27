using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LuminaDB.Core.Projects
{
    /// <summary>What a snapshot holds (snapshots\&lt;project&gt;\&lt;stamp&gt;.json).</summary>
    [DataContract]
    public sealed class SnapshotInfo
    {
        [DataMember] public string Project { get; set; }
        [DataMember] public string Database { get; set; }
        [DataMember] public DateTime Taken { get; set; }
        [DataMember] public string Note { get; set; }
        [DataMember] public long CodeBytes { get; set; }
        [DataMember] public long DatabaseBytes { get; set; }
        [DataMember] public bool HasDatabase { get; set; }

        /// <summary>Folder-safe id: 2026-11-24_153012.</summary>
        public string Id { get; set; }
        public string CodeZip { get; set; }
        public string DatabaseSql { get; set; }
        public string SizeText => DatabaseSummary.FormatSize(CodeBytes + DatabaseBytes);
    }

    /// <summary>
    /// Snapshots (SPEC §5.2/§5.10: code + database together, restore either or both) and the share pack
    /// (project zip + &lt;db&gt;.sql + README, named StudentID_Project_YYYY-MM-DD.zip).
    /// </summary>
    public sealed class ProjectArchive
    {
        /// <summary>Never worth keeping: rebuilt by Composer/npm, or private.</summary>
        public static readonly string[] HeavyFolders = { "vendor", "node_modules" };
        public static readonly string[] AlwaysSkipped = { ".git", ".idea", ".vscode", ".vs", RestoreNew, RestoreOld };

        /// <summary>
        /// Left out of the share pack: they hold passwords and keys for this PC (the pack's README says so, and the
        /// example files that come with projects stay in).
        /// </summary>
        public static bool IsSecretFile(string fileName)
        {
            var n = (fileName ?? "").ToLowerInvariant();
            if (n == ".env" || (n.StartsWith(".env.", StringComparison.Ordinal) && !IsExampleName(n))) return true;   // .env, .env.local, .env.production
            if (n == "auth.json") return true;                                   // Composer's tokens
            if (n == ".htpasswd" || n == ".npmrc" || n == ".pgpass" || n == ".my.cnf") return true;
            return n.EndsWith(".pem", StringComparison.Ordinal) || n.EndsWith(".key", StringComparison.Ordinal)
                || n.EndsWith(".pfx", StringComparison.Ordinal) || n.EndsWith(".p12", StringComparison.Ordinal)
                || n == "id_rsa" || n == "id_ed25519";
        }

        private static bool IsExampleName(string n) =>
            n.EndsWith(".example", StringComparison.Ordinal) || n.EndsWith(".sample", StringComparison.Ordinal)
            || n.EndsWith(".dist", StringComparison.Ordinal) || n.EndsWith(".template", StringComparison.Ordinal);

        private readonly AppPaths _paths;

        public ProjectArchive(AppPaths paths) { _paths = paths ?? throw new ArgumentNullException(nameof(paths)); }

        private string SnapshotDir(string project) => Path.Combine(_paths.Snapshots, project);

        // ------------------------------------------------------------------ snapshots

        public async Task<SnapshotInfo> TakeSnapshotAsync(Project p, MariaDbService server, string note, IProgress<string> progress = null, CancellationToken ct = default)
        {
            var now = DateTime.Now;
            var dir = SnapshotDir(p.Name);
            Directory.CreateDirectory(dir);
            var id = now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture);
            for (int i = 2; File.Exists(Path.Combine(dir, id + ".json")); i++) id = now.ToString("yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture) + "-" + i;
            var info = new SnapshotInfo { Project = p.Name, Database = p.Database, Taken = now, Note = (note ?? "").Trim(), Id = id };
            var zip = Path.Combine(dir, id + ".zip");
            var sql = Path.Combine(dir, id + ".sql");
            try
            {
                progress?.Report("Saving the code…");
                // Snapshots keep vendor\ too: restoring must give back a project that runs without the internet.
                await Task.Run(() => ZipFolder(p.Folder, zip, skipHeavy: false, ct), ct).ConfigureAwait(false);
                info.CodeBytes = new FileInfo(zip).Length;
                if (server != null && server.State == ServerState.Running && await DatabaseExistsAsync(server, p.Database, ct).ConfigureAwait(false))
                {
                    progress?.Report("Saving the database " + p.Database + "…");
                    await DatabaseDumper.DumpAsync(server, p.Database, sql, ct).ConfigureAwait(false);
                    info.HasDatabase = true;
                    info.DatabaseBytes = new FileInfo(sql).Length;
                }
                JsonFile.Write(Path.Combine(dir, id + ".json"), info);
            }
            catch
            {
                foreach (var f in new[] { zip, sql, Path.Combine(dir, id + ".json") }) TryDelete(f);
                throw;
            }
            info.CodeZip = zip;
            info.DatabaseSql = info.HasDatabase ? sql : null;
            return info;
        }

        public List<SnapshotInfo> ListSnapshots(string project)
        {
            var dir = SnapshotDir(project);
            if (!Directory.Exists(dir)) return new List<SnapshotInfo>();
            var result = new List<SnapshotInfo>();
            foreach (var json in Directory.GetFiles(dir, "*.json"))
            {
                var info = JsonFile.Read<SnapshotInfo>(json);
                if (info == null) continue;
                info.Id = Path.GetFileNameWithoutExtension(json);
                info.CodeZip = Path.Combine(dir, info.Id + ".zip");
                info.DatabaseSql = info.HasDatabase ? Path.Combine(dir, info.Id + ".sql") : null;
                if (!File.Exists(info.CodeZip)) continue;
                result.Add(info);
            }
            return result.OrderByDescending(s => s.Taken).ToList();
        }

        public void DeleteSnapshot(SnapshotInfo s)
        {
            var dir = Path.GetDirectoryName(s.CodeZip);
            foreach (var ext in new[] { ".zip", ".sql", ".json" }) TryDelete(Path.Combine(dir, s.Id + ext));
        }

        /// <summary>
        /// Restores code and/or database. The current state is snapshotted first (SPEC §5.10: restore always backs up
        /// first). Code restore replaces the folder's contents but keeps vendor\ and node_modules\ when the snapshot
        /// doesn't have them.
        /// </summary>
        public async Task<SnapshotInfo> RestoreAsync(Project p, SnapshotInfo s, bool code, bool database, MariaDbService server, IProgress<string> progress = null, CancellationToken ct = default)
        {
            if (!code && !database) throw new LuminaException(ErrorKind.InvalidInput, "Choose the code, the database, or both.");
            if (database && !s.HasDatabase) throw new LuminaException(ErrorKind.InvalidInput, "This snapshot has no database.");
            if (database && (server == null || server.State != ServerState.Running)) throw new LuminaException(ErrorKind.ServerNotRunning, "Start the database server to restore the database.");
            progress?.Report("Saving the current state first…");
            var safety = await TakeSnapshotAsync(p, server, "Before restoring " + s.Taken.ToString("d MMM HH:mm", CultureInfo.InvariantCulture), progress, ct).ConfigureAwait(false);
            if (code)
            {
                progress?.Report("Putting the code back…");
                await Task.Run(() => RestoreCode(p.Folder, s.CodeZip), ct).ConfigureAwait(false);
            }
            if (database)
            {
                progress?.Report("Putting the database back…");
                await DatabaseDumper.RestoreAsync(server, s.DatabaseSql, ct).ConfigureAwait(false);
            }
            return safety;
        }

        /// <summary>Work folders inside the project while code is restored; never zipped, served or kept.</summary>
        internal const string RestoreNew = ".lumina-restore-new", RestoreOld = ".lumina-restore-old";

        /// <summary>
        /// Puts the snapshot's code in the folder without ever leaving it half done:
        /// 1. the snapshot is unpacked into a work folder inside the project (a bad zip or a full disk stops here, nothing touched);
        /// 2. the current files are moved aside (a file another program holds open stops here, and what moved goes back);
        /// 3. the unpacked files are moved in (a rename on the same drive), and what was moved aside is deleted.
        /// </summary>
        internal static void RestoreCode(string folder, string zipPath)
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            var fresh = Path.Combine(root, RestoreNew);
            var old = Path.Combine(root, RestoreOld);
            DeleteFolder(fresh);
            // Left by a restore that was cut off (power loss): it may hold the project's own files, so never delete it here.
            if (Directory.Exists(old) && Directory.EnumerateFileSystemEntries(old).Any())
                throw new LuminaException(ErrorKind.InvalidInput, "An earlier restore of this project was cut off. Its files are in the " + RestoreOld +
                    " folder inside the project: move back what you need, delete that folder, then restore again.");
            DeleteFolder(old);

            // 1. unpack aside
            HashSet<string> tops;
            try
            {
                Directory.CreateDirectory(fresh);
                var freshRoot = fresh + Path.DirectorySeparatorChar;
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    tops = new HashSet<string>(zip.Entries.Select(e => e.FullName.Split('/')[0]).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
                    foreach (var e in zip.Entries)
                    {
                        var target = Path.GetFullPath(Path.Combine(fresh, e.FullName));
                        if (!target.StartsWith(freshRoot, StringComparison.OrdinalIgnoreCase)) continue;
                        if (e.FullName.EndsWith("/", StringComparison.Ordinal)) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        e.ExtractToFile(target, true);
                        File.SetLastWriteTime(target, e.LastWriteTime.LocalDateTime);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                DeleteFolder(fresh);
                throw new LuminaException(ErrorKind.Unknown, "Couldn't unpack the snapshot: " + ex.Message + " Nothing in the project was changed.", ex);
            }

            // 2. move the current files aside
            Directory.CreateDirectory(old);
            var movedAside = new List<string>();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(root).ToList())
                {
                    var name = Path.GetFileName(entry);
                    if (name.Equals(RestoreNew, StringComparison.OrdinalIgnoreCase) || name.Equals(RestoreOld, StringComparison.OrdinalIgnoreCase)) continue;
                    if (AlwaysSkipped.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (HeavyFolders.Contains(name, StringComparer.OrdinalIgnoreCase) && !tops.Contains(name)) continue;
                    MoveEntry(entry, Path.Combine(old, name));
                    movedAside.Add(name);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                foreach (var name in movedAside) TryMoveBack(Path.Combine(old, name), Path.Combine(root, name));
                DeleteFolder(fresh);
                if (!Directory.EnumerateFileSystemEntries(old).Any()) DeleteFolder(old);
                throw new LuminaException(ErrorKind.Unknown, "Couldn't replace the project's files: " + ex.Message +
                    " Close any program using the project folder (editor, terminal, Explorer window) and try again. Nothing was changed.", ex);
            }

            // 3. move the snapshot's files in
            var movedIn = new List<string>();
            try
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(fresh).ToList())
                {
                    var name = Path.GetFileName(entry);
                    MoveEntry(entry, Path.Combine(root, name));
                    movedIn.Add(name);
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                foreach (var name in movedIn) TryMoveBack(Path.Combine(root, name), Path.Combine(fresh, name));
                foreach (var name in movedAside) TryMoveBack(Path.Combine(old, name), Path.Combine(root, name));
                DeleteFolder(fresh);
                if (!Directory.EnumerateFileSystemEntries(old).Any()) DeleteFolder(old);
                throw new LuminaException(ErrorKind.Unknown, "Couldn't put the snapshot's files in place: " + ex.Message + " The project's files were put back.", ex);
            }
            DeleteFolder(fresh);
            DeleteFolder(old);
        }

        private static void MoveEntry(string from, string to)
        {
            if (Directory.Exists(from)) Directory.Move(from, to); else File.Move(from, to);
        }

        private static void TryMoveBack(string from, string to)
        {
            try { if ((File.Exists(from) || Directory.Exists(from)) && !File.Exists(to) && !Directory.Exists(to)) MoveEntry(from, to); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private static void DeleteFolder(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                // read-only files (from a Git checkout) would stop Directory.Delete
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(dir, true);
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ------------------------------------------------------------------ share pack

        /// <summary>StudentID_Project_YYYY-MM-DD.zip, with anything Windows can't use in a file name removed.</summary>
        public static string PackName(string studentId, string project, DateTime date)
        {
            string Clean(string s) => Regex.Replace((s ?? "").Trim(), @"[^\w\-.]+", "-").Trim('-', '.');
            var id = Clean(studentId);
            var name = (id.Length > 0 ? id + "_" : "") + Clean(project) + "_" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".zip";
            return name;
        }

        /// <summary>
        /// Builds the share pack in exports\: the project folder (without vendor\ and node_modules\ unless asked),
        /// &lt;db&gt;.sql, and a README with run instructions. Returns the zip path.
        /// </summary>
        public async Task<string> BuildSubmissionPackAsync(Project p, MariaDbService server, string studentId, bool includeVendor,
            IProgress<string> progress = null, CancellationToken ct = default)
        {
            Directory.CreateDirectory(_paths.Exports);
            var target = Path.Combine(_paths.Exports, PackName(studentId, p.Name, DateTime.Now));
            var work = Path.Combine(_paths.Temp, "pack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                string sql = null;
                bool hasDb = server != null && server.State == ServerState.Running && await DatabaseExistsAsync(server, p.Database, ct).ConfigureAwait(false);
                if (hasDb)
                {
                    progress?.Report("Exporting the database " + p.Database + "…");
                    sql = Path.Combine(work, p.Database + ".sql");
                    await DatabaseDumper.DumpAsync(server, p.Database, sql, ct).ConfigureAwait(false);
                }
                progress?.Report("Zipping the project…");
                var tmpZip = target + ".part";
                TryDelete(tmpZip);
                var leftOut = new List<string>();
                await Task.Run(() =>
                {
                    using (var fs = new FileStream(tmpZip, FileMode.Create))
                    using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                    {
                        AddFolder(zip, p.Folder, p.Name + "/", skipHeavy: !includeVendor, ct, leftOut);
                        if (sql != null) zip.CreateEntryFromFile(sql, "database/" + p.Database + ".sql", CompressionLevel.Optimal);
                        var readme = zip.CreateEntry("README.txt", CompressionLevel.Optimal);
                        using (var w = new StreamWriter(readme.Open(), new UTF8Encoding(false)))
                            w.Write(Readme(p, hasDb, includeVendor, leftOut));
                    }
                }, ct).ConfigureAwait(false);
                TryDelete(target);
                File.Move(tmpZip, target);
                progress?.Report("Saved " + Path.GetFileName(target) + "." + (leftOut.Count == 0 ? "" :
                    " Left out " + string.Join(", ", leftOut) + ": " + (leftOut.Count == 1 ? "it holds" : "they hold") + " passwords or keys for this PC."));
                return target;
            }
            finally
            {
                try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        internal static string Readme(Project p, bool hasDb, bool includeVendor, IList<string> secretsLeftOut = null)
        {
            var hasComposer = File.Exists(Path.Combine(p.Folder, "composer.json"));
            var sb = new StringBuilder();
            sb.AppendLine(p.Name);
            sb.AppendLine(new string('=', p.Name.Length));
            sb.AppendLine();
            sb.AppendLine("Packed by LuminaDB on " + DateTime.Now.ToString("d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture) + ".");
            sb.AppendLine();
            sb.AppendLine("Contents");
            sb.AppendLine("  " + p.Name + "/        the project code");
            if (hasDb) sb.AppendLine("  database/" + p.Database + ".sql   the database (tables and data)");
            sb.AppendLine();
            if (secretsLeftOut != null && secretsLeftOut.Count > 0)
            {
                sb.AppendLine("Left out on purpose (passwords and keys stay on the PC they belong to)");
                foreach (var f in secretsLeftOut) sb.AppendLine("  " + p.Name + "/" + f);
                bool env = secretsLeftOut.Any(f => f.Equals(".env", StringComparison.OrdinalIgnoreCase));
                if (env && File.Exists(Path.Combine(p.Folder, ".env.example")))
                    sb.AppendLine("  To run it, copy .env.example to .env and fill in your own settings.");
                else if (env)
                    sb.AppendLine("  To run it, make a .env with your own settings.");
                sb.AppendLine();
            }
            sb.AppendLine("How to run it with XAMPP or WAMP");
            sb.AppendLine("  1. Copy the " + p.Name + " folder into htdocs (XAMPP) or www (WAMP).");
            int step = 2;
            if (hasDb)
            {
                sb.AppendLine("  " + step++ + ". Open phpMyAdmin > Import, choose database/" + p.Database + ".sql and press Go.");
                sb.AppendLine("     The file creates the " + p.Database + " database itself; no need to make it first.");
            }
            if (hasComposer && !includeVendor) sb.AppendLine("  " + step++ + ". In the project folder run: composer install");
            sb.AppendLine("  " + step++ + ". Check the database settings in config.php (or .env): host, port, user and password.");
            sb.AppendLine("     XAMPP's defaults are host 127.0.0.1, port 3306, user root, empty password.");
            sb.AppendLine("  " + step + ". Open http://localhost/" + p.Name + "/" + (p.DocumentRoot != p.Folder ? Path.GetFileName(p.DocumentRoot) + "/" : "") + " in a browser.");
            sb.AppendLine();
            sb.AppendLine("How to run it with LuminaDB");
            sb.AppendLine("  Projects > Import folder, pick the " + p.Name + " folder, then open it from the list.");
            return sb.ToString().Replace("\n", "\r\n").Replace("\r\r\n", "\r\n");
        }

        // ------------------------------------------------------------------ helpers

        private static void ZipFolder(string folder, string zipPath, bool skipHeavy, CancellationToken ct)
        {
            using (var fs = new FileStream(zipPath, FileMode.CreateNew))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                AddFolder(zip, folder, "", skipHeavy, ct);
        }

        /// <param name="secretsLeftOut">When given, files <see cref="IsSecretFile"/> names are left out and listed here
        /// (project-relative, with /); null keeps everything (snapshots stay on this PC and must restore exactly).</param>
        private static void AddFolder(ZipArchive zip, string folder, string prefix, bool skipHeavy, CancellationToken ct, List<string> secretsLeftOut = null)
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var dir = stack.Pop();
                var rel = dir.Substring(root.Length).Replace('\\', '/');
                bool any = false;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (AlwaysSkipped.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    if (skipHeavy && HeavyFolders.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                    // Laravel's caches are rebuilt on the next request.
                    if (skipHeavy && (rel + name).Equals("storage/framework/cache/data", StringComparison.OrdinalIgnoreCase)) continue;
                    if ((new FileInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    stack.Push(sub + Path.DirectorySeparatorChar);
                    any = true;
                }
                foreach (var file in Directory.GetFiles(dir))
                {
                    any = true;
                    if (secretsLeftOut != null && IsSecretFile(Path.GetFileName(file)))
                    {
                        secretsLeftOut.Add(file.Substring(root.Length).Replace('\\', '/'));
                        continue;
                    }
                    var entryName = prefix + file.Substring(root.Length).Replace('\\', '/');
                    try { zip.CreateEntryFromFile(file, entryName, CompressionLevel.Optimal); }
                    catch (IOException ex) { throw new LuminaException(ErrorKind.Unknown, "Couldn't read " + entryName + ": " + ex.Message + " Close the program that has it open.", ex); }
                }
                if (!any && rel.Length > 0) zip.CreateEntry(prefix + rel);   // keep empty folders (uploads/)
            }
        }

        private static async Task<bool> DatabaseExistsAsync(MariaDbService server, string db, CancellationToken ct)
        {
            using (var c = await server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @n";
                cmd.Parameters.AddWithValue("@n", db);
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
            }
        }

        private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
