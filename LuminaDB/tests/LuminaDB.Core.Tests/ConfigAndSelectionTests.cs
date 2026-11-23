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
                "max_allowed_packet=256M", "innodb_buffer_pool_size=256M", "performance_schema=ON",
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
        [InlineData(2UL << 30, 256)]   // 2 GB  -> 256 MB
        [InlineData(256UL << 20, 64)]  // tiny  -> floor 64 MB
        [InlineData(32UL << 30, 256)]  // big   -> cap 256 MB
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
