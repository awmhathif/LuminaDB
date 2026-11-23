"""Check that every function a set of binaries imports from the VC++ runtime DLLs
(vcruntime140.dll, vcruntime140_1.dll, msvcp140*.dll, concrt140.dll) is actually exported
by a candidate copy of those DLLs (e.g. the copies shipped in MariaDB's bin\\).
Also prints the linker version of each binary (tells which MSVC toolset built it).

usage: python -I crtcheck.py <runtime-dir> <binary-or-dir> [...]
"""
import os
import struct
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import peinfo  # noqa: E402

CRT = ("vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "msvcp140_2.dll",
       "msvcp140_atomic_wait.dll", "msvcp140_codecvt_ids.dll", "concrt140.dll")


def exports(path):
    with open(path, "rb") as f:
        data = f.read()
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec, optsize = struct.unpack_from("<H", data, pe + 6)[0], struct.unpack_from("<H", data, pe + 20)[0]
    opt = pe + 24
    is64 = struct.unpack_from("<H", data, opt)[0] == 0x20B
    dd = opt + (112 if is64 else 96)
    exp_rva = struct.unpack_from("<I", data, dd)[0]
    sections = []
    for i in range(nsec):
        s = opt + optsize + 40 * i
        vsize, va, rsize, raw = struct.unpack_from("<IIII", data, s + 8)
        sections.append((va, vsize, raw, rsize))
    off = peinfo.rva_to_off(sections, exp_rva)
    if not exp_rva or off is None:
        return set()
    nnames, _, names_rva = struct.unpack_from("<III", data, off + 24)
    noff = peinfo.rva_to_off(sections, names_rva)
    out = set()
    for i in range(nnames):
        r = struct.unpack_from("<I", data, noff + 4 * i)[0]
        out.add(peinfo.read_cstr(data, peinfo.rva_to_off(sections, r)))
    return out


def linker(path):
    with open(path, "rb") as f:
        data = f.read(4096)
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    return "%d.%d" % (data[pe + 26], data[pe + 27])


def main():
    rtdir = sys.argv[1]
    files = []
    for p in sys.argv[2:]:
        if os.path.isdir(p):
            files += [os.path.join(p, n) for n in sorted(os.listdir(p)) if n.lower().endswith((".exe", ".dll", ".so"))]
        else:
            files.append(p)
    cache = {}
    bad = 0
    for fp in files:
        info = peinfo.parse(fp)
        used = []
        for dll, funcs in list(info["imports"].items()) + list(info["delay_imports"].items()):
            if dll.lower() not in CRT:
                continue
            used.append(dll)
            cand = os.path.join(rtdir, dll)
            if not os.path.exists(cand):
                print("%-28s linker=%s  %s: NOT PRESENT in %s" % (os.path.basename(fp), linker(fp), dll, rtdir))
                bad += 1
                continue
            if cand not in cache:
                cache[cand] = exports(cand)
            missing = [fn for fn in funcs if not fn.startswith("#") and fn not in cache[cand]]
            if missing:
                bad += 1
                print("%-28s linker=%s  %s MISSING %d: %s" % (os.path.basename(fp), linker(fp), dll, len(missing), " ".join(missing)))
        print("%-28s linker=%s  CRT deps: %s" % (os.path.basename(fp), linker(fp), ", ".join(used) or "-"))
    print("RESULT: %s" % ("ALL CRT IMPORTS RESOLVED" if bad == 0 else "%d PROBLEMS" % bad))


if __name__ == "__main__":
    main()
