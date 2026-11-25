using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace LuminaDB.Core.Admin
{
    /// <summary>One look at SHOW GLOBAL STATUS (SPEC §5.8).</summary>
    public sealed class ServerStatus
    {
        public DateTime TakenUtc { get; set; }
        public TimeSpan Uptime { get; set; }
        public long ThreadsConnected { get; set; }
        public long ThreadsRunning { get; set; }
        public long MaxUsedConnections { get; set; }
        public long Questions { get; set; }
        public long BytesReceived { get; set; }
        public long BytesSent { get; set; }
        public long SlowQueries { get; set; }
        public long BufferPoolPagesTotal { get; set; }
        public long BufferPoolPagesFree { get; set; }
        public long BufferPoolReadRequests { get; set; }
        public long BufferPoolReads { get; set; }

        /// <summary>Share of the InnoDB buffer pool in use, 0..1.</summary>
        public double BufferPoolUsed => BufferPoolPagesTotal <= 0 ? 0 : 1.0 - (double)BufferPoolPagesFree / BufferPoolPagesTotal;

        /// <summary>Reads served from memory rather than disk, 0..1 (1 when nothing was read yet).</summary>
        public double HitRatio => BufferPoolReadRequests <= 0 ? 1 : 1.0 - (double)BufferPoolReads / BufferPoolReadRequests;

        /// <summary>Queries per second between two looks (the average since start when there is no earlier look).</summary>
        public double QueriesPerSecond(ServerStatus earlier)
        {
            if (earlier != null && earlier.Questions <= Questions)
            {
                var secs = (TakenUtc - earlier.TakenUtc).TotalSeconds;
                if (secs > 0.2) return (Questions - earlier.Questions) / secs;
            }
            return Uptime.TotalSeconds <= 0 ? 0 : Questions / Uptime.TotalSeconds;
        }

        public static string Bytes(long n)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            double v = n;
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString(i == 0 ? "0" : v < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + u[i];
        }

        public static string Duration(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return ((int)t.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " s";
            if (t.TotalHours < 1) return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + " min";
            if (t.TotalDays < 1) return ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + " h " + t.Minutes.ToString(CultureInfo.InvariantCulture) + " min";
            return ((int)t.TotalDays).ToString(CultureInfo.InvariantCulture) + " d " + t.Hours.ToString(CultureInfo.InvariantCulture) + " h";
        }
    }

    /// <summary>One row of the process list.</summary>
    public sealed class ServerConnection
    {
        public long Id { get; set; }
        public string User { get; set; }
        public string Host { get; set; }
        public string Database { get; set; }
        public string Command { get; set; }
        public long Seconds { get; set; }
        public string State { get; set; }
        public string Query { get; set; }
        /// <summary>The connection that is reading this list.</summary>
        public bool IsOurs { get; set; }

        /// <summary>MariaDB's own threads (event scheduler, InnoDB purge…): not connections anyone opened.</summary>
        public bool IsSystem => string.Equals(User, "system user", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(User, "event_scheduler", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(Command, "Daemon", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(Command, "Binlog Dump", StringComparison.OrdinalIgnoreCase);

        public bool CanKill => !IsOurs && !IsSystem;
        public bool IsBusy => !string.Equals(Command, "Sleep", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(Query);
        /// <summary>Host without the client's port ("localhost:52011" → "localhost").</summary>
        public string Client
        {
            get
            {
                var h = Host ?? "";
                int colon = h.LastIndexOf(':');
                return colon > 0 && h.IndexOf(']') < colon && h.Count(c => c == ':') == 1 ? h.Substring(0, colon) : h;
            }
        }
        public string Time => ServerStatus.Duration(TimeSpan.FromSeconds(Seconds));
    }

    /// <summary>A server variable with what the server says about it, plus a plain note for the ones students touch.</summary>
    public sealed class ServerVariable
    {
        public string Name { get; set; }
        public string Value { get; set; }
        public string Default { get; set; }
        /// <summary>COMPILE-TIME, CONFIG, COMMAND-LINE, SQL, AUTO…</summary>
        public string Origin { get; set; }
        /// <summary>GLOBAL, SESSION or SESSION ONLY.</summary>
        public string Scope { get; set; }
        /// <summary>BOOLEAN, INT, INT UNSIGNED, BIGINT UNSIGNED, DOUBLE, ENUM, SET, VARCHAR…</summary>
        public string Type { get; set; }
        public string Comment { get; set; }
        public string Min { get; set; }
        public string Max { get; set; }
        public List<string> Choices { get; set; } = new List<string>();
        public bool ReadOnly { get; set; }
        /// <summary>Can it be set in my.ini (COMMAND_LINE_ARGUMENT isn't NULL)?</summary>
        public bool InConfig { get; set; }

        public string Note => ServerMonitor.NoteFor(Name);
        public bool IsBoolean => string.Equals(Type, "BOOLEAN", StringComparison.OrdinalIgnoreCase);
        public bool IsEnum => string.Equals(Type, "ENUM", StringComparison.OrdinalIgnoreCase);
        public bool IsSet => string.Equals(Type, "SET", StringComparison.OrdinalIgnoreCase);
        public bool IsNumber => Type != null && (Type.StartsWith("INT", StringComparison.OrdinalIgnoreCase) || Type.StartsWith("BIGINT", StringComparison.OrdinalIgnoreCase)
                                                 || Type.StartsWith("DOUBLE", StringComparison.OrdinalIgnoreCase));
        /// <summary>SET GLOBAL works on it.</summary>
        public bool CanSetNow => !ReadOnly && !string.Equals(Scope, "SESSION ONLY", StringComparison.OrdinalIgnoreCase);
        public bool IsChanged => Default != null && !string.Equals(Value, Default, StringComparison.OrdinalIgnoreCase);
        public string Help => string.IsNullOrEmpty(Note) ? Comment : Note;
    }

    /// <summary>Reads the server's state for the Server page (SPEC §5.8). Everything is plain SELECTs; changes are shown as SQL first.</summary>
    public static class ServerMonitor
    {
        // ------------------------------------------------------------------ status

        public static async Task<ServerStatus> ReadStatusAsync(MySqlConnection c, CancellationToken ct = default)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var cmd = new MySqlCommand("SHOW GLOBAL STATUS", c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await r.ReadAsync(ct).ConfigureAwait(false)) map[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            return FromStatus(map, DateTime.UtcNow);
        }

        public static ServerStatus FromStatus(IDictionary<string, string> map, DateTime takenUtc)
        {
            long L(string k) => map.TryGetValue(k, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
            return new ServerStatus
            {
                TakenUtc = takenUtc,
                Uptime = TimeSpan.FromSeconds(L("Uptime")),
                ThreadsConnected = L("Threads_connected"),
                ThreadsRunning = L("Threads_running"),
                MaxUsedConnections = L("Max_used_connections"),
                Questions = L("Questions"),
                BytesReceived = L("Bytes_received"),
                BytesSent = L("Bytes_sent"),
                SlowQueries = L("Slow_queries"),
                BufferPoolPagesTotal = L("Innodb_buffer_pool_pages_total"),
                BufferPoolPagesFree = L("Innodb_buffer_pool_pages_free"),
                BufferPoolReadRequests = L("Innodb_buffer_pool_read_requests"),
                BufferPoolReads = L("Innodb_buffer_pool_reads"),
            };
        }

        // ------------------------------------------------------------------ connections

        public static async Task<List<ServerConnection>> ListConnectionsAsync(MySqlConnection c, CancellationToken ct = default)
        {
            long ours;
            using (var cmd = new MySqlCommand("SELECT CONNECTION_ID()", c))
                ours = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            var list = new List<ServerConnection>();
            const string sql = "SELECT ID, USER, HOST, DB, COMMAND, TIME, STATE, INFO FROM information_schema.PROCESSLIST ORDER BY ID";
            using (var cmd = new MySqlCommand(sql, c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    string S(int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
                    var id = Convert.ToInt64(r.GetValue(0), CultureInfo.InvariantCulture);
                    list.Add(new ServerConnection
                    {
                        Id = id,
                        User = S(1),
                        Host = S(2),
                        Database = S(3),
                        Command = S(4),
                        Seconds = r.IsDBNull(5) ? 0 : Convert.ToInt64(r.GetValue(5), CultureInfo.InvariantCulture),
                        State = S(6),
                        Query = S(7),
                        IsOurs = id == ours,
                    });
                }
            }
            return list;
        }

        /// <summary>KILL QUERY stops what it is running and keeps the connection; KILL closes it.</summary>
        public static string KillSql(ServerConnection target, bool queryOnly)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (target.IsOurs) throw new LuminaException(ErrorKind.InvalidInput, "That's LuminaDB's own connection, the one reading this list.");
            if (target.IsSystem) throw new LuminaException(ErrorKind.InvalidInput, "That's one of MariaDB's own threads; it isn't a connection to stop.");
            return (queryOnly ? "KILL QUERY " : "KILL ") + target.Id.ToString(CultureInfo.InvariantCulture);
        }

        public static async Task KillAsync(MySqlConnection c, ServerConnection target, bool queryOnly, CancellationToken ct = default)
        {
            using (var cmd = new MySqlCommand(KillSql(target, queryOnly), c))
            {
                try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
                catch (MySqlException ex) when (ex.Number == 1094) { /* unknown thread: it already ended */ }
            }
        }

        // ------------------------------------------------------------------ variables

        public static async Task<List<ServerVariable>> ReadVariablesAsync(MySqlConnection c, CancellationToken ct = default)
        {
            const string sql = "SELECT VARIABLE_NAME, GLOBAL_VALUE, SESSION_VALUE, DEFAULT_VALUE, GLOBAL_VALUE_ORIGIN, VARIABLE_SCOPE, VARIABLE_TYPE, " +
                               "VARIABLE_COMMENT, NUMERIC_MIN_VALUE, NUMERIC_MAX_VALUE, ENUM_VALUE_LIST, READ_ONLY, COMMAND_LINE_ARGUMENT " +
                               "FROM information_schema.SYSTEM_VARIABLES ORDER BY VARIABLE_NAME";
            var list = new List<ServerVariable>();
            using (var cmd = new MySqlCommand(sql, c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    string S(int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
                    var scope = S(5);
                    var enumList = S(10);
                    list.Add(new ServerVariable
                    {
                        Name = (S(0) ?? "").ToLowerInvariant(),
                        Value = string.Equals(scope, "SESSION ONLY", StringComparison.OrdinalIgnoreCase) ? S(2) : S(1),
                        Default = S(3),
                        Origin = S(4),
                        Scope = scope,
                        Type = S(6),
                        Comment = S(7),
                        Min = S(8),
                        Max = S(9),
                        Choices = string.IsNullOrEmpty(enumList) ? new List<string>() : enumList.Split(',').ToList(),
                        ReadOnly = string.Equals(S(11), "YES", StringComparison.OrdinalIgnoreCase),
                        InConfig = S(12) != null,
                    });
                }
            }
            return list;
        }

        /// <summary>
        /// Checks and tidies a value the user typed for a variable. Returns the value to use, or throws a friendly
        /// <see cref="LuminaException"/>. Booleans become ON/OFF, numbers must be in range, enums must be one of the choices.
        /// </summary>
        public static string Normalize(ServerVariable v, string text)
        {
            if (v == null) throw new ArgumentNullException(nameof(v));
            var t = (text ?? "").Trim();
            if (v.IsBoolean)
            {
                switch (t.ToUpperInvariant())
                {
                    case "ON": case "1": case "TRUE": case "YES": return "ON";
                    case "OFF": case "0": case "FALSE": case "NO": return "OFF";
                }
                throw Bad(v.Name + " is ON or OFF.");
            }
            if (v.IsNumber)
            {
                var digits = t.Replace("_", "");
                long mult = 1;
                if (digits.Length > 1 && "KMG".IndexOf(char.ToUpperInvariant(digits[digits.Length - 1])) >= 0 && !v.Type.StartsWith("DOUBLE", StringComparison.OrdinalIgnoreCase))
                {
                    mult = char.ToUpperInvariant(digits[digits.Length - 1]) == 'K' ? 1024L : char.ToUpperInvariant(digits[digits.Length - 1]) == 'M' ? 1024L * 1024 : 1024L * 1024 * 1024;
                    digits = digits.Substring(0, digits.Length - 1);
                }
                if (!decimal.TryParse(digits, NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var n))
                    throw Bad(v.Name + " takes a number" + (v.Type.StartsWith("DOUBLE", StringComparison.OrdinalIgnoreCase) ? "." : " (you can write 64M or 1G)."));
                n *= mult;
                if (!v.Type.StartsWith("DOUBLE", StringComparison.OrdinalIgnoreCase) && n != decimal.Truncate(n)) throw Bad(v.Name + " takes a whole number.");
                if (decimal.TryParse(v.Min, NumberStyles.Float, CultureInfo.InvariantCulture, out var min) && n < min) throw Bad(v.Name + " can't be less than " + v.Min + ".");
                if (decimal.TryParse(v.Max, NumberStyles.Float, CultureInfo.InvariantCulture, out var max) && n > max) throw Bad(v.Name + " can't be more than " + v.Max + ".");
                return n.ToString(CultureInfo.InvariantCulture);
            }
            if (v.IsEnum)
            {
                var hit = v.Choices.FirstOrDefault(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase));
                if (hit == null) throw Bad(v.Name + " is one of: " + string.Join(", ", v.Choices) + ".");
                return hit;
            }
            if (v.IsSet)
            {
                var parts = t.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
                var result = new List<string>();
                foreach (var p in parts)
                {
                    var hit = v.Choices.FirstOrDefault(x => string.Equals(x, p, StringComparison.OrdinalIgnoreCase));
                    if (hit == null) throw Bad("\"" + p + "\" isn't one of " + v.Name + "'s choices: " + string.Join(", ", v.Choices) + ".");
                    if (!result.Contains(hit)) result.Add(hit);
                }
                return string.Join(",", result);
            }
            if (t.Any(char.IsControl)) throw Bad("The value has an invisible character in it.");
            return t;
        }

        /// <summary>SET GLOBAL name = value, quoted for its type.</summary>
        public static string SetGlobalSql(ServerVariable v, string normalized)
        {
            if (!v.CanSetNow) throw Bad(v.Name + " can't be changed while the server runs" + (v.InConfig ? "; save it for the next start instead." : "."));
            return "SET GLOBAL " + v.Name + " = " + SqlValue(v, normalized);
        }

        private static string SqlValue(ServerVariable v, string normalized) =>
            v.IsNumber || v.IsBoolean ? normalized : Accounts.Literal(normalized);

        public static async Task SetGlobalAsync(MySqlConnection c, ServerVariable v, string normalized, CancellationToken ct = default)
        {
            using (var cmd = new MySqlCommand(SetGlobalSql(v, normalized), c)) await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        private static LuminaException Bad(string msg) => new LuminaException(ErrorKind.InvalidInput, msg);

        /// <summary>Plain notes for the variables a student is likely to look at; null for the rest (the server's comment is used).</summary>
        public static string NoteFor(string name)
        {
            switch ((name ?? "").ToLowerInvariant())
            {
                case "max_connections": return "How many programs can be connected at once. The default is plenty for local development.";
                case "max_allowed_packet": return "Largest single query or row. Raise it if importing a big .sql file fails with \"packet too large\".";
                case "innodb_buffer_pool_size": return "Memory MariaDB keeps for table data. LuminaDB sets it from your PC's RAM.";
                case "slow_query_log": return "Write queries slower than long_query_time to the slow log (Server › Logs).";
                case "long_query_time": return "Seconds before a query counts as slow.";
                case "general_log": return "Write EVERY query to the general log. Useful to see what your PHP sends; turn it off after, the file grows fast.";
                case "sql_mode": return "How strict MariaDB is about bad data. STRICT_TRANS_TABLES makes it refuse a wrong value instead of guessing.";
                case "event_scheduler": return "Runs CREATE EVENT jobs on their schedule.";
                case "time_zone": return "The time zone NOW() uses. SYSTEM means the PC's own.";
                case "wait_timeout": return "Seconds an idle connection is kept before the server closes it.";
                case "lower_case_table_names": return "1: table names are stored in lower case, as on every Windows server. Fixed when the data folder is created.";
                case "character_set_server": return "Text encoding for new databases. utf8mb4 holds every language and emoji.";
                case "collation_server": return "How text is compared and sorted for new databases.";
                case "autocommit": return "Each statement is saved at once unless you START TRANSACTION.";
                case "foreign_key_checks": return "Refuse rows that point at a parent that doesn't exist. Leave it ON.";
                case "local_infile": return "Allows LOAD DATA LOCAL INFILE (CSV import).";
                case "secure_file_priv": return "The only folder SELECT … INTO OUTFILE may write to: LuminaDB's exports folder.";
                case "port": return "The port this server listens on. Change it in Settings.";
                case "datadir": return "Where the databases are stored. Move it in Settings.";
                case "version": return "The MariaDB version of this engine set.";
                case "tmp_table_size": return "Biggest in-memory temporary table before it spills to disk.";
                case "max_heap_table_size": return "Biggest MEMORY table.";
                case "group_concat_max_len": return "Longest result GROUP_CONCAT returns before cutting it short.";
                case "default_storage_engine": return "Engine for CREATE TABLE without ENGINE=. InnoDB has transactions and foreign keys.";
                case "innodb_lock_wait_timeout": return "Seconds a statement waits for a locked row before giving up with error 1205.";
                case "performance_schema": return "Detailed timing tables (performance_schema database).";
                default: return null;
            }
        }
    }
}
