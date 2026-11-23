using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;

namespace LuminaDB.Core
{
    public enum CheckSeverity { Ok, Warn, Bad }

    public sealed class CheckResult
    {
        public CheckResult(string id, CheckSeverity severity, string title, string text, string code = null)
        {
            Id = id; Severity = severity; Title = title; Text = text; Code = code;
        }
        public string Id { get; }
        public CheckSeverity Severity { get; }
        public string Title { get; }
        public string Text { get; }
        /// <summary>The technical thing checked (shown small, for the curious).</summary>
        public string Code { get; }
    }

    /// <summary>
    /// The quick checks shown on Today. Read-only and fast (well under a second). The full Doctor
    /// with fixes builds on these (SPEC §5.9).
    /// </summary>
    public static class StartupChecks
    {
        public const long LowDiskBytes = 500L * 1024 * 1024;

        public static List<CheckResult> Run(AppPaths paths, WindowsInfo windows, EngineSet engine, MariaDbService server, int preferredPort)
        {
            var list = new List<CheckResult>();
            list.Add(CheckEngine(engine, windows));
            if (server != null) list.Add(CheckPort(server, preferredPort));
            if (server != null && server.State == ServerState.Running) list.Add(CheckLoopbackOnly(server.Port));
            list.Add(CheckPath(paths.Root));
            list.Add(CheckDisk(paths.Root));
            list.Add(CheckWritable(paths));
            return list;
        }

        public static CheckResult CheckEngine(EngineSet engine, WindowsInfo windows)
        {
            if (engine == null)
                return new CheckResult("engine", CheckSeverity.Bad, "Database engine missing",
                    "The engine files for this PC are not in the LuminaDB folder. Re-extract LuminaDB.", "engines\\");
            string v;
            try { v = engine.ServerVersion.ToString(); }
            catch (LuminaException ex) { return new CheckResult("engine", CheckSeverity.Bad, "Database engine damaged", ex.Message, "engines\\"); }
            return new CheckResult("engine", CheckSeverity.Ok, "Right engine for this PC",
                $"MariaDB {v} ({EngineSet.FolderName(engine.Kind)} set) for {windows?.FriendlyName ?? "this Windows"}. The Visual C++ runtime ships inside it, so nothing needs installing.",
                Path.GetFileName(engine.ServerExe));
        }

        public static CheckResult CheckPort(MariaDbService server, int preferredPort) =>
            CheckPort(server.State, server.Port, preferredPort, new SystemPortProbe());

        public static CheckResult CheckPort(ServerState state, int port, int preferredPort, IPortProbe probe)
        {
            if (state != ServerState.Running || port == 0)
                return new CheckResult("port", CheckSeverity.Ok, "Port",
                    $"A free port is picked at start ({preferredPort} if it's free).", "port");
            var code = "port=" + port.ToString(CultureInfo.InvariantCulture);
            if (port == preferredPort)
                return new CheckResult("port", CheckSeverity.Ok, $"Port {preferredPort}", "The standard MySQL port, so tutorials and PHP code work as written.", code);
            if (probe.IsInUse(preferredPort))
                return new CheckResult("port", CheckSeverity.Warn, $"Port {preferredPort} is taken",
                    $"{PortOwner.Describe(preferredPort)} holds it, so the server runs on {port}. Use port {port} in your PHP code (LuminaDB projects get it automatically).", code);
            return new CheckResult("port", CheckSeverity.Ok, $"Port {port}, kept from last time",
                $"{preferredPort} is free again, but LuminaDB keeps {port} so the PHP code you already wrote still connects.", code);
        }

        public static CheckResult CheckLoopbackOnly(int port)
        {
            var open = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Where(e => e.Port == port && !IPAddress.IsLoopback(e.Address)).ToList();
            return open.Count == 0
                ? new CheckResult("firewall", CheckSeverity.Ok, "Firewall", "Listening on 127.0.0.1 only, so Windows won't ask and nobody else can connect.", "bind-address")
                : new CheckResult("firewall", CheckSeverity.Warn, "Reachable from the network",
                    $"Something is listening on {open[0]} as well. Only this PC should reach the database.", "bind-address");
        }

        public static CheckResult CheckPath(string root)
        {
            var problems = PathProblems(root);
            return problems.Count == 0
                ? new CheckResult("path", CheckSeverity.Ok, "LuminaDB folder", $"{root} is a good place: local, short, plain characters.", "folder")
                : new CheckResult("path", CheckSeverity.Warn, "LuminaDB folder", string.Join(" ", problems), "folder");
        }

        /// <summary>Plain-English problems with where LuminaDB lives (empty if none).</summary>
        public static List<string> PathProblems(string root)
        {
            var p = new List<string>();
            if (root.Any(ch => ch > 127)) p.Add("The path has non-English characters, which some PHP tools mishandle. A folder like C:\\LuminaDB is safer.");
            if (root.Length > 120) p.Add("The path is very long; Windows may refuse deep project files. Move LuminaDB closer to the drive root.");
            if (root.IndexOf("OneDrive", StringComparison.OrdinalIgnoreCase) >= 0 || root.IndexOf("Dropbox", StringComparison.OrdinalIgnoreCase) >= 0 || root.IndexOf("Google Drive", StringComparison.OrdinalIgnoreCase) >= 0)
                p.Add("It is inside a cloud-synced folder. Syncing live database files can corrupt them; keep LuminaDB outside OneDrive/Dropbox and use Backups instead.");
            if (root.StartsWith(@"\\", StringComparison.Ordinal)) p.Add("It is on a network share. The database needs a local disk.");
            return p;
        }

        public static CheckResult CheckDisk(string root)
        {
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(root));
                long free = drive.AvailableFreeSpace;
                var text = $"{DatabaseSummary.FormatSize(free)} free on {drive.Name.TrimEnd('\\')}.";
                return free < LowDiskBytes
                    ? new CheckResult("disk", CheckSeverity.Bad, "Disk almost full", text + " Below 500 MB the database may stop accepting changes. Free some space.", drive.Name)
                    : new CheckResult("disk", CheckSeverity.Ok, "Disk space", text, drive.Name);
            }
            catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
            {
                return new CheckResult("disk", CheckSeverity.Warn, "Disk space", "Couldn't read the free space on this drive.", "drive");
            }
        }

        public static CheckResult CheckWritable(AppPaths paths)
        {
            try
            {
                Directory.CreateDirectory(paths.Temp);
                var probe = Path.Combine(paths.Temp, "write-test-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return new CheckResult("write", CheckSeverity.Ok, "Can save files", "The LuminaDB folder is writable.", "tmp\\");
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new CheckResult("write", CheckSeverity.Bad, "Folder is read-only",
                    "LuminaDB can't save files here (read-only USB stick, or a lab PC that resets). Move the LuminaDB folder to Documents or D:\\.", "tmp\\");
            }
        }
    }
}
