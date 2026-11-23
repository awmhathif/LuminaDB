using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace LuminaDB.Core
{
    /// <summary>
    /// The real Windows version. <see cref="Environment.OSVersion"/> reports 6.2 on Windows 8+
    /// for apps without a compatibility manifest, so we read the registry instead.
    /// </summary>
    public sealed class WindowsInfo
    {
        public WindowsInfo(Version version, int build, bool is64BitOs, string productName)
        {
            Version = version ?? throw new ArgumentNullException(nameof(version));
            Build = build;
            Is64BitOs = is64BitOs;
            ProductName = productName ?? "Windows";
        }

        /// <summary>6.1 = Windows 7, 6.2 = 8, 6.3 = 8.1, 10.0 = Windows 10 and 11.</summary>
        public Version Version { get; }
        public int Build { get; }
        public bool Is64BitOs { get; }
        public string ProductName { get; }

        public bool IsWindows10OrNewer => Version.Major >= 10;
        public bool IsWindows11 => Version.Major >= 10 && Build >= 22000;
        public bool IsBelowWindows7 => Version < new Version(6, 1);

        public string FriendlyName
        {
            get
            {
                string name;
                if (IsWindows11) name = "Windows 11";
                else if (Version.Major >= 10) name = "Windows 10";
                else if (Version >= new Version(6, 3)) name = "Windows 8.1";
                else if (Version >= new Version(6, 2)) name = "Windows 8";
                else if (Version >= new Version(6, 1)) name = "Windows 7";
                else name = ProductName;
                return name + (Is64BitOs ? " (64-bit)" : " (32-bit)");
            }
        }

        public static WindowsInfo Current()
        {
            const string key = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
            using (var k = Registry.LocalMachine.OpenSubKey(key))
            {
                Version v = null;
                // Windows 10+: CurrentMajorVersionNumber / CurrentMinorVersionNumber (DWORDs)
                if (k?.GetValue("CurrentMajorVersionNumber") is int major)
                {
                    int minor = k.GetValue("CurrentMinorVersionNumber") is int m ? m : 0;
                    v = new Version(major, minor);
                }
                // Windows 7/8/8.1: "6.1" / "6.2" / "6.3"
                if (v == null && Version.TryParse(k?.GetValue("CurrentVersion") as string ?? "", out var parsed)) v = parsed;
                if (v == null) v = Environment.OSVersion.Version;

                int build = int.TryParse(k?.GetValue("CurrentBuildNumber") as string ?? k?.GetValue("CurrentBuild") as string, out var b) ? b : Environment.OSVersion.Version.Build;
                return new WindowsInfo(new Version(v.Major, v.Minor), build, Environment.Is64BitOperatingSystem, k?.GetValue("ProductName") as string);
            }
        }
    }

    /// <summary>Physical memory, used to size MariaDB's buffer pool on old PCs.</summary>
    public static class SystemMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryStatusEx
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

        /// <summary>Total physical RAM in bytes, or 0 if it can't be read.</summary>
        public static ulong TotalBytes()
        {
            var s = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)) };
            return GlobalMemoryStatusEx(ref s) ? s.ullTotalPhys : 0;
        }
    }
}
