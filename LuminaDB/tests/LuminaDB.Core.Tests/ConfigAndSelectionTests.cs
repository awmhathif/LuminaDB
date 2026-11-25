using System;
using System.IO;
using System.Linq;
using LuminaDB.Core;
using Xunit;

namespace LuminaDB.Core.Tests
{
    public class MariaDbConfigWriterTests
    {
        private static MariaDbConfig Sample(bool collations) => new MariaDbConfig
        {
            DataDir = @"C:\Lumina DB\data\mariadb-modern",
            Port = 3307,
            SecureFilePriv = @"C:\Lumina DB\exports",
            PluginDir = @"C:\Lumina DB\engines\modern\mariadb\lib\plugin",
            TempDir = @"C:\Lumina DB\tmp",
            TotalRamBytes = 8UL << 30,
            SupportsCharacterSetCollations = collations,
        };

        private static string Section(string ini, string name)
        {
            var start = ini.IndexOf("[" + name + "]", StringComparison.Ordinal);
            Assert.True(start >= 0, "missing section " + name);
            var next = ini.IndexOf("\n[", start + 1, StringComparison.Ordinal);
            return next < 0 ? ini.Substring(start) : ini.Substring(start, next - start);
        }

        [Fact]
        public void Contains_verified_server_defaults()
        {
            var s = Section(MariaDbConfigWriter.Render(Sample(true)), "mysqld");
            foreach (var line in new[]
            {
                "datadir=C:/Lumina DB/data/mariadb-modern", "port=3307", "bind-address=127.0.0.1",
                "character-set-server=utf8mb4", "collation-server=utf8mb4_unicode_ci",
                "character_set_collations=utf8mb4=utf8mb4_unicode_ci,utf8mb3=utf8mb3_unicode_ci",
                "event_scheduler=ON", "local_infile=ON", "secure_file_priv=\"C:/Lumina DB/exports\"",
                "max_allowed_packet=256M", "innodb_buffer_pool_size=128M", "performance_schema=OFF",
                "lower_case_table_names=1",
            })
                Assert.Contains(line + Environment.NewLine, s);
            Assert.DoesNotContain("skip-name-resolve", s);
        }

        [Fact]
        public void Omits_character_set_collations_for_10_4()
        {
            Assert.DoesNotContain("character_set_collations", MariaDbConfigWriter.Render(Sample(false)));
        }

        [Fact]
        public void Local_infile_is_under_mysql_not_client()
        {
            var ini = MariaDbConfigWriter.Render(Sample(true));
            Assert.DoesNotContain("local-infile", Section(ini, "client"));
            Assert.Contains("local-infile=1", Section(ini, "mysql"));
        }

        [Fact]
        public void Paths_use_forward_slashes()
        {
            var ini = MariaDbConfigWriter.Render(Sample(true));
            Assert.DoesNotContain("\\", ini);
        }

        [Theory]
        [InlineData(0UL, 128)]
        [InlineData(1UL << 30, 128)]   // 1 GB  -> 128 MB
        [InlineData(2UL << 30, 128)]   // 2 GB  -> 128 MB
        [InlineData(512UL << 20, 64)]  // 512 MB -> 64 MB
        [InlineData(256UL << 20, 64)]  // tiny  -> floor 64 MB
        [InlineData(32UL << 30, 128)]  // big   -> cap 128 MB
        public void Buffer_pool_is_sized_by_ram(ulong ram, int expectedMb)
        {
            Assert.Equal(expectedMb, MariaDbConfigWriter.BufferPoolMb(ram));
        }

        [Fact]
        public void Rejects_missing_paths_and_bad_ports()
        {
            var c = Sample(true); c.DataDir = "";
            Assert.Throws<ArgumentException>(() => MariaDbConfigWriter.Render(c));
            c = Sample(true); c.Port = 0;
            Assert.Throws<ArgumentOutOfRangeException>(() => MariaDbConfigWriter.Render(c));
        }

