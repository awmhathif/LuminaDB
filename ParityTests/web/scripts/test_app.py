"""Drive the sample student app with curl and assert results.

usage: python -I test_app.py <base-url> [--label NAME] [--pretty]
  base-url e.g. http://127.0.0.1:8010   (app at docroot)  or http://127.0.0.1:8030/app
  --pretty  also test the /students and /students/N pretty-URL routes
Writes nothing except a temp dir under ../tmp/curl; prints PASS/FAIL lines and timing.
"""
import json
import os
import hashlib
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
TMP = os.path.join(HERE, "..", "tmp", "curl")
os.makedirs(TMP, exist_ok=True)

TEXT_SI = "ශ්‍රී ලංකා ශිෂ්‍යයා"      # Sinhala incl. ZWJ
TEXT_TA = "தமிழ் மாணவர்"             # Tamil
TEXT_EMOJI = "🎓📚👩🏽‍💻"              # 4-byte UTF-8 + ZWJ sequence
results = []
timings = []


def curl(args, label=None):
    cmd = ["curl", "-sS", "-o", os.path.join(TMP, "body"), "-w", "%{http_code} %{time_total}"] + args
    t0 = time.perf_counter()
    p = subprocess.run(cmd, capture_output=True)
    wall = time.perf_counter() - t0
    out = p.stdout.decode().strip().split()
    code = int(out[0]) if out else 0
    t = float(out[1]) if len(out) > 1 else wall
    with open(os.path.join(TMP, "body"), "rb") as f:
        body = f.read()
    if label:
        timings.append((label, t))
    return code, body, p.stderr.decode().strip()


_n = [0]


def tf(text):
    """Write text (UTF-8) to a temp file so curl never sees non-ASCII on its command line
    (Windows curl.exe would convert argv to the ANSI code page)."""
    _n[0] += 1
    fp = os.path.join(TMP, "v%d.txt" % _n[0])
    with open(fp, "w", encoding="utf-8", newline="") as fh:
        fh.write(text)
    return fp


def check(name, cond, detail=""):
    results.append((name, bool(cond)))
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else "  -> " + detail[:600]))


