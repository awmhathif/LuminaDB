using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LuminaDB.Core.Projects;
using MySqlConnector;

namespace LuminaDB.Core
{
    /// <summary>Why a backup was taken. Manual ones are never removed automatically.</summary>
    public enum BackupKind { Auto, Manual, BeforeRisky, OnClose, BeforeRestore, First }

    /// <summary>One database backup: backups\&lt;db&gt;\&lt;stamp&gt;.sql plus this record as &lt;stamp&gt;.json.</summary>
    [DataContract]
    public sealed class BackupInfo
    {
        [DataMember] public string Database { get; set; }
        [DataMember] public DateTime Taken { get; set; }
        [DataMember] public string Reason { get; set; }
        [DataMember] public BackupKind Kind { get; set; }
        [DataMember] public long Bytes { get; set; }
        /// <summary>Which engine made it ("11.8.10"); dumps restore on either engine set.</summary>
        [DataMember] public string EngineVersion { get; set; }

        /// <summary>Folder-safe stamp, e.g. 2026-11-24_153012_481.</summary>
        public string Id { get; set; }
        public string SqlFile { get; set; }
        public string SizeText => DatabaseSummary.FormatSize(Bytes);
        public bool IsManual => Kind == BackupKind.Manual;
    }

    [DataContract]
    internal sealed class BackupState
    {
        /// <summary>Database name → what it looked like at its last backup (see <see cref="BackupService.FingerprintsAsync"/>).</summary>
        [DataMember] public Dictionary<string, string> Fingerprints { get; set; } = new Dictionary<string, string>();
    }

    /// <summary>
    /// Database backups (SPEC §5.10): every 30 minutes while something changed, before risky commands, when LuminaDB
    /// closes, and on demand. Each is a plain mariadb-dump file, so it restores on any engine set. Keeps the last 20
    /// per database plus the newest of each day for 7 days; ones you made yourself stay until you delete them.
    /// Restoring always backs up the current state first.
    /// </summary>
    public sealed class BackupService
    {
        public const int KeepLatest = 20;
        public const int KeepDays = 7;

        private readonly AppPaths _paths;
        private readonly MariaDbService _server;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public BackupService(AppPaths paths, MariaDbService server)
        {
            _paths = paths ?? throw new ArgumentNullException(nameof(paths));
            _server = server;
        }

        /// <summary>Raised after backups were added, restored or removed (on a worker thread).</summary>
        public event Action Changed;

        /// <summary>True while a backup or restore is running.</summary>
        public bool IsBusy => _gate.CurrentCount == 0;

        private string StateFile => Path.Combine(_paths.Backups, "state.json");

        public static string FolderName(string database)
        {
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(database.Length);
            foreach (var ch in database) sb.Append(bad.Contains(ch) ? '_' : ch);
            return sb.ToString();
        }

        private string DatabaseDir(string database) => Path.Combine(_paths.Backups, FolderName(database));

        // ------------------------------------------------------------------ list

