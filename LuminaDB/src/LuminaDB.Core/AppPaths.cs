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
            _data = ReadDataFolder() ?? Path.Combine(Root, "data");
        }

        private string _data;

        /// <summary>config\paths.json: where the data folder lives when the user moved it (Settings › Data folder).</summary>
        public string PathsFile => Path.Combine(Config, "paths.json");

        /// <summary>True when the data folder is not the default one under the LuminaDB folder.</summary>
        public bool DataMoved => !string.Equals(_data.TrimEnd('\\'), Path.Combine(Root, "data"), StringComparison.OrdinalIgnoreCase);

        private string ReadDataFolder()
        {
            var rec = JsonFile.Read<PathsRecord>(PathsFile);
            if (rec == null || string.IsNullOrWhiteSpace(rec.DataFolder)) return null;
            try { return Path.GetFullPath(rec.DataFolder); } catch (ArgumentException) { return null; } catch (NotSupportedException) { return null; }
        }

        /// <summary>Switches to another data folder and remembers it (null = back to the default). The server must be stopped.</summary>
        public void UseDataFolder(string folder)
        {
            if (folder == null || string.Equals(Path.GetFullPath(folder).TrimEnd('\\'), Path.Combine(Root, "data"), StringComparison.OrdinalIgnoreCase))
            {
                JsonFile.Delete(PathsFile);
                _data = Path.Combine(Root, "data");
                return;
            }
            var full = Path.GetFullPath(folder);
            JsonFile.Write(PathsFile, new PathsRecord { DataFolder = full });
            _data = full;
        }

        /// <summary>The folder that contains LuminaDB.exe.</summary>
        public static AppPaths FromExecutable() => new AppPaths(AppDomain.CurrentDomain.BaseDirectory);

        public string Root { get; }
        public string Engines => Path.Combine(Root, "engines");
        public string Data => _data;
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

    [System.Runtime.Serialization.DataContract]
    public sealed class PathsRecord
    {
        [System.Runtime.Serialization.DataMember] public string DataFolder { get; set; }
    }
}
