using System;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;

namespace LuminaDB.Core
{
    /// <summary>
    /// Names the program listening on a TCP port ("Skype.exe (PID 4120)"), so the user is told who
    /// took 3306 instead of a bare "port in use". Uses GetExtendedTcpTable (Windows XP SP2+, so fine on Windows 7).
    /// </summary>
    public static class PortOwner
    {
        public sealed class Owner
        {
            public int ProcessId { get; set; }
            public string ProcessName { get; set; }
            public override string ToString() =>
                string.IsNullOrEmpty(ProcessName) ? $"another program (PID {ProcessId})" : $"{ProcessName}.exe (PID {ProcessId})";
        }

        /// <summary>The process listening on <paramref name="port"/>, or null if none/unknown.</summary>
        public static Owner Find(int port)
        {
            int pid = FindPid(port, AfInet) ?? FindPid(port, AfInet6) ?? 0;
            if (pid <= 0) return null;
            string name = null;
            try { using (var p = Process.GetProcessById(pid)) name = p.ProcessName; }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return new Owner { ProcessId = pid, ProcessName = name };
        }

        /// <summary>"Skype.exe (PID 4120)" or "another program".</summary>
        public static string Describe(int port) => Find(port)?.ToString() ?? "another program";

        private const int AfInet = 2, AfInet6 = 23;
        private const int TcpTableOwnerPidListener = 3;
        private const int ErrorInsufficientBuffer = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

        private static int? FindPid(int port, int af)
        {
            int size = 0;
            uint rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TcpTableOwnerPidListener, 0);
            if (rc != ErrorInsufficientBuffer && rc != 0) return null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    rc = GetExtendedTcpTable(buf, ref size, false, af, TcpTableOwnerPidListener, 0);
                    if (rc == ErrorInsufficientBuffer) continue;
                    if (rc != 0) return null;
                    int count = Marshal.ReadInt32(buf);
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid (6 DWORDs)
                    // MIB_TCP6ROW_OWNER_PID: localAddr[16], localScope, localPort, remoteAddr[16], remoteScope, remotePort, state, pid
                    int rowSize = af == AfInet ? 24 : 56;
                    int portOffset = af == AfInet ? 8 : 20;
                    int pidOffset = af == AfInet ? 20 : 52;
                    for (int i = 0; i < count; i++)
                    {
                        var row = buf + 4 + i * rowSize;
                        int raw = Marshal.ReadInt32(row, portOffset);
                        int p = IPAddress.NetworkToHostOrder((short)(raw & 0xFFFF)) & 0xFFFF;
                        if (p == port) return Marshal.ReadInt32(row, pidOffset);
                    }
                    return null;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return null;
        }
    }
}
