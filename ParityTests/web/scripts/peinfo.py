"""Tiny PE import parser (no third-party deps). Run with: python -I peinfo.py <file-or-dir> [...]

For every .exe/.dll prints: machine, OS/subsystem version from the optional header,
normal + delay-load imported DLLs, and flags imports of functions that only exist on
Windows 8+ (so the binary cannot load on Windows 7).

Options:  --json out.json   write full results (incl. function names) as JSON
          --funcs           print every imported function too
"""
import json
import os
import struct
import sys

WIN8_PLUS = {
    # kernel32 / api-ms-win-core-* functions introduced in Windows 8 / 8.1 / 10
    "GetSystemTimePreciseAsFileTime", "WaitOnAddress", "WakeByAddressSingle", "WakeByAddressAll",
    "CreateFile2", "GetCurrentThreadStackLimits", "SetThreadInformation", "GetThreadInformation",
    "PrefetchVirtualMemory", "SetProcessMitigationPolicy", "GetProcessMitigationPolicy",
    "CreateFileMappingFromApp", "MapViewOfFileFromApp", "GetOverlappedResultEx",
    "VirtualAllocFromApp", "SetThreadDescription", "GetThreadDescription", "VirtualAlloc2",
    "MapViewOfFile3", "GetSystemCpuSetInformation", "SetThreadSelectedCpuSets",
    "CopyFile2", "GetPackageFamilyName", "GetCurrentPackageId",
    "GetCurrentPackageFullName", "DiscardVirtualMemory", "OfferVirtualMemory",
    "QueryUnbiasedInterruptTimePrecise", "GetFirmwareType",
    "RoInitialize", "RoGetActivationFactory", "WindowsCreateString",
    "GetProcessInformation", "SetProcessInformation", "GetFileInformationByName",
    "BCryptHash",  # Win10 only
    "ProcessPrng",  # bcryptprimitives, Win8+ (used by Rust/Go >=1.22?)
}
# api-set contracts that do not exist on Win7 at all (Win7 has no api-ms-win-core-synch-l1-2-0, etc.)
WIN8_PLUS_DLLS_PREFIX = (
    "api-ms-win-core-synch-l1-2-",
    "api-ms-win-core-file-l1-2-",
    "api-ms-win-core-winrt",
    "api-ms-win-core-path-l1-1-0",
    "api-ms-win-core-memory-l1-1-3",
)


def rva_to_off(sections, rva):
    for va, vsize, raw, rsize in sections:
        if va <= rva < va + max(vsize, rsize):
            return rva - va + raw
    return None


def read_cstr(data, off):
    end = data.find(b"\0", off)
    return data[off:end].decode("ascii", "replace")


def parse(path):
    with open(path, "rb") as f:
        data = f.read()
    if data[:2] != b"MZ":
        raise ValueError("not MZ")
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    if data[pe:pe + 4] != b"PE\0\0":
        raise ValueError("not PE")
    machine, nsec, _, _, _, optsize, chars = struct.unpack_from("<HHIIIHH", data, pe + 4)
    opt = pe + 24
    magic = struct.unpack_from("<H", data, opt)[0]
    is64 = magic == 0x20B
    # Major/Minor OS version @ +40, Image @ +44, Subsystem @ +48 (same in PE32 and PE32+)
    os_maj, os_min, img_maj, img_min, ss_maj, ss_min = struct.unpack_from("<HHHHHH", data, opt + 40)
    subsystem = struct.unpack_from("<H", data, opt + 68)[0]
    dd = opt + (112 if is64 else 96)
    ndd = struct.unpack_from("<I", data, dd - 4)[0]
    dirs = [struct.unpack_from("<II", data, dd + 8 * i) for i in range(min(ndd, 16))]
    sec = opt + optsize
    sections = []
    for i in range(nsec):
        s = sec + 40 * i
        vsize, va, rsize, raw = struct.unpack_from("<IIII", data, s + 8)
        sections.append((va, vsize, raw, rsize))
    ptr_size = 8 if is64 else 4
    ordflag = 1 << 63 if is64 else 1 << 31

    def thunks(rva):
        out = []
        off = rva_to_off(sections, rva)
        if off is None:
            return out
        while True:
            v = struct.unpack_from("<Q" if is64 else "<I", data, off)[0]
            if v == 0:
                break
            if v & ordflag:
                out.append("#%d" % (v & 0xFFFF))
            else:
                hoff = rva_to_off(sections, v & 0x7FFFFFFF)
                out.append(read_cstr(data, hoff + 2) if hoff is not None else "?")
            off += ptr_size
        return out

    imports = {}
    if len(dirs) > 1 and dirs[1][0]:
        off = rva_to_off(sections, dirs[1][0])
        while off is not None:
            oft, _, _, name_rva, ft = struct.unpack_from("<IIIII", data, off)
            if name_rva == 0:
                break
            name = read_cstr(data, rva_to_off(sections, name_rva))
            imports.setdefault(name, []).extend(thunks(oft or ft))
            off += 20
    delay = {}
    if len(dirs) > 13 and dirs[13][0]:
        off = rva_to_off(sections, dirs[13][0])
        while off is not None:
            attrs, name_rva, _, _, int_rva = struct.unpack_from("<IIIII", data, off)
            if name_rva == 0:
                break
            name = read_cstr(data, rva_to_off(sections, name_rva))
            delay.setdefault(name, []).extend(thunks(int_rva))
            off += 32
    return {
        "file": path,
        "machine": hex(machine),
        "pe32plus": is64,
        "os_version": "%d.%d" % (os_maj, os_min),
        "subsystem_version": "%d.%d" % (ss_maj, ss_min),
        "subsystem": subsystem,
        "imports": imports,
        "delay_imports": delay,
    }


def win8_flags(info):
    hits = []
    for table in ("imports", "delay_imports"):
        for dll, funcs in info[table].items():
            if dll.lower().startswith(WIN8_PLUS_DLLS_PREFIX):
                hits.append("%s(dll:%s)" % (table[0], dll))
            for fn in funcs:
                if fn in WIN8_PLUS:
                    hits.append("%s:%s!%s" % (table[0], dll, fn))
    return hits


def main(argv):
    out_json = None
    show_funcs = False
    paths = []
    it = iter(argv)
    for a in it:
        if a == "--json":
            out_json = next(it)
        elif a == "--funcs":
            show_funcs = True
        else:
            paths.append(a)
    files = []
    for p in paths:
        if os.path.isdir(p):
            for n in sorted(os.listdir(p)):
                if n.lower().endswith((".exe", ".dll", ".so")):
                    files.append(os.path.join(p, n))
        else:
            files.append(p)
    results = []
    for fp in files:
        try:
            info = parse(fp)
        except Exception as e:  # noqa
            print("%s: ERROR %s" % (fp, e))
            continue
        info["win8_plus_imports"] = win8_flags(info)
        results.append(info)
        print("== %s  machine=%s OSver=%s SubsysVer=%s" % (os.path.basename(fp), info["machine"],
                                                           info["os_version"], info["subsystem_version"]))
        print("   imports: " + ", ".join(sorted(info["imports"])))
        if info["delay_imports"]:
            print("   delay:   " + ", ".join(sorted(info["delay_imports"])))
        if info["win8_plus_imports"]:
            print("   WIN8+:   " + ", ".join(info["win8_plus_imports"]))
        if show_funcs:
            for dll, fns in info["imports"].items():
                print("     %s: %s" % (dll, " ".join(fns)))
    if out_json:
        with open(out_json, "w", encoding="utf-8") as f:
            json.dump(results, f, indent=1)


if __name__ == "__main__":
    main(sys.argv[1:])
