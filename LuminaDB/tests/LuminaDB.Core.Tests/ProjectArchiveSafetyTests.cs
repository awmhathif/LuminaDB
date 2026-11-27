using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LuminaDB.Core.Projects;
using Xunit;

namespace LuminaDB.Core.Tests
{
    /// <summary>Restoring a snapshot's code never leaves the project half done, and the share pack keeps secrets home.</summary>
    public class ProjectArchiveSafetyTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "lumina-archive-" + Guid.NewGuid().ToString("N"));
        private readonly string _project;
        private readonly string _zip;

        public ProjectArchiveSafetyTests()
        {
            _project = Path.Combine(_root, "shop");
            Directory.CreateDirectory(_project);
            _zip = Path.Combine(_root, "snap.zip");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        private void Write(string rel, string text)
        {
            var f = Path.Combine(_project, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(f));
            File.WriteAllText(f, text);
        }

        private string Read(string rel) => File.ReadAllText(Path.Combine(_project, rel));

        /// <summary>A snapshot of: index.php = "old", css/site.css, .env.</summary>
        private void MakeSnapshot()
        {
            var src = Path.Combine(_root, "src");
            Directory.CreateDirectory(Path.Combine(src, "css"));
            File.WriteAllText(Path.Combine(src, "index.php"), "old");
            File.WriteAllText(Path.Combine(src, "css", "site.css"), "body{}");
            File.WriteAllText(Path.Combine(src, ".env"), "DB_PASSWORD=x");
            ZipFile.CreateFromDirectory(src, _zip);
        }

        [Fact]
        public void Restore_replaces_the_code_and_keeps_git_and_vendor()
        {
            MakeSnapshot();
            Write("index.php", "new");
            Write("extra.php", "added later");
            Write(".git/HEAD", "ref: main");
            Write("vendor/autoload.php", "<?php");

            ProjectArchive.RestoreCode(_project, _zip);

            Assert.Equal("old", Read("index.php"));
            Assert.Equal("body{}", Read("css/site.css"));
            Assert.Equal("DB_PASSWORD=x", Read(".env"));   // snapshots stay on this PC: everything comes back
            Assert.False(File.Exists(Path.Combine(_project, "extra.php")));
            Assert.Equal("ref: main", Read(".git/HEAD"));
            Assert.Equal("<?php", Read("vendor/autoload.php"));   // not in the snapshot, so kept
            Assert.False(Directory.Exists(Path.Combine(_project, ProjectArchive.RestoreNew)));
            Assert.False(Directory.Exists(Path.Combine(_project, ProjectArchive.RestoreOld)));
        }

        [Fact]
        public void A_broken_snapshot_changes_nothing()
        {
            File.WriteAllText(_zip, "this is not a zip");
            Write("index.php", "current");
            Write("css/site.css", "current css");

            var ex = Assert.Throws<LuminaException>(() => ProjectArchive.RestoreCode(_project, _zip));
            Assert.Contains("Nothing in the project was changed", ex.Message);
            Assert.Equal("current", Read("index.php"));
            Assert.Equal("current css", Read("css/site.css"));
            Assert.Equal(new[] { "css", "index.php" }, Directory.EnumerateFileSystemEntries(_project).Select(Path.GetFileName).OrderBy(n => n));
        }

        [Fact]
        public void A_file_held_open_puts_everything_back()
        {
            MakeSnapshot();
            Write("a.php", "current a");
            Write("index.php", "current");
            Write("z.php", "current z");
            using (new FileStream(Path.Combine(_project, "z.php"), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var ex = Assert.Throws<LuminaException>(() => ProjectArchive.RestoreCode(_project, _zip));
                Assert.Contains("Nothing was changed", ex.Message);
            }
            Assert.Equal("current a", Read("a.php"));
            Assert.Equal("current", Read("index.php"));
            Assert.Equal("current z", Read("z.php"));
            Assert.False(File.Exists(Path.Combine(_project, ".env")));
            Assert.False(Directory.Exists(Path.Combine(_project, ProjectArchive.RestoreNew)));
            Assert.False(Directory.Exists(Path.Combine(_project, ProjectArchive.RestoreOld)));
        }

        [Fact]
        public void Files_left_by_a_cut_off_restore_are_never_deleted()
        {
            MakeSnapshot();
            Write("index.php", "current");
            Write(ProjectArchive.RestoreOld + "/precious.php", "only copy");

            var ex = Assert.Throws<LuminaException>(() => ProjectArchive.RestoreCode(_project, _zip));
            Assert.Contains(ProjectArchive.RestoreOld, ex.Message);
            Assert.Equal("only copy", Read(ProjectArchive.RestoreOld + "/precious.php"));
            Assert.Equal("current", Read("index.php"));
        }

        [Theory]
        [InlineData(".env", true)]
        [InlineData(".ENV", true)]
        [InlineData(".env.local", true)]
        [InlineData(".env.production", true)]
        [InlineData(".env.example", false)]
        [InlineData(".env.sample", false)]
        [InlineData("auth.json", true)]
        [InlineData(".htpasswd", true)]
        [InlineData("oauth-private.key", true)]
        [InlineData("server.pem", true)]
        [InlineData("config.php", false)]
        [InlineData("composer.json", false)]
        [InlineData("index.php", false)]
        public void Secret_files_are_recognised(string name, bool secret) => Assert.Equal(secret, ProjectArchive.IsSecretFile(name));

        [Fact]
        public void Readme_lists_what_was_left_out_and_how_to_run_without_it()
        {
            Write(".env.example", "DB_PASSWORD=");
            var p = new Project("shop", _project, null);
            var text = ProjectArchive.Readme(p, hasDb: true, includeVendor: false, secretsLeftOut: new[] { ".env", "storage/oauth-private.key" });
            Assert.Contains("Left out on purpose", text);
            Assert.Contains("shop/.env", text);
            Assert.Contains("shop/storage/oauth-private.key", text);
            Assert.Contains("copy .env.example to .env", text);
            Assert.DoesNotContain("Left out", ProjectArchive.Readme(p, true, false));
        }
    }
}
