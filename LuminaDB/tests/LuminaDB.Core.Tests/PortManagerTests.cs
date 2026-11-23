using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using LuminaDB.Core;
using Xunit;

namespace LuminaDB.Core.Tests
{
    public class PortManagerTests
    {
        private sealed class FakeProbe : IPortProbe
        {
            private readonly HashSet<int> _busy;
            public FakeProbe(params int[] busy) { _busy = new HashSet<int>(busy); }
            public bool IsInUse(int port) => _busy.Contains(port);
        }

        [Fact]
        public void Prefers_last_used_port_when_free()
        {
            var pm = new PortManager(new FakeProbe());
            Assert.Equal(3310, pm.Pick(3306, lastUsed: 3310));
        }

        [Fact]
        public void Falls_back_to_preferred_when_last_used_is_busy()
        {
            var pm = new PortManager(new FakeProbe(3310));
            Assert.Equal(3306, pm.Pick(3306, lastUsed: 3310));
        }

        [Fact]
        public void Skips_busy_ports()
        {
            var pm = new PortManager(new FakeProbe(3306, 3307));
            Assert.Equal(3308, pm.Pick(3306));
        }

        [Fact]
        public void Skips_ports_already_taken_by_our_other_services()
        {
            var pm = new PortManager(new FakeProbe(3306));
            Assert.Equal(3308, pm.Pick(3306, alreadyTaken: new[] { 3307 }));
        }

        [Fact]
        public void Throws_friendly_error_when_range_exhausted()
        {
            var busy = new int[PortManager.SearchRange];
            for (int i = 0; i < busy.Length; i++) busy[i] = 3306 + i;
            var ex = Assert.Throws<LuminaException>(() => new PortManager(new FakeProbe(busy)).Pick(3306));
            Assert.Equal(ErrorKind.NoFreePort, ex.Kind);
        }

        [Fact]
        public void Real_probe_sees_listener_on_loopback()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            try
            {
                int port = ((IPEndPoint)l.LocalEndpoint).Port;
                Assert.True(new SystemPortProbe().IsInUse(port));
                Assert.NotEqual(port, new PortManager().Pick(port));
            }
            finally { l.Stop(); }
        }

        [Fact]
        public void Real_probe_sees_listener_on_any_address()
        {
            // php -S and other tools bind 0.0.0.0; we must treat that port as busy too.
            var l = new TcpListener(IPAddress.Any, 0);
            l.Start();
            try { Assert.True(new SystemPortProbe().IsInUse(((IPEndPoint)l.LocalEndpoint).Port)); }
            finally { l.Stop(); }
        }

        [Fact]
        public void Real_probe_sees_ipv6_listener()
        {
            if (!Socket.OSSupportsIPv6) return;
            var l = new TcpListener(IPAddress.IPv6Loopback, 0);
            l.Start();
            try { Assert.True(new SystemPortProbe().IsInUse(((IPEndPoint)l.LocalEndpoint).Port)); }
            finally { l.Stop(); }
        }
    }
}
