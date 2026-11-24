using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;

namespace LuminaDB.Core.Admin
{
    /// <summary>One permanent setting: written under [mysqld] in my.ini at every start.</summary>
    [DataContract]
    public sealed class ServerOverride
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public string Value { get; set; }
    }

    /// <summary>
    /// The server settings the user chose to keep (SPEC §5.8 "save for next start"), one file per engine set:
    /// config\my-&lt;set&gt;-overrides.json. my.ini itself stays generated, so a bad choice is easy to undo: if the
    /// server then won't start, LuminaDB sets this file aside and starts without it.
    /// </summary>
    [DataContract]
    public sealed class ServerOverrides
    {
        [DataMember] public List<ServerOverride> Items { get; set; } = new List<ServerOverride>();

        /// <summary>Settings LuminaDB itself must control: changing them would break logins, paths or safety.</summary>
        public static readonly IReadOnlyCollection<string> Forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "port", "bind_address", "datadir", "plugin_dir", "plugin_maturity", "tmpdir", "secure_file_priv", "skip_grant_tables",
            "skip_networking", "socket", "basedir", "log_error", "pid_file", "init_file", "init_connect", "general_log_file",
            "slow_query_log_file", "lower_case_table_names", "innodb_data_home_dir", "innodb_log_group_home_dir",
            "innodb_undo_directory", "innodb_data_file_path", "character_sets_dir", "lc_messages_dir", "named_pipe",
            "shared_memory", "skip_name_resolve", "local_infile", "user", "language", "log_bin", "server_id",
        };

        public static string FileFor(AppPaths p, EngineSetKind kind) => Path.Combine(p.Config, "my-" + EngineSet.FolderName(kind) + "-overrides.json");

        public static ServerOverrides Load(AppPaths p, EngineSetKind kind)
        {
            var o = JsonFile.Read<ServerOverrides>(FileFor(p, kind)) ?? new ServerOverrides();
            if (o.Items == null) o.Items = new List<ServerOverride>();
            // A hand-edited file can't smuggle in a forbidden or malformed line.
            o.Items = o.Items.Where(i => i != null && IsSafeName(i.Name) && !Forbidden.Contains(Key(i.Name)) && IsSafeValue(i.Value)).ToList();
            return o;
        }

        public void Save(AppPaths p, EngineSetKind kind)
        {
            if (Items.Count == 0) JsonFile.Delete(FileFor(p, kind));
            else JsonFile.Write(FileFor(p, kind), this);
        }

        public string Get(string name) => Items.FirstOrDefault(i => Key(i.Name) == Key(name))?.Value;

        public void Set(string name, string value)
        {
            Remove(name);
            Items.Add(new ServerOverride { Name = Key(name), Value = value });
            Items.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        }

        public bool Remove(string name) => Items.RemoveAll(i => Key(i.Name) == Key(name)) > 0;

        /// <summary>Null when the variable may be kept for the next start, else why not.</summary>
        public static string CheckKeep(ServerVariable v)
        {
            if (v == null) return "Unknown setting.";
            if (Forbidden.Contains(Key(v.Name))) return v.Name + " is looked after by LuminaDB" + (v.Name == "port" || v.Name == "datadir" ? "; change it in Settings." : ".");
            if (!v.InConfig) return v.Name + " can't be set in the server's start-up file.";
            return null;
        }

        /// <summary>Puts the file aside (after a failed start), keeping it for the user to look at. Returns where it went.</summary>
        public static string SetAside(AppPaths p, EngineSetKind kind)
        {
            var f = FileFor(p, kind);
            if (!File.Exists(f)) return null;
            var aside = Path.Combine(p.Config, "my-" + EngineSet.FolderName(kind) + "-overrides.failed-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json");
            File.Move(f, aside);
            return aside;
        }

        /// <summary>The lines for my.ini, already checked. Names use '_' like SHOW VARIABLES.</summary>
        public IEnumerable<KeyValuePair<string, string>> IniLines() =>
            Items.Select(i => new KeyValuePair<string, string>(Key(i.Name), IniValue(i.Value)));

        internal static string Key(string name) => (name ?? "").Trim().Replace('-', '_').ToLowerInvariant();

        private static bool IsSafeName(string name) =>
            !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && name.All(ch => char.IsLetterOrDigit(ch) && ch < 128 || ch == '_' || ch == '-');

        private static bool IsSafeValue(string v) => v != null && v.Length <= 1024 && !v.Any(ch => ch == '\r' || ch == '\n' || ch == '"' || char.IsControl(ch));

        /// <summary>Values with spaces or '#' are quoted (a '#' would otherwise start a comment).</summary>
        private static string IniValue(string v) =>
            v.Length == 0 || v.IndexOfAny(new[] { ' ', '#', ';', '\'', '\\' }) >= 0 ? "\"" + v.Replace("\\", "/") + "\"" : v;
    }
}