def js(body):
    try:
        return json.loads(body.decode("utf-8"))
    except Exception:  # noqa
        return {"_raw": body[:400].decode("utf-8", "replace")}


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    base = sys.argv[1].rstrip("/")
    pretty = "--pretty" in sys.argv
    jar = os.path.join(TMP, "jar.txt")
    if os.path.exists(jar):
        os.remove(jar)

    code, body, err = curl([base + "/extcheck.php"], "GET extcheck.php")
    d = js(body)
    check("extcheck 200", code == 200, "%s %s %s" % (code, body[:300], err))
    check("all 10 extensions loaded", d.get("loaded") and all(d["loaded"].values()), str(d.get("loaded")))
    check("intl si_LK currency formats", "1,234.50" in str(d.get("functional", {}).get("intl", "")), str(d.get("functional")))
    print("   server:", d.get("server"), "| php", d.get("php"), d.get("sapi"))

    ids = {}
    for drv in ("mysqli", "pdo"):
        name = "%s %s %s (%s)" % (TEXT_SI, TEXT_TA, TEXT_EMOJI, drv)
        code, body, err = curl(["-X", "POST", "--data-urlencode", "name@" + tf(name),
                                "--data-urlencode", "course=HNDIT ICT", base + "/api.php?driver=" + drv],
                               "POST api create (%s)" % drv)
        d = js(body)
        check("%s create 201" % drv, code == 201 and d.get("ok"), "%s %s" % (code, d))
        ids[drv] = d.get("id")
        # read back with the OTHER driver to prove the bytes on disk are identical
        other = "pdo" if drv == "mysqli" else "mysqli"
        code, body, err = curl([base + "/api.php?driver=%s&id=%s" % (other, ids[drv])], "GET api by id (%s)" % other)
        d = js(body)
        got = d.get("student", {}).get("name")
        check("%s->%s utf8mb4 round-trip exact" % (drv, other), got == name,
              "expected %r got %r" % (name, got))
    # JSON body update (PUT) via PDO, then mysqli
    for drv in ("pdo", "mysqli"):
        new = "යාවත්කාලීන %s 😀" % drv
        code, body, err = curl(["-X", "PUT", "-H", "Content-Type: application/json",
                                "--data-binary", "@" + tf(json.dumps({"name": new, "course": "BIT"})),
                                base + "/api.php?driver=%s&id=%s" % (drv, ids["mysqli"])], "PUT api (%s)" % drv)
        d = js(body)
        check("%s PUT update" % drv, code == 200 and d.get("student", {}).get("name") == new, "%s %s" % (code, d))
    code, body, err = curl(["-X", "POST", "--data-urlencode", "name=", base + "/api.php"])
    check("validation 422 on empty name", code == 422, "%s %s" % (code, body[:200]))
    code, body, err = curl(["-X", "PATCH", base + "/api.php"])
    check("405 on PATCH", code == 405, "%s" % code)

    # HTML form CRUD with POST-redirect-GET
    code, body, err = curl(["-X", "POST", "--data-urlencode", "op=add", "--data-urlencode", "name@" + tf("Form " + TEXT_TA),
                            "--data-urlencode", "course=X", base + "/index.php?driver=pdo"], "POST index form add")
    check("HTML form add -> 303", code == 303, "%s %s" % (code, body[:200]))
    code, body, err = curl([base + "/index.php?driver=mysqli"], "GET index.php (list)")
    check("HTML list shows Tamil row", code == 200 and ("Form " + TEXT_TA).encode() in body, "%s %s" % (code, body[:300]))

    # list (both drivers)
    for drv in ("mysqli", "pdo"):
        code, body, err = curl([base + "/api.php?driver=" + drv], "GET api list (%s)" % drv)
        d = js(body)
        check("%s list" % drv, code == 200 and len(d.get("students", [])) >= 2, "%s %s" % (code, str(d)[:300]))

    # delete
    for drv, i in ids.items():
        code, body, err = curl(["-X", "DELETE", base + "/api.php?driver=%s&id=%s" % (drv, i)], "DELETE api (%s)" % drv)
        check("%s delete" % drv, code == 200 and js(body).get("affected") == 1, "%s %s" % (code, body[:200]))
    code, body, err = curl([base + "/api.php?id=%s" % ids["pdo"]])
    check("deleted row -> 404", code == 404, "%s" % code)

    # sessions
    code, body, err = curl(["-b", jar, "-c", jar, base + "/session.php"])
    check("session: 401 before login", code == 401, "%s" % code)
    code, body, err = curl(["-b", jar, "-c", jar, "--data-urlencode", "user@" + tf("නිමල්"), base + "/session.php"], "POST session login")
    d = js(body)
    check("session: login count=1", code == 200 and d.get("count") == 1 and d.get("user") == "නිමල්", str(d))
    counts = []
    for _ in range(3):
        code, body, err = curl(["-b", jar, "-c", jar, base + "/session.php"], "GET session counter")
        counts.append(js(body).get("count"))
    check("session: counter 2,3,4", counts == [2, 3, 4], str(counts))
    code, body, err = curl([base + "/session.php"])
    check("session: no cookie -> 401", code == 401, "%s" % code)

    # uploads: 1 MiB random binary + a small PNG, with a Sinhala comment
    binf = os.path.join(TMP, "random.bin")
    with open(binf, "wb") as f:
        f.write(b"\x00\x00PARITY-RANDOM\x00" + os.urandom(1024 * 1024 - 16))  # fixed prefix: random bytes once began with "MZ" and fileinfo said application/x-dosexec
    pngf = os.path.join(TMP, "tiny.png")
    with open(pngf, "wb") as f:
        f.write(bytes.fromhex("89504e470d0a1a0a0000000d4948445200000001000000010806000000"
                              "1f15c4890000000d49444154789c6360000002000154a24f5d0000000049454e44ae426082"))
    for fp, mime in ((binf, "application/octet-stream"), (pngf, "image/png")):
        code, body, err = curl(["-F", "file=@" + fp, "-F", "comment=<" + tf("උඩුගත කිරීම"), base + "/upload.php"],
                               "POST upload %s" % os.path.basename(fp))
        d = js(body)
        with open(fp, "rb") as f:
            sha = hashlib.sha256(f.read()).hexdigest()
        check("upload %s sha256 match + mime %s" % (os.path.basename(fp), mime),
              code == 200 and d.get("sha256") == sha and d.get("mime") == mime and d.get("comment") == "උඩුගත කිරීම",
              "%s %s" % (code, d))
    code, body, err = curl([base + "/upload.php"], "GET upload form")
    check("upload form page", code == 200 and b"multipart/form-data" in body, str(code))

    if pretty:
        code, body, err = curl([base + "/students"], "GET pretty /students")
        check("pretty URL /students", code == 200 and "students" in js(body), "%s %s" % (code, body[:200]))
        # create one to fetch by pretty id
        code, body, err = curl(["-X", "POST", "--data-urlencode", "name@" + tf("pretty " + TEXT_EMOJI), base + "/api.php"])
        pid = js(body).get("id")
        code, body, err = curl([base + "/students/%s?driver=mysqli" % pid], "GET pretty /students/N")
        check("pretty URL /students/N", code == 200 and js(body).get("student", {}).get("id") == pid,
              "%s %s" % (code, body[:200]))

    ok = sum(1 for _, r in results if r)
    print("TIMINGS (curl time_total, seconds):")
    agg = {}
    for lbl, t in timings:
        agg.setdefault(lbl, []).append(t)
    for lbl, ts in agg.items():
        print("   %-32s n=%d avg=%.4f max=%.4f" % (lbl, len(ts), sum(ts) / len(ts), max(ts)))
    allt = [t for _, t in timings]
    print("   ALL requests n=%d avg=%.4f max=%.4f" % (len(allt), sum(allt) / len(allt), max(allt)))
    print("SUMMARY %d/%d checks passed" % (ok, len(results)))
    sys.exit(0 if ok == len(results) else 1)


if __name__ == "__main__":
    main()
