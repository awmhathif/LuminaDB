using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LuminaDB.Core;
using Microsoft.Win32;
using Xunit;

namespace LuminaDB.Core.Tests
{
    /// <summary>The error report (nothing personal or from the databases in it) and the command-line tools in &lt;LuminaDB&gt;\bin.</summary>
    public class ReportAndToolsTests
    {
        private static string Temp() { var d = Path.Combine(Path.GetTempPath(), "LuminaReport " + Guid.NewGuid().ToString("N").Substring(0, 6)); Directory.CreateDirectory(d); return d; }

        [Fact]
        public void Scrub_replaces_names_and_leaves_out_sql_and_values()
        {
            var text = @"C:\Users\Nimal Perera\AppData\Local\LuminaDB\logs\app.log on NIMAL-PC by Nimal Perera
You have an error in your SQL syntax; check the manual near 'SELEC * FROM students WHERE nic = ''991234567V''' at line 1
Duplicate entry 'nimal@example.com' for key 'email'
Incorrect integer value: 'abc' for column `age`
Server=127.0.0.1;Port=3306;User=root;Password=s3cret;
Local SQL is fine";
            var s = ErrorReport.Scrub(text, "Nimal Perera", "NIMAL-PC", @"C:\Users\Nimal Perera");
            Assert.DoesNotContain("Nimal", s);
            Assert.DoesNotContain("NIMAL-PC", s);
            Assert.DoesNotContain("991234567V", s);
            Assert.DoesNotContain("SELEC", s);
            Assert.DoesNotContain("nimal@example.com", s);
            Assert.DoesNotContain("'abc'", s);
            Assert.DoesNotContain("s3cret", s);
            Assert.Contains(@"%USERPROFILE%\AppData\Local\LuminaDB\logs\app.log on <pc> by <user>", s);
            Assert.Contains("near '…' at line 1", s);
            Assert.Contains("for key 'email'", s);
            Assert.Contains("Local SQL is fine", s);
        }

        [Fact]
        public void Short_user_names_only_match_whole_words()
        {
            var s = ErrorReport.Scrub("SQL local Al al-mail", "Al", "PC1", null);
            Assert.Equal("SQL local <user> <user>-mail", s);
        }

