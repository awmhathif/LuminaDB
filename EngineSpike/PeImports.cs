using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace EngineSpike
{
    /// <summary>
    /// Minimal PE reader: lists the DLLs and functions an .exe/.dll imports (normal + delay-load)
    /// and the minimum Windows version written in its header. Used to predict whether
    /// MariaDB binaries can start on Windows 7 without actually having a Windows 7 PC.
    /// </summary>
    public sealed class PeInfo
    {
        public string Path;
        public bool Is64;
        public Version SubsystemVersion;
        public Version OsVersion;
        public readonly List<KeyValuePair<string, string>> Imports = new List<KeyValuePair<string, string>>();      // dll, function
        public readonly List<KeyValuePair<string, string>> DelayImports = new List<KeyValuePair<string, string>>(); // dll, function

        public static PeInfo Read(string path)
        {
            var bytes = File.ReadAllBytes(path);
            var pe = new PeInfo { Path = path };
            if (U16(bytes, 0) != 0x5A4D) throw new InvalidDataException("Not a PE file (no MZ)");
            int peOff = (int)U32(bytes, 0x3C);
            if (U32(bytes, peOff) != 0x00004550) throw new InvalidDataException("Not a PE file (no PE signature)");
            int fileHeader = peOff + 4;
            int numSections = U16(bytes, fileHeader + 2);
            int optSize = U16(bytes, fileHeader + 16);
            int opt = fileHeader + 20;
            ushort magic = U16(bytes, opt);
            pe.Is64 = magic == 0x20B;
            pe.OsVersion = new Version(U16(bytes, opt + 40), U16(bytes, opt + 42));
            pe.SubsystemVersion = new Version(U16(bytes, opt + 48), U16(bytes, opt + 50));
            int dataDir = opt + (pe.Is64 ? 112 : 96);
            int sectionTable = opt + optSize;

            var sections = new List<int[]>(); // va, vsize, rawPtr, rawSize
            for (int i = 0; i < numSections; i++)
            {
                int s = sectionTable + i * 40;
                sections.Add(new[] { (int)U32(bytes, s + 12), (int)U32(bytes, s + 8), (int)U32(bytes, s + 20), (int)U32(bytes, s + 16) });
            }
            int Rva(long rva)
            {
                foreach (var sec in sections)
                {
                    int size = Math.Max(sec[1], sec[3]);
                    if (rva >= sec[0] && rva < sec[0] + size) return (int)(rva - sec[0] + sec[2]);
                }
                return -1;
            }

            // Normal imports (data directory 1)
            uint impRva = U32(bytes, dataDir + 8);
            if (impRva != 0)
            {
                int d = Rva(impRva);
                while (d > 0 && d + 20 <= bytes.Length)
                {
                    uint oft = U32(bytes, d), nameRva = U32(bytes, d + 12), ft = U32(bytes, d + 16);
                    if (nameRva == 0 && ft == 0) break;
                    string dll = CStr(bytes, Rva(nameRva));
                    ReadThunks(bytes, Rva(oft != 0 ? oft : ft), pe.Is64, Rva, dll, pe.Imports);
                    d += 20;
                }
            }

            // Delay-load imports (data directory 13)
            uint delayRva = U32(bytes, dataDir + 13 * 8);
            if (delayRva != 0)
            {
                int d = Rva(delayRva);
                while (d > 0 && d + 32 <= bytes.Length)
                {
                    uint attrs = U32(bytes, d), nameRva = U32(bytes, d + 4), intRva = U32(bytes, d + 16);
                    if (nameRva == 0) break;
                    // attrs bit 0 set = RVA based (all modern linkers)
                    string dll = CStr(bytes, Rva(nameRva));
                    if ((attrs & 1) == 1 && intRva != 0) ReadThunks(bytes, Rva(intRva), pe.Is64, Rva, dll, pe.DelayImports);
                    d += 32;
                }
            }
            return pe;
        }

        static void ReadThunks(byte[] b, int t, bool is64, Func<long, int> rva, string dll, List<KeyValuePair<string, string>> into)
        {
            if (t < 0) return;
            while (t + (is64 ? 8 : 4) <= b.Length)
            {
                ulong v = is64 ? BitConverter.ToUInt64(b, t) : U32(b, t);
                if (v == 0) break;
                bool ordinal = is64 ? (v & 0x8000000000000000UL) != 0 : (v & 0x80000000UL) != 0;
                into.Add(new KeyValuePair<string, string>(dll, ordinal ? "#" + (v & 0xFFFF) : CStr(b, rva((long)(v & 0x7FFFFFFF)) + 2)));
                t += is64 ? 8 : 4;
            }
        }

        static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);
        static uint U32(byte[] b, int o) => BitConverter.ToUInt32(b, o);
        static string CStr(byte[] b, int o)
        {
            if (o < 0) return "?";
            int e = o;
            while (e < b.Length && b[e] != 0) e++;
            return Encoding.ASCII.GetString(b, o, e - o);
        }
    }

    public static class Win7Check
    {
        /// <summary>Kernel functions that do NOT exist on Windows 7 SP1, with the first Windows that has them.</summary>
        public static readonly Dictionary<string, string> NewerThanWin7 = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "GetSystemTimePreciseAsFileTime", "Windows 8" },
            { "CreateFile2", "Windows 8" },
            { "CopyFile2", "Windows 8" },
            { "GetCurrentThreadStackLimits", "Windows 8" },
            { "PrefetchVirtualMemory", "Windows 8" },
            { "SetThreadInformation", "Windows 8" },
            { "GetThreadInformation", "Windows 8" },
            { "WaitOnAddress", "Windows 8" },
            { "WakeByAddressSingle", "Windows 8" },
            { "WakeByAddressAll", "Windows 8" },
            { "GetOverlappedResultEx", "Windows 8" },
            { "CreateFileMappingFromApp", "Windows 8" },
            { "MapViewOfFileFromApp", "Windows 8" },
            { "GetProcessMitigationPolicy", "Windows 8" },
            { "SetProcessMitigationPolicy", "Windows 8" },
            { "GetFirmwareType", "Windows 8" },
            { "GetPackageFamilyName", "Windows 8" },
            { "GetCurrentPackageId", "Windows 8" },
            { "DiscardVirtualMemory", "Windows 8.1" },
            { "OfferVirtualMemory", "Windows 8.1" },
            { "ReclaimVirtualMemory", "Windows 8.1" },
            { "SetThreadDescription", "Windows 10 1607" },
            { "GetThreadDescription", "Windows 10 1607" },
            { "VirtualAlloc2", "Windows 10 1803" },
            { "MapViewOfFile3", "Windows 10 1803" },
            { "GetSystemCpuSetInformation", "Windows 10" },
            { "SetThreadSelectedCpuSets", "Windows 10" },
            { "GetSystemTimeAdjustmentPrecise", "Windows 10" },
        };

        /// <summary>DLLs that only exist on Windows 8+ (when not shipped next to the exe).</summary>
        public static readonly HashSet<string> DllsNewerThanWin7 = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "api-ms-win-core-synch-l1-2-0.dll", "api-ms-win-core-file-l1-2-0.dll", "api-ms-win-core-file-l2-1-0.dll",
            "api-ms-win-core-path-l1-1-0.dll", "api-ms-win-core-winrt-l1-1-0.dll", "api-ms-win-core-realtime-l1-1-0.dll",
            "api-ms-win-core-memory-l1-1-2.dll", "api-ms-win-core-memory-l1-1-3.dll", "api-ms-win-core-sysinfo-l1-2-0.dll",
            "api-ms-win-core-processthreads-l1-1-2.dll", "api-ms-win-core-processthreads-l1-1-3.dll",
            "shcore.dll", "bcp47mrm.dll",
        };
    }
}
