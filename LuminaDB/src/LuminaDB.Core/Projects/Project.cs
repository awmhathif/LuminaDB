using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;

namespace LuminaDB.Core.Projects
{
    /// <summary>What LuminaDB remembers about a project. Kept in config\projects\&lt;name&gt;.json so the project folder stays clean.</summary>
    [DataContract]
    public sealed class ProjectMeta
    {
        [DataMember] public string Name { get; set; }
        [DataMember] public string Template { get; set; }
        [DataMember] public string Database { get; set; }
        /// <summary>Sub-folder served as the web root ("" or "public" for Laravel).</summary>
        [DataMember] public string WebRoot { get; set; } = "";
        [DataMember] public DateTime Created { get; set; }
        [DataMember] public ProjectPhpSettings Php { get; set; }
    }

    /// <summary>Per-project PHP settings (SPEC §5.2), written to the project's .user.ini.</summary>
    [DataContract]
    public sealed class ProjectPhpSettings
    {
        public static readonly string[] UploadSizes = { "2M", "8M", "32M", "64M", "128M", "256M" };
        public static readonly string[] MemoryLimits = { "128M", "256M", "512M", "1G" };

        [DataMember] public string UploadMax { get; set; } = "64M";
        [DataMember] public string MemoryLimit { get; set; } = "256M";
        /// <summary>Empty = the PC's time zone (from php.ini).</summary>
        [DataMember] public string TimeZone { get; set; } = "";
        [DataMember] public int MaxExecutionSeconds { get; set; } = 120;
        /// <summary>Show deprecation notices in the error strip.</summary>
        [DataMember] public bool ShowDeprecations { get; set; }

        public ProjectPhpSettings Clone() => (ProjectPhpSettings)MemberwiseClone();

        /// <summary>A plain reason the settings can't be used, or null.</summary>
        public string Problem()
        {
            if (!IsSize(UploadMax)) return "Upload size must look like 64M or 1G.";
            if (!IsSize(MemoryLimit)) return "Memory limit must look like 256M or 1G.";
            if (MaxExecutionSeconds < 5 || MaxExecutionSeconds > 3600) return "Time limit must be between 5 and 3600 seconds.";
            if (!string.IsNullOrEmpty(TimeZone) && !Regex.IsMatch(TimeZone, @"^[A-Za-z]+(/[A-Za-z0-9_+\-]+){0,2}$")) return "Time zone must look like Asia/Colombo.";
            return null;
        }

        private static bool IsSize(string s) => s != null && Regex.IsMatch(s, @"^[1-9][0-9]{0,4}[KMG]$", RegexOptions.IgnoreCase);
    }

    /// <summary>A project as shown in the app: its folder plus what LuminaDB knows about it.</summary>
    public sealed class Project
    {
        public Project(string name, string folder, ProjectMeta meta)
        {
            Name = name;
            Folder = folder;
            Meta = meta ?? new ProjectMeta { Name = name, Template = "existing", Database = ProjectNames.DatabaseFor(name), Created = Directory.GetCreationTime(folder) };
        }

        public string Name { get; }
        public string Folder { get; }
        public ProjectMeta Meta { get; }
        public string Database => Meta.Database;
        public string Template => Meta.Template;

        /// <summary>The folder the web server serves (the project folder, or its public\ for Laravel).</summary>
        public string DocumentRoot
        {
            get
            {
                if (!string.IsNullOrEmpty(Meta.WebRoot)) return Path.Combine(Folder, Meta.WebRoot);
                // A Laravel-style project copied in by hand: serve public\ when it holds the front controller.
                var pub = Path.Combine(Folder, "public");
                if (File.Exists(Path.Combine(pub, "index.php")) && File.Exists(Path.Combine(Folder, "artisan"))) return pub;
                return Folder;
            }
        }

        /// <summary>Projects with an .htaccess in their web root are served by Apache (SPEC §5.2), the rest by Caddy.</summary>
        public bool NeedsApache => File.Exists(Path.Combine(DocumentRoot, ".htaccess"));
    }

    public static class ProjectNames
    {
        /// <summary>Names that would clash with LuminaDB's own addresses.</summary>
        private static readonly HashSet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "localhost", "phpmyadmin", "www", "lumina", "luminadb", "demo", "mail", "admin", "api-lumina", "con", "prn", "aux", "nul",
            "com1", "com2", "com3", "com4", "lpt1", "lpt2", "lpt3",
        };

        /// <summary>
        /// A plain reason the name can't be used, or null. Names become a folder, a database and the first part of
        /// http://&lt;name&gt;.localhost, so they follow the strictest of the three: lowercase letters, digits and hyphens.
        /// </summary>
        public static string Problem(string name, IEnumerable<string> existing = null)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Give the project a name.";
            if (name.Length > 40) return "Keep the name to 40 characters or fewer.";
            if (name != name.Trim()) return "Remove the spaces at the start or end.";
            if (name.Any(char.IsUpper)) return "Use lowercase letters, like " + Suggest(name) + ". Web addresses ignore capitals, so mixed case causes mix-ups.";
            if (name.Contains(' ') || name.Contains('_')) return "Use hyphens instead of spaces or underscores, like " + Suggest(name) + ". The name becomes part of a web address.";
            if (!Regex.IsMatch(name, "^[a-z0-9-]+$")) return "Use only English letters, digits and hyphens, like " + Suggest(name) + ".";
            if (name.StartsWith("-") || name.EndsWith("-")) return "The name can't start or end with a hyphen.";
            if (Reserved.Contains(name)) return "\"" + name + "\" is used by LuminaDB itself. Pick another name.";
            if (existing != null && existing.Contains(name, StringComparer.OrdinalIgnoreCase)) return "There's already a project called " + name + ".";
            return null;
        }

        /// <summary>A usable name built from what the person typed ("My Shop" → "my-shop").</summary>
        public static string Suggest(string name)
        {
            var s = Regex.Replace((name ?? "").Trim().ToLowerInvariant(), "[ _.]+", "-");
            s = Regex.Replace(s, "[^a-z0-9-]", "");
            s = Regex.Replace(s, "-{2,}", "-").Trim('-');
            if (s.Length > 40) s = s.Substring(0, 40).TrimEnd('-');
            return s.Length == 0 ? "my-project" : s;
        }

        /// <summary>The database that goes with a project: hyphens aren't allowed unquoted in SQL, so they become underscores.</summary>
        public static string DatabaseFor(string projectName) => (projectName ?? "").Replace('-', '_');
    }
}
