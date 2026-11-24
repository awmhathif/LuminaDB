using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LuminaDB.Core.Web;

namespace LuminaDB.Core.Projects
{
    /// <summary>Runs the bundled composer.phar with the bundled PHP, in a project folder (SPEC §5.2).</summary>
    public static class Composer
    {
        /// <summary>Runs composer with <paramref name="args"/>; each output line goes to <paramref name="onLine"/>. Returns the exit code.</summary>
        public static async Task<int> RunAsync(AppPaths paths, WebStack stack, string workingDir, IEnumerable<string> args, Action<string> onLine, CancellationToken ct = default)
        {
            if (stack == null || !stack.HasPhp) throw new LuminaException(ErrorKind.EngineFileMissing, "PHP is missing, so Composer can't run.");
            if (!stack.HasComposer) throw new LuminaException(ErrorKind.EngineFileMissing, "Composer isn't included in this copy of LuminaDB.");
            var home = Path.Combine(paths.Run, "composer");
            Directory.CreateDirectory(home);
            // php.exe (CLI) with our php.ini so the same extensions are on; no error page (it skips the CLI anyway).
            var all = new List<string>();
            var ini = Directory.Exists(paths.Config) ? Directory.GetFiles(paths.Config, "php-*.ini").FirstOrDefault() : null;
            if (ini != null) { all.Add("-c"); all.Add(ini); }
            all.AddRange(new[] { "-d", "auto_prepend_file=", "-d", "memory_limit=-1", stack.ComposerPhar });
            all.AddRange(args);
            all.Add("--no-ansi");   // plain output: the console shows text, not escape codes

            var psi = new ProcessStartInfo(stack.PhpExe, ManagedProcess.JoinArgs(all))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDir,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.EnvironmentVariables["COMPOSER_HOME"] = home;
            psi.EnvironmentVariables["COMPOSER_CACHE_DIR"] = Path.Combine(home, "cache");
            psi.EnvironmentVariables["COMPOSER_NO_INTERACTION"] = "1";
            psi.EnvironmentVariables["COMPOSER_ALLOW_SUPERUSER"] = "1";
            psi.EnvironmentVariables["PATH"] = stack.PhpDir + ";" + Environment.GetEnvironmentVariable("PATH");

            var log = Path.Combine(paths.Logs, "composer.log");
            using (var writer = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true })
            using (var p = new Process { StartInfo = psi, EnableRaisingEvents = true })
            {
                var gate = new object();
                void Line(string l)
                {
                    if (l == null) return;
                    lock (gate) { try { writer.WriteLine(l); } catch (IOException) { } }
                    try { onLine?.Invoke(l); } catch { }
                }
                Line("--- composer " + string.Join(" ", args) + " in " + workingDir + " at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                p.OutputDataReceived += (s, e) => Line(e.Data);
                p.ErrorDataReceived += (s, e) => Line(e.Data);
                p.Exited += (s, e) => exited.TrySetResult(0);
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                using (ct.Register(() => { try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }))
                    await exited.Task.ConfigureAwait(false);
                p.WaitForExit();
                Line("--- exit code " + p.ExitCode);
                ct.ThrowIfCancellationRequested();
                return p.ExitCode;
            }
        }

        /// <summary>Splits what the person typed in the console ("require monolog/monolog") into arguments.</summary>
        public static List<string> SplitCommandLine(string text)
        {
            var result = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false, any = false;
            foreach (var ch in (text ?? "").Trim())
            {
                if (ch == '"') { quoted = !quoted; any = true; continue; }
                if (char.IsWhiteSpace(ch) && !quoted)
                {
                    if (any) { result.Add(sb.ToString()); sb.Clear(); any = false; }
                    continue;
                }
                sb.Append(ch);
                any = true;
            }
            if (any) result.Add(sb.ToString());
            if (result.Count > 0 && result[0].Equals("composer", StringComparison.OrdinalIgnoreCase)) result.RemoveAt(0);
            return result;
        }
    }

    /// <summary>Sends files and folders to the Recycle Bin, so a deleted project can be brought back.</summary>
    public static class RecycleBin
    {
        public static void Send(string path)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return;
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE,
                pFrom = Path.GetFullPath(path) + "\0\0",
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT,
            };
            int rc = SHFileOperation(ref op);
            if (rc != 0 || op.fAnyOperationsAborted)
                throw new LuminaException(ErrorKind.Unknown, "Windows couldn't move " + Path.GetFileName(path) + " to the Recycle Bin (code " + rc + "). Close any program that has files open in it, then try again.");
            if (Directory.Exists(path) || File.Exists(path))
                throw new LuminaException(ErrorKind.Unknown, Path.GetFileName(path) + " is still there: a program has files open in it. Close it and try again.");
        }

        private const uint FO_DELETE = 3;
        private const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEOPSTRUCT
        {
            public IntPtr hwnd;
            public uint wFunc;
            public string pFrom;
            public string pTo;
            public ushort fFlags;
            [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
            public IntPtr hNameMappings;
            public string lpszProgressTitle;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);
    }
}