        [Fact]
        public void Build_has_versions_the_error_and_the_log_tail()
        {
            var dir = Temp();
            try
            {
                var log = Path.Combine(dir, "app.log");
                File.WriteAllLines(log, Enumerable.Range(1, 100).Select(i => "line " + i + (i == 100 ? @" C:\Users\Kasun\x" : "")));
                Exception ex;
                try { throw new InvalidOperationException("outer", new IOException("inner near 'DROP TABLE x' at line 2")); }
                catch (Exception e) { ex = e; }
                var r = ErrorReport.Build(ex, new ErrorReportContext { AppVersion = "1.0.0", Windows = "Windows 10", EngineSet = "modern", MariaDbVersion = "11.8.10", Where = "window", LogFile = log, LogLines = 5 },
                    "Kasun", "DESK1", @"C:\Users\Kasun");
                Assert.Contains("LuminaDB:  1.0.0", r);
                Assert.Contains("modern, MariaDB 11.8.10", r);
                Assert.Contains("System.InvalidOperationException: outer", r);
                Assert.Contains("--- caused by", r);
                Assert.Contains("near '…' at line 2", r);
                Assert.DoesNotContain("DROP TABLE", r);
                Assert.Contains("line 96", r);
                Assert.DoesNotContain("line 95\r", r);
                Assert.Contains(@"line 100 %USERPROFILE%\x", r);
                Assert.Contains("ReportAndToolsTests", r);                      // the stack trace
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Crash_reports_are_told_about_once()
        {
            var dir = Temp();
            try
            {
                var paths = new AppPaths(dir);
                Assert.Empty(ErrorReport.UnseenCrashes(paths));
                var f = ErrorReport.Save(paths, "x", "crash");
                ErrorReport.Save(paths, "y");                                   // a saved report is not a crash
                Assert.Equal(new[] { f }, ErrorReport.UnseenCrashes(paths));
                var seen = ErrorReport.MarkSeen(f);
                Assert.EndsWith("-seen.txt", seen);
                Assert.Empty(ErrorReport.UnseenCrashes(paths));
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Tools_point_at_this_copy_and_port()
        {
            var plan = new CommandLineTools.Plan
            {
                PhpExe = @"C:\L\engines\modern\php\php.exe", PhpIni = @"C:\L\config\php-modern.ini", ComposerPhar = @"C:\L\engines\modern\composer\composer.phar",
                ComposerHome = @"C:\L\run\composer", ClientExe = @"C:\L\engines\modern\mariadb\bin\mariadb.exe", DumpExe = @"C:\L\engines\modern\mariadb\bin\mariadb-dump.exe", DatabasePort = 3307,
            };
            var f = CommandLineTools.Files(plan);
            Assert.Equal(new[] { "composer.cmd", "mariadb-dump.cmd", "mariadb.cmd", "mysql.cmd", "mysqldump.cmd", "php.cmd", "README.txt" }.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), f.Keys);
            Assert.Contains(@"-c ""C:\L\config\php-modern.ini""", f["php.cmd"]);
            Assert.Contains("extension=mysqli", f["php.cmd"]);
            Assert.Contains("--port=3307 --user=root", f["mariadb.cmd"]);
            Assert.Equal(f["mariadb.cmd"], f["mysql.cmd"]);
            Assert.Contains(@"""C:\L\engines\modern\composer\composer.phar"" %*", f["composer.cmd"]);

            var none = CommandLineTools.Files(new CommandLineTools.Plan { DatabasePort = 3306 });
            Assert.Equal(new[] { "README.txt" }, none.Keys);
        }

        [Fact]
        public void Written_tools_run_php_and_drop_tools_that_went()
        {
            var dir = Temp();
            try
            {
                var php = FindPhp();
                if (php == null) return;                                         // no PHP on this PC to try it with
                var paths = new AppPaths(dir);
                CommandLineTools.Write(paths, new CommandLineTools.Plan { PhpExe = php, PhpIni = Path.Combine(dir, "none.ini"), ClientExe = php, DatabasePort = 3306 });
                var bin = CommandLineTools.Folder(paths);
                Assert.True(File.Exists(Path.Combine(bin, "mariadb.cmd")));

                var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c \"\"" + Path.Combine(bin, "php.cmd") + "\" -r \"echo extension_loaded('mysqli') ? 'yes' : 'no';\"\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
                var output = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
                Assert.Equal("yes", output.Trim());

                CommandLineTools.Write(paths, new CommandLineTools.Plan { PhpExe = php, PhpIni = Path.Combine(dir, "none.ini"), DatabasePort = 3306 });
                Assert.False(File.Exists(Path.Combine(bin, "mariadb.cmd")));
                Assert.True(File.Exists(Path.Combine(bin, "php.cmd")));
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string FindPhp()
        {
            for (var d = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory); d != null; d = d.Parent)
            {
                var p = Path.Combine(d.FullName, "ParityTests", "web", "bundle", "php85", "php.exe");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        [Fact]
        public void User_path_adds_first_and_removes_only_ours()
        {
            const string key = @"Software\LuminaDB-tests\Environment";
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(key))
                    k.SetValue("Path", @"%USERPROFILE%\bin;C:\xampp\php;C:\L\bin\;", RegistryValueKind.ExpandString);
                var up = new UserPath(key);
                Assert.True(up.Contains(@"C:\L\bin"));
                up.Set(@"C:\L\bin", true);
                Assert.Equal(@"C:\L\bin;%USERPROFILE%\bin;C:\xampp\php", Raw(key));
                up.Set(@"C:\L\bin", false);
                Assert.Equal(@"%USERPROFILE%\bin;C:\xampp\php", Raw(key));
                Assert.False(up.Contains(@"C:\L\bin"));
                using (var k = Registry.CurrentUser.OpenSubKey(key))
                    Assert.Equal(RegistryValueKind.ExpandString, k.GetValueKind("Path"));
            }
            finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\LuminaDB-tests", false); }
        }

        private static string Raw(string key)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(key))
                return (string)k.GetValue("Path", "", RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
    }
}
