using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LuminaDB.Core.Web
{
    /// <summary>
    /// A Windows Job Object with KILL_ON_JOB_CLOSE: every process put in it (and the children it starts, such as the
    /// php-cgi workers) dies when LuminaDB exits, even after a crash (SPEC §4). MariaDB is never put in a job.
    /// </summary>
    public sealed class JobObject : IDisposable
    {
        private IntPtr _handle;

        public JobObject()
        {
            _handle = CreateJobObject(IntPtr.Zero, null);
            if (_handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, ptr, (uint)size))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(ptr); }
        }

        /// <summary>Puts a process in the job. Returns false if Windows refuses (e.g. Windows 7 with the process already in another job).</summary>
        public bool Add(int processId)
        {
            if (_handle == IntPtr.Zero) return false;
            try
            {
                using (var p = Process.GetProcessById(processId))
                    return AssignProcessToJobObject(_handle, p.Handle);
            }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (Win32Exception) { return false; }
        }

        /// <summary>
        /// Adds a process and any children it already started. Children started after this inherit the job by themselves;
        /// this closes the few milliseconds between Process.Start and the assignment (php-cgi forks its workers at once).
        /// </summary>
        public bool AddWithChildren(int processId)
        {
            bool ok = Add(processId);
            foreach (var child in ChildrenOf(processId)) Add(child);
            return ok;
        }

        /// <summary>Direct children of a process (Toolhelp snapshot).</summary>
        public static System.Collections.Generic.List<int> ChildrenOf(int parentId)
        {
            var result = new System.Collections.Generic.List<int>();
            IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return result;
            try
            {
                var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                if (!Process32First(snap, ref e)) return result;
                do { if (e.th32ParentProcessID == (uint)parentId) result.Add((int)e.th32ProcessID); }
                while (Process32Next(snap, ref e));
            }
            finally { CloseHandle(snap); }
            return result;
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }

        // ------------------------------------------------------------------ Win32

        private const int JobObjectExtendedLimitInformation = 9;
        private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private const uint TH32CS_SNAPPROCESS = 0x2;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
        private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
        private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
    }
}
