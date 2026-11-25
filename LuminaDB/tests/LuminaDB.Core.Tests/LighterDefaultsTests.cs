using System.IO;
using LuminaDB.Core.Sql;
using LuminaDB.Core.Web;
using Xunit;

namespace LuminaDB.Core.Tests
{
    public class LighterDefaultsTests
    {
        [Theory]
        [InlineData(4UL << 30, 2)]    // 4 GB
        [InlineData(8UL << 30, 2)]    // 8 GB (shows as 7.9)
        [InlineData(16UL << 30, 4)]   // 16 GB
        [InlineData(0UL, 4)]          // unknown
        public void Php_workers_follow_ram(ulong ram, int expected) => Assert.Equal(expected, WebStackService.PhpChildrenFor(ram));

        [Fact]
        public void Notebook_keeps_ten_thousand_rows_and_can_show_more()
        {
            Assert.Equal(10_000, NotebookSession.DefaultRowCap);
            Assert.True(NotebookSession.MaxRowCap > NotebookSession.DefaultRowCap);
        }

        [Fact]
        public void Root_password_cache_follows_a_new_save()
        {
            var dir = Path.Combine(Path.GetTempPath(), "lumina-cred-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                var store = new RootCredentialStore(Path.Combine(dir, "root.secret"));
                Assert.Equal("", store.Load());
                store.Save("first");
                Assert.Equal("first", store.Load());
                Assert.Equal("first", store.Load());          // from memory
                store.Save("second");
                Assert.Equal("second", store.Load());
                store.Save("");
                Assert.Equal("", store.Load());
                // another copy of the store (another part of the app) changing the file is seen too
                store.Save("third");
                Assert.Equal("third", store.Load());
                new RootCredentialStore(Path.Combine(dir, "root.secret")).Save("fourth");
                File.SetLastWriteTimeUtc(Path.Combine(dir, "root.secret"), System.DateTime.UtcNow.AddSeconds(5));
                Assert.Equal("fourth", store.Load());
            }
            finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
        }
    }
}
