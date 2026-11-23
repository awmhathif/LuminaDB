using System;
using System.Net;
using System.Net.Sockets;
using LuminaDB.Core;
using Xunit;

namespace LuminaDB.Core.Tests
{
    public class ShellSupportTests
    {
        [Theory]
        [InlineData("college", null)]
        [InlineData("hndit_assignment_2", null)]
        [InlineData("_tmp", null)]
        public void Good_database_names_pass(string name, string expected) => Assert.Equal(expected, DatabaseCatalog.ValidateName(name));

        [Theory]
        [InlineData("", "Give the database a name.")]
        [InlineData("my shop", "Use _ instead of spaces, e.g. my_shop.")]
        [InlineData("2026project", "Start with a letter, e.g. db_2026project.")]
        [InlineData("shop-db", "Use only English letters")]
        [InlineData("ශ්‍රී", "Use only English letters")]
        [InlineData("MySQL", "reserved")]
        [InlineData(" shop", "Remove the spaces")]
        public void Bad_database_names_are_explained(string name, string expectedPart) =>
            Assert.Contains(expectedPart, DatabaseCatalog.ValidateName(name));

        [Fact]
        public void Existing_name_is_caught_case_insensitively() =>
            Assert.Contains("already exists", DatabaseCatalog.ValidateName("College", new[] { "college" }));

        [Fact]
        public void Too_long_name_is_caught() => Assert.Contains("64", DatabaseCatalog.ValidateName(new string('a', 65)));

        [Theory]
        [InlineData(512L, "512 B")]
        [InlineData(98304L, "96 KB")]
        [InlineData(5L * 1024 * 1024, "5.0 MB")]
        [InlineData(3L * 1024 * 1024 * 1024, "3.00 GB")]
        public void Sizes_are_human(long bytes, string expected) => Assert.Equal(expected, DatabaseSummary.FormatSize(bytes));

        [Fact]
        public void When_is_human()
        {
            var now = new DateTime(2026, 11, 23, 15, 0, 0);
            Assert.Equal("today 11:02", DatabaseSummary.FormatWhen(new DateTime(2026, 11, 23, 11, 2, 0), now));
            Assert.Equal("yesterday", DatabaseSummary.FormatWhen(new DateTime(2026, 11, 22, 9, 0, 0), now));
            Assert.Equal("18 Nov", DatabaseSummary.FormatWhen(new DateTime(2026, 11, 18), now));
            Assert.Equal("3 Jan 2025", DatabaseSummary.FormatWhen(new DateTime(2025, 1, 3), now));
            Assert.Equal("—", DatabaseSummary.FormatWhen(null, now));
        }

        [Fact]
        public void Port_owner_names_this_process()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            try
            {
                var owner = PortOwner.Find(((IPEndPoint)l.LocalEndpoint).Port);
                Assert.NotNull(owner);
                Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, owner.ProcessId);
                Assert.Contains("(PID ", owner.ToString());
            }
            finally { l.Stop(); }
        }

        [Fact]
        public void Port_owner_ipv6()
        {
            if (!Socket.OSSupportsIPv6) return;
            var l = new TcpListener(IPAddress.IPv6Loopback, 0);
            l.Start();
            try { Assert.Equal(System.Diagnostics.Process.GetCurrentProcess().Id, PortOwner.Find(((IPEndPoint)l.LocalEndpoint).Port).ProcessId); }
            finally { l.Stop(); }
        }

        private sealed class Probe : IPortProbe
        {
            private readonly bool _busy;
            public Probe(bool busy) { _busy = busy; }
            public bool IsInUse(int port) => _busy;
        }

        [Fact]
        public void Port_check_standard_port_is_ok() =>
            Assert.Equal(CheckSeverity.Ok, StartupChecks.CheckPort(ServerState.Running, 3306, 3306, new Probe(false)).Severity);

        [Fact]
        public void Port_check_warns_only_while_preferred_port_is_really_taken()
        {
            var taken = StartupChecks.CheckPort(ServerState.Running, 3307, 3306, new Probe(true));
            Assert.Equal(CheckSeverity.Warn, taken.Severity);
            Assert.Contains("3307", taken.Text);

            var kept = StartupChecks.CheckPort(ServerState.Running, 3307, 3306, new Probe(false));
            Assert.Equal(CheckSeverity.Ok, kept.Severity);
            Assert.Contains("kept from last time", kept.Title);
            Assert.DoesNotContain("holds it", kept.Text);
        }

        [Fact]
        public void Port_check_when_stopped_is_neutral() =>
            Assert.Equal(CheckSeverity.Ok, StartupChecks.CheckPort(ServerState.Stopped, 0, 3306, new Probe(true)).Severity);

        [Theory]
        [InlineData(@"C:\LuminaDB", 0)]
        [InlineData(@"C:\Users\Nimal\OneDrive\LuminaDB", 1)]
        [InlineData(@"C:\Users\නිමල්\LuminaDB", 1)]
        [InlineData(@"\\server\share\LuminaDB", 1)]
        public void Path_problems(string root, int expected) => Assert.Equal(expected, StartupChecks.PathProblems(root).Count);
    }
}
