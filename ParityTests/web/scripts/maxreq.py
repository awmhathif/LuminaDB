"""Send N sequential requests and report the first failing request number + status codes histogram.
usage: python -I maxreq.py <url> <n>"""
import subprocess, sys, collections
url, n = sys.argv[1], int(sys.argv[2])
hist = collections.Counter(); first_fail = None
for i in range(1, n + 1):
    p = subprocess.run(["curl", "-s", "-o", "NUL", "-m", "10", "-w", "%{http_code}", url], capture_output=True)
    c = p.stdout.decode()
    hist[c] += 1
    if c != "200" and first_fail is None:
        first_fail = i
print("requests=%d codes=%s first_non_200_at=%s" % (n, dict(hist), first_fail))
