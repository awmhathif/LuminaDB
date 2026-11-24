using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using MySqlConnector;

namespace LuminaDB.Core.Admin
{
    /// <summary>config\settings.json: the choices on the Settings page that aren't web settings (SPEC §5.12).</summary>
    [DataContract]
    public sealed class AppSettings
    {
        public static readonly int[] BackupChoices = { 0, 15, 30, 60 };
        public static readonly double[] TextSizeChoices = { 1.0, 1.1, 1.25 };

        [DataMember] public int DatabasePort { get; set; } = 3306;
        /// <summary>Minutes between automatic backups; 0 = off.</summary>
        [DataMember] public int BackupMinutes { get; set; } = 30;
        /// <summary>1.0, 1.1 or 1.25: everything is drawn this much larger.</summary>
        [DataMember] public double TextSize { get; set; } = 1.0;

        public static string FileFor(AppPaths p) => Path.Combine(p.Config, "settings.json");

        public static AppSettings Load(AppPaths p)
        {
            var s = JsonFile.Read<AppSettings>(FileFor(p)) ?? new AppSettings();
            if (s.DatabasePort < 1024 || s.DatabasePort > 65535) s.DatabasePort = 3306;
            if (!BackupChoices.Contains(s.BackupMinutes)) s.BackupMinutes = 30;
            if (!TextSizeChoices.Any(x => Math.Abs(x - s.TextSize) < 0.001)) s.TextSize = 1.0;
            return s;
        }

        public void Save(AppPaths p) => JsonFile.Write(FileFor(p), this);

        /// <summary>Null when the port can be used for the database, else why not.</summary>
        public static string CheckPort(int port, IEnumerable<int> usedByLumina = null)
        {
            if (port < 1024 || port > 65535) return "Choose a port from 1024 to 65535 (lower ones need administrator rights).";
            if (usedByLumina != null && usedByLumina.Contains(port)) return "LuminaDB already uses " + port + " for something else.";
            return null;
        }
    }

    /// <summary>
    /// "Start LuminaDB when Windows starts": one value under HKCU\...\Run (no administrator rights, removed with the
    /// switch). Written only when the user turns it on (SPEC §5.12).
    /// </summary>
    public sealed class WindowsStartup
    {
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private readonly string _key, _name;

        public WindowsStartup(string valueName = "LuminaDB", string keyPath = RunKey) { _name = valueName; _key = keyPath; }

        public string Command(string exe) => "\"" + exe + "\" --minimized";

