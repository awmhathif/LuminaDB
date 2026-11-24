using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LuminaDB.Core.Import
{
    /// <summary>Which MariaDB the script is going into: 11.8 (Modern) accepts more MySQL 8 syntax than 10.4 (Legacy, X86).</summary>
    public enum ImportTarget { Modern, Legacy }

    /// <summary>Where a .sql file came from, read from its first lines.</summary>
    public enum SqlSource { Unknown, MySql8Dump, MySqlDump, MariaDbDump, PhpMyAdmin, Workbench }

    /// <summary>One kind of change the rewriter made, counted over a whole import.</summary>
    public sealed class RewriteNote
    {
        public string Id { get; set; }
        /// <summary>Plain English: what was changed and why.</summary>
        public string Text { get; set; }
        public int Count { get; set; }
        /// <summary>The first line it happened on.</summary>
        public int FirstLine { get; set; }
        /// <summary>A short before → after, from the first place.</summary>
        public string Example { get; set; }
        public override string ToString() => $"{Id} ×{Count}: {Text}";
    }

    /// <summary>
    /// Turns MySQL 8 / Workbench / phpMyAdmin SQL into SQL MariaDB runs (SPEC §5.6). Every rule is one verified in
    /// ParityTests/database/RESULTS.md §2 (R01–R25) on both MariaDB 11.8 and 10.4. Rules never touch the inside of
    /// string literals, so data that happens to contain "VISIBLE" or "->" stays exactly as it was. The rewrites that
    /// need a real parser (LATERAL, JSON_TABLE, GROUPING, functional indexes, MEMBER OF, …) are not guessed at: when
    /// such a statement fails, <see cref="ExplainUnsupported"/> says what to change by hand.
    /// </summary>
    public static class ImportRewriter
    {
        public static ImportTarget TargetFor(EngineSetKind kind) => kind == EngineSetKind.Modern ? ImportTarget.Modern : ImportTarget.Legacy;

        private enum For { Both, Modern, Legacy }

        private sealed class Rule
        {
            public string Id;
            public string Text;
            public For Target;
            /// <summary>Cheap test before the regex runs: the statement must contain this (ignoring case).</summary>
            public string Trigger;
            public Regex Pattern;
            public string Replacement;
            public MatchEvaluator Evaluator;
            /// <summary>Applied to the text with string literals in place (collation names live in version comments and strings).</summary>
            public bool Raw;
            /// <summary>Only for statements starting with one of these words (null = any).</summary>
            public string[] Verbs;
        }

        private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;
        private const RegexOptions OS = O | RegexOptions.Singleline;

        private static readonly Regex Insert = new Regex(@"^\s*(?:INSERT|REPLACE)\b", O);
        private static readonly Regex HasSelect = new Regex(@"\bSELECT\b", O);

        private static readonly List<Rule> Rules = new List<Rule>
        {
            // MariaDB 11.8's own first line; the 10.4 client and server don't know it (R05c)
            new Rule { Id = "R05c", Target = For.Both, Trigger = "sandbox mode", Raw = true,
                Pattern = new Regex(@"/\*M!999999\\- enable the sandbox mode \*/\s*", O), Replacement = "",
                Text = "Removed MariaDB 11.8's \"sandbox mode\" line, which older servers don't understand." },
            // MySQL 8-only parts of mysqldump headers: /*!80016 DEFAULT ENCRYPTION='N' */, /*!80000 '+'*/
            new Rule { Id = "M80", Target = For.Both, Trigger = "/*!80", Raw = true,
                Pattern = new Regex(@"/\*!80\d{3}\s(?:[^*]|\*(?!/))*\*/", O), Replacement = "",
                Text = "Removed MySQL 8-only settings from the dump header (such as DEFAULT ENCRYPTION); MariaDB has no use for them." },

            // collations (R05, R05b): 10.4 has neither MySQL 8's 0900 nor MariaDB 11's uca1400 collations
            new Rule { Id = "R05", Target = For.Legacy, Trigger = "_0900_", Raw = true,
                Pattern = new Regex(@"\b(utf8mb4)_0900_(?:as_cs|bin)\b", O), Replacement = "$1_bin",
                Text = "Changed MySQL 8's utf8mb4_0900 binary collation to utf8mb4_bin (same sorting)." },
            new Rule { Id = "R05", Target = For.Legacy, Trigger = "_0900_", Raw = true,
                Pattern = new Regex(@"\b(utf8mb4)_0900_\w+", O), Replacement = "$1_unicode_520_ci",
                Text = "Changed MySQL 8's utf8mb4_0900_ai_ci collation to utf8mb4_unicode_520_ci, the closest one this MariaDB has (Sinhala and Tamil sort the same; a few rare characters may sort differently)." },
            new Rule { Id = "R05b", Target = For.Legacy, Trigger = "uca1400", Raw = true,
                Pattern = new Regex(@"\b(utf8mb[34])_uca1400_\w+", O), Replacement = "utf8mb4_unicode_520_ci",
                Text = "Changed MariaDB 11's uca1400 collation to utf8mb4_unicode_520_ci, which MariaDB 10.4 has." },
            new Rule { Id = "U3", Target = For.Legacy, Trigger = "utf8mb3", Raw = true,
                Pattern = new Regex(@"\butf8mb3(?=\b|_)", O), Replacement = "utf8",
                Text = "Wrote utf8mb3 as utf8, the name MariaDB 10.4 uses for the same character set." },

            // index visibility (R01, R02, R15)
            new Rule { Id = "R01", Target = For.Legacy, Trigger = "VISIBLE",
                Pattern = new Regex(@"\)\s+VISIBLE\b", O), Replacement = ")",
                Text = "Removed VISIBLE after indexes (Workbench adds it; every MariaDB index is visible)." },
            new Rule { Id = "R02", Target = For.Modern, Trigger = "INVISIBLE",
                // only after an index's column list: a column's INVISIBLE (VARCHAR(10) INVISIBLE) is MariaDB syntax too
                Pattern = new Regex(@"(\b(?:INDEX|KEY)\b(?:\s+(?:`[^`]+`|\w+))?(?:\s+USING\s+\w+)?\s*\((?:[^()]|\([^()]*\))*\)(?:\s+(?:COMMENT\s+'[^']*'|USING\s+\w+))*)\s+INVISIBLE\b", O), Replacement = "$1 IGNORED",
                Text = "Changed INVISIBLE indexes to IGNORED, MariaDB's word for the same thing." },
            new Rule { Id = "R02", Target = For.Legacy, Trigger = "INVISIBLE",
                Pattern = new Regex(@"(\b(?:INDEX|KEY)\b(?:\s+(?:`[^`]+`|\w+))?(?:\s+USING\s+\w+)?\s*\((?:[^()]|\([^()]*\))*\)(?:\s+(?:COMMENT\s+'[^']*'|USING\s+\w+))*)\s+INVISIBLE\b", O), Replacement = "$1",
                Text = "Made INVISIBLE indexes normal ones: MariaDB 10.4 can't hide an index." },
            new Rule { Id = "R15", Target = For.Modern, Trigger = "ALTER INDEX",
                Pattern = new Regex(@"\bALTER\s+INDEX\s+(`[^`]+`|\w+)\s+INVISIBLE\b", O), Replacement = "ALTER INDEX $1 IGNORED",
                Text = "Changed ALTER INDEX … INVISIBLE to IGNORED." },
            new Rule { Id = "R15", Target = For.Modern, Trigger = "ALTER INDEX",
                Pattern = new Regex(@"\bALTER\s+INDEX\s+(`[^`]+`|\w+)\s+VISIBLE\b", O), Replacement = "ALTER INDEX $1 NOT IGNORED",
                Text = "Changed ALTER INDEX … VISIBLE to NOT IGNORED." },

            // accounts (R03, R04, R20, R21)
            new Rule { Id = "R03", Target = For.Both, Trigger = "IDENTIFIED",
                Pattern = new Regex(@"\bIDENTIFIED\s+WITH\s+'?(?:caching_sha2_password|sha256_password|mysql_native_password)'?\s+BY\b", O), Replacement = "IDENTIFIED BY",
                Text = "Changed IDENTIFIED WITH caching_sha2_password BY to IDENTIFIED BY: MariaDB stores the password its own way and the same password works." },
            new Rule { Id = "R20", Target = For.Both, Trigger = "FAILED_LOGIN_ATTEMPTS",
                Pattern = new Regex(@"\s+FAILED_LOGIN_ATTEMPTS\s+\d+", O), Replacement = "",
                Text = "Removed FAILED_LOGIN_ATTEMPTS (MariaDB only has one setting for the whole server)." },
            new Rule { Id = "R20", Target = For.Both, Trigger = "PASSWORD_LOCK_TIME",
                Pattern = new Regex(@"\s+PASSWORD_LOCK_TIME\s+(?:\d+|UNBOUNDED)", O), Replacement = "",
                Text = "Removed PASSWORD_LOCK_TIME (MariaDB only has one setting for the whole server)." },
            new Rule { Id = "R21", Target = For.Both, Trigger = "ROLE",
                Pattern = new Regex(@"\b(CREATE|DROP)\s+ROLE\s+(IF\s+(?:NOT\s+)?EXISTS\s+)?('[^']+'|`[^`]+`|\w+)\s*@\s*('[^']*'|`[^`]*`|%)", O), Replacement = "$1 ROLE $2$3",
                Text = "Removed the @host part from role names (MariaDB roles have no host)." },

            // CHECK … ENFORCED (R13)
            new Rule { Id = "R13", Target = For.Both, Trigger = "ENFORCED",
                Pattern = new Regex(@",\s*(?:CONSTRAINT\s+(?:`[^`]+`|\w+)\s+)?CHECK\s*\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)\s+NOT\s+ENFORCED", OS), Replacement = "",
                Text = "Removed CHECK … NOT ENFORCED: MariaDB always enforces a CHECK, and this one was switched off in MySQL." },
            new Rule { Id = "R13", Target = For.Both, Trigger = "ENFORCED",
                Pattern = new Regex(@"(CHECK\s*\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\))\s+ENFORCED\b", OS), Replacement = "$1",
                Text = "Removed the word ENFORCED after CHECK (MariaDB always enforces it)." },

            // JSON operators (R09), CAST AS JSON (R18)
            new Rule { Id = "R09", Target = For.Both, Trigger = "->>",
                Pattern = new Regex(@"((?:`[^`]+`|\b\w+)(?:\.(?:`[^`]+`|\w+))?)\s*->>\s*('[^']*')", O), Replacement = "JSON_UNQUOTE(JSON_EXTRACT($1, $2))",
                Text = "Changed col->>'$.x' to JSON_UNQUOTE(JSON_EXTRACT(col, '$.x')): MariaDB has no -> operators." },
            new Rule { Id = "R09", Target = For.Both, Trigger = "->",
                Pattern = new Regex(@"((?:`[^`]+`|\b\w+)(?:\.(?:`[^`]+`|\w+))?)\s*->\s*('[^']*')", O), Replacement = "JSON_EXTRACT($1, $2)",
                Text = "Changed col->'$.x' to JSON_EXTRACT(col, '$.x'): MariaDB has no -> operators." },
            new Rule { Id = "R18", Target = For.Both, Trigger = "JSON",
                Pattern = new Regex(@"\bCAST\s*\(\s*([^()]+?)\s+AS\s+JSON\s*\)", O), Replacement = "JSON_COMPACT($1)",
                Text = "Changed CAST(x AS JSON) to JSON_COMPACT(x)." },

            // functions and statements (R08, R12, R17, R19, R24)
            new Rule { Id = "R08", Target = For.Both, Trigger = "REGEXP_LIKE",
                Pattern = new Regex(@"\bREGEXP_LIKE\s*\(\s*([^,()]+?)\s*,\s*('[^']*')\s*,\s*'i'\s*\)", O),
                Evaluator = m => "(" + m.Groups[1].Value + " REGEXP " + CaseInsensitivePattern(m.Groups[2].Value) + ")",
                Text = "Changed REGEXP_LIKE(a, 'p', 'i') to a REGEXP '(?i)p'." },
            new Rule { Id = "R08", Target = For.Both, Trigger = "REGEXP_LIKE",
                Pattern = new Regex(@"\bREGEXP_LIKE\s*\(\s*([^,()]+?)\s*,\s*('[^']*')\s*\)", O), Replacement = "($1 REGEXP $2)",
                Text = "Changed REGEXP_LIKE(a, 'p') to a REGEXP 'p'." },
            new Rule { Id = "R12", Target = For.Both, Trigger = "ANALYZE",
                Pattern = new Regex(@"^(\s*)EXPLAIN\s+ANALYZE\b", O), Replacement = "$1ANALYZE FORMAT=JSON",
                Text = "Changed EXPLAIN ANALYZE to ANALYZE FORMAT=JSON." },
            new Rule { Id = "R17", Target = For.Both, Trigger = "TABLE", Verbs = new[] { "TABLE" },
                Pattern = new Regex(@"^(\s*)TABLE\s+((?:`[^`]+`|\w+)(?:\.(?:`[^`]+`|\w+))?)\s*$", OS), Replacement = "$1SELECT * FROM $2",
                Text = "Changed TABLE t to SELECT * FROM t." },
            new Rule { Id = "R17", Target = For.Both, Trigger = "ROW", Verbs = new[] { "VALUES", "INSERT", "REPLACE" },
                Pattern = new Regex(@"(?<=\bVALUES\s*|,\s*)ROW\s*\(", O), Replacement = "(",
                Text = "Changed VALUES ROW(…) to VALUES (…)." },
            new Rule { Id = "R19", Target = For.Both, Trigger = "PERSIST",
                Pattern = new Regex(@"\bSET\s+PERSIST(?:_ONLY)?\b", O), Replacement = "SET GLOBAL",
                Text = "Changed SET PERSIST to SET GLOBAL: it lasts until the server restarts (Settings keeps values for good)." },
            new Rule { Id = "R24", Target = For.Both, Trigger = "SRID",
                Pattern = new Regex(@"\b(POINT|GEOMETRY|LINESTRING|POLYGON|MULTIPOINT|MULTILINESTRING|MULTIPOLYGON|GEOMETRYCOLLECTION)\s+(NOT\s+NULL\s+)?SRID\s+(\d+)", O),
                Evaluator = m => m.Groups[1].Value + " REF_SYSTEM_ID=" + m.Groups[3].Value + (m.Groups[2].Success ? " NOT NULL" : ""),
                Text = "Changed SRID n on spatial columns to REF_SYSTEM_ID=n." },

            // INSERT … VALUES (…) AS new ON DUPLICATE KEY UPDATE a = new.a  (R16)
            new Rule { Id = "R16", Target = For.Both, Trigger = "DUPLICATE", Verbs = new[] { "INSERT", "REPLACE" },
                Pattern = new Regex(@"\)\s*AS\s+(`[^`]+`|\w+)\s+ON\s+DUPLICATE\s+KEY\s+UPDATE\b(.*)$", OS),
                Evaluator = RowAlias,
                Text = "Changed INSERT … AS new ON DUPLICATE KEY UPDATE a = new.a to … UPDATE a = VALUES(a)." },
        };

        /// <summary>Statements MariaDB can't run and that have no meaning here: skipped, and said so.</summary>
        private static readonly (string Id, Regex Pattern, string Text)[] Skips =
        {
            ("GTID", new Regex(@"^\s*SET\s+@@(?:GLOBAL\.)?GTID_PURGED\b", O),
                "Skipped SET @@GLOBAL.GTID_PURGED: it is MySQL replication bookkeeping and means nothing to this server."),
            ("R22", new Regex(@"^\s*SET\s+(?:DEFAULT\s+)?ROLE\s+ALL\b", O),
                "Skipped SET ROLE ALL / SET DEFAULT ROLE ALL: MariaDB needs the role named (SET DEFAULT ROLE r FOR user). Users (F7) can set it."),
        };

        // ------------------------------------------------------------------ the rewrite

        public sealed class Result
        {
            public string Text;
            /// <summary>Rule ids applied, with their explanation (one entry per rule per statement).</summary>
            public List<(string Id, string Text, string Before, string After)> Applied = new List<(string, string, string, string)>();
            /// <summary>The statement should not be run at all; the reason.</summary>
            public string Skip;
            public string SkipId;
        }

        /// <summary>Rewrites one statement (no trailing delimiter). Fast for statements none of the rules can apply to.</summary>
        public static Result Rewrite(string statement, ImportTarget target)
        {
            var r = new Result { Text = statement ?? "" };
            if (r.Text.Length == 0) return r;

            foreach (var (id, pattern, why) in Skips)
                if (pattern.IsMatch(r.Text)) { r.Skip = why; r.SkipId = id; return r; }

            List<Rule> live = null;
            foreach (var rule in Rules)
            {
                if (rule.Target == For.Modern && target != ImportTarget.Modern || rule.Target == For.Legacy && target != ImportTarget.Legacy) continue;
                if (r.Text.IndexOf(rule.Trigger, StringComparison.OrdinalIgnoreCase) < 0) continue;
                (live ?? (live = new List<Rule>())).Add(rule);
            }
            if (live == null) return r;

            string verb = FirstWord(r.Text);
            // raw rules first (collations, header comments), on the text as it is
            string text = r.Text;
            foreach (var rule in live.Where(x => x.Raw)) text = Apply(rule, text, verb, r, null);

            var masked = live.Any(x => !x.Raw) ? Mask(text) : null;
            if (masked != null)
            {
                string m = masked.Value.Text;
                foreach (var rule in live.Where(x => !x.Raw)) m = Apply(rule, m, verb, r, masked.Value.Strings);
                text = Unmask(m, masked.Value.Strings);
            }
            r.Text = text;
            return r;
        }

        private static string Apply(Rule rule, string text, string verb, Result r, List<string> strings)
        {
            if (rule.Verbs != null && !rule.Verbs.Contains(verb, StringComparer.OrdinalIgnoreCase)) return text;
            if (rule.Id == "R16" && (!Insert.IsMatch(text) || HasSelect.IsMatch(text))) return text;   // INSERT … SELECT … AS t keeps t
            var m = rule.Pattern.Match(text);
            if (!m.Success) return text;
            string before = m.Value;
            string after = rule.Evaluator != null ? rule.Pattern.Replace(text, rule.Evaluator) : rule.Pattern.Replace(text, rule.Replacement);
            if (after == text) return text;
            string afterPiece = rule.Evaluator != null ? rule.Evaluator(m) : m.Result(rule.Replacement);
            r.Applied.Add((rule.Id, rule.Text, Short(strings == null ? before : Unmask(before, strings)), Short(strings == null ? afterPiece : Unmask(afterPiece, strings))));
            return after;
        }

        private static string RowAlias(Match m)
        {
            string alias = m.Groups[1].Value.Trim('`');
            var refs = new Regex(@"(?:`" + Regex.Escape(alias) + @"`|\b" + Regex.Escape(alias) + @")\.(`[^`]+`|\w+)", O);
            return ") ON DUPLICATE KEY UPDATE" + refs.Replace(m.Groups[2].Value, x => "VALUES(" + x.Groups[1].Value + ")");
        }

        private static string CaseInsensitivePattern(string quoted) => "'(?i)" + quoted.Substring(1);

        private static string FirstWord(string s)
        {
            int i = 0;
            while (i < s.Length)
            {
                if (char.IsWhiteSpace(s[i])) { i++; continue; }
                if (s[i] == '/' && i + 1 < s.Length && s[i + 1] == '*' && !(i + 2 < s.Length && (s[i + 2] == '!' || s[i + 2] == 'M')))
                {
                    int close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? s.Length : close + 2;
                    continue;
                }
                break;
            }
            int j = i;
            while (j < s.Length && (char.IsLetter(s[j]) || s[j] == '_')) j++;
            return s.Substring(i, j - i).ToUpperInvariant();
        }

        private static string Short(string s)
        {
            s = Regex.Replace(s ?? "", @"\s+", " ").Trim();
            return s.Length > 90 ? s.Substring(0, 89) + "…" : s;
        }

        // ------------------------------------------------------------------ string literals stay untouched

        private const char MaskOpen = '\u0001', MaskClose = '\u0002';

        /// <summary>
        /// Replaces the inside of every '…' and "…" string with a numbered marker, so rules see the quotes but never the
        /// data. Comments and `names` are left as they are.
        /// </summary>
        internal static (string Text, List<string> Strings)? Mask(string s)
        {
            var sb = new StringBuilder(s.Length);
            var strings = new List<string>();
            int n = s.Length, i = 0;
            while (i < n)
            {
                char c = s[i];
                if (c == '\'' || c == '"')
                {
                    int j = i + 1;
                    while (j < n)
                    {
                        if (s[j] == '\\') { j += 2; continue; }
                        if (s[j] == c) { if (j + 1 < n && s[j + 1] == c) { j += 2; continue; } break; }
                        j++;
                    }
                    if (j >= n) { sb.Append(s, i, n - i); break; }   // unterminated: leave the rest alone
                    sb.Append(c).Append(MaskOpen).Append(strings.Count.ToString(CultureInfo.InvariantCulture)).Append(MaskClose).Append(c);
                    strings.Add(s.Substring(i + 1, j - i - 1));
                    i = j + 1;
                    continue;
                }
                if (c == '`')
                {
                    int j = s.IndexOf('`', i + 1);
                    while (j >= 0 && j + 1 < n && s[j + 1] == '`') j = s.IndexOf('`', j + 2);
                    if (j < 0) { sb.Append(s, i, n - i); break; }
                    sb.Append(s, i, j - i + 1);
                    i = j + 1;
                    continue;
                }
                if (c == '#' || c == '-' && i + 2 < n && s[i + 1] == '-' && char.IsWhiteSpace(s[i + 2]))
                {
                    int j = s.IndexOf('\n', i);
                    if (j < 0) j = n;
                    sb.Append(s, i, j - i);
                    i = j;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            return (sb.ToString(), strings);
        }

        private static readonly Regex Marker = new Regex("\u0001(\\d+)\u0002", RegexOptions.Compiled);

        internal static string Unmask(string s, List<string> strings) =>
            s.IndexOf(MaskOpen) < 0 ? s : Marker.Replace(s, m => strings[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)]);

        // ------------------------------------------------------------------ source and unsupported features

        /// <summary>Reads the first lines of a script to say where it came from.</summary>
        public static SqlSource Detect(string head)
        {
            head = head ?? "";
            if (head.IndexOf("phpMyAdmin SQL Dump", StringComparison.OrdinalIgnoreCase) >= 0) return SqlSource.PhpMyAdmin;
            if (head.IndexOf("MySQL Workbench", StringComparison.OrdinalIgnoreCase) >= 0) return SqlSource.Workbench;
            if (head.IndexOf("-- MariaDB dump", StringComparison.OrdinalIgnoreCase) >= 0 || head.IndexOf("/*M!999999", StringComparison.Ordinal) >= 0) return SqlSource.MariaDbDump;
            if (head.IndexOf("-- MySQL dump", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var v = Regex.Match(head, @"Server version\s+(\d+)\.", RegexOptions.IgnoreCase);
                if (v.Success && int.Parse(v.Groups[1].Value, CultureInfo.InvariantCulture) >= 8) return SqlSource.MySql8Dump;
                if (Regex.IsMatch(head, @"-MariaDB", RegexOptions.IgnoreCase)) return SqlSource.MariaDbDump;
                return SqlSource.MySqlDump;
            }
            return SqlSource.Unknown;
        }

        public static string Describe(SqlSource s)
        {
            switch (s)
            {
                case SqlSource.PhpMyAdmin: return "a phpMyAdmin export";
                case SqlSource.Workbench: return "a MySQL Workbench script";
                case SqlSource.MariaDbDump: return "a MariaDB dump";
                case SqlSource.MySql8Dump: return "a MySQL 8 dump";
                case SqlSource.MySqlDump: return "a MySQL dump";
                default: return "an SQL script";
            }
        }

        private static readonly (Regex Pattern, string Text)[] Unsupported =
        {
            (new Regex(@"\bLATERAL\s*\(", O), "LATERAL is MySQL 8 only. Move the subquery into the SELECT list, or JOIN a grouped subquery: LEFT JOIN (SELECT k, … GROUP BY k) x ON x.k = a.k."),
            (new Regex(@"\bJSON_TABLE\s*\(", O), "JSON_TABLE isn't in this MariaDB. Read the array with JSON_EXTRACT(j, CONCAT('$.a[', n, ']')) over a list of numbers n."),
            (new Regex(@"\bGROUPING\s*\(", O), "GROUPING() is MySQL 8 only. With WITH ROLLUP, the total rows have NULL in that column: use (col IS NULL)."),
            (new Regex(@"\bWITH\s+ROLLUP\b[\s\S]*\bORDER\s+BY\b", O), "MariaDB can't ORDER BY next to WITH ROLLUP. Wrap it: SELECT * FROM (… WITH ROLLUP) t ORDER BY …"),
            (new Regex(@"\bMEMBER\s+OF\s*\(", O), "MEMBER OF is MySQL 8 only. Use JSON_CONTAINS(json_column, 'value')."),
            (new Regex(@"\bJSON_(?:ARRAYAGG|OBJECTAGG)\s*\(", O), "JSON_ARRAYAGG/JSON_OBJECTAGG aren't in MariaDB 10.4. Use CONCAT('[', GROUP_CONCAT(JSON_QUOTE(x)), ']')."),
            (new Regex(@"\b(?:INDEX|KEY)\s+(?:`[^`]+`|\w+)?\s*\(\s*\(", O), "An index on an expression is MySQL 8 only. Add a generated column (x VARCHAR(100) AS (LOWER(email)) VIRTUAL) and index that."),
            (new Regex(@"\bDEFAULT\s+ROLE\b", O), "MariaDB sets one default role per user: SET DEFAULT ROLE r FOR 'user'@'host'."),
            (new Regex(@"\bCAST\s*\([\s\S]*\bAS\s+JSON\b", O), "CAST(… AS JSON) with an expression inside: write JSON_COMPACT(…) instead."),
        };

        /// <summary>When a statement failed: the MySQL 8 feature in it that MariaDB lacks, and what to write instead (null if none found).</summary>
        public static string ExplainUnsupported(string statement)
        {
            if (string.IsNullOrEmpty(statement)) return null;
            var masked = Mask(statement)?.Text ?? statement;
            foreach (var (p, t) in Unsupported) if (p.IsMatch(masked)) return t;
            return null;
        }
    }
}