        [Fact]
        public void Write_replaces_existing_file_without_bom()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lumina-cfg-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = Path.Combine(dir, "my.ini");
                MariaDbConfigWriter.Write(path, Sample(true));
                var c = Sample(true); c.Port = 3399;
                MariaDbConfigWriter.Write(path, c);
                var bytes = File.ReadAllBytes(path);
                Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB);
                Assert.Contains("port=3399", File.ReadAllText(path));
                Assert.False(File.Exists(path + ".tmp"));
            }
            finally { try { Directory.Delete(dir, true); } catch { } }
        }
    }

    public class EngineSetSelectorTests
    {
        [Theory]
        [InlineData(10, 0, 22631, true, EngineSetKind.Modern)]   // Windows 11
        [InlineData(10, 0, 19045, true, EngineSetKind.Modern)]   // Windows 10
        [InlineData(6, 3, 9600, true, EngineSetKind.Legacy)]     // Windows 8.1
        [InlineData(6, 1, 7601, true, EngineSetKind.Legacy)]     // Windows 7 SP1
        [InlineData(6, 1, 7601, false, EngineSetKind.X86)]       // Windows 7 32-bit
        [InlineData(10, 0, 19045, false, EngineSetKind.X86)]     // Windows 10 32-bit
        public void Picks_engine_set_for_windows(int major, int minor, int build, bool x64, EngineSetKind expected)
        {
            Assert.Equal(expected, EngineSetSelector.Choose(new WindowsInfo(new Version(major, minor), build, x64, "Windows")));
        }

        private static WindowsInfo Win(int major, int minor, int build, bool x64) => new WindowsInfo(new Version(major, minor), build, x64, "Windows");

        [Fact]
        public void Uses_the_best_installed_set()
        {
            var win10 = Win(10, 0, 19045, true);
            Assert.Equal(EngineSetKind.Modern, EngineSetSelector.ChooseInstalled(win10, k => true));
            // A Windows 7 PC upgraded to 10 keeps its Legacy set (and the data in data\mariadb-legacy).
            Assert.Equal(EngineSetKind.Legacy, EngineSetSelector.ChooseInstalled(win10, k => k == EngineSetKind.Legacy));
            // The 32-bit installer on 64-bit Windows 10.
            Assert.Equal(EngineSetKind.X86, EngineSetSelector.ChooseInstalled(win10, k => k == EngineSetKind.X86));
            // Windows 7 64-bit can't run the Modern set even when it's there.
            Assert.Equal(EngineSetKind.Legacy, EngineSetSelector.ChooseInstalled(Win(6, 1, 7601, true), k => k == EngineSetKind.Modern));
            // 32-bit Windows runs only the x86 set; with nothing usable it names the set it needs.
            Assert.Equal(EngineSetKind.X86, EngineSetSelector.ChooseInstalled(Win(10, 0, 19045, false), k => k != EngineSetKind.X86));
        }

        [Fact]
        public void Keeps_the_set_that_has_the_data()
        {
            var win10 = Win(10, 0, 19045, true);
            // Upgraded from Windows 7, then the Windows 10 installer added Modern: the databases are in the Legacy set.
            Assert.Equal(EngineSetKind.Legacy, EngineSetSelector.ChooseInstalled(win10, k => k != EngineSetKind.X86, k => k == EngineSetKind.Legacy));
            // Both have data: the best one wins.
            Assert.Equal(EngineSetKind.Modern, EngineSetSelector.ChooseInstalled(win10, k => true, k => true));
            // No data yet (first start): the best installed set.
            Assert.Equal(EngineSetKind.Modern, EngineSetSelector.ChooseInstalled(win10, k => true, k => false));
            // Data left by a set that's no longer installed doesn't count.
            Assert.Equal(EngineSetKind.Modern, EngineSetSelector.ChooseInstalled(win10, k => k == EngineSetKind.Modern, k => k == EngineSetKind.Legacy));
        }

        [Fact]
        public void Sets_that_run_on_each_pc()
        {
            Assert.Equal(new[] { EngineSetKind.Modern, EngineSetKind.Legacy, EngineSetKind.X86 }, EngineSetSelector.RunsOn(Win(10, 0, 22631, true)));
            Assert.Equal(new[] { EngineSetKind.Legacy, EngineSetKind.X86 }, EngineSetSelector.RunsOn(Win(6, 1, 7601, true)));
            Assert.Equal(new[] { EngineSetKind.X86 }, EngineSetSelector.RunsOn(Win(6, 1, 7601, false)));
        }

        [Fact]
        public void Parses_the_developer_override()
        {
            Assert.Equal(EngineSetKind.Legacy, EngineSetSelector.Parse(" Legacy "));
            Assert.Equal(EngineSetKind.X86, EngineSetSelector.Parse("x86"));
            Assert.Null(EngineSetSelector.Parse(""));
            Assert.Null(EngineSetSelector.Parse("arm64"));
        }

        [Fact]
        public void Refuses_vista()
        {
            var ex = Assert.Throws<LuminaException>(() => EngineSetSelector.Choose(new WindowsInfo(new Version(6, 0), 6002, true, "Windows Vista")));
            Assert.Equal(ErrorKind.UnsupportedWindows, ex.Kind);
        }

        [Fact]
        public void Friendly_names()
        {
            Assert.Equal("Windows 11 (64-bit)", new WindowsInfo(new Version(10, 0), 22631, true, null).FriendlyName);
            Assert.Equal("Windows 7 (32-bit)", new WindowsInfo(new Version(6, 1), 7601, false, null).FriendlyName);
        }

        [Fact]
        public void Current_windows_is_detected_correctly()
        {
            var w = WindowsInfo.Current();
            Assert.True(w.Version >= new Version(6, 1));
            Assert.True(w.Build > 0);
        }

        [Fact]
        public void Missing_engine_folder_gives_friendly_error()
        {
            var ex = Assert.Throws<LuminaException>(() => EngineSet.FromMariaDbFolder(EngineSetKind.Modern, @"C:\does-not-exist-lumina"));
            Assert.Equal(ErrorKind.EngineMissing, ex.Kind);
        }

        [Fact]
        public void Per_set_data_folders_are_separate()
        {
            var p = new AppPaths(@"C:\L");
            var all = Enum.GetValues(typeof(EngineSetKind)).Cast<EngineSetKind>().Select(p.MariaDbData).ToList();
            Assert.Equal(all.Count, all.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Equal(@"C:\L\data\mariadb-legacy", p.MariaDbData(EngineSetKind.Legacy));
        }
    }
}
