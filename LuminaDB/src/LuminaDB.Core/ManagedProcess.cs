using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LuminaDB.Core
{
    /// <summary>
    /// A child process (MariaDB, PHP, web server) with its console output captured to a log file
    /// and raised as events for the UI's receipt-tape log. No console window ever appears.
    /// </summary>
    public sealed class ManagedProcess : IDisposable
    {
        public const long MaxLogBytes = 5L * 1024 * 1024;

        private readonly Process _process;
        private readonly StreamWriter _log;
        private readonly object _gate = new object();
        private readonly Queue<string> _recent = new Queue<string>();
        private readonly TaskCompletionSource<int> _exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _disposed;

        private ManagedProcess(Process process, StreamWriter log)
        {
            _process = process;
            _log = log;
        }

        public event Action<string> OutputLine;

        public int Id { get; private set; }
        public bool Adopted { get; private set; }
        public bool HasExited { get { try { return _process.HasExited; } catch (InvalidOperationException) { return true; } } }
        public Task<int> Exited => _exited.Task;

        /// <summary>The last lines the process printed (for error messages).</summary>
        public string RecentOutput(int lines = 20)
        {
            lock (_gate) return string.Join(Environment.NewLine, _recent.Skip(Math.Max(0, _recent.Count - lines)));
        }

        public static ManagedProcess Start(string exe, IEnumerable<string> args, string logPath,
            string workingDir = null, IDictionary<string, string> environment = null)
        {
            if (!File.Exists(exe))
                throw new LuminaException(ErrorKind.EngineFileMissing, $"{Path.GetFileName(exe)} is missing. Antivirus may have removed it; the Doctor can restore it.");

            var psi = new ProcessStartInfo(exe, JoinArgs(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                WorkingDirectory = workingDir ?? Path.GetDirectoryName(exe),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (environment != null)
                foreach (var kv in environment) psi.EnvironmentVariables[kv.Key] = kv.Value;

            var mp = new ManagedProcess(new Process { StartInfo = psi, EnableRaisingEvents = true }, OpenLog(logPath));
            mp._process.OutputDataReceived += (s, e) => mp.OnLine(e.Data);
            mp._process.ErrorDataReceived += (s, e) => mp.OnLine(e.Data);
            mp._process.Exited += (s, e) => mp.OnExited();
            try { mp._process.Start(); }
            catch (System.ComponentModel.Win32Exception ex)
            {
                mp.Dispose();
                throw new LuminaException(ErrorKind.ServerFailedToStart,
                    $"Windows refused to run {Path.GetFileName(exe)} ({ex.Message}). Antivirus may be blocking it.", ex);
            }
            mp.Id = mp._process.Id;
            mp.Log($"--- started {Path.GetFileName(exe)} (PID {mp.Id}) at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            mp._process.BeginOutputReadLine();
            mp._process.BeginErrorReadLine();
            return mp;
        }

        /// <summary>Re-attaches to a process started by an earlier LuminaDB run. Returns null if it's gone or isn't ours.</summary>
        public static ManagedProcess TryAdopt(int pid, string expectedExe, string logPath)
        {
            Process p;
            try { p = Process.GetProcessById(pid); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }

            if (!PathEquals(SafeExePath(p), expectedExe)) { p.Dispose(); return null; }
            var mp = new ManagedProcess(p, OpenLog(logPath)) { Id = pid, Adopted = true };
            try
            {
                p.EnableRaisingEvents = true;
                p.Exited += (s, e) => mp.OnExited();
                if (p.HasExited) mp.OnExited();
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception)
            {
                mp.Dispose();
                return null;
            }
            mp.Log($"--- adopted running {Path.GetFileName(expectedExe)} (PID {pid}) at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            return mp;
        }

        /// <summary>
        /// True when the process's command line names a file inside <paramref name="root"/> (every server LuminaDB starts
        /// gets its config from there). Another LuminaDB copy's servers run the same exe but name another folder, so they
        /// are never adopted or stopped. An unreadable command line counts as "not ours".
        /// </summary>
        public static bool StartedFrom(int pid, string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            var cmd = CommandLine(pid);
            if (string.IsNullOrEmpty(cmd)) return false;
            string folder;
            try { folder = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\"; }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return false; }
            return cmd.Replace('/', '\\').IndexOf(folder, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>The process's full command line (WMI), or null when it's gone or Windows won't say.</summary>
        internal static string CommandLine(int pid)
        {
            try
            {
                using (var search = new System.Management.ManagementObjectSearcher("SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + pid))
                using (var found = search.Get())
                {
                    foreach (System.Management.ManagementBaseObject o in found)
                        using (o) return o["CommandLine"] as string;
                }
            }
            catch (System.Management.ManagementException) { }
            catch (System.Runtime.InteropServices.COMException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        /// <summary>Every running copy of <paramref name="exe"/> (same full path), e.g. servers left behind by a crash.</summary>
        public static List<int> FindRunning(string exe)
        {
            var result = new List<int>();
            foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            {
                using (p) if (PathEquals(SafeExePath(p), exe)) result.Add(p.Id);
            }
            return result;
        }

        public async Task<bool> WaitForExitAsync(TimeSpan timeout)
        {
            if (HasExited) return true;
            var done = await Task.WhenAny(Exited, Task.Delay(timeout)).ConfigureAwait(false);
            return done == Exited || HasExited;
        }

        /// <summary>Hard stop. Safe for MariaDB: InnoDB replays its log on next start (proven in EngineSpike).</summary>
        public void Kill()
        {
            try { if (!_process.HasExited) _process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { _process.WaitForExit(10000); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        }

        public void Log(string line) => OnLine(line);

        private void OnLine(string line)
        {
            if (line == null) return;
            lock (_gate)
            {
                if (_disposed) return;
                _recent.Enqueue(line);
                while (_recent.Count > 200) _recent.Dequeue();
                try { _log?.WriteLine(line); } catch (IOException) { } catch (ObjectDisposedException) { }
            }
            try { OutputLine?.Invoke(line); } catch { /* a UI handler must never kill the reader thread */ }
        }

        private void OnExited()
        {
            int code;
            try { code = _process.ExitCode; } catch (InvalidOperationException) { code = -1; }
            // Let the async readers drain the last lines before we close the log.
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (!Adopted) _process.WaitForExit(); } catch { }
                OnLine($"--- exited with code {code} at {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _exited.TrySetResult(code);
            });
        }

        private static StreamWriter OpenLog(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxLogBytes)
                {
                    var old = path + ".1";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return new StreamWriter(fs, new UTF8Encoding(false)) { AutoFlush = true };
        }

        internal static string JoinArgs(IEnumerable<string> args) => string.Join(" ", (args ?? Enumerable.Empty<string>()).Select(Quote));

        /// <summary>Windows command-line quoting (CommandLineToArgvW rules).</summary>
        internal static string Quote(string a)
        {
            if (a == null) return "\"\"";
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (var ch in a)
            {
                if (ch == '\\') { backslashes++; continue; }
                if (ch == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', backslashes); sb.Append(ch); }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        private static string SafeExePath(Process p)
        {
            try { return p.MainModule?.FileName; }
            catch (System.ComponentModel.Win32Exception) { return null; }
            catch (InvalidOperationException) { return null; }
        }

        private static bool PathEquals(string a, string b) =>
            a != null && b != null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                try { _log?.Dispose(); } catch (IOException) { }
            }
            _process.Dispose();
        }
    }
}
