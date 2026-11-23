using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace LuminaDB.Core.Sql
{
    /// <summary>A saved "before" picture of what one command was about to change.</summary>
    public sealed class UndoBatch
    {
        public int Id { get; set; }
        public DateTime Created { get; set; }
        public string Statement { get; set; }
        public string Summary { get; set; }
        /// <summary>pending, ready, restored, discarded.</summary>
        public string State { get; set; }
        /// <summary>Human list of what Undo puts back, e.g. "shop.orders (42 rows)".</summary>
        public string What { get; set; }
        public override string ToString() => $"#{Id} {State} {What}";
    }

    /// <summary>Why a command ran without an Undo copy (shown on its receipt).</summary>
    public sealed class UndoPlan
    {
        public UndoBatch Batch { get; set; }
        /// <summary>Null when the copy was made; otherwise the reason it wasn't.</summary>
        public string SkippedReason { get; set; }
        public bool Covered => Batch != null;
    }

    /// <summary>
    /// Undo for the Notebook. Before a command changes tables, it copies them (structure, rows,
    /// triggers) into the hidden lumina_undo database on its OWN connection, so the user's open
    /// transaction isn't committed by our DDL. Undo restores the newest batch. Keeps the last 20.
    /// </summary>
    public sealed class UndoService
    {
        public const int KeepBatches = 20;
        public const long MaxRows = 200_000;
        public const long MaxBytes = 64L * 1024 * 1024;
        private const string Schema = DatabaseCatalog.UndoSchema;

        private readonly Func<CancellationToken, Task<MySqlConnection>> _open;
        private bool _ready;

        public UndoService(MariaDbService server) : this(ct => server.OpenConnectionAsync(null, ct)) { }

        public UndoService(Func<CancellationToken, Task<MySqlConnection>> open) { _open = open; }

        private async Task<MySqlConnection> OpenAsync(CancellationToken ct)
        {
            var c = await _open(ct).ConfigureAwait(false);
            // read the rows without locking them: the user's own session may hold locks
            await Exec(c, "SET SESSION TRANSACTION ISOLATION LEVEL READ COMMITTED", ct).ConfigureAwait(false);
            await Exec(c, "SET SESSION lock_wait_timeout = 10", ct).ConfigureAwait(false);
            await Exec(c, "SET SESSION innodb_lock_wait_timeout = 10", ct).ConfigureAwait(false);
            await Exec(c, "SET SESSION sql_mode = CONCAT(@@sql_mode, ',NO_AUTO_VALUE_ON_ZERO')", ct).ConfigureAwait(false);
            if (!_ready)
            {
                await Exec(c, "CREATE DATABASE IF NOT EXISTS `" + Schema + "` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", ct).ConfigureAwait(false);
                await Exec(c, @"CREATE TABLE IF NOT EXISTS `" + Schema + @"`.`_log` (
  batch INT NOT NULL AUTO_INCREMENT PRIMARY KEY,
  created DATETIME NOT NULL,
  statement MEDIUMTEXT NOT NULL,
  summary VARCHAR(500) NOT NULL,
  what VARCHAR(1000) NOT NULL DEFAULT '',
  state VARCHAR(16) NOT NULL
) ENGINE=InnoDB", ct).ConfigureAwait(false);
                await Exec(c, @"CREATE TABLE IF NOT EXISTS `" + Schema + @"`.`_items` (
  batch INT NOT NULL,
  seq INT NOT NULL,
  kind VARCHAR(16) NOT NULL,
  db VARCHAR(64) NOT NULL,
  name VARCHAR(64) NOT NULL,
  create_sql MEDIUMTEXT NULL,
  copy_table VARCHAR(64) NULL,
  columns_list MEDIUMTEXT NULL,
  PRIMARY KEY (batch, seq)
) ENGINE=InnoDB", ct).ConfigureAwait(false);
                _ready = true;
            }
            return c;
        }

        /// <summary>
        /// Copies everything <paramref name="info"/> may change. Returns the plan; a skipped plan
        /// means the command can still run but can't be undone (the reason says why).
        /// </summary>
        public Task<UndoPlan> SnapshotAsync(StatementInfo info, CancellationToken ct = default) =>
            info == null ? Task.FromResult(new UndoPlan()) : SnapshotAsync(new[] { info }, info.Statement.Text, SqlGuard.Describe(info), ct);

        /// <summary>
        /// One copy for a whole run (Run all): everything any of the commands may change, taken
        /// once before the first one runs, so Undo takes the whole run back in one step.
        /// </summary>
        public async Task<UndoPlan> SnapshotAsync(IReadOnlyList<StatementInfo> infos, string statementText, string summary, CancellationToken ct = default)
        {
            var items = new List<Item>();
            foreach (var info in infos)
            {
                if (info == null || !info.ChangesData) continue;
                var planned = PlanItems(info, out string why);
                if (planned == null) return new UndoPlan { SkippedReason = why };
                items.AddRange(planned);
            }
            if (items.Count == 0) return new UndoPlan { SkippedReason = null };
            // a database item covers its tables; drop table items inside it
            var dbs = new HashSet<string>(items.Where(i => i.Kind == "db").Select(i => i.Db), StringComparer.OrdinalIgnoreCase);
            items = items.Where(i => i.Kind == "db" || !dbs.Contains(i.Db)).ToList();


            using (var c = await OpenAsync(ct).ConfigureAwait(false))
            {
                // sizes first: never start copying a table we'd have to abandon
                foreach (var it in items.Where(i => i.Kind == "table"))
                {
                    var size = await TableSize(c, it.Db, it.Name, ct).ConfigureAwait(false);
                    if (size == null) { it.Kind = "absent_table"; continue; }
                    var (kind, rows, bytes) = size.Value;
                    if (kind == "VIEW") { it.Kind = "view"; continue; }
                    if (rows > MaxRows || bytes > MaxBytes)
                        return new UndoPlan { SkippedReason = it.Db + "." + it.Name + " is too big for Undo (" + rows.ToString("N0", CultureInfo.InvariantCulture) + " rows). Make a backup first if you may need it back." };
                }
                foreach (var it in items.Where(i => i.Kind == "db"))
                {
                    if (!await DatabaseExists(c, it.Db, ct).ConfigureAwait(false)) { it.Kind = "absent_db"; continue; }
                    // a whole database: add each of its tables and views
                    using (var cmd = new MySqlCommand("SELECT TABLE_NAME, TABLE_TYPE, COALESCE(TABLE_ROWS,0), COALESCE(DATA_LENGTH,0)+COALESCE(INDEX_LENGTH,0) FROM information_schema.TABLES WHERE TABLE_SCHEMA = @d", c))
                    {
                        cmd.Parameters.AddWithValue("@d", it.Db);
                        long totalRows = 0, totalBytes = 0;
                        var add = new List<Item>();
                        using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                            while (await r.ReadAsync(ct).ConfigureAwait(false))
                            {
                                bool view = string.Equals(r.GetString(1), "VIEW", StringComparison.OrdinalIgnoreCase);
                                totalRows += Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture);
                                totalBytes += Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture);
                                add.Add(new Item { Kind = view ? "view" : "table", Db = it.Db, Name = r.GetString(0) });
                            }
                        if (totalRows > MaxRows || totalBytes > MaxBytes)
                            return new UndoPlan { SkippedReason = "The database " + it.Db + " is too big for Undo (" + totalRows.ToString("N0", CultureInfo.InvariantCulture) + " rows). Make a backup first." };
                        it.Children = add;
                    }
                }

                var all = new List<Item>();
                foreach (var it in items)
                {
                    all.Add(it);
                    if (it.Children != null) all.AddRange(it.Children);
                }
                // the same table listed twice (UPDATE a JOIN a) is copied once
                all = all.GroupBy(i => i.Kind + "\u0001" + i.Db.ToLowerInvariant() + "\u0001" + i.Name.ToLowerInvariant()).Select(g => g.First()).ToList();

                int batch;
                using (var cmd = new MySqlCommand("INSERT INTO `" + Schema + "`.`_log` (created, statement, summary, what, state) VALUES (NOW(), @s, @m, @w, 'pending'); SELECT LAST_INSERT_ID();", c))
                {
                    cmd.Parameters.AddWithValue("@s", statementText ?? "");
                    cmd.Parameters.AddWithValue("@m", Trim(summary, 500));
                    cmd.Parameters.AddWithValue("@w", Trim(Describe(all), 1000));
                    batch = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
                }

                try
                {
                    int seq = 0;
                    foreach (var it in all)
                    {
                        seq++;
                        string createSql = null, copy = null, cols = null;
                        switch (it.Kind)
                        {
                            case "table":
                                createSql = await ShowCreate(c, "TABLE", it.Db, it.Name, 1, ct).ConfigureAwait(false);
                                cols = await InsertableColumns(c, it.Db, it.Name, ct).ConfigureAwait(false);
                                copy = "b" + batch + "_" + seq;
                                await Exec(c, "CREATE TABLE `" + Schema + "`.`" + copy + "` ENGINE=InnoDB AS SELECT " + cols + " FROM " + Q(it.Db, it.Name), ct).ConfigureAwait(false);
                                await SaveItem(c, batch, seq, it, createSql, copy, cols, ct).ConfigureAwait(false);
                                foreach (var trig in await Triggers(c, it.Db, it.Name, ct).ConfigureAwait(false))
                                {
                                    seq++;
                                    var tsql = await ShowCreate(c, "TRIGGER", it.Db, trig, 2, ct).ConfigureAwait(false);
                                    await SaveItem(c, batch, seq, new Item { Kind = "trigger", Db = it.Db, Name = trig }, tsql, null, null, ct).ConfigureAwait(false);
                                }
                                continue;
                            case "view":
                                createSql = await ShowCreate(c, "VIEW", it.Db, it.Name, 1, ct).ConfigureAwait(false);
                                break;
                            case "db":
                                createSql = await ShowCreate(c, "DATABASE", it.Db, null, 1, ct).ConfigureAwait(false);
                                break;
                        }
                        await SaveItem(c, batch, seq, it, createSql, copy, cols, ct).ConfigureAwait(false);
                    }
                    await Exec(c, "UPDATE `" + Schema + "`.`_log` SET state = 'ready' WHERE batch = " + batch, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (!(ex is OperationCanceledException))
                {
                    await DiscardAsync(batch, CancellationToken.None).ConfigureAwait(false);
                    return new UndoPlan { SkippedReason = "Couldn't make the Undo copy: " + ex.Message };
                }

                await PruneAsync(c, ct).ConfigureAwait(false);
                return new UndoPlan { Batch = new UndoBatch { Id = batch, Created = DateTime.Now, Statement = statementText, Summary = summary, State = "ready", What = Describe(all) } };
            }
        }

        /// <summary>The command failed: its copy is useless, so drop it.</summary>
        public async Task DiscardAsync(int batch, CancellationToken ct = default)
        {
            try
            {
                using (var c = await OpenAsync(ct).ConfigureAwait(false))
                {
                    await DropCopies(c, batch, ct).ConfigureAwait(false);
                    await Exec(c, "DELETE FROM `" + Schema + "`.`_items` WHERE batch = " + batch, ct).ConfigureAwait(false);
                    await Exec(c, "DELETE FROM `" + Schema + "`.`_log` WHERE batch = " + batch, ct).ConfigureAwait(false);
                }
            }
            catch (MySqlException) { /* best effort: Prune removes leftovers later */ }
        }

        /// <summary>The newest batch that can be undone, or null.</summary>
        public async Task<UndoBatch> LatestAsync(CancellationToken ct = default)
        {
            using (var c = await OpenAsync(ct).ConfigureAwait(false))
            using (var cmd = new MySqlCommand("SELECT batch, created, statement, summary, what, state FROM `" + Schema + "`.`_log` WHERE state = 'ready' ORDER BY batch DESC LIMIT 1", c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
                return new UndoBatch
                {
                    Id = r.GetInt32(0), Created = r.GetDateTime(1), Statement = r.GetString(2),
                    Summary = r.GetString(3), What = r.GetString(4), State = r.GetString(5),
                };
            }
        }

        /// <summary>Puts every object in the batch back exactly as it was copied.</summary>
        public async Task RestoreAsync(int batch, CancellationToken ct = default)
        {
            using (var c = await OpenAsync(ct).ConfigureAwait(false))
            {
                var items = new List<(int seq, string kind, string db, string name, string sql, string copy, string cols)>();
                using (var cmd = new MySqlCommand("SELECT seq, kind, db, name, create_sql, copy_table, columns_list FROM `" + Schema + "`.`_items` WHERE batch = @b ORDER BY seq", c))
                {
                    cmd.Parameters.AddWithValue("@b", batch);
                    using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                        while (await r.ReadAsync(ct).ConfigureAwait(false))
                            items.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                                       r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6)));
                }
                if (items.Count == 0) throw new LuminaException(ErrorKind.InvalidInput, "There's nothing left to undo for this change.");

                await Exec(c, "SET FOREIGN_KEY_CHECKS = 0", ct).ConfigureAwait(false);
                try
                {
                    // 1. things the command created: remove them
                    foreach (var it in items.Where(i => i.kind == "absent_table"))
                    {
                        if (!await DatabaseExists(c, it.db, ct).ConfigureAwait(false)) continue;
                        await Exec(c, "DROP VIEW IF EXISTS " + Q(it.db, it.name), ct).ConfigureAwait(false);
                        await Exec(c, "DROP TABLE IF EXISTS " + Q(it.db, it.name), ct).ConfigureAwait(false);
                    }
                    foreach (var it in items.Where(i => i.kind == "absent_db"))
                        await Exec(c, "DROP DATABASE IF EXISTS " + SqlLexer.Quote(it.db), ct).ConfigureAwait(false);

                    // 2. databases that were dropped
                    foreach (var it in items.Where(i => i.kind == "db"))
                        if (!await DatabaseExists(c, it.db, ct).ConfigureAwait(false))
                            await Exec(c, it.sql, ct).ConfigureAwait(false);

                    // 3. tables: drop the changed one, recreate, refill
                    foreach (var it in items.Where(i => i.kind == "table"))
                    {
                        await Exec(c, "USE " + SqlLexer.Quote(it.db), ct).ConfigureAwait(false);
                        await Exec(c, "DROP VIEW IF EXISTS " + Q(it.db, it.name), ct).ConfigureAwait(false);
                        await Exec(c, "DROP TABLE IF EXISTS " + Q(it.db, it.name), ct).ConfigureAwait(false);
                        await Exec(c, it.sql, ct).ConfigureAwait(false);
                        // triggers recreated below; drop any the CREATE brought back (none) and refill
                        await Exec(c, "INSERT INTO " + Q(it.db, it.name) + " (" + it.cols + ") SELECT " + it.cols + " FROM `" + Schema + "`.`" + it.copy + "`", ct).ConfigureAwait(false);
                    }

                    // 4. views and triggers last (they refer to the tables)
                    foreach (var it in items.Where(i => i.kind == "view"))
                    {
                        await Exec(c, "USE " + SqlLexer.Quote(it.db), ct).ConfigureAwait(false);
                        await Exec(c, "DROP TABLE IF EXISTS " + Q(it.db, it.name), ct).ConfigureAwait(false);
                        await Exec(c, "DROP VIEW IF EXISTS " + Q(it.db, it.name), ct).ConfigureAwait(false);
                        await Exec(c, it.sql, ct).ConfigureAwait(false);
                    }
                    foreach (var it in items.Where(i => i.kind == "trigger"))
                    {
                        await Exec(c, "USE " + SqlLexer.Quote(it.db), ct).ConfigureAwait(false);
                        await Exec(c, "DROP TRIGGER IF EXISTS " + Q(it.db, it.name), ct).ConfigureAwait(false);
                        await Exec(c, it.sql, ct).ConfigureAwait(false);
                    }
                }
                finally
                {
                    try { await Exec(c, "SET FOREIGN_KEY_CHECKS = 1", CancellationToken.None).ConfigureAwait(false); } catch (MySqlException) { }
                }

                await DropCopies(c, batch, ct).ConfigureAwait(false);
                await Exec(c, "DELETE FROM `" + Schema + "`.`_items` WHERE batch = " + batch, ct).ConfigureAwait(false);
                await Exec(c, "UPDATE `" + Schema + "`.`_log` SET state = 'restored' WHERE batch = " + batch, ct).ConfigureAwait(false);
            }
        }

        // ------------------------------------------------------------------ planning

        private sealed class Item
        {
            public string Kind;   // table, view, absent_table, db, absent_db, trigger
            public string Db;
            public string Name;
            public List<Item> Children;
        }

        private static List<Item> PlanItems(StatementInfo info, out string why)
        {
            why = null;
            var list = new List<Item>();
            if (info.IsRoutineBody)
            {
                why = info.Verb == "CREATE" ? null : "Undo can't see inside procedure code.";
                return info.Verb == "CREATE" ? list : null;
            }
            if (info.Verb == "CALL" || info.Verb == "EXECUTE" || info.Verb == "DO" || info.Verb == "HANDLER")
            {
                why = "Undo can't see what " + info.Verb + " changes.";
                return null;
            }
            if (info.Object == "DATABASE" && info.DatabaseName != null)
            {
                if (SchemaSnapshot.IsSystem(info.DatabaseName)) { why = "System databases aren't copied."; return null; }
                list.Add(new Item { Kind = "db", Db = info.DatabaseName, Name = "" });
                return list;
            }
            if (info.Object == "TRIGGER" || info.Object == "PROCEDURE" || info.Object == "FUNCTION" || info.Object == "EVENT" || info.Object == "USER" || info.Object == "ROLE")
                return list;   // not table data
            if (info.IsTemporary) { why = "Temporary tables vanish on their own; no Undo copy."; return null; }
            if (info.ChangesData && info.Targets.Count == 0)
            {
                why = "Couldn't tell which table this changes, so there's no Undo copy.";
                return null;
            }
            foreach (var t in info.Targets)
            {
                if (t.ResolvedDatabase == null) { why = "No database selected."; return null; }
                if (SchemaSnapshot.IsSystem(t.ResolvedDatabase)) { why = "System tables aren't copied."; return null; }
                if (info.CteNames.Contains(t.Name)) continue;
                list.Add(new Item { Kind = "table", Db = t.ResolvedDatabase, Name = t.Name });
            }
            foreach (var n in info.NewNames)
                if (n.ResolvedDatabase != null) list.Add(new Item { Kind = "absent_table", Db = n.ResolvedDatabase, Name = n.Name });
            return list;
        }

        private static string Describe(List<Item> items)
        {
            var parts = new List<string>();
            foreach (var it in items)
            {
                switch (it.Kind)
                {
                    case "table": parts.Add(it.Db + "." + it.Name); break;
                    case "view": parts.Add("view " + it.Db + "." + it.Name); break;
                    case "absent_table": parts.Add("the new " + it.Db + "." + it.Name + " is deleted"); break;
                    case "db": parts.Add("database " + it.Db); break;
                    case "absent_db": parts.Add("the new database " + it.Db + " is deleted"); break;
                }
            }
            return string.Join(", ", parts.Distinct());
        }

        // ------------------------------------------------------------------ helpers

        private static string Q(string db, string name) => SqlLexer.Quote(db) + "." + SqlLexer.Quote(name);

        private static string Trim(string s, int max) => s == null ? "" : s.Length <= max ? s : s.Substring(0, max);

        private static async Task Exec(MySqlConnection c, string sql, CancellationToken ct)
        {
            using (var cmd = new MySqlCommand(sql, c)) await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        private static async Task<(string kind, long rows, long bytes)?> TableSize(MySqlConnection c, string db, string table, CancellationToken ct)
        {
            using (var cmd = new MySqlCommand("SELECT TABLE_TYPE, COALESCE(TABLE_ROWS,0), COALESCE(DATA_LENGTH,0)+COALESCE(INDEX_LENGTH,0) FROM information_schema.TABLES WHERE TABLE_SCHEMA = @d AND TABLE_NAME = @t", c))
            {
                cmd.Parameters.AddWithValue("@d", db);
                cmd.Parameters.AddWithValue("@t", table);
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                {
                    if (!await r.ReadAsync(ct).ConfigureAwait(false)) return null;
                    var kind = string.Equals(r.GetString(0), "VIEW", StringComparison.OrdinalIgnoreCase) ? "VIEW" : "TABLE";
                    return (kind, Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture), Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture));
                }
            }
        }

        private static async Task<bool> DatabaseExists(MySqlConnection c, string db, CancellationToken ct)
        {
            using (var cmd = new MySqlCommand("SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME = @d", c))
            {
                cmd.Parameters.AddWithValue("@d", db);
                return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0;
            }
        }

        private static async Task<string> ShowCreate(MySqlConnection c, string what, string db, string name, int column, CancellationToken ct)
        {
            string sql = what == "DATABASE" ? "SHOW CREATE DATABASE " + SqlLexer.Quote(db) : "SHOW CREATE " + what + " " + Q(db, name);
            using (var cmd = new MySqlCommand(sql, c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (!await r.ReadAsync(ct).ConfigureAwait(false)) throw new LuminaException(ErrorKind.InvalidInput, "Couldn't read the definition of " + db + "." + name);
                return r.GetString(column);
            }
        }

        private static async Task<string> InsertableColumns(MySqlConnection c, string db, string table, CancellationToken ct)
        {
            var cols = new List<string>();
            using (var cmd = new MySqlCommand("SELECT COLUMN_NAME, EXTRA FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = @d AND TABLE_NAME = @t ORDER BY ORDINAL_POSITION", c))
            {
                cmd.Parameters.AddWithValue("@d", db);
                cmd.Parameters.AddWithValue("@t", table);
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                    {
                        var extra = r.IsDBNull(1) ? "" : r.GetString(1);
                        if (extra.IndexOf("GENERATED", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            extra.IndexOf("VIRTUAL", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            extra.IndexOf("PERSISTENT", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        cols.Add(SqlLexer.Quote(r.GetString(0)));
                    }
            }
            if (cols.Count == 0) throw new LuminaException(ErrorKind.InvalidInput, "The table " + db + "." + table + " has no columns to copy.");
            return string.Join(", ", cols);
        }

        private static async Task<List<string>> Triggers(MySqlConnection c, string db, string table, CancellationToken ct)
        {
            var list = new List<string>();
            using (var cmd = new MySqlCommand("SELECT TRIGGER_NAME FROM information_schema.TRIGGERS WHERE EVENT_OBJECT_SCHEMA = @d AND EVENT_OBJECT_TABLE = @t ORDER BY ACTION_ORDER", c))
            {
                cmd.Parameters.AddWithValue("@d", db);
                cmd.Parameters.AddWithValue("@t", table);
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(r.GetString(0));
            }
            return list;
        }

        private static async Task SaveItem(MySqlConnection c, int batch, int seq, Item it, string sql, string copy, string cols, CancellationToken ct)
        {
            using (var cmd = new MySqlCommand("INSERT INTO `" + Schema + "`.`_items` (batch, seq, kind, db, name, create_sql, copy_table, columns_list) VALUES (@b,@s,@k,@d,@n,@q,@c,@l)", c))
            {
                cmd.Parameters.AddWithValue("@b", batch);
                cmd.Parameters.AddWithValue("@s", seq);
                cmd.Parameters.AddWithValue("@k", it.Kind);
                cmd.Parameters.AddWithValue("@d", it.Db);
                cmd.Parameters.AddWithValue("@n", it.Name ?? "");
                cmd.Parameters.AddWithValue("@q", (object)sql ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@c", (object)copy ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@l", (object)cols ?? DBNull.Value);
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        private static async Task DropCopies(MySqlConnection c, int batch, CancellationToken ct)
        {
            var copies = new List<string>();
            using (var cmd = new MySqlCommand("SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA = @s AND TABLE_NAME LIKE @p", c))
            {
                cmd.Parameters.AddWithValue("@s", Schema);
                cmd.Parameters.AddWithValue("@p", "b" + batch + "\\_%");
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    while (await r.ReadAsync(ct).ConfigureAwait(false)) copies.Add(r.GetString(0));
            }
            foreach (var t in copies) await Exec(c, "DROP TABLE IF EXISTS `" + Schema + "`.`" + t + "`", ct).ConfigureAwait(false);
        }

        private static async Task PruneAsync(MySqlConnection c, CancellationToken ct)
        {
            var old = new List<int>();
            using (var cmd = new MySqlCommand("SELECT batch FROM `" + Schema + "`.`_log` WHERE state IN ('ready','pending') ORDER BY batch DESC LIMIT 1000 OFFSET " + KeepBatches, c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await r.ReadAsync(ct).ConfigureAwait(false)) old.Add(r.GetInt32(0));
            foreach (var b in old)
            {
                await DropCopies(c, b, ct).ConfigureAwait(false);
                await Exec(c, "DELETE FROM `" + Schema + "`.`_items` WHERE batch = " + b, ct).ConfigureAwait(false);
                await Exec(c, "UPDATE `" + Schema + "`.`_log` SET state = 'expired' WHERE batch = " + b, ct).ConfigureAwait(false);
            }
            await Exec(c, "DELETE FROM `" + Schema + "`.`_log` WHERE state IN ('restored','expired','discarded') AND created < NOW() - INTERVAL 30 DAY", ct).ConfigureAwait(false);
        }
    }
}
