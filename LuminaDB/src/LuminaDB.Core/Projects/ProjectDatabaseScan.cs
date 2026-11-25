using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LuminaDB.Core.Projects
{
    /// <summary>What a project folder says about its database.</summary>
    public sealed class ProjectDbScan
    {
        /// <summary>The database the PHP connects to (new mysqli, mysqli_connect, PDO dbname=, DB_NAME, .env), or null.</summary>
        public string Database { get; set; }
        /// <summary>The file (relative to the folder) that names it.</summary>
        public string FoundIn { get; set; }
        /// <summary>.sql files in the folder, most likely first (named after the database, then the biggest).</summary>
        public List<string> SqlFiles { get; } = new List<string>();
    }

    /// <summary>
    /// An old XAMPP project brings its PHP but not its database. This reads the folder (no PHP is run) for the database
    /// name the code connects to and for .sql dumps that hold it, so the import can say "found feedback_db.sql".
    /// </summary>
    public static class ProjectDatabaseScan
    {
        private const int MaxPhpFiles = 600;
        private const long MaxPhpBytes = 512 * 1024;
        private const int MaxSqlFiles = 20;

        private static readonly HashSet<string> SkipFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "vendor", "node_modules", ".git", ".svn", ".idea", ".vscode", "storage", "cache", "tmp", "temp", "wp-includes", "wp-admin",
        };

        /// <summary>Placeholder names from templates and tutorials, not a real database.</summary>
        private static readonly HashSet<string> Placeholders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "database_name_here", "your_database", "your_db", "your_database_name", "dbname", "db_name", "database", "databasename", "mysql",
            "information_schema", "performance_schema", "sys",
        };

        private static readonly Regex Connect = new Regex(@"(?:new\s+mysqli|mysqli_connect|mysql_connect)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SelectDb = new Regex(@"(?:mysqli_select_db|mysql_select_db|->\s*select_db)\s*\(", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PdoDbName = new Regex(@"dbname\s*=\s*(\{?\$[A-Za-z_][A-Za-z0-9_]*\}?|[A-Za-z0-9_$-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Define = new Regex(@"define\s*\(\s*['""](DB_NAME|DB_DATABASE|DATABASE|DB|DBNAME|DATABASE_NAME)['""]\s*,\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex Assign = new Regex(@"\$([A-Za-z_][A-Za-z0-9_]*)\s*=\s*['""]([^'""\r\n]*)['""]\s*;", RegexOptions.Compiled);
        private static readonly Regex ArrayKey = new Regex(@"['""](database|dbname|db_name|db)['""]\s*=>\s*['""]([^'""]+)['""]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex EnvLine = new Regex(@"^\s*DB_DATABASE\s*=\s*['""]?([^'""\s#]+)", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly HashSet<string> DbVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "dbname", "db_name", "database", "db_database", "databasename", "database_name", "dbName", "db",
        };

        public static ProjectDbScan Scan(string folder)
        {
            var scan = new ProjectDbScan();
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return scan;
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);

            var php = new List<string>();
            var sql = new List<string>();
            Walk(root, 0, php, sql);

            // votes: a name in a connect call counts most; the file people keep it in (config/db/connect) breaks ties
            var votes = new Dictionary<string, (int score, string file)>(StringComparer.OrdinalIgnoreCase);
            void Vote(string name, int score, string file)
            {
                name = Clean(name);
                if (name == null) return;
                votes.TryGetValue(name, out var v);
                votes[name] = (v.score + score, v.file ?? Relative(root, file));
            }

            var env = Path.Combine(root, ".env");
            if (File.Exists(env))
            {
                var m = EnvLine.Match(ReadSafe(env) ?? "");
                if (m.Success) Vote(m.Groups[1].Value, 5, env);
            }

            foreach (var file in php.OrderByDescending(f => LooksLikeConfig(f)).Take(MaxPhpFiles))
            {
                var text = ReadSafe(file);
                if (string.IsNullOrEmpty(text)) continue;
                var vars = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (Match a in Assign.Matches(text)) vars[a.Groups[1].Value] = a.Groups[2].Value;
                int bonus = LooksLikeConfig(file) ? 1 : 0;

                foreach (Match m in Connect.Matches(text))
                {
                    var args = Arguments(text, m.Index + m.Length);
                    if (args.Count >= 4) Vote(Resolve(args[3], vars), 10 + bonus, file);
                }
                foreach (Match m in SelectDb.Matches(text))
                {
                    var args = Arguments(text, m.Index + m.Length);
                    var arg = m.Value.Contains("->") ? args.ElementAtOrDefault(0) : args.ElementAtOrDefault(1) ?? args.ElementAtOrDefault(0);
                    if (arg != null) Vote(Resolve(arg, vars), 8 + bonus, file);
                }
                foreach (Match m in PdoDbName.Matches(text)) Vote(Resolve(m.Groups[1].Value, vars), 8 + bonus, file);
                foreach (Match m in Define.Matches(text)) Vote(m.Groups[2].Value, 6 + bonus, file);
                foreach (Match m in ArrayKey.Matches(text)) Vote(m.Groups[2].Value, 4 + bonus, file);
                foreach (var kv in vars) if (DbVariables.Contains(kv.Key)) Vote(kv.Value, 2 + bonus, file);
            }

            if (votes.Count > 0)
            {
                var best = votes.OrderByDescending(v => v.Value.score).ThenBy(v => v.Key, StringComparer.OrdinalIgnoreCase).First();
                scan.Database = best.Key;
                scan.FoundIn = best.Value.file;
            }

            var db = scan.Database;
            foreach (var f in sql
                .OrderByDescending(f => db != null && string.Equals(Path.GetFileNameWithoutExtension(f), db, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(f => db != null && Path.GetFileNameWithoutExtension(f).IndexOf(db, StringComparison.OrdinalIgnoreCase) >= 0)
                .ThenByDescending(SizeOf)
                .Take(MaxSqlFiles))
                scan.SqlFiles.Add(Relative(root, f));
            return scan;
        }

        private static void Walk(string dir, int depth, List<string> php, List<string> sql)
        {
            if (depth > 8 || php.Count >= MaxPhpFiles * 2) return;
            string[] files, dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return; }
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext == ".php" || ext == ".inc") { if (SizeOf(f) <= MaxPhpBytes) php.Add(f); }
                else if (ext == ".sql") sql.Add(f);
            }
            foreach (var d in dirs)
            {
                var name = Path.GetFileName(d);
                if (SkipFolders.Contains(name)) continue;
                try { if ((new DirectoryInfo(d).Attributes & FileAttributes.ReparsePoint) != 0) continue; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { continue; }
                Walk(d, depth + 1, php, sql);
            }
        }

        private static bool LooksLikeConfig(string file)
        {
            var n = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            return n.Contains("config") || n.Contains("conn") || n.Contains("db") || n.Contains("database") || n == "dbh" || n.Contains("setting") || n == "wp-config";
        }

        /// <summary>The arguments of a call starting right after its "(": split on commas outside quotes and brackets.</summary>
        public static List<string> Arguments(string text, int start)
        {
            var args = new List<string>();
            var sb = new StringBuilder();
            int depth = 0;
            char quote = '\0';
            for (int i = start; i < text.Length && i < start + 2000; i++)
            {
                char c = text[i];
                if (quote != '\0')
                {
                    sb.Append(c);
                    if (c == '\\' && i + 1 < text.Length) { sb.Append(text[++i]); continue; }
                    if (c == quote) quote = '\0';
                    continue;
                }
                if (c == '\'' || c == '"') { quote = c; sb.Append(c); continue; }
                if (c == '(' || c == '[') { depth++; sb.Append(c); continue; }
                if (c == ')' || c == ']')
                {
                    if (depth == 0) { args.Add(sb.ToString().Trim()); return args; }
                    depth--; sb.Append(c); continue;
                }
                if (c == ',' && depth == 0) { args.Add(sb.ToString().Trim()); sb.Clear(); continue; }
                if (c == ';' && depth == 0) break;
                sb.Append(c);
            }
            return args;
        }

        /// <summary>'name', "name", $var (from an assignment in the same file) or {$var}; null otherwise.</summary>
        private static string Resolve(string arg, Dictionary<string, string> vars)
        {
            if (string.IsNullOrWhiteSpace(arg)) return null;
            arg = arg.Trim().Trim('{', '}').Trim();
            if (arg.Length >= 2 && (arg[0] == '\'' || arg[0] == '"') && arg[arg.Length - 1] == arg[0])
            {
                var inner = arg.Substring(1, arg.Length - 2);
                return inner.Contains("$") ? null : inner;
            }
            if (arg.StartsWith("$", StringComparison.Ordinal))
            {
                var name = arg.Substring(1);
                return vars.TryGetValue(name, out var v) ? v : null;
            }
            // a bare word (PDO dbname=feedback_db inside a string)
            return Regex.IsMatch(arg, @"^[A-Za-z0-9_$-]+$") ? arg : null;
        }

        /// <summary>A usable database name, or null for empty values and placeholders.</summary>
        private static string Clean(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            name = name.Trim();
            if (name.Length > 64 || !Regex.IsMatch(name, @"^[A-Za-z0-9_$-]+$") || Regex.IsMatch(name, @"^\d+$")) return null;
            return Placeholders.Contains(name) ? null : name;
        }

        private static string ReadSafe(string file)
        {
            try { return File.ReadAllText(file); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
        }

        private static long SizeOf(string f)
        {
            try { return new FileInfo(f).Length; } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return 0; }
        }

        private static string Relative(string root, string file)
        {
            var full = Path.GetFullPath(file);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length + 1) : full;
        }
    }
}