        /// <summary>Every backup, newest first (one database when <paramref name="database"/> is given).</summary>
        public List<BackupInfo> List(string database = null)
        {
            var result = new List<BackupInfo>();
            if (!Directory.Exists(_paths.Backups)) return result;
            IEnumerable<string> dirs = database != null ? new[] { DatabaseDir(database) } : Directory.GetDirectories(_paths.Backups);
            foreach (var dir in dirs)
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var json in Directory.GetFiles(dir, "*.json"))
                {
                    var info = JsonFile.Read<BackupInfo>(json);
                    if (info == null || string.IsNullOrEmpty(info.Database)) continue;
                    if (database != null && !string.Equals(info.Database, database, StringComparison.Ordinal)) continue;
                    info.Id = Path.GetFileNameWithoutExtension(json);
                    info.SqlFile = Path.ChangeExtension(json, ".sql");
                    if (!File.Exists(info.SqlFile)) continue;
                    result.Add(info);
                }
            }
            return result.OrderByDescending(b => b.Taken).ToList();
        }

        public BackupInfo Latest(string database = null) => List(database).FirstOrDefault();

        // ------------------------------------------------------------------ back up

        /// <summary>Backs up the given databases (skipping ones that don't exist). Returns what was written.</summary>
        public async Task<List<BackupInfo>> BackUpAsync(IEnumerable<string> databases, string reason, BackupKind kind, CancellationToken ct = default)
        {
            RequireServer();
            var wanted = databases.Where(d => !string.IsNullOrWhiteSpace(d)).Distinct(StringComparer.Ordinal).ToList();
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            var made = new List<BackupInfo>();
            try
            {
                var prints = await FingerprintsAsync(ct).ConfigureAwait(false);
                var state = LoadState();
                foreach (var db in wanted)
                {
                    if (!prints.ContainsKey(db)) continue;   // gone, or a server schema
                    made.Add(await DumpOneAsync(db, reason, kind, ct).ConfigureAwait(false));
                    state.Fingerprints[db] = prints[db];
                }
                SaveState(state);
                foreach (var db in made.Select(m => m.Database).Distinct()) Prune(db, DateTime.Now);
            }
            finally { _gate.Release(); }
            if (made.Count > 0) RaiseChanged();
            return made;
        }

        /// <summary>Every user database.</summary>
        public async Task<List<BackupInfo>> BackUpAllAsync(string reason, BackupKind kind, CancellationToken ct = default)
        {
            RequireServer();
            var prints = await FingerprintsAsync(ct).ConfigureAwait(false);
            return await BackUpAsync(prints.Keys, reason, kind, ct).ConfigureAwait(false);
        }

        /// <summary>The automatic backup: only databases that changed since their last backup (or never had one).</summary>
        public async Task<List<BackupInfo>> BackUpChangedAsync(string reason, BackupKind kind, CancellationToken ct = default)
        {
            var changed = await ChangedDatabasesAsync(ct).ConfigureAwait(false);
            if (changed.Count == 0) return new List<BackupInfo>();
            return await BackUpAsync(changed, reason, kind, ct).ConfigureAwait(false);
        }

        /// <summary>User databases whose fingerprint differs from the one saved at their last backup.</summary>
        public async Task<List<string>> ChangedDatabasesAsync(CancellationToken ct = default)
        {
            RequireServer();
            var prints = await FingerprintsAsync(ct).ConfigureAwait(false);
            var state = LoadState();
            return prints.Where(p => !state.Fingerprints.TryGetValue(p.Key, out var old) || old != p.Value || !HasAny(p.Key))
                         .Select(p => p.Key).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private bool HasAny(string database)
        {
            var dir = DatabaseDir(database);
            return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*.sql").Any();
        }

        /// <summary>
        /// A cheap summary of each user database (tables, rows, sizes, change times, routines): when it moves, the
        /// database changed. One query each to information_schema, no table scans.
        /// </summary>
        public async Task<Dictionary<string, string>> FingerprintsAsync(CancellationToken ct = default)
        {
            RequireServer();
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var hidden = "'" + string.Join("','", DatabaseCatalog.HiddenSchemas) + "'";
            using (var c = await _server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
            {
                using (var cmd = new MySqlCommand("SELECT SCHEMA_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME NOT IN (" + hidden + ")", c))
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    while (await r.ReadAsync(ct).ConfigureAwait(false)) result[r.GetString(0)] = "";

                const string tables = @"SELECT TABLE_SCHEMA, COUNT(*), COALESCE(SUM(TABLE_ROWS),0), COALESCE(SUM(DATA_LENGTH+INDEX_LENGTH),0),
  COALESCE(DATE_FORMAT(MAX(UPDATE_TIME),'%Y%m%d%H%i%s'),''), COALESCE(DATE_FORMAT(MAX(CREATE_TIME),'%Y%m%d%H%i%s'),''),
  COALESCE(SUM(AUTO_INCREMENT),0), GROUP_CONCAT(TABLE_NAME ORDER BY TABLE_NAME SEPARATOR ',')
FROM information_schema.TABLES GROUP BY TABLE_SCHEMA";
                await AppendAsync(c, tables, result, ct).ConfigureAwait(false);
                const string routines = @"SELECT ROUTINE_SCHEMA, COUNT(*), COALESCE(DATE_FORMAT(MAX(LAST_ALTERED),'%Y%m%d%H%i%s'),'')
FROM information_schema.ROUTINES GROUP BY ROUTINE_SCHEMA";
                await AppendAsync(c, routines, result, ct).ConfigureAwait(false);
            }
            return result;
        }

        private static async Task AppendAsync(MySqlConnection c, string sql, Dictionary<string, string> into, CancellationToken ct)
        {
            using (var cmd = new MySqlCommand(sql, c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var db = r.GetString(0);
                    if (!into.ContainsKey(db)) continue;
                    var sb = new StringBuilder(into[db]);
                    for (int i = 1; i < r.FieldCount; i++) sb.Append('|').Append(r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture));
                    into[db] = sb.ToString();
                }
            }
        }

        private async Task<BackupInfo> DumpOneAsync(string db, string reason, BackupKind kind, CancellationToken ct)
        {
            var dir = DatabaseDir(db);
            Directory.CreateDirectory(dir);
            var now = DateTime.Now;
            string id = now.ToString("yyyy-MM-dd_HHmmss_fff", CultureInfo.InvariantCulture);
            for (int n = 2; File.Exists(Path.Combine(dir, id + ".sql")); n++) id = now.ToString("yyyy-MM-dd_HHmmss_fff", CultureInfo.InvariantCulture) + "-" + n;
            var sql = Path.Combine(dir, id + ".sql");
            await DatabaseDumper.DumpAsync(_server, db, sql, ct).ConfigureAwait(false);
            var info = new BackupInfo
            {
                Database = db, Taken = now, Reason = (reason ?? "").Trim(), Kind = kind, Bytes = new FileInfo(sql).Length,
                EngineVersion = SafeVersion(), Id = id, SqlFile = sql,
            };
            JsonFile.Write(Path.Combine(dir, id + ".json"), info);
            return info;
        }

        private string SafeVersion()
        {
            try { return _server.Engine?.ServerVersion?.ToString(); }
            catch (LuminaException) { return null; }
        }

        // ------------------------------------------------------------------ restore / delete

        /// <summary>
        /// Puts the database back as it was in <paramref name="backup"/>. The current state is backed up first, then the
        /// database is emptied (so tables made later go too) and the dump is replayed. If the replay fails, the safety
        /// copy goes back in. Returns the safety copy (null when the database didn't exist any more).
        /// </summary>
        public async Task<BackupInfo> RestoreAsync(BackupInfo backup, CancellationToken ct = default)
        {
            RequireServer();
            if (backup == null) throw new ArgumentNullException(nameof(backup));
            if (!File.Exists(backup.SqlFile)) throw new LuminaException(ErrorKind.InvalidInput, "The backup file " + Path.GetFileName(backup.SqlFile) + " is missing.");
            string when = backup.Taken.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
            var safety = (await BackUpAsync(new[] { backup.Database }, "Before restoring the " + when + " backup", BackupKind.BeforeRestore, ct).ConfigureAwait(false)).FirstOrDefault();

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await DropAsync(backup.Database, ct).ConfigureAwait(false);
                try { await DatabaseDumper.RestoreAsync(_server, backup.SqlFile, ct).ConfigureAwait(false); }
                catch (Exception) when (safety != null)
                {
                    await DropAsync(backup.Database, CancellationToken.None).ConfigureAwait(false);
                    await DatabaseDumper.RestoreAsync(_server, safety.SqlFile, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                // what's there now is exactly a backed-up state: no need to back it up again
                var prints = await FingerprintsAsync(ct).ConfigureAwait(false);
                var state = LoadState();
                if (prints.TryGetValue(backup.Database, out var fp)) state.Fingerprints[backup.Database] = fp;
                SaveState(state);
            }
            finally { _gate.Release(); }
            RaiseChanged();
            return safety;
        }

        private async Task DropAsync(string db, CancellationToken ct)
        {
            using (var c = await _server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
            using (var cmd = new MySqlCommand("DROP DATABASE IF EXISTS `" + db.Replace("`", "``") + "`", c))
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        public void Delete(BackupInfo backup)
        {
            if (backup == null) return;
            TryDelete(backup.SqlFile);
            TryDelete(Path.ChangeExtension(backup.SqlFile, ".json"));
            RaiseChanged();
        }

        // ------------------------------------------------------------------ keeping

        /// <summary>Removes automatic backups of <paramref name="database"/> that <see cref="ToPrune"/> says go.</summary>
        public int Prune(string database, DateTime now)
        {
            var gone = ToPrune(List(database), now);
            foreach (var b in gone)
            {
                TryDelete(b.SqlFile);
                TryDelete(Path.ChangeExtension(b.SqlFile, ".json"));
            }
            return gone.Count;
        }

        /// <summary>
        /// Per database: the newest <see cref="KeepLatest"/> automatic backups stay, and so does the newest of each day
        /// for the last <see cref="KeepDays"/> days. Manual backups always stay and don't use up a place.
        /// </summary>
        public static List<BackupInfo> ToPrune(IEnumerable<BackupInfo> backups, DateTime now)
        {
            var gone = new List<BackupInfo>();
            foreach (var group in backups.Where(b => !b.IsManual).GroupBy(b => b.Database))
            {
                var newest = group.OrderByDescending(b => b.Taken).ToList();
                var keep = new HashSet<BackupInfo>(newest.Take(KeepLatest));
                foreach (var day in newest.Where(b => b.Taken.Date > now.Date.AddDays(-KeepDays)).GroupBy(b => b.Taken.Date))
                    keep.Add(day.First());
                gone.AddRange(newest.Where(b => !keep.Contains(b)));
            }
            return gone;
        }

        // ------------------------------------------------------------------ helpers

        private BackupState LoadState()
        {
            var s = JsonFile.Read<BackupState>(StateFile) ?? new BackupState();
            if (s.Fingerprints == null) s.Fingerprints = new Dictionary<string, string>();   // DataContract skips the initialiser
            return s;
        }

        private void SaveState(BackupState s)
        {
            if (s.Fingerprints == null) s.Fingerprints = new Dictionary<string, string>();
            Directory.CreateDirectory(_paths.Backups);
            JsonFile.Write(StateFile, s);
        }

        private void RequireServer()
        {
            if (_server == null || _server.State != ServerState.Running || _server.Port == 0)
                throw new LuminaException(ErrorKind.ServerNotRunning, "Start the database server first.");
        }

        private void RaiseChanged()
        {
            try { Changed?.Invoke(); } catch (Exception) { /* a listener's problem is not the backup's */ }
        }

        private static void TryDelete(string f)
        {
            try { if (f != null && File.Exists(f)) File.Delete(f); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
