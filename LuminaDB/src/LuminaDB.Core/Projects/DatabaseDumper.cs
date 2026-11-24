using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LuminaDB.Core.Projects
{
    /// <summary>
    /// Dumps and restores one database with the bundled mariadb-dump / mariadb client (SPEC §5.10: dumps are portable
    /// between engine sets). The password goes in a temporary option file, never on the command line, and SQL is fed
    /// through stdin, never as an argument (SPEC §4: Unicode on the command line breaks on 10.4).
    /// </summary>
    public static class DatabaseDumper
    {
        public static async Task DumpAsync(MariaDbService server, string database, string targetFile, CancellationToken ct = default)
        {
            if (server.Port == 0) throw new LuminaException(ErrorKind.ServerNotRunning, "Start the database server first.");
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile));
            using (var opts = new OptionFile(server))
            {
                var args = new List<string>
                {
                    "--defaults-extra-file=" + opts.Path,
                    "--single-transaction", "--routines", "--triggers", "--events",
                    "--default-character-set=utf8mb4", "--hex-blob", "--skip-dump-date",
                    "--add-drop-table", "--databases", database,
                };
                var tmp = targetFile + ".part";
                var (code, _, err) = await RunAsync(server.Engine.DumpExe, args, null, tmp, ct).ConfigureAwait(false);
                if (code != 0)
                {
                    TryDelete(tmp);
                    throw new LuminaException(ErrorKind.Unknown, "The database " + database + " couldn't be exported: " + FirstLine(err), detail: err);
                }
                if (File.Exists(targetFile)) File.Delete(targetFile);
                File.Move(tmp, targetFile);
            }
        }

        /// <summary>
        /// Export: several whole databases into one file (each with its CREATE DATABASE), or some tables of one database
        /// (no CREATE DATABASE, so it can be imported into any database).
        /// </summary>
        public static async Task DumpAsync(MariaDbService server, IList<string> databases, IList<string> tables, string targetFile, CancellationToken ct = default)
        {
            if (server.Port == 0) throw new LuminaException(ErrorKind.ServerNotRunning, "Start the database server first.");
            if (databases == null || databases.Count == 0) throw new LuminaException(ErrorKind.InvalidInput, "Choose a database to export.");
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile));
            using (var opts = new OptionFile(server))
            {
                var args = new List<string>
                {
                    "--defaults-extra-file=" + opts.Path,
                    "--single-transaction", "--routines", "--triggers", "--events",
                    "--default-character-set=utf8mb4", "--hex-blob", "--skip-dump-date", "--add-drop-table",
                };
                if (tables != null && tables.Count > 0) { args.Add(databases[0]); args.AddRange(tables); }
                else { args.Add("--databases"); args.AddRange(databases); }
                var tmp = targetFile + ".part";
                var (code, _, err) = await RunAsync(server.Engine.DumpExe, args, null, tmp, ct).ConfigureAwait(false);
                if (code != 0)
                {
                    TryDelete(tmp);
                    throw new LuminaException(ErrorKind.Unknown, "The export failed: " + FirstLine(err), detail: err);
                }
                if (File.Exists(targetFile)) File.Delete(targetFile);
                File.Move(tmp, targetFile);
            }
        }

        /// <summary>Runs a .sql file (from <see cref="DumpAsync"/>) into the server. The dump recreates its own database.</summary>
        public static async Task RestoreAsync(MariaDbService server, string sqlFile, CancellationToken ct = default)
        {
            if (server.Port == 0) throw new LuminaException(ErrorKind.ServerNotRunning, "Start the database server first.");
            if (!File.Exists(sqlFile)) throw new LuminaException(ErrorKind.InvalidInput, "The database file " + Path.GetFileName(sqlFile) + " is missing.");
            using (var opts = new OptionFile(server))
            {
                var args = new List<string> { "--defaults-extra-file=" + opts.Path, "--default-character-set=utf8mb4", "--binary-mode" };
                var (code, _, err) = await RunAsync(server.Engine.ClientExe, args, sqlFile, null, ct).ConfigureAwait(false);
                if (code != 0)
                    throw new LuminaException(ErrorKind.Unknown, "The database couldn't be restored: " + FirstLine(err), detail: err);
            }
        }

        /// <summary>Runs another bundled client tool (e.g. mariadb-check) against the server. Returns (exit code, output, errors).</summary>
        public static async Task<(int Code, string Output, string Errors)> RunToolAsync(MariaDbService server, string exe, IEnumerable<string> args, CancellationToken ct = default)
        {
            if (server.Port == 0) throw new LuminaException(ErrorKind.ServerNotRunning, "Start the database server first.");
            using (var opts = new OptionFile(server))
            {
                var all = new List<string> { "--defaults-extra-file=" + opts.Path };
                all.AddRange(args);
                return await RunAsync(exe, all, null, null, ct).ConfigureAwait(false);
            }
        }

        private static string FirstLine(string s)
        {
            s = (s ?? "").Trim();
            int nl = s.IndexOf('\n');
            return nl < 0 ? s : s.Substring(0, nl).Trim();
        }

        private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

        /// <summary>Runs a client tool with stdin from a file and stdout to a file (or captured). Returns (exit code, stdout, stderr).</summary>
        private static async Task<(int, string, string)> RunAsync(string exe, IEnumerable<string> args, string stdinFile, string stdoutFile, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(exe, ManagedProcess.JoinArgs(args))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = stdinFile != null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe),
            };
            using (var p = new Process { StartInfo = psi })
            {
                p.Start();
                var errTask = p.StandardError.ReadToEndAsync();
                Task outTask;
                if (stdoutFile != null)
                {
                    outTask = Task.Run(async () =>
                    {
                        using (var fs = new FileStream(stdoutFile, FileMode.Create, FileAccess.Write))
                            await p.StandardOutput.BaseStream.CopyToAsync(fs, 81920, ct).ConfigureAwait(false);
                    });
                }
                else outTask = p.StandardOutput.ReadToEndAsync();
                if (stdinFile != null)
                {
                    using (var fs = new FileStream(stdinFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        try { await fs.CopyToAsync(p.StandardInput.BaseStream, 81920, ct).ConfigureAwait(false); }
                        catch (IOException) { /* the client stopped reading because of an error; stderr says why */ }
                    }
                    try { p.StandardInput.Close(); } catch (IOException) { }
                }
                using (ct.Register(() => { try { if (!p.HasExited) p.Kill(); } catch (InvalidOperationException) { } }))
                {
                    await Task.WhenAll(outTask, errTask).ConfigureAwait(false);
                    await Task.Run(() => p.WaitForExit(), CancellationToken.None).ConfigureAwait(false);
                }
                ct.ThrowIfCancellationRequested();
                return (p.ExitCode, (outTask as Task<string>)?.Result ?? "", errTask.Result);
            }
        }

        /// <summary>A [client] option file with the connection details, deleted afterwards.</summary>
        private sealed class OptionFile : IDisposable
        {
            public OptionFile(MariaDbService server)
            {
                var cs = server.ConnectionString();
                Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lumina-" + Guid.NewGuid().ToString("N") + ".cnf");
                var sb = new StringBuilder();
                sb.AppendLine("[client]");
                sb.AppendLine("host=127.0.0.1");
                sb.AppendLine("port=" + cs.Port);
                sb.AppendLine("user=" + cs.UserID);
                // Option-file values may be quoted; backslash escapes apply inside quotes.
                sb.AppendLine("password=\"" + (cs.Password ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
                sb.AppendLine("protocol=tcp");
                File.WriteAllText(Path, sb.ToString(), new UTF8Encoding(false));
            }

            public string Path { get; }

            public void Dispose() => TryDelete(Path);
        }
    }
}
