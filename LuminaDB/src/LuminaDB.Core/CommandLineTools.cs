using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LuminaDB.Core.Web;
using Microsoft.Win32;

namespace LuminaDB.Core
{
    /// <summary>
    /// &lt;LuminaDB&gt;\bin: small .cmd files that run this copy's PHP, Composer and MariaDB tools from any terminal (for the
    /// person, or an AI coding assistant working in a project). Rewritten at every start and whenever the database port
    /// changes, so they always point at the engine set and port in use. Settings can put the folder on the PATH.
    /// </summary>
    public static class CommandLineTools
    {
        public static string Folder(AppPaths p) => Path.Combine(p.Root, "bin");

        public sealed class Plan
        {
            public string PhpExe { get; set; }
            public string PhpIni { get; set; }
            public string ComposerPhar { get; set; }
            public string ComposerHome { get; set; }
            public string ClientExe { get; set; }
            public string DumpExe { get; set; }
            public int DatabasePort { get; set; }
        }

        public static Plan PlanFor(AppPaths p, WebStack stack, EngineSet engine, EngineSetKind kind, int databasePort) => new Plan
        {
            PhpExe = stack != null && File.Exists(stack.PhpExe ?? "") ? stack.PhpExe : null,
            PhpIni = PhpConfig.IniFile(p, kind),
            ComposerPhar = stack != null && stack.HasComposer ? stack.ComposerPhar : null,
            ComposerHome = Path.Combine(p.Run, "composer"),
            ClientExe = engine != null && File.Exists(engine.ClientExe ?? "") ? engine.ClientExe : null,
            DumpExe = engine != null && File.Exists(engine.DumpExe ?? "") ? engine.DumpExe : null,
            DatabasePort = databasePort,
        };

        /// <summary>The files and their text, by file name (only the tools this copy has).</summary>
        public static IDictionary<string, string> Files(Plan plan)
        {
            var files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (plan.PhpExe != null)
            {
                // php.exe with LuminaDB's php.ini (same extensions as the web server), without the browser error page.
                files["php.cmd"] =
                    "@echo off\r\n" +
                    "rem LuminaDB: this copy's PHP, with the php.ini the web server uses.\r\n" +
                    "if not exist \"" + plan.PhpIni + "\" goto plain\r\n" +
                    "\"" + plan.PhpExe + "\" -c \"" + plan.PhpIni + "\" -d auto_prepend_file= %*\r\n" +
                    "exit /b %errorlevel%\r\n" +
                    ":plain\r\n" +
                    "rem The web server hasn't run yet, so there is no php.ini: turn on the usual extensions here.\r\n" +
                    "\"" + plan.PhpExe + "\" -n -d extension_dir=\"" + Path.Combine(Path.GetDirectoryName(plan.PhpExe), "ext") + "\"" +
                    " -d extension=mysqli -d extension=pdo_mysql -d extension=mbstring -d extension=openssl -d extension=curl %*\r\n";
                if (plan.ComposerPhar != null)
                    files["composer.cmd"] =
                        "@echo off\r\n" +
                        "rem LuminaDB: the bundled Composer, run with this copy's PHP.\r\n" +
                        "setlocal\r\n" +
                        "if not defined COMPOSER_HOME set \"COMPOSER_HOME=" + plan.ComposerHome + "\"\r\n" +
                        "call \"%~dp0php.cmd\" -d memory_limit=-1 \"" + plan.ComposerPhar + "\" %*\r\n" +
                        "exit /b %errorlevel%\r\n";
            }
            var port = plan.DatabasePort.ToString(CultureInfo.InvariantCulture);
            if (plan.ClientExe != null)
            {
                // Options given after ours win, so "mariadb -u app -p shop" still works.
                var client = "@echo off\r\n" +
                             "rem LuminaDB: the MariaDB command line, connected to this copy's server (127.0.0.1:" + port + ") as root.\r\n" +
                             "\"" + plan.ClientExe + "\" --host=127.0.0.1 --port=" + port + " --user=root --default-character-set=utf8mb4 --skip-ssl %*\r\n";
                files["mariadb.cmd"] = client;
                files["mysql.cmd"] = client;
            }
            if (plan.DumpExe != null)
            {
                var dump = "@echo off\r\n" +
                           "rem LuminaDB: mariadb-dump for this copy's server (127.0.0.1:" + port + ").\r\n" +
                           "\"" + plan.DumpExe + "\" --host=127.0.0.1 --port=" + port + " --user=root --default-character-set=utf8mb4 --skip-ssl %*\r\n";
                files["mariadb-dump.cmd"] = dump;
                files["mysqldump.cmd"] = dump;
            }
            files["README.txt"] =
                "LuminaDB command-line tools\r\n" +
                "===========================\r\n\r\n" +
                "These run this copy of LuminaDB's programs from any terminal. LuminaDB rewrites them every time it starts,\r\n" +
                "so they always point at the PHP and MariaDB it uses and at the database port.\r\n\r\n" +
                "  php.cmd        PHP " + (plan.PhpExe != null ? "(" + plan.PhpExe + ")" : "(not in this copy)") + "\r\n" +
                "  composer.cmd   Composer\r\n" +
                "  mariadb.cmd    MariaDB command line, as root, on 127.0.0.1:" + port + " (mysql.cmd is the same)\r\n" +
                "  mariadb-dump.cmd  dump a database to .sql (mysqldump.cmd is the same)\r\n\r\n" +
                "The database must be running (start LuminaDB). If root has a password, add -p.\r\n" +
                "Settings > Command line can add this folder to your PATH, so plain \"php\" and \"mariadb\" use LuminaDB.\r\n";
            return files;
        }

