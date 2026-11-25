<#
  Reads the import tables (normal and delay-load) of every .exe and .dll in an engine set and reports:
    - DLLs that are neither in the set nor part of Windows (a missing runtime DLL would stop the program on a clean PC);
    - functions that Windows 7 does not have (a program using one won't start there). Used for the legacy and x86 sets.
  Exit code 1 when anything is found.
#>
param([Parameter(Mandatory = $true)][string]$SetDir, [switch]$Win7)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
public static class PeImports
{
    // Returns "dll!function" (or "dll!#ordinal") for every import, normal and delay-loaded.
    public static List<string> Read(string path)
    {
        var result = new List<string>();
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return result;
        int pe = BitConverter.ToInt32(b, 0x3C);
        if (pe <= 0 || pe + 24 > b.Length || BitConverter.ToUInt32(b, pe) != 0x4550) return result;
        int sections = BitConverter.ToUInt16(b, pe + 6);
        int optSize = BitConverter.ToUInt16(b, pe + 20);
        int opt = pe + 24;
        bool pe32plus = BitConverter.ToUInt16(b, opt) == 0x20B;
        int dirs = opt + (pe32plus ? 112 : 96);
        int secTable = opt + optSize;
        Func<uint, int> off = rva =>
        {
            for (int i = 0; i < sections; i++)
            {
                int s = secTable + i * 40;
                uint va = BitConverter.ToUInt32(b, s + 12), vs = BitConverter.ToUInt32(b, s + 8), raw = BitConverter.ToUInt32(b, s + 20), rs = BitConverter.ToUInt32(b, s + 16);
                uint size = Math.Max(vs, rs);
                if (rva >= va && rva < va + size) return (int)(rva - va + raw);
            }
            return -1;
        };
        Func<int, string> str = o =>
        {
            if (o < 0) return "?";
            int e = o; while (e < b.Length && b[e] != 0) e++;
            return Encoding.ASCII.GetString(b, o, e - o);
        };
        Action<int, string> thunks = (o, dll) =>
        {
            if (o < 0) return;
            for (; ; o += pe32plus ? 8 : 4)
            {
                ulong t = pe32plus ? BitConverter.ToUInt64(b, o) : BitConverter.ToUInt32(b, o);
                if (t == 0) break;
                bool ord = pe32plus ? (t & 0x8000000000000000UL) != 0 : (t & 0x80000000UL) != 0;
                if (ord) result.Add(dll + "!#" + (t & 0xFFFF));
                else result.Add(dll + "!" + str(off((uint)t) + 2));
            }
        };
        // Normal imports (directory 1)
        uint impRva = BitConverter.ToUInt32(b, dirs + 8);
        if (impRva != 0)
            for (int d = off(impRva); d >= 0 && d + 20 <= b.Length; d += 20)
            {
                uint oft = BitConverter.ToUInt32(b, d), nameRva = BitConverter.ToUInt32(b, d + 12), ft = BitConverter.ToUInt32(b, d + 16);
                if (nameRva == 0) break;
                thunks(off(oft != 0 ? oft : ft), str(off(nameRva)).ToLowerInvariant());
            }
        // Delay-load imports (directory 13)
        uint dRva = BitConverter.ToUInt32(b, dirs + 13 * 8);
        if (dRva != 0)
            for (int d = off(dRva); d >= 0 && d + 32 <= b.Length; d += 32)
            {
                uint attrs = BitConverter.ToUInt32(b, d), nameRva = BitConverter.ToUInt32(b, d + 4), intRva = BitConverter.ToUInt32(b, d + 16);
                if (nameRva == 0) break;
                thunks(off(intRva), str(off(nameRva)).ToLowerInvariant());
            }
        return result;
    }
}
'@

# Functions added after Windows 7 that builds sometimes import directly.
$notOnWin7 = @(
    'GetSystemTimePreciseAsFileTime', 'CreateFile2', 'CopyFile2', 'GetCurrentThreadStackLimits', 'SetThreadInformation', 'GetThreadInformation',
    'GetOverlappedResultEx', 'WaitOnAddress', 'WakeByAddressSingle', 'WakeByAddressAll', 'PrefetchVirtualMemory', 'GetProcessMitigationPolicy',
    'SetProcessMitigationPolicy', 'GetSystemCpuSetInformation', 'SetThreadDescription', 'GetThreadDescription', 'MapViewOfFile3', 'VirtualAlloc2',
    'GetPackageFamilyName', 'GetCurrentPackageId', 'GetCurrentPackageFullName', 'CreateFileMappingFromApp', 'MapViewOfFileFromApp', 'DiscardVirtualMemory',
    'OfferVirtualMemory', 'ReclaimVirtualMemory', 'GetFirmwareType', 'GetProcessInformation', 'SetProcessInformation',
    'CreateRandomAccessStreamOnFile', 'RoInitialize', 'RoGetActivationFactory', 'WindowsCreateString', 'PathCchCombine', 'PathCchCanonicalize',
    'PathCchAppend', 'PathCchRemoveFileSpec', 'GetDpiForWindow', 'GetDpiForMonitor', 'SetProcessDpiAwareness', 'SetThreadDpiAwarenessContext',
    'BCryptHash'
)
# DLLs that only exist from Windows 8 on (the api-ms-win-crt-* set comes with the Universal CRT update on Windows 7).
$win8Dlls = '^(api-ms-win-core-(synch-l1-2|path-l1-1|processthreads-l1-1-[23]|memory-l1-1-[3-9]|sysinfo-l1-2|file-l1-2-[2-9]|libraryloader-l1-2|winrt|wow64-l1-1-1)|api-ms-win-security-base-l1-2|shcore\.dll|combase\.dll|d3d12\.dll|dcomp\.dll|bcryptprimitives\.dll|mfplat)'

$systemDir = "$env:SystemRoot\System32"
$setFiles = @{}
Get-ChildItem $SetDir -Recurse -File -Include *.exe, *.dll | ForEach-Object { $setFiles[$_.Name.ToLowerInvariant()] = $true }

$missing = @{}; $old7 = @{}
foreach ($f in Get-ChildItem $SetDir -Recurse -File -Include *.exe, *.dll) {
    foreach ($imp in [PeImports]::Read($f.FullName)) {
        $dll, $fn = $imp.Split('!', 2)
        if ($Win7) {
            if ($notOnWin7 -contains $fn) { $old7["$($f.Name): $dll!$fn"] = $true }
            if ($dll -match $win8Dlls -and -not $setFiles.ContainsKey($dll)) { $old7["$($f.Name): $dll"] = $true }
        }
        if ($setFiles.ContainsKey($dll)) { continue }
        if ($dll -like 'api-ms-win-*' -or $dll -like 'ext-ms-win-*') { continue }
        if ($dll -eq 'ucrtbase.dll') { continue }
        if (Test-Path (Join-Path $systemDir $dll)) {
            # Runtime DLLs that exist on this PC but not on a clean one must ship with the set.
            if ($dll -match '^(vcruntime|msvcp|concrt|vcomp|mfc)\d') { $missing["$dll (needed by $($f.Name))"] = $true }
            continue
        }
        $missing["$dll (needed by $($f.Name))"] = $true
    }
}
$bad = $false
if ($missing.Count) { $bad = $true; Write-Host "Not in the set and not part of Windows:"; $missing.Keys | Sort-Object | ForEach-Object { "  $_" } }
if ($old7.Count) { $bad = $true; Write-Host "Not available on Windows 7:"; $old7.Keys | Sort-Object | ForEach-Object { "  $_" } }
if (-not $bad) { Write-Host "OK: every import is in the set or part of Windows$(if ($Win7) { ' 7' })." }
exit ([int]$bad)
