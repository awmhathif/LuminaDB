using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LuminaDB.Core;
using MySqlConnector;
using Xunit;
using Xunit.Abstractions;

namespace LuminaDB.Core.Tests
{
    public class BackupRetentionTests
    {
        private static BackupInfo B(string db, DateTime t, BackupKind k = BackupKind.Auto) => new BackupInfo { Database = db, Taken = t, Kind = k };

        [Fact]
        public void Keeps_the_newest_20_plus_one_a_day_for_a_week()
        {
            var now = new DateTime(2026, 11, 24, 18, 0, 0);
            var list = new List<BackupInfo>();
            // 30 today, every 10 minutes
            for (int i = 0; i < 30; i++) list.Add(B("shop", now.AddMinutes(-10 * i)));
            // three on each of the 10 days before
            for (int d = 1; d <= 10; d++)
                for (int h = 0; h < 3; h++) list.Add(B("shop", now.Date.AddDays(-d).AddHours(9 + h)));

            var gone = BackupService.ToPrune(list, now);
            var kept = list.Except(gone).ToList();

            Assert.Equal(list.OrderByDescending(b => b.Taken).Take(20), kept.OrderByDescending(b => b.Taken).Take(20));
            for (int d = 1; d < BackupService.KeepDays; d++)   // the newest of each of the last days stays
                Assert.Contains(kept, b => b.Taken == now.Date.AddDays(-d).AddHours(11));
            Assert.DoesNotContain(kept, b => b.Taken.Date <= now.Date.AddDays(-BackupService.KeepDays));
            Assert.Equal(20 + (BackupService.KeepDays - 1), kept.Count);
        }

        [Fact]
        public void Manual_backups_always_stay_and_databases_are_counted_apart()
        {
            var now = new DateTime(2026, 11, 24, 18, 0, 0);
            var list = new List<BackupInfo> { B("a", now.AddDays(-60), BackupKind.Manual) };
            for (int i = 0; i < 25; i++) { list.Add(B("a", now.AddMinutes(-i))); list.Add(B("b", now.AddMinutes(-i))); }
            var gone = BackupService.ToPrune(list, now);
            Assert.DoesNotContain(gone, b => b.IsManual);
            Assert.Equal(5, gone.Count(b => b.Database == "a"));
            Assert.Equal(5, gone.Count(b => b.Database == "b"));
        }

        [Fact]
        public void Folder_names_are_safe() => Assert.Equal("we_rd_db", BackupService.FolderName("we:rd?db"));
    }

    public class DoctorParsingTests
    {
        [Fact]
        public void Healthy_output_has_no_problems()
        {
            var (bad, warn) = Doctor.ParseCheckOutput("mysql.column_stats                                 OK\nmysql.db                                           OK\n");
            Assert.Empty(bad);
            Assert.Empty(warn);
        }

        [Fact]
        public void Errors_and_warnings_name_their_table_and_notes_do_not()
        {
            const string output = @"mysql.db
warning  : 1 client is using or hasn't closed the table properly
status   : OK
mysql.user
error    : Table './mysql/user' is marked as crashed and should be repaired
mysql.general_log
note     : The storage engine for the table doesn't support check
mysql.proc                                         OK";
            var (bad, warn) = Doctor.ParseCheckOutput(output);
            Assert.Equal(new[] { "mysql.user" }, bad);
            Assert.Equal(new[] { "mysql.db" }, warn);
        }

        [Fact]
        public void Ago_reads_naturally()
        {
            Assert.Equal("just now", Doctor.Ago(DateTime.Now));
            Assert.Equal("5 minutes ago", Doctor.Ago(DateTime.Now.AddMinutes(-5.2)));
            Assert.Equal("2 hours ago", Doctor.Ago(DateTime.Now.AddHours(-2.1)));
        }
    }

    [Collection("RealMariaDb")]
    public class BackupDoctorIntegrationTests
    {
        private readonly ITestOutputHelper _out;
        public BackupDoctorIntegrationTests(ITestOutputHelper output) { _out = output; }