        /// <summary>Writes &lt;LuminaDB&gt;\bin, removing tools this copy no longer has.</summary>
        public static void Write(AppPaths p, Plan plan)
        {
            var dir = Folder(p);
            Directory.CreateDirectory(dir);
            var files = Files(plan);
            foreach (var kv in files)
            {
                var path = Path.Combine(dir, kv.Key);
                if (File.Exists(path) && File.ReadAllText(path) == kv.Value) continue;
                File.WriteAllText(path, kv.Value, kv.Key.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? Encoding.Default : new UTF8Encoding(false));
            }
            foreach (var old in Directory.GetFiles(dir, "*.cmd"))
                if (!files.ContainsKey(Path.GetFileName(old))) File.Delete(old);
        }
    }

    /// <summary>
    /// Settings › Command line: &lt;LuminaDB&gt;\bin on the PATH of this Windows account (HKCU\Environment, no administrator
    /// rights), first, so "php" and "mariadb" in a new terminal run LuminaDB's. Removed with the switch and by the uninstaller.
    /// </summary>
    public sealed class UserPath
    {
        private readonly string _key;

        public UserPath(string keyPath = "Environment") { _key = keyPath; }

        private string Raw()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(_key))
                return k?.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
        }

        private static string Norm(string entry) => Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"')).TrimEnd('\\');

        public static IEnumerable<string> Split(string path) => (path ?? "").Split(';').Where(e => e.Trim().Length > 0);

        public bool Contains(string folder) => Split(Raw()).Any(e => string.Equals(Norm(e), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));

        /// <summary>Adds the folder at the front (or removes every copy of it), keeping everything else as it was.</summary>
        public void Set(string folder, bool on)
        {
            folder = folder.TrimEnd('\\');
            var rest = Split(Raw()).Where(e => !string.Equals(Norm(e), folder, StringComparison.OrdinalIgnoreCase)).ToList();
            if (on) rest.Insert(0, folder);
            using (var k = Registry.CurrentUser.CreateSubKey(_key))
            {
                if (rest.Count == 0) k.DeleteValue("Path", false);
                else k.SetValue("Path", string.Join(";", rest), RegistryValueKind.ExpandString);
            }
            if (_key == "Environment") Broadcast();
        }

        /// <summary>Tells Explorer the environment changed, so terminals opened from now on see the new PATH.</summary>
        private static void Broadcast()
        {
            try { SendMessageTimeout((IntPtr)0xffff, 0x001A, IntPtr.Zero, "Environment", 0x0002, 3000, out _); }
            catch (Exception) { }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
    }
}
