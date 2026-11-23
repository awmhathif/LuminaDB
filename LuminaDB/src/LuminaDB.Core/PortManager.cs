using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LuminaDB.Core
{
    /// <summary>Answers "is something already listening on this TCP port?"</summary>
    public interface IPortProbe
    {
        bool IsInUse(int port);
    }

    /// <summary>
    /// A port counts as busy if anything listens on it on ANY address (127.0.0.1, 0.0.0.0, ::, ::1).
    /// The web test found that php -S on 127.0.0.1 starts silently when another program holds 0.0.0.0,
    /// so checking only 127.0.0.1 is not enough. As a second opinion we also try to bind exclusively.
    /// </summary>
    public sealed class SystemPortProbe : IPortProbe
    {
        public bool IsInUse(int port)
        {
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            if (listeners.Any(e => e.Port == port)) return true;
            return !CanBindExclusively(IPAddress.Loopback, port);
        }

        private static bool CanBindExclusively(IPAddress address, int port)
        {
            var s = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                s.ExclusiveAddressUse = true;
                s.Bind(new IPEndPoint(address, port));
                return true;
            }
            catch (SocketException) { return false; }
            finally { s.Close(); }
        }
    }

    /// <summary>
    /// Chooses ports so the user never sees "port already in use" (SPEC §4).
    /// Order: the port we used last time (keeps project URLs stable) → the preferred port → the next free ones.
    /// </summary>
    public sealed class PortManager
    {
        public const int SearchRange = 100;
        private readonly IPortProbe _probe;

        public PortManager(IPortProbe probe = null) { _probe = probe ?? new SystemPortProbe(); }

        public int Pick(int preferred, int? lastUsed = null, ICollection<int> alreadyTaken = null)
        {
            if (preferred < 1 || preferred > 65535) throw new ArgumentOutOfRangeException(nameof(preferred));
            bool Free(int p) => p >= 1 && p <= 65535 && (alreadyTaken == null || !alreadyTaken.Contains(p)) && !_probe.IsInUse(p);

            if (lastUsed.HasValue && Free(lastUsed.Value)) return lastUsed.Value;
            for (int p = preferred; p < preferred + SearchRange && p <= 65535; p++)
                if (Free(p)) return p;

            throw new LuminaException(ErrorKind.NoFreePort,
                $"Every port from {preferred} to {Math.Min(65535, preferred + SearchRange - 1)} is taken by other programs. " +
                "Close some programs or restart the PC, then try again.");
        }

        public bool IsInUse(int port) => _probe.IsInUse(port);
    }
}
