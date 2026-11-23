using System;
using System.IO;
using System.Linq;

namespace LuminaDB.Core
{
    /// <summary>The three bundled engine sets (SPEC §2).</summary>
    public enum EngineSetKind
    {
        /// <summary>Windows 10/11 64-bit: MariaDB 11.8, PHP 8.5, Caddy.</summary>
        Modern,
        /// <summary>Windows 7/8/8.1 64-bit: MariaDB 10.4.34, PHP 8.2, Apache.</summary>
        Legacy,
        /// <summary>Any 32-bit Windows: MariaDB 10.4.22 (last 32-bit build), PHP x86, Apache Win32.</summary>
        X86,
    }

    /// <summary>
    /// One engine set on disk: engines\&lt;set&gt;\mariadb, \php, \web. Executable names differ between
    /// MariaDB 10.4 (mysqld.exe) and 11.x (mariadbd.exe), so they are probed, never assumed.
    /// </summary>
    public sealed class EngineSet
    {
        private EngineSet(EngineSetKind kind, string root, string mariaDbDir)
        {
            Kind = kind;
            Root = root;
            MariaDbDir = mariaDbDir;
        }

        public EngineSetKind Kind { get; }
        public string Root { get; }
        public string MariaDbDir { get; }
        public string MariaDbBin => Path.Combine(MariaDbDir, "bin");

        public string ServerExe => Probe("mariadbd.exe", "mysqld.exe");
        public string InstallDbExe => Probe("mariadb-install-db.exe", "mysql_install_db.exe");
        public string ClientExe => Probe("mariadb.exe", "mysql.exe");
        public string DumpExe => Probe("mariadb-dump.exe", "mysqldump.exe");
        public string PluginDir => Path.Combine(MariaDbDir, "lib", "plugin");

        /// <summary>MariaDB version from the server exe's version resource (e.g. 11.8.10, 10.4.34).</summary>
        public Version ServerVersion
        {
            get
            {
                var v = System.Diagnostics.FileVersionInfo.GetVersionInfo(ServerExe);
                return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
            }
        }

        /// <summary>character_set_collations exists from MariaDB 11.2 (SPEC §8: keeps dumps importable into 10.4).</summary>
        public bool SupportsCharacterSetCollations => ServerVersion >= new Version(11, 2);

        public static string FolderName(EngineSetKind kind) => kind.ToString().ToLowerInvariant();

        /// <summary>Uses engines\&lt;set&gt;\mariadb under the app root.</summary>
        public static EngineSet FromAppRoot(AppPaths paths, EngineSetKind kind)
        {
            var root = Path.Combine(paths.Engines, FolderName(kind));
            return FromMariaDbFolder(kind, Path.Combine(root, "mariadb"), root);
        }

        /// <summary>Uses an explicit MariaDB folder (tests, developer setups).</summary>
        public static EngineSet FromMariaDbFolder(EngineSetKind kind, string mariaDbDir, string setRoot = null)
        {
            if (!Directory.Exists(mariaDbDir))
                throw new LuminaException(ErrorKind.EngineMissing,
                    $"The database engine for this PC is missing ({mariaDbDir}). Re-extract LuminaDB, or let the Doctor restore it.");
            var set = new EngineSet(kind, setRoot ?? Path.GetDirectoryName(mariaDbDir), Path.GetFullPath(mariaDbDir));
            // Touch the required files now so a broken copy fails early with a clear message.
            _ = set.ServerExe;
            _ = set.InstallDbExe;
            return set;
        }

        private string Probe(params string[] names)
        {
            var found = names.Select(n => Path.Combine(MariaDbBin, n)).FirstOrDefault(File.Exists);
            if (found == null)
                throw new LuminaException(ErrorKind.EngineFileMissing,
                    $"{names[0]} is missing from {MariaDbBin}. Antivirus may have removed it; the Doctor can restore it.");
            return found;
        }
    }

    public static class EngineSetSelector
    {
        /// <summary>Picks the engine set for this PC (SPEC §2). Throws for Windows older than 7.</summary>
        public static EngineSetKind Choose(WindowsInfo windows)
        {
            if (windows == null) throw new ArgumentNullException(nameof(windows));
            if (windows.IsBelowWindows7)
                throw new LuminaException(ErrorKind.UnsupportedWindows,
                    $"{windows.FriendlyName} is too old. LuminaDB needs Windows 7 SP1 or newer.");
            if (!windows.Is64BitOs) return EngineSetKind.X86;
            return windows.IsWindows10OrNewer ? EngineSetKind.Modern : EngineSetKind.Legacy;
        }
    }
}
