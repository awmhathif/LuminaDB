using System;
using System.IO;

namespace LuminaDB.Core
{
    /// <summary>
    /// Every folder LuminaDB uses. Portable and installed copies have the same layout
    /// (SPEC §3); everything lives under one root so the whole thing can sit on a USB stick.
    /// </summary>
    public sealed class AppPaths
    {
        public AppPaths(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Root folder is required.", nameof(root));
            Root = Path.GetFullPath(root);
        }

        /// <summary>The folder that contains LuminaDB.exe.</summary>
        public static AppPaths FromExecutable() => new AppPaths(AppDomain.CurrentDomain.BaseDirectory);

        public string Root { get; }
        public string Engines => Path.Combine(Root, "engines");
        public string Data => Path.Combine(Root, "data");
        public string Config => Path.Combine(Root, "config");
        public string Logs => Path.Combine(Root, "logs");
        public string Run => Path.Combine(Root, "run");
        public string Backups => Path.Combine(Root, "backups");
        public string Snapshots => Path.Combine(Root, "snapshots");
        public string Projects => Path.Combine(Root, "projects");
        public string Exports => Path.Combine(Root, "exports");
        public string Mail => Path.Combine(Root, "mail");
        public string Temp => Path.Combine(Root, "tmp");

        /// <summary>One MariaDB data folder per engine set (SPEC §3: a server never opens another set's folder).</summary>
        public string MariaDbData(EngineSetKind kind) => Path.Combine(Data, "mariadb-" + EngineSet.FolderName(kind));

        public string MariaDbConfigFile(EngineSetKind kind) => Path.Combine(Config, "my-" + EngineSet.FolderName(kind) + ".ini");
        public string MariaDbLog(EngineSetKind kind) => Path.Combine(Logs, "mariadb-" + EngineSet.FolderName(kind) + ".log");
        public string MariaDbPidFile(EngineSetKind kind) => Path.Combine(Run, "mariadb-" + EngineSet.FolderName(kind) + ".json");
        public string StateFile => Path.Combine(Config, "state.json");
        /// <summary>config\projects: one .json per project (the project folder itself stays clean).</summary>
        public string ProjectMetaDir => Path.Combine(Config, "projects");
        /// <summary>logs\projects: PHP error and access logs, one pair per project.</summary>
        public string ProjectLogs => Path.Combine(Logs, "projects");
        public string WebSettingsFile => Path.Combine(Config, "web.json");

        /// <summary>Creates every writable folder. Throws a friendly exception if the location is read-only.</summary>
        public void EnsureCreated()
        {
            foreach (var d in new[] { Data, Config, Logs, Run, Backups, Snapshots, Projects, Exports, Mail, Temp })
            {
                try { Directory.CreateDirectory(d); }
                catch (UnauthorizedAccessException ex)
                {
                    throw new LuminaException(ErrorKind.FolderNotWritable,
                        $"LuminaDB can't write to \"{Root}\". Move the LuminaDB folder somewhere you own, like Documents or D:\\.", ex);
                }
                catch (IOException ex)
                {
                    throw new LuminaException(ErrorKind.FolderNotWritable,
                        $"LuminaDB can't create \"{d}\": {ex.Message}", ex);
                }
            }
        }
    }
}
