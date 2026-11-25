using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace LuminaDB.Core
{
    /// <summary>What an error report says about this copy of LuminaDB (no names, no data).</summary>
    public sealed class ErrorReportContext
    {
        public string AppVersion { get; set; }
        public string Windows { get; set; }
        public string EngineSet { get; set; }
        public string MariaDbVersion { get; set; }
        /// <summary>Where it happened: "window", "background", "startup".</summary>
        public string Where { get; set; }
        /// <summary>logs\app.log, for its last lines.</summary>
        public string LogFile { get; set; }
        public int LogLines { get; set; } = 40;
    }

    /// <summary>
    /// The text a person can copy or save and send to the developer when LuminaDB hits an unexpected error. Nothing is
    /// ever sent by itself. The person's name, PC name and Windows profile path are replaced, and SQL text and values
    /// that MariaDB quotes in its messages are left out.
    /// </summary>
    public static class ErrorReport
    {
        public static string Build(Exception ex, ErrorReportContext ctx) => Build(ex, ctx, Environment.UserName, Environment.MachineName,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        public static string Build(Exception ex, ErrorReportContext ctx, string userName, string machineName, string userProfile)
        {
            ctx = ctx ?? new ErrorReportContext();
            var sb = new StringBuilder();
            sb.AppendLine("LuminaDB error report");
            sb.AppendLine("Read it first: it holds no databases, passwords or SQL. Send it only if you want to.");
            sb.AppendLine();
            sb.AppendLine("When:      " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture));
            sb.AppendLine("LuminaDB:  " + (ctx.AppVersion ?? "?"));
            var win = ctx.Windows ?? Environment.OSVersion.VersionString;
            sb.AppendLine("Windows:   " + win + (win.Contains("bit") ? "" : Environment.Is64BitOperatingSystem ? ", 64-bit" : ", 32-bit"));
            sb.AppendLine(".NET:      " + Environment.Version + " (release " + (NetRelease()?.ToString(CultureInfo.InvariantCulture) ?? "?") + ")");
            sb.AppendLine("Engines:   " + (ctx.EngineSet ?? "?") + (string.IsNullOrEmpty(ctx.MariaDbVersion) ? "" : ", MariaDB " + ctx.MariaDbVersion));
            sb.AppendLine("Language:  " + CultureInfo.CurrentUICulture.Name);
            sb.AppendLine("Where:     " + (ctx.Where ?? "?"));
            sb.AppendLine();
            sb.AppendLine("What happened");
            sb.AppendLine("-------------");
            sb.AppendLine(ex == null ? "(no details)" : Describe(ex));

            var tail = LogTail(ctx.LogFile, ctx.LogLines);
            if (tail.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Last lines of logs\\app.log");
                sb.AppendLine("--------------------------");
                foreach (var l in tail) sb.AppendLine(l);
            }
            return Scrub(sb.ToString(), userName, machineName, userProfile);
        }

        private static string Describe(Exception ex)
        {
            var sb = new StringBuilder();
            int depth = 0;
            for (var e = ex; e != null && depth < 6; e = e.InnerException, depth++)
            {
                if (depth > 0) sb.AppendLine("--- caused by");
                sb.AppendLine(e.GetType().FullName + ": " + e.Message);
                if (e is LuminaException lex) sb.AppendLine("Kind: " + lex.Kind);
                if (!string.IsNullOrEmpty(e.StackTrace)) sb.AppendLine(e.StackTrace);
            }
            return sb.ToString().TrimEnd();
        }

        private static List<string> LogTail(string file, int lines)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(file) || lines <= 0) return result;
            try
            {
                if (!File.Exists(file)) return result;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    // Only the end of a big log is needed.
                    if (fs.Length > 256 * 1024) fs.Seek(-256 * 1024, SeekOrigin.End);
                    using (var r = new StreamReader(fs, Encoding.UTF8))
                    {
                        var all = r.ReadToEnd().Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).Where(l => l.Length > 0).ToList();
                        result.AddRange(all.Skip(Math.Max(0, all.Count - lines)));
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return result;
        }

        private static readonly Regex[] DataPatterns =
        {
            // the SQL MariaDB quotes in a syntax error: ... near 'SELEC * FROM x' at line 1
            new Regex(@"near '(?:[^'\\]|\\.|'')*' at line", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            // values: Duplicate entry 'a@b.c' for key, Incorrect integer value: 'x' for column, Data truncated ... 'x'
            new Regex(@"(Duplicate entry|value:|for value) '(?:[^'\\]|\\.|'')*'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            // connection strings and passwords in a message
            new Regex(@"(password|pwd)\s*[=:]\s*[^;\s""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            // e-mail addresses
            new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant),
        };

        /// <summary>Replaces the person's Windows profile path, user name and PC name, and leaves out quoted SQL and values.</summary>
        public static string Scrub(string text, string userName, string machineName, string userProfile)
        {
            if (string.IsNullOrEmpty(text)) return text ?? "";
            var s = text;
            if (!string.IsNullOrEmpty(userProfile) && userProfile.Length > 3)
                s = Regex.Replace(s, Regex.Escape(userProfile.TrimEnd('\\')), "%USERPROFILE%", RegexOptions.IgnoreCase);
            s = DataPatterns[0].Replace(s, "near '…' at line");
            s = DataPatterns[1].Replace(s, m => m.Groups[1].Value + " '…'");
            s = DataPatterns[2].Replace(s, m => m.Groups[1].Value + "=…");
            s = DataPatterns[3].Replace(s, "…@…");
            // Whole words only: a short name like "Al" must not change "SQL" or "local".
            if (!string.IsNullOrEmpty(userName) && userName.Length >= 2)
                s = Regex.Replace(s, @"(?<![A-Za-z0-9])" + Regex.Escape(userName) + @"(?![A-Za-z0-9])", "<user>", RegexOptions.IgnoreCase);
            if (!string.IsNullOrEmpty(machineName) && machineName.Length >= 2)
                s = Regex.Replace(s, @"(?<![A-Za-z0-9])" + Regex.Escape(machineName) + @"(?![A-Za-z0-9])", "<pc>", RegexOptions.IgnoreCase);
            return s;
        }

        private static int? NetRelease()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                    return k?.GetValue("Release") as int?;
            }
            catch (Exception) { return null; }
        }

        /// <summary>logs\reports: saved reports, and the ones written when LuminaDB had to close.</summary>
        public static string Folder(AppPaths p) => Path.Combine(p.Logs, "reports");

        /// <summary>Saves a report as logs\reports\&lt;prefix&gt;-yyyyMMdd-HHmmss.txt and returns the file.</summary>
        public static string Save(AppPaths p, string report, string prefix = "report")
        {
            var dir = Folder(p);
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".txt");
            for (int i = 2; File.Exists(file); i++)
                file = Path.Combine(dir, prefix + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + i + ".txt");
            File.WriteAllText(file, report, new UTF8Encoding(false));
            return file;
        }

        /// <summary>Reports LuminaDB wrote when it had to close (crash-*.txt) that nobody has been told about yet.</summary>
        public static IReadOnlyList<string> UnseenCrashes(AppPaths p)
        {
            var dir = Folder(p);
            if (!Directory.Exists(dir)) return new string[0];
            return Directory.GetFiles(dir, "crash-*.txt").Where(f => !f.EndsWith("-seen.txt", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f).ToList();
        }

        /// <summary>Marks a crash report as told about (renamed to …-seen.txt); returns the new name.</summary>
        public static string MarkSeen(string file)
        {
            var seen = Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + "-seen.txt");
            try { File.Move(file, seen); return seen; }
            catch (IOException) { return file; }
            catch (UnauthorizedAccessException) { return file; }
        }
    }
}
