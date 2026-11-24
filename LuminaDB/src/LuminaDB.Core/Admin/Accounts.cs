using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LuminaDB.Core.Sql;
using MySqlConnector;

namespace LuminaDB.Core.Admin
{
    /// <summary>One login account or role (SPEC §5.7).</summary>
    public sealed class Account
    {
        public string User { get; set; }
        /// <summary>"localhost", "%", "127.0.0.1"… Empty for a role.</summary>
        public string Host { get; set; }
        public string Plugin { get; set; }
        public bool HasPassword { get; set; }
        public bool IsRole { get; set; }
        public bool IsLocked { get; set; }
        /// <summary>Roles this account has been given.</summary>
        public List<string> Roles { get; set; } = new List<string>();

        /// <summary>'user'@'host', or 'role' for a role.</summary>
        public string Sql => IsRole ? Accounts.Literal(User) : Accounts.Literal(User) + "@" + Accounts.Literal(Host);
        public string Display => IsRole ? User : User + "@" + Host;

        /// <summary>
        /// root (LuminaDB looks after it on every host), MariaDB's own mariadb.sys and the PUBLIC role: shown, never
        /// edited or dropped from here.
        /// </summary>
        public bool IsSystem => string.Equals(User, "root", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(User, "mariadb.sys", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(User, "mysql.sys", StringComparison.OrdinalIgnoreCase)
                                || (IsRole && string.Equals(User, "PUBLIC", StringComparison.OrdinalIgnoreCase))
                                || string.IsNullOrEmpty(User);

        /// <summary>Plain words for the login method.</summary>
        public string PluginNote
        {
            get
            {
                if (IsRole) return "role: give it to users, it holds privileges";
                switch ((Plugin ?? "").ToLowerInvariant())
                {
                    case "":
                    case "mysql_native_password": return HasPassword ? "password" : "no password";
                    case "ed25519": return "password (ed25519)";
                    case "unix_socket": return "Windows can't use unix_socket logins";
                    case "invalid": return "can't log in (locked by MariaDB)";
                    default: return Plugin;
                }
            }
        }
    }

    public enum PrivilegeLevel { Global, Database, Table, Column }

    /// <summary>One privilege at one place: SELECT on shop.*, or UPDATE (price) on shop.products.</summary>
    public sealed class Grant : IEquatable<Grant>
    {
        public Grant(PrivilegeLevel level, string privilege, string database = null, string table = null, string column = null)
        {
            Level = level;
            Privilege = (privilege ?? "").Trim().ToUpperInvariant();
            Database = level == PrivilegeLevel.Global ? null : database;
            Table = level == PrivilegeLevel.Table || level == PrivilegeLevel.Column ? table : null;
            Column = level == PrivilegeLevel.Column ? column : null;
        }

        public PrivilegeLevel Level { get; }
        public string Privilege { get; }
        public string Database { get; }
        public string Table { get; }
        public string Column { get; }

        /// <summary>`shop`.*, `shop`.`products` or *.*</summary>
        public string On => Level == PrivilegeLevel.Global ? "*.*"
            : Level == PrivilegeLevel.Database ? SqlLexer.Quote(Database) + ".*"
            : SqlLexer.Quote(Database) + "." + SqlLexer.Quote(Table);

        public bool Equals(Grant o) => o != null && Level == o.Level && Privilege == o.Privilege
                                        && string.Equals(Database, o.Database, StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(Table, o.Table, StringComparison.OrdinalIgnoreCase)
                                        && string.Equals(Column, o.Column, StringComparison.OrdinalIgnoreCase);
        public override bool Equals(object obj) => Equals(obj as Grant);
        public override int GetHashCode() =>
            ((int)Level * 397) ^ Privilege.GetHashCode() ^ (Database ?? "").ToLowerInvariant().GetHashCode()
            ^ ((Table ?? "").ToLowerInvariant().GetHashCode() * 7) ^ ((Column ?? "").ToLowerInvariant().GetHashCode() * 13);
        public override string ToString() => Privilege + (Column != null ? " (" + Column + ")" : "") + " ON " + On;
    }

    /// <summary>The privileges a page of checkboxes offers, with plain words for each (SPEC §5.7).</summary>
    public static class Privileges
    {
        public sealed class Info
        {
            public Info(string name, string words) { Name = name; Words = words; }
            public string Name { get; }
            public string Words { get; }
        }

        /// <summary>Per database, in the order a student meets them.</summary>
        public static readonly IReadOnlyList<Info> Database = new[]
        {
            new Info("SELECT", "read rows"),
            new Info("INSERT", "add rows"),
            new Info("UPDATE", "change rows"),
            new Info("DELETE", "delete rows"),
            new Info("CREATE", "create tables"),
            new Info("ALTER", "change tables"),
            new Info("DROP", "drop tables"),
            new Info("INDEX", "add indexes"),
            new Info("REFERENCES", "foreign keys"),
            new Info("CREATE VIEW", "create views"),
            new Info("SHOW VIEW", "see view code"),
            new Info("TRIGGER", "triggers"),
            new Info("CREATE ROUTINE", "create procedures"),
            new Info("ALTER ROUTINE", "change procedures"),
            new Info("EXECUTE", "run procedures"),
            new Info("EVENT", "scheduled events"),
            new Info("CREATE TEMPORARY TABLES", "temporary tables"),
            new Info("LOCK TABLES", "lock tables"),
        };

        /// <summary>Per table.</summary>
        public static readonly IReadOnlyList<Info> Table = new[]
        {
            new Info("SELECT", "read"), new Info("INSERT", "add"), new Info("UPDATE", "change"), new Info("DELETE", "delete"),
            new Info("CREATE", "create"), new Info("ALTER", "alter"), new Info("DROP", "drop"), new Info("INDEX", "index"),
            new Info("REFERENCES", "foreign keys"), new Info("TRIGGER", "triggers"), new Info("SHOW VIEW", "view code"),
        };

        /// <summary>Per column: the only four MariaDB allows there.</summary>
        public static readonly IReadOnlyList<Info> Column = new[]
        {
            new Info("SELECT", "read"), new Info("INSERT", "add"), new Info("UPDATE", "change"), new Info("REFERENCES", "foreign keys"),
        };

        /// <summary>Ready-made sets for one database.</summary>
        public static readonly IReadOnlyList<string> ReadOnly = new[] { "SELECT", "SHOW VIEW" };
        public static readonly IReadOnlyList<string> ReadWrite = new[] { "SELECT", "INSERT", "UPDATE", "DELETE", "EXECUTE", "SHOW VIEW", "CREATE TEMPORARY TABLES", "LOCK TABLES" };
        public static IReadOnlyList<string> Full => Database.Select(i => i.Name).ToList();

        /// <summary>Privileges that only exist on *.* (shown, kept, not offered as checkboxes).</summary>
        public static bool IsGlobalOnly(string p) => !Database.Any(i => i.Name == p);
    }

    /// <summary>
    /// Reads and changes accounts (SPEC §5.7). Privileges come from information_schema (structured, the same on 10.4
    /// and 11.8), never by parsing SHOW GRANTS. Every change is a list of plain SQL statements the user sees first.
    /// </summary>
    public static class Accounts
    {
        public const int MaxUserLength = 80;

        public static string Literal(string s) => "'" + (s ?? "").Replace("\\", "\\\\").Replace("'", "''") + "'";

        // ------------------------------------------------------------------ read

        public static async Task<List<Account>> ListAsync(MySqlConnection c, CancellationToken ct = default)
        {
            var list = new List<Account>();
            // mysql.global_priv (10.4 and later) holds every account as JSON; mysql.user is only a view of it and has no
            // account_locked column. Roles are stored there too, with is_role = true.
            const string sql = "SELECT User, Host, JSON_VALUE(Priv, '$.plugin'), JSON_VALUE(Priv, '$.authentication_string'), " +
                               "JSON_VALUE(Priv, '$.is_role'), JSON_VALUE(Priv, '$.account_locked') FROM mysql.global_priv ORDER BY User, Host";
            using (var cmd = new MySqlCommand(sql, c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    string Str(int i) => r.IsDBNull(i) ? "" : Convert.ToString(r.GetValue(i));
                    list.Add(new Account
                    {
                        User = Str(0),
                        Host = Str(1),
                        Plugin = Str(2),
                        HasPassword = Str(3).Length > 0,
                        IsRole = Yes(Str(4)),
                        IsLocked = Yes(Str(5)),
                    });
                }
            }
            list.Sort((x, y) => x.IsRole != y.IsRole ? (x.IsRole ? 1 : -1)
                                : string.Compare(x.User, y.User, StringComparison.OrdinalIgnoreCase) != 0 ? string.Compare(x.User, y.User, StringComparison.OrdinalIgnoreCase)
                                : string.Compare(x.Host, y.Host, StringComparison.OrdinalIgnoreCase));
            // Role membership: who has which role.
            using (var cmd = new MySqlCommand("SELECT User, Host, Role FROM mysql.roles_mapping", c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    var a = list.FirstOrDefault(x => !x.IsRole && x.User == r.GetString(0) && string.Equals(x.Host, r.GetString(1), StringComparison.OrdinalIgnoreCase));
                    a?.Roles.Add(r.GetString(2));
                }
            }
            return list;
        }

        private static bool Yes(string v) => v == "true" || v == "1" || string.Equals(v, "Y", StringComparison.OrdinalIgnoreCase);

        /// <summary>information_schema writes grantees as 'user'@'host' (roles as 'role'@'' or just 'role').</summary>
        private static bool IsGrantee(string grantee, Account a)
        {
            if (grantee == null) return false;
            if (string.Equals(grantee, a.Sql, StringComparison.OrdinalIgnoreCase)) return true;
            return a.IsRole && (string.Equals(grantee, Literal(a.User) + "@''", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(grantee, a.User, StringComparison.OrdinalIgnoreCase));
        }

        public static async Task<List<Grant>> ReadGrantsAsync(MySqlConnection c, Account a, CancellationToken ct = default)
        {
            var grants = new List<Grant>();
            async Task Read(string sql, Func<MySqlDataReader, Grant> make)
            {
                using (var cmd = new MySqlCommand(sql, c))
                using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    while (await r.ReadAsync(ct).ConfigureAwait(false))
                        if (IsGrantee(r.GetString(0), a))
                        {
                            var g = make(r);
                            if (g.Privilege != "USAGE") grants.Add(g);
                        }
            }
            await Read("SELECT GRANTEE, PRIVILEGE_TYPE FROM information_schema.USER_PRIVILEGES",
                r => new Grant(PrivilegeLevel.Global, r.GetString(1))).ConfigureAwait(false);
            await Read("SELECT GRANTEE, PRIVILEGE_TYPE, TABLE_SCHEMA FROM information_schema.SCHEMA_PRIVILEGES",
                r => new Grant(PrivilegeLevel.Database, r.GetString(1), r.GetString(2))).ConfigureAwait(false);
            await Read("SELECT GRANTEE, PRIVILEGE_TYPE, TABLE_SCHEMA, TABLE_NAME FROM information_schema.TABLE_PRIVILEGES",
                r => new Grant(PrivilegeLevel.Table, r.GetString(1), r.GetString(2), r.GetString(3))).ConfigureAwait(false);
            await Read("SELECT GRANTEE, PRIVILEGE_TYPE, TABLE_SCHEMA, TABLE_NAME, COLUMN_NAME FROM information_schema.COLUMN_PRIVILEGES",
                r => new Grant(PrivilegeLevel.Column, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))).ConfigureAwait(false);
            return grants.Distinct().ToList();
        }

        /// <summary>What SHOW GRANTS says (for "show the generated GRANT" and for copying).</summary>
        public static async Task<List<string>> ShowGrantsAsync(MySqlConnection c, Account a, CancellationToken ct = default)
        {
            var list = new List<string>();
            using (var cmd = new MySqlCommand("SHOW GRANTS FOR " + a.Sql, c))
            using (var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
                while (await r.ReadAsync(ct).ConfigureAwait(false)) list.Add(r.GetString(0));
            return list;
        }

        // ------------------------------------------------------------------ check what the user typed

        /// <summary>Null when fine, else a sentence for the user.</summary>
        public static string CheckName(string user, IEnumerable<Account> existing, string host, bool role = false)
        {
            if (string.IsNullOrWhiteSpace(user)) return role ? "Give the role a name." : "Give the user a name.";
            if (user != user.Trim()) return "The name starts or ends with a space; take it out so logins aren't confusing.";
            if (user.Length > MaxUserLength) return "That name is longer than MariaDB allows (" + MaxUserLength + " characters).";
            if (user.Any(char.IsControl)) return "The name has an invisible character in it.";
            if (string.Equals(user, "root", StringComparison.OrdinalIgnoreCase) || string.Equals(user, "PUBLIC", StringComparison.OrdinalIgnoreCase)
                || user.StartsWith("mariadb.", StringComparison.OrdinalIgnoreCase) || user.StartsWith("mysql.", StringComparison.OrdinalIgnoreCase))
                return "\"" + user + "\" belongs to MariaDB itself; choose another name.";
            if (!role && string.IsNullOrWhiteSpace(host)) return "Choose where the user may log in from.";
            if (existing != null && existing.Any(a => string.Equals(a.User, user, StringComparison.OrdinalIgnoreCase)
                                                    && (role ? a.IsRole : !a.IsRole && string.Equals(a.Host, host, StringComparison.OrdinalIgnoreCase))))
                return role ? "There's already a role called " + user + "." : "There's already a user " + user + "@" + host + ".";
            if (existing != null && existing.Any(a => string.Equals(a.User, user, StringComparison.OrdinalIgnoreCase) && a.IsRole != role))
                return "\"" + user + "\" is already used by a " + (role ? "user" : "role") + "; MariaDB needs different names.";
            return null;
        }

        public static string CheckPassword(string password, bool allowEmpty)
        {
            if (string.IsNullOrEmpty(password)) return allowEmpty ? null : "Type a password (PHP code will use it to connect).";
            if (password.Trim().Length == 0) return "A password made only of spaces is easy to get wrong. Use letters or numbers.";
            if (password.Any(char.IsControl)) return "The password has an invisible character in it.";
            return null;
        }

        // ------------------------------------------------------------------ build SQL

        /// <summary>Statements; a password is passed as @pwd so it never shows in the script or the log.</summary>
        public static List<string> CreateUser(string user, string host, bool withPassword) => new List<string>
        {
            "CREATE USER " + Literal(user) + "@" + Literal(host) + (withPassword ? " IDENTIFIED BY @pwd" : ""),
        };

        public static List<string> CreateRole(string role) => new List<string> { "CREATE ROLE " + Literal(role) };

        public static string SetPassword(Account a, bool clear) =>
            "ALTER USER " + a.Sql + " IDENTIFIED BY " + (clear ? "''" : "@pwd");

        public static string Drop(Account a) => (a.IsRole ? "DROP ROLE " : "DROP USER ") + a.Sql;

        public static string Lock(Account a, bool locked) => "ALTER USER " + a.Sql + " ACCOUNT " + (locked ? "LOCK" : "UNLOCK");

        public static string GrantRole(string role, Account to) => "GRANT " + Literal(role) + " TO " + to.Sql;
        public static string RevokeRole(string role, Account from) => "REVOKE " + Literal(role) + " FROM " + from.Sql;
        /// <summary>A role is only active after SET ROLE, unless it is the default: make it so, which is what people expect.</summary>
        public static string DefaultRole(string role, Account a) => "SET DEFAULT ROLE " + (role == null ? "NONE" : Literal(role)) + " FOR " + a.Sql;

        /// <summary>
        /// The GRANT and REVOKE statements that turn <paramref name="before"/> into <paramref name="after"/>, one per place,
        /// privileges listed in a stable order. Privileges on *.* that the page doesn't offer are left alone.
        /// </summary>
        public static List<string> Diff(Account a, IEnumerable<Grant> before, IEnumerable<Grant> after)
        {
            var b = new HashSet<Grant>(before ?? Enumerable.Empty<Grant>());
            var n = new HashSet<Grant>(after ?? Enumerable.Empty<Grant>());
            var added = n.Where(g => !b.Contains(g) && g.Privilege != "GRANT OPTION").ToList();
            var removed = b.Where(g => !n.Contains(g) && g.Privilege != "GRANT OPTION").ToList();
            var sql = new List<string>();
            foreach (var group in Group(removed)) sql.Add("REVOKE " + group.list + " ON " + group.on + " FROM " + a.Sql);
            foreach (var group in Group(added)) sql.Add("GRANT " + group.list + " ON " + group.on + " TO " + a.Sql);
            return sql;
        }

        private static IEnumerable<(string on, string list)> Group(IEnumerable<Grant> grants)
        {
            int Order(string p)
            {
                for (int i = 0; i < Privileges.Database.Count; i++) if (Privileges.Database[i].Name == p) return i;
                return 100;
            }
            foreach (var place in grants.GroupBy(g => (g.Level, Db: (g.Database ?? "").ToLowerInvariant(), T: (g.Table ?? "").ToLowerInvariant()))
                                       .OrderBy(x => x.Key.Level).ThenBy(x => x.Key.Db, StringComparer.Ordinal).ThenBy(x => x.Key.T, StringComparer.Ordinal))
            {
                var first = place.First();
                var parts = new List<string>();
                foreach (var priv in place.GroupBy(g => g.Privilege).OrderBy(x => Order(x.Key)).ThenBy(x => x.Key, StringComparer.Ordinal))
                {
                    var cols = priv.Where(g => g.Level == PrivilegeLevel.Column).Select(g => g.Column).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                    parts.Add(cols.Count == 0 ? priv.Key : priv.Key + " (" + string.Join(", ", cols.Select(SqlLexer.Quote)) + ")");
                }
                yield return (first.On, string.Join(", ", parts));
            }
        }

        /// <summary>The script as the user sees it: @pwd shown as a row of dots.</summary>
        public static string Script(IEnumerable<string> statements)
        {
            var sb = new StringBuilder();
            foreach (var s in statements) sb.Append(s.Replace("@pwd", "'••••••'")).Append(";\n");
            return sb.ToString();
        }

        /// <summary>Runs the statements in order. FLUSH PRIVILEGES is not needed after GRANT/CREATE USER; it's done anyway for old habits' sake.</summary>
        public static async Task RunAsync(MySqlConnection c, IEnumerable<string> statements, string password = null, CancellationToken ct = default)
        {
            foreach (var s in statements)
            {
                using (var cmd = new MySqlCommand(s, c))
                {
                    if (s.Contains("@pwd")) cmd.Parameters.AddWithValue("@pwd", password ?? "");
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }
        }
    }
}
