"""Concurrency probes with curl.

usage: python -I concurrency.py <base-url> [n=20]
 1) n parallel GETs of api.php (DB hit) -> wall time, per-request min/avg/max, distinct PHP pids, failures
 2) head-of-line: start slow.php?s=2, then 100ms later a fast extcheck.php; report fast request latency
"""
import json
import subprocess
import sys
import threading
import time


def one(url, out, i):
    t0 = time.perf_counter()
    p = subprocess.run(["curl", "-sS", "-m", "30", "-w", "\n%{http_code}", url], capture_output=True)
    dt = time.perf_counter() - t0
    txt = p.stdout.decode("utf-8", "replace")
    body, _, code = txt.rpartition("\n")
    pid = None
    try:
        pid = json.loads(body).get("pid")
    except Exception:  # noqa
        pass
    out[i] = (code, dt, pid, p.stderr.decode().strip()[:200])


def main():
    base = sys.argv[1].rstrip("/")
    n = int(sys.argv[2]) if len(sys.argv) > 2 else 20
    out = [None] * n
    th = [threading.Thread(target=one, args=(base + "/api.php?driver=%s" % ("pdo" if i % 2 else "mysqli"), out, i)) for i in range(n)]
    t0 = time.perf_counter()
    for t in th:
        t.start()
    for t in th:
        t.join()
    wall = time.perf_counter() - t0
    codes = [o[0] for o in out]
    dts = [o[1] for o in out]
    pids = {o[2] for o in out if o[2]}
    bad = [o for o in out if o[0] != "200"]
    print("PARALLEL n=%d wall=%.3fs per-req min=%.3f avg=%.3f max=%.3f ok=%d fail=%d distinct_php_pids=%d"
          % (n, wall, min(dts), sum(dts) / n, max(dts), codes.count("200"), len(bad), len(pids)))
    for b in bad[:5]:
        print("   failure:", b)
    holder = [None, None]
    ts = threading.Thread(target=one, args=(base + "/slow.php?s=2", holder, 0))
    ts.start()
    time.sleep(0.1)
    one(base + "/extcheck.php", holder, 1)
    ts.join()
    print("HEAD-OF-LINE: slow(2s)=%.3fs, fast request issued 0.1s later took %.3fs (code %s) -> %s"
          % (holder[0][1], holder[1][1], holder[1][0], "BLOCKED" if holder[1][1] > 1.5 else "not blocked"))


if __name__ == "__main__":
    main()