        private static async Task Exec(MariaDbService s, string sql)
        {
            using (var c = await s.OpenConnectionAsync())
            using (var cmd = new MySqlCommand(sql, c)) await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<object> Scalar(MariaDbService s, string sql)
        {
            using (var c = await s.OpenConnectionAsync())
            using (var cmd = new MySqlCommand(sql, c)) return await cmd.ExecuteScalarAsync();
        }

        [Theory]
        [InlineData("mariadb-11.8.10-winx64", EngineSetKind.Modern)]
        [InlineData("mariadb-10.4.34-winx64", EngineSetKind.Legacy)]
        public async Task Backups_follow_changes_and_restore_puts_the_database_back_exactly(string folder, EngineSetKind kind)
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig(folder, kind, _out))
            {
                if (rig == null) return;
                var s = rig.NewService(3436);
                await s.StartAsync();
                var backups = new BackupService(rig.Paths, s);

                await Exec(s, "DROP DATABASE IF EXISTS test");   // the engine's sample database
                Assert.Empty(await backups.ChangedDatabasesAsync());   // no user databases yet
                await Exec(s, "CREATE DATABASE shop CHARACTER SET utf8mb4; CREATE TABLE shop.items (id INT PRIMARY KEY, name VARCHAR(40)); INSERT INTO shop.items VALUES (1,'à·à·à¶±à·Šà¶­à·’'),(2,'à®¤à®®à®¿à®´à¯')");
                await Exec(s, "CREATE DATABASE empty_one");
                Assert.Equal(new[] { "empty_one", "shop" }, await backups.ChangedDatabasesAsync());

                var first = await backups.BackUpChangedAsync("first", BackupKind.Auto);
                Assert.Equal(2, first.Count);
                Assert.Empty(await backups.ChangedDatabasesAsync());   // nothing changed since
                Assert.Empty(await backups.BackUpChangedAsync("again", BackupKind.Auto));

                // a change in shop only
                await Exec(s, "INSERT INTO shop.items VALUES (3,'later'); CREATE TABLE shop.added_later (x INT)");
                Assert.Equal(new[] { "shop" }, await backups.ChangedDatabasesAsync());

                // Fill shop's automatic places so the first backup is the oldest one kept: the safety copy taken by the
                // restore must not tidy it away (it used to, and the restore then had nothing to read).
                for (int i = 1; i < BackupService.KeepLatest; i++) await backups.BackUpAsync(new[] { "shop" }, "filler " + i, BackupKind.Auto);
                var shopFirst = first.Single(b => b.Database == "shop");
                Assert.Contains(backups.List("shop"), b => b.Id == shopFirst.Id);

                // restore the first backup: row 3 and the later table go, the Unicode stays, a safety copy is made first
                var safety = await backups.RestoreAsync(shopFirst);
                Assert.Contains(backups.List("shop"), b => b.Id == shopFirst.Id);   // still there to restore again
                Assert.NotNull(safety);
                Assert.Equal(BackupKind.BeforeRestore, safety.Kind);
                Assert.Equal(2L, Convert.ToInt64(await Scalar(s, "SELECT COUNT(*) FROM shop.items")));
                Assert.Equal("à·à·à¶±à·Šà¶­à·’", await Scalar(s, "SELECT name FROM shop.items WHERE id = 1"));
                Assert.Equal(0L, Convert.ToInt64(await Scalar(s, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA='shop' AND TABLE_NAME='added_later'")));
                Assert.Empty(await backups.ChangedDatabasesAsync());   // restored = a backed-up state

                // and the safety copy brings row 3 back
                await backups.RestoreAsync(backups.List("shop").Single(b => b.Id == safety.Id));
                Assert.Equal(3L, Convert.ToInt64(await Scalar(s, "SELECT COUNT(*) FROM shop.items")));

                // listing, newest first, and delete
                var all = backups.List("shop");
                Assert.True(all.Count >= 3);
                Assert.True(all.SequenceEqual(all.OrderByDescending(b => b.Taken)));
                backups.Delete(all.Last());
                Assert.Equal(all.Count - 1, backups.List("shop").Count);

                // a dropped database can still be brought back from its backup
                await Exec(s, "DROP DATABASE shop");
                Assert.Null(await backups.RestoreAsync(backups.List("shop").Single(b => b.Id == safety.Id)));   // the 3-row copy
                Assert.Equal(3L, Convert.ToInt64(await Scalar(s, "SELECT COUNT(*) FROM shop.items")));
            }
        }

        [Fact]
        public async Task Doctor_checks_a_healthy_server_and_its_fixes_work()
        {
            using (var rig = MariaDbServiceIntegrationTests.NewRig("mariadb-11.8.10-winx64", EngineSetKind.Modern, _out))
            {
                if (rig == null) return;
                var s = rig.NewService(3446);
                await s.StartAsync();
                var backups = new BackupService(rig.Paths, s);
                var doctor = new Doctor(new DoctorContext { Paths = rig.Paths, Engine = rig.Engine, Server = s, Backups = backups, PreferredPort = 3446 });

                await Exec(s, "CREATE DATABASE notes; CREATE TABLE notes.n (id INT)");
                var results = await doctor.RunAsync();
                foreach (var r in results) _out.WriteLine($"{r.Severity} {r.Id}: {r.Title} â€” {r.Text}");
                Assert.Equal(CheckSeverity.Ok, results.Single(r => r.Id == "server").Severity);
                Assert.Equal(CheckSeverity.Ok, results.Single(r => r.Id == "engine").Severity);
                Assert.Equal(CheckSeverity.Ok, results.Single(r => r.Id == "systables").Severity);
                Assert.Equal(CheckSeverity.Ok, results.Single(r => r.Id == "clock").Severity);
                Assert.Equal(CheckSeverity.Ok, results.Single(r => r.Id == "leftovers").Severity);
                Assert.Equal(CheckSeverity.Ok, results.Single(r => r.Id == "config").Severity);
                var b = results.Single(r => r.Id == "backups");
                Assert.Equal(CheckSeverity.Warn, b.Severity);
                Assert.Equal("backup-now", b.FixId);

                Assert.StartsWith("Backed up notes", await doctor.FixAsync("backup-now"));
                Assert.Equal(CheckSeverity.Ok, (await doctor.RunAsync()).Single(r => r.Id == "backups").Severity);
                Assert.True(backups.Latest("notes").IsManual);

                // the repair runs (backing up first) and leaves every system table passing
                int before = backups.List().Count;
                Assert.StartsWith("Repaired", await doctor.FixAsync("repair-system-tables"));
                Assert.True(backups.List().Count > before);
            }
        }

        [Fact]
        public void Engine_file_problems_name_missing_and_emptied_files()
        {
            var engines = Path.Combine(Path.GetTempPath(), "Lumina Engine " + Guid.NewGuid().ToString("N").Substring(0, 6));
            var bin = Path.Combine(engines, "bin");
            Directory.CreateDirectory(bin);
            try
            {
                File.WriteAllText(Path.Combine(bin, "mariadbd.exe"), "x");
                File.WriteAllText(Path.Combine(bin, "mariadb.exe"), "x");
                File.WriteAllText(Path.Combine(bin, "mariadb-install-db.exe"), "x");
                File.WriteAllText(Path.Combine(bin, "mariadb-dump.exe"), "");   // quarantined to 0 bytes
                var set = EngineSet.FromMariaDbFolder(EngineSetKind.Modern, engines);
                var p = Doctor.EngineFileProblems(set);
                Assert.Contains("mariadb-check.exe is missing", p);
                Assert.Contains("mariadb-dump.exe is empty", p);
                Assert.Equal(2, p.Count);
            }
            finally { Directory.Delete(engines, true); }
        }
    }
}
