using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;

namespace LuminaDB.Core
{
    public sealed class DatabaseSummary
    {
        public string Name { get; set; }
        public int Tables { get; set; }
        /// <summary>InnoDB's estimate (information_schema), exact enough for an overview.</summary>
        public long Rows { get; set; }
        public long Bytes { get; set; }
        public DateTime? Changed { get; set; }

        public string SizeText => FormatSize(Bytes);
        public string ChangedText => FormatWhen(Changed, DateTime.Now);

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            return (bytes / 1073741824.0).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
        }

        public static string FormatWhen(DateTime? when, DateTime now)
        {
            if (!when.HasValue) return "—";
            var w = when.Value;
            if (w.Date == now.Date) return "today " + w.ToString("HH:mm", CultureInfo.InvariantCulture);
            if (w.Date == now.Date.AddDays(-1)) return "yesterday";
            if (w.Year == now.Year) return w.ToString("d MMM", CultureInfo.InvariantCulture);
            return w.ToString("d MMM yyyy", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Reads and creates databases. All SQL goes through MySqlConnector, never the command line.</summary>
    public static class DatabaseCatalog
    {
        public static readonly string[] SystemSchemas = { "mysql", "information_schema", "performance_schema", "sys" };
        /// <summary>Where the Notebook keeps Undo copies. Hidden from every list.</summary>
        public const string UndoSchema = "lumina_undo";
        public static readonly string[] HiddenSchemas = { "mysql", "information_schema", "performance_schema", "sys", UndoSchema };

        public static async Task<List<DatabaseSummary>> ListAsync(MariaDbService server, CancellationToken ct = default)
        {
            const string sql = @"
SELECT s.SCHEMA_NAME,
       COUNT(t.TABLE_NAME),
       COALESCE(SUM(t.TABLE_ROWS), 0),
       COALESCE(SUM(t.DATA_LENGTH + t.INDEX_LENGTH), 0),
       GREATEST(COALESCE(MAX(t.UPDATE_TIME), '1970-01-01'), COALESCE(MAX(t.CREATE_TIME), '1970-01-01'))
FROM information_schema.SCHEMATA s
LEFT JOIN information_schema.TABLES t ON t.TABLE_SCHEMA = s.SCHEMA_NAME
WHERE s.SCHEMA_NAME NOT IN ('mysql','information_schema','performance_schema','sys','lumina_undo')
GROUP BY s.SCHEMA_NAME
ORDER BY s.SCHEMA_NAME";
            var list = new List<DatabaseSummary>();
            using (var c = await server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
            using (var cmd = new MySqlCommand(sql, c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    DateTime? changed = r.IsDBNull(4) ? (DateTime?)null : Convert.ToDateTime(r.GetValue(4), CultureInfo.InvariantCulture);
                    if (changed.HasValue && changed.Value.Year <= 1970) changed = null;
                    list.Add(new DatabaseSummary
                    {
                        Name = r.GetString(0),
                        Tables = Convert.ToInt32(r.GetValue(1), CultureInfo.InvariantCulture),
                        Rows = Convert.ToInt64(r.GetValue(2), CultureInfo.InvariantCulture),
                        Bytes = Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture),
                        Changed = changed,
                    });
                }
            }
            return list;
        }

        private static readonly Regex SafeName = new Regex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Null if the name is fine, otherwise a plain explanation. We only allow names that work
        /// unquoted in PHP/SQL, on every engine set, and on case-insensitive Windows folders.
        /// </summary>
        public static string ValidateName(string name, IEnumerable<string> existing = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Give the database a name.";
            if (name != name.Trim()) return "Remove the spaces at the start or end.";
            if (name.Length > 64) return "Names can be at most 64 characters.";
            if (name.Contains(" ")) return "Use _ instead of spaces, e.g. " + name.Replace(' ', '_') + ".";
            if (char.IsDigit(name[0])) return "Start with a letter, e.g. db_" + name + ".";
            if (!SafeName.IsMatch(name)) return "Use only English letters, digits and _ (so it works in every tool and in PHP).";
            if (HiddenSchemas.Contains(name, StringComparer.OrdinalIgnoreCase)) return "\"" + name + "\" is reserved by the server. Pick another name.";
            if (existing != null && existing.Contains(name, StringComparer.OrdinalIgnoreCase)) return "A database called \"" + name + "\" already exists.";
            return null;
        }

        public static async Task CreateAsync(MariaDbService server, string name, CancellationToken ct = default)
        {
            var problem = ValidateName(name);
            if (problem != null) throw new LuminaException(ErrorKind.InvalidInput, problem);
            using (var c = await server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
            using (var cmd = new MySqlCommand("CREATE DATABASE `" + name + "` CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci", c))
            {
                try { await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false); }
                catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DatabaseCreateExists)
                {
                    throw new LuminaException(ErrorKind.InvalidInput, "A database called \"" + name + "\" already exists.", ex);
                }
            }
        }
    }

    /// <summary>Queries per second for the activity line, from the server's own Questions counter.</summary>
    public sealed class ActivityMeter
    {
        private long? _lastQuestions;
        private DateTime _lastAt;

        public async Task<double?> SampleAsync(MariaDbService server, CancellationToken ct = default)
        {
            long questions;
            using (var c = await server.OpenConnectionAsync(null, ct).ConfigureAwait(false))
            using (var cmd = new MySqlCommand("SELECT VARIABLE_VALUE FROM information_schema.GLOBAL_STATUS WHERE VARIABLE_NAME = 'QUESTIONS'", c))
                questions = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);
            var now = DateTime.UtcNow;
            double? qps = null;
            if (_lastQuestions.HasValue && questions >= _lastQuestions.Value)
            {
                var secs = Math.Max(0.001, (now - _lastAt).TotalSeconds);
                qps = Math.Max(0, (questions - _lastQuestions.Value - 1) / secs);   // -1: our own SELECT
            }
            _lastQuestions = questions;
            _lastAt = now;
            return qps;
        }

        public void Reset() => _lastQuestions = null;
    }
}
