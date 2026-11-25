using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace LuminaDB.Core.Admin
{
    /// <summary>
    /// The <c>mariadb</c> command-line client running inside the app (SPEC §5.8 "Console: the mariadb command line in a tab").
    /// No window is opened: stdin, stdout and stderr are pipes. The client runs in batch mode, so each statement's output
    /// arrives when the statement ends; <c>--force</c> keeps it going after an error, <c>--table</c> draws the usual boxes.
    /// The password goes through MYSQL_PWD in the client's own environment, never on its command line.
    /// Text is UTF-8 both ways (Sinhala and Tamil survive), whatever the console code page is.
    /// If LuminaDB dies, the client sees its stdin close and exits by itself.
    /// </summary>
    public sealed class MariaDbConsole : IDisposable
    {
        private readonly object _lock = new object();
        private Process _p;
        private bool _disposed;

        /// <summary>A line of output (stdout or stderr), raised on a background thread.</summary>
        public event Action<string, bool> Line;
        /// <summary>The client ended (exit, quit, killed); raised on a background thread with its exit code.</summary>
        public event Action<int> Exited;

        public bool IsRunning
        {
            get { lock (_lock) return _p != null && !HasExited(_p); }
        }

        public int Port { get; private set; }

        /// <summary>The arguments the client gets (exposed for tests and the note on the page). No password here.</summary>
        public static string Arguments(int port, string database = null) =>
            "--host=127.0.0.1 --port=" + port.ToString(CultureInfo.InvariantCulture) +
            " --user=root --default-character-set=utf8mb4 --table --force --unbuffered --show-warnings --comments" +
            (string.IsNullOrEmpty(database) ? "" : " --database=" + Quote(database));

        private static string Quote(string s) => "\"" + s.Replace("\"", "") + "\"";

        public void Start(string clientExe, int port, string password, string workingDirectory, string database = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(MariaDbConsole));
            if (string.IsNullOrEmpty(clientExe) || !System.IO.File.Exists(clientExe))
                throw new LuminaException(ErrorKind.EngineMissing, "The mariadb client isn't in the engine folder (" + clientExe + ").");
            Stop();
            var psi = new ProcessStartInfo(clientExe, Arguments(port, database))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
            };
            psi.EnvironmentVariables.Remove("MYSQL_PWD");
            if (!string.IsNullOrEmpty(password)) psi.EnvironmentVariables["MYSQL_PWD"] = password;
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) Line?.Invoke(e.Data, false); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null && !IsNoise(e.Data)) Line?.Invoke(e.Data, true); };
            p.Exited += (s, e) =>
            {
                int code = -1;
                try { code = p.ExitCode; } catch (InvalidOperationException) { }
                bool ours;
                lock (_lock) ours = ReferenceEquals(_p, p);
                if (ours) Exited?.Invoke(code);
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            lock (_lock) { _p = p; Port = port; }
        }

        /// <summary>The client's own complaint about a password-less login over TCP; it says nothing useful to a student.</summary>
        private static bool IsNoise(string line) =>
            line.StartsWith("WARNING: option --ssl-verify-server-cert is disabled", StringComparison.Ordinal);

        /// <summary>Sends text exactly as typed, plus a newline. The client runs it once a statement ends with ; (or \G).</summary>
        public void Send(string text)
        {
            Process p;
            lock (_lock) p = _p;
            if (p == null || HasExited(p)) throw new InvalidOperationException("The console isn't running.");
            var bytes = new UTF8Encoding(false).GetBytes((text ?? "") + "\n");
            var s = p.StandardInput.BaseStream;   // bytes, not StandardInput's OEM code page
            s.Write(bytes, 0, bytes.Length);
            s.Flush();
        }

        public void Stop()
        {
            Process p;
            lock (_lock) { p = _p; _p = null; }
            if (p == null) return;
            try
            {
                if (!HasExited(p))
                {
                    try { p.StandardInput.Close(); } catch (Exception ex) when (ex is InvalidOperationException || ex is System.IO.IOException) { }
                    if (!p.WaitForExit(1500)) p.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
            finally { p.Dispose(); }
        }

        private static bool HasExited(Process p)
        {
            try { return p.HasExited; }
            catch (InvalidOperationException) { return true; }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
