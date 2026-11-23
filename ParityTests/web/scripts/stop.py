"""Kill processes WE started (recorded in ../results/pids.txt by launch.py), optionally only one name.

usage: python -I stop.py [name]
Uses taskkill /T (tree: php-cgi children of httpd/mod_fcgid etc.) /F on the recorded PID only.
"""
import os
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PIDS = os.path.join(HERE, "..", "results", "pids.txt")


def main():
    only = sys.argv[1] if len(sys.argv) > 1 else None
    if not os.path.exists(PIDS):
        print("no pids file")
        return
    keep = []
    for line in open(PIDS):
        parts = line.split()
        if len(parts) != 2:
            continue
        name, pid = parts
        if only and name != only:
            keep.append(line)
            continue
        r = subprocess.run(["taskkill", "/T", "/F", "/PID", pid], capture_output=True, text=True)
        print("%s pid=%s: %s" % (name, pid, (r.stdout + r.stderr).strip().replace("\n", " | ") or "ok"))
    with open(PIDS, "w") as f:
        f.writelines(keep)


if __name__ == "__main__":
    main()