        /// <summary>On when the Run value points at this exe (a value left by another LuminaDB folder doesn't count).</summary>
        public bool IsOn(string exe)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(_key))
            {
                var v = k?.GetValue(_name) as string;
                return v != null && v.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        public void Set(bool on, string exe)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(_key))
            {
                if (on) k.SetValue(_name, Command(exe), RegistryValueKind.String);
                else if (k.GetValue(_name) != null) k.DeleteValue(_name, false);
            }
        }
    }

    /// <summary>
    /// Moves the data folder somewhere else (SPEC §5.12): stop, copy, check every file arrived, switch, start, check the
    /// databases are all there. Anything wrong puts it back the way it was. The old folder is kept (renamed) until the user
    /// deletes it, so nothing is ever lost.
    /// </summary>
    public static class DataFolderMover
    {
        public sealed class Result
        {
            public string From { get; set; }
            public string To { get; set; }
            /// <summary>Where the old copy now is (renamed "data.old-…" when possible).</summary>
            public string OldCopy { get; set; }
            public long Files { get; set; }
            public long Bytes { get; set; }
        }

        /// <summary>Null when <paramref name="target"/> can take the data folder, else why not.</summary>
        public static string Check(AppPaths paths, string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return "Choose a folder.";
            string full;
            try { full = Path.GetFullPath(target).TrimEnd('\\'); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return "That isn't a folder path Windows accepts."; }
            var current = paths.Data.TrimEnd('\\');
            if (string.Equals(full, current, StringComparison.OrdinalIgnoreCase)) return "That's where the data folder is now.";
            if (full.Length <= 3) return "Choose a folder, not the whole drive (for example D:\\LuminaData).";
            if (Inside(full, current)) return "The new place can't be inside the current data folder.";
            if (Inside(current, full)) return "The new place can't hold the current data folder inside it; choose an empty folder.";
            if (Inside(full, paths.Engines) || Inside(full, paths.Backups) || Inside(full, paths.Snapshots) || Inside(full, paths.Projects))
                return "Choose a folder outside LuminaDB's engines, backups and projects.";
            if (File.Exists(full)) return "There's a file with that name already.";
            if (Directory.Exists(full) && Directory.EnumerateFileSystemEntries(full).Any()) return "That folder isn't empty. Choose an empty or new folder.";
            var root = Path.GetPathRoot(full);
            try
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady) return "Drive " + root + " isn't ready.";
                long need = (long)(Size(current).bytes * 1.1) + 64L * 1024 * 1024;
                if (drive.AvailableFreeSpace < need) return "Drive " + root + " has " + ServerStatus.Bytes(drive.AvailableFreeSpace) + " free; the data needs " + ServerStatus.Bytes(need) + ".";
                if (drive.DriveType == DriveType.Network) return "A network drive is too slow and unreliable for a database. Choose a local drive.";
            }
            catch (ArgumentException) { return "That drive doesn't exist."; }
            catch (IOException ex) { return "Drive " + root + " can't be read: " + ex.Message; }
            return null;
        }

        /// <param name="expected">Databases that must be there after the move (when the caller stopped the server itself); read from the server when null and it runs.</param>
        public static async Task<Result> MoveAsync(MariaDbService server, string target, IProgress<string> progress = null,
                                                   IReadOnlyCollection<string> expected = null, CancellationToken ct = default)
        {
            var paths = server.Paths;
            var problem = Check(paths, target);
            if (problem != null) throw new LuminaException(ErrorKind.InvalidInput, problem);
            var from = paths.Data.TrimEnd('\\');
            var to = Path.GetFullPath(target).TrimEnd('\\');
            bool wasRunning = server.State == ServerState.Running;
            List<string> before = expected?.ToList();

            if (before == null && wasRunning && !server.NeedsPassword)
            {
                progress?.Report("Listing your databases…");
                before = await ListDatabasesAsync(server, ct).ConfigureAwait(false);
            }
            progress?.Report("Stopping the server…");
            await server.StopAsync(ct).ConfigureAwait(false);

            bool createdTarget = !Directory.Exists(to);
            var result = new Result { From = from, To = to };
            try
            {
                progress?.Report("Copying the data folder…");
                await Task.Run(() => CopyTree(from, to, ct), ct).ConfigureAwait(false);
                var a = Size(from);
                var b = Size(to);
                if (a.files != b.files || a.bytes != b.bytes)
                    throw new LuminaException(ErrorKind.DataFolderInitFailed,
                        $"The copy doesn't match ({b.files} of {a.files} files, {ServerStatus.Bytes(b.bytes)} of {ServerStatus.Bytes(a.bytes)}). Nothing was changed.");
                result.Files = b.files;
                result.Bytes = b.bytes;

                paths.UseDataFolder(to);
                progress?.Report("Starting the server from the new place…");
                await server.StartAsync(ct).ConfigureAwait(false);
                if (before != null)
                {
                    var after = await ListDatabasesAsync(server, ct).ConfigureAwait(false);
                    var missing = before.Except(after, StringComparer.OrdinalIgnoreCase).ToList();
                    if (missing.Count > 0)
                        throw new LuminaException(ErrorKind.DataFolderInitFailed, "After the move these databases were missing: " + string.Join(", ", missing) + ". Nothing was changed.");
                }
                if (!wasRunning) await server.StopAsync(ct).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Put everything back: old folder, old setting, server as it was. Remove only what we copied.
                try { await server.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
                paths.UseDataFolder(from);
                TryDelete(to, createdTarget);
                if (wasRunning) { try { await server.StartAsync(CancellationToken.None).ConfigureAwait(false); } catch (Exception) { } }
                throw;
            }

            // The new folder works. Rename the old one so nobody edits it by mistake (keep it if the rename fails).
            result.OldCopy = from;
            var renamed = from + ".old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            try { Directory.Move(from, renamed); result.OldCopy = renamed; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            progress?.Report("Done.");
            return result;
        }

        private static async Task<List<string>> ListDatabasesAsync(MariaDbService server, CancellationToken ct)
        {
            var list = new List<string>();
            using (var c = await server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
            using (var cmd = new MySqlCommand("SHOW DATABASES", c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(r.GetString(0));
            return list;
        }

        private static void CopyTree(string from, string to, CancellationToken ct)
        {
            Directory.CreateDirectory(to);
            foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(to + dir.Substring(from.Length));
            foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                File.Copy(file, to + file.Substring(from.Length), false);
            }
        }

        internal static (long files, long bytes) Size(string dir)
        {
            if (!Directory.Exists(dir)) return (0, 0);
            long files = 0, bytes = 0;
            foreach (var f in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories)) { files++; bytes += f.Length; }
            return (files, bytes);
        }

        private static void TryDelete(string to, bool createdTarget)
        {
            try
            {
                if (!Directory.Exists(to)) return;
                if (createdTarget) { Directory.Delete(to, true); return; }
                foreach (var d in Directory.GetDirectories(to)) Directory.Delete(d, true);
                foreach (var f in Directory.GetFiles(to)) File.Delete(f);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static bool Inside(string path, string folder)
        {
            var f = folder.TrimEnd('\\') + "\\";
            return path.TrimEnd('\\').StartsWith(f, StringComparison.OrdinalIgnoreCase) || string.Equals(path.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
