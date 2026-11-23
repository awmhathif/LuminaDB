"""Start a server process detached, wait until its TCP port accepts connections, report startup time.

usage: python -I launch.py <name> <port> <log> [--env K=V ...] -- <exe> [args...]
Prints: STARTED name pid=<pid> port=<port> ready_ms=<ms>   (or FAILED ... with log tail)
Appends "<name> <pid>" to ../results/pids.txt so stop.py can kill exactly what we started.
Also exports port_free(port) used by the port-conflict test.
"""
import os
import socket
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
PIDS = os.path.join(HERE, "..", "results", "pids.txt")


def port_free(port, host="127.0.0.1", strict=True):
    """True if the port is free for us. strict=True also probes 0.0.0.0, because on Windows another
    program listening on 0.0.0.0:<port> does NOT stop us binding 127.0.0.1:<port> (tested), which
    silently splits traffic between two servers."""
    if strict and host != "0.0.0.0" and not port_free(port, "0.0.0.0", strict=False):
        return False
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    # SO_EXCLUSIVEADDRUSE so Windows does not let us "share" a port another process holds
    s.setsockopt(socket.SOL_SOCKET, getattr(socket, "SO_EXCLUSIVEADDRUSE", socket.SO_REUSEADDR), 1)
    try:
        s.bind((host, port))
        return True
    except OSError:
        return False
    finally:
        s.close()


def accepting(port, host="127.0.0.1"):
    try:
        with socket.create_connection((host, port), timeout=0.2):
            return True
    except OSError:
        return False


def main():
    a = sys.argv[1:]
    name, port, log = a[0], int(a[1]), a[2]
    rest = a[3:]
    env = dict(os.environ)
    while rest and rest[0] == "--env":
        k, v = rest[1].split("=", 1)
        env[k] = v
        rest = rest[2:]
    assert rest[0] == "--"
    cmd = rest[1:]
    if not port_free(port):
        print("FAILED %s: port %d already in use (pre-check)" % (name, port))
        sys.exit(2)
    flags = 0x00000200 | 0x08000000  # CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW
    t0 = time.perf_counter()
    lf = open(log, "ab")
    p = subprocess.Popen(cmd, stdout=lf, stderr=subprocess.STDOUT, stdin=subprocess.DEVNULL,
                         creationflags=flags, env=env, cwd=os.path.dirname(cmd[0]) or None)
    with open(PIDS, "a") as f:
        f.write("%s %d\n" % (name, p.pid))
    while time.perf_counter() - t0 < 20:
        if p.poll() is not None:
            lf.close()
            print("FAILED %s exit=%s log tail:\n%s" % (name, p.returncode,
                  open(log, "rb").read()[-1500:].decode("utf-8", "replace")))
            sys.exit(1)
        if accepting(port):
            print("STARTED %s pid=%d port=%d ready_ms=%.0f" % (name, p.pid, port, (time.perf_counter() - t0) * 1000))
            return
        time.sleep(0.01)
    print("FAILED %s: not accepting on %d after 20s (pid %d left running; run stop.py)" % (name, port, p.pid))
    sys.exit(1)


if __name__ == "__main__":
    main()
