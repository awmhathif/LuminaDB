"""Drive phpMyAdmin 5.2.x with curl: login page, cookie login as root (empty password),
main page, SQL query via /import, database export as SQL.

usage: python -I test_pma.py <base-url> [--label NAME]
  e.g. http://127.0.0.1:8020   or   http://127.0.0.1:8030/phpmyadmin
Saves the export to ../results/pma_export_<label>.sql and the main page to ../results/pma_main_<label>.html
"""
import html
import json
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
RES = os.path.join(HERE, "..", "results")
TMP = os.path.join(HERE, "..", "tmp", "curl")
os.makedirs(TMP, exist_ok=True)
results = []
timings = []


def curl(args, label=None, out="body"):
    body_path = os.path.join(TMP, out)
    cmd = ["curl", "-sS", "-o", body_path, "-w", "%{http_code} %{time_total} %{redirect_url}"] + args
    p = subprocess.run(cmd, capture_output=True)
    parts = p.stdout.decode().strip().split(" ", 2)
    code = int(parts[0]) if parts and parts[0].isdigit() else 0
    t = float(parts[1]) if len(parts) > 1 else -1
    redir = parts[2] if len(parts) > 2 else ""
    body = open(body_path, "rb").read() if os.path.exists(body_path) else b""
    if label:
        timings.append((label, t))
    return code, body, redir, p.stderr.decode().strip()


def check(name, cond, detail=""):
    results.append((name, bool(cond)))
    print(("PASS " if cond else "FAIL ") + name + ("" if cond else "  -> " + str(detail)[:800]))


def token_of(body):
    m = re.search(rb'name="token" value="([0-9a-zA-Z%\-_:]+)"', body) or \
        re.search(rb'"token":"([^"]+)"', body) or re.search(rb"token=([0-9a-f]{20,})", body)
    return m.group(1).decode() if m else None


def alerts(body):
    """Return the text of every alert/notice box phpMyAdmin rendered (warnings about extensions etc.)."""
    out = []
    for m in re.finditer(rb'<div class="alert alert-(danger|warning|info|primary)[^"]*"[^>]*>(.*?)</div>', body, re.S):
        txt = re.sub(r"<[^>]+>", " ", m.group(2).decode("utf-8", "replace"))
        txt = re.sub(r"\s+", " ", html.unescape(txt)).strip()
        if txt:
            out.append("%s: %s" % (m.group(1).decode(), txt[:300]))
    return out


def main():
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    base = sys.argv[1].rstrip("/")
    label = sys.argv[sys.argv.index("--label") + 1] if "--label" in sys.argv else "run"
    jar = os.path.join(TMP, "pma_jar_%s.txt" % label)
    if os.path.exists(jar):
        os.remove(jar)
    J = ["-b", jar, "-c", jar]

    code, body, _, err = curl(J + [base + "/index.php"], "GET login page (cold)")
    tok = token_of(body)
    check("login page 200 + form", code == 200 and b"pma_username" in body and tok, "%s %s %s" % (code, body[:300], err))
    pre_alerts = alerts(body)
    for a in pre_alerts:
        print("   login-page alert:", a)
    code, body, _, err = curl(J + [base + "/index.php"], "GET login page (warm)")
    tok = token_of(body) or tok
    m = re.search(rb'name="set_session" value="([0-9a-zA-Z]+)"', body)
    set_session = m.group(1).decode() if m else ""

    code, body, redir, err = curl(J + ["--data-urlencode", "pma_username=root", "--data-urlencode", "pma_password=",
                                       "--data-urlencode", "server=1", "--data-urlencode", "route=/",
                                       "--data-urlencode", "token=" + tok, "--data-urlencode", "set_session=" + set_session,
                                       "--data-urlencode", "lang=en", base + "/index.php?route=/"], "POST login")
    if code != 302:
        for a in alerts(body):
            print("   login-response alert:", a)
    check("login POST -> 302", code == 302, "%s redir=%s body=%s" % (code, redir, body[:400]))
    code, body, _, err = curl(J + ["-L", base + "/index.php?route=/"], "GET main page")
    with open(os.path.join(RES, "pma_main_%s.html" % label), "wb") as f:
        f.write(body)
    logged_in = code == 200 and (b"pma_navigation" in body or b"Database server" in body) and b"pma_username" not in body
    check("main page after login", logged_in, "%s %s" % (code, body[:400]))
    tok2 = token_of(body) or tok
    m = re.search(rb"Server version:\s*(?:<[^>]+>\s*)*([^<]+)", body)
    print("   main page server version:", m.group(1).decode().strip() if m else "?")
    for a in alerts(body):
        print("   main-page alert:", a)
    m = re.search(rb"PHP extension:(.*?)</li>", body, re.S)
    if m:
        print("   main page 'PHP extension':", re.sub(r"<[^>]+>|\s+", " ", m.group(1).decode()).strip())
    # PHP notices/deprecations surfaced by phpMyAdmin's error handler
    errs = re.findall(rb'class="alert alert-danger"[^>]*>(?:.*?)(Deprecat\w+|Warning|Notice)[^<]*', body, re.S)
    print("   php errors shown on main page:", len(errs))

    sql = "SELECT COUNT(*) AS n, MAX(id) AS maxid, @@version AS v FROM paritytest.students"
    code, body, _, err = curl(J + ["--data-urlencode", "db=paritytest", "--data-urlencode", "sql_query=" + sql,
                                   "--data-urlencode", "token=" + tok2, "--data-urlencode", "ajax_request=true",
                                   "--data-urlencode", "server=1", base + "/index.php?route=/import"], "POST SQL /import")
    try:
        d = json.loads(body.decode("utf-8"))
    except Exception:  # noqa
        d = {"_raw": body[:400]}
    ok = code == 200 and d.get("success") is True and "11.8.10-MariaDB" in json.dumps(d)
    check("SQL via /import (ajax) returns result grid", ok, "%s %s" % (code, str(d)[:600]))

    create = ("CREATE TABLE IF NOT EXISTS paritytest.pma_made (id INT PRIMARY KEY, t VARCHAR(50)) "
              "DEFAULT CHARSET=utf8mb4; REPLACE INTO paritytest.pma_made VALUES (1,'phpMyAdmin wrote this')")
    code, body, _, err = curl(J + ["--data-urlencode", "db=paritytest", "--data-urlencode", "sql_query=" + create,
                                   "--data-urlencode", "token=" + tok2, "--data-urlencode", "ajax_request=true",
                                   "--data-urlencode", "server=1", base + "/index.php?route=/import"], "POST multi-stmt /import")
    try:
        d = json.loads(body.decode("utf-8"))
    except Exception:  # noqa
        d = {"_raw": body[:400]}
    check("multi-statement DDL+DML via /import", code == 200 and d.get("success") is True, "%s %s" % (code, str(d)[:600]))

    exp = ["db=paritytest", "export_type=database", "export_method=quick", "what=sql", "output_format=sendit",
           "filename_template=@DATABASE@", "charset=utf-8", "compression=none", "sql_structure_or_data=structure_and_data",
           "sql_create_table=something", "sql_if_not_exists=something", "sql_type=INSERT", "sql_insert_syntax=both",
           "sql_max_query_size=50000", "sql_hex_for_binary=something", "sql_utc_time=something",
           "sql_header_comment=", "sql_include_comments=something", "sql_compatibility=NONE", "sql_backquotes=something",
           "table_select[]=students", "table_structure[]=students", "table_data[]=students",
           "table_select[]=pma_made", "table_structure[]=pma_made", "table_data[]=pma_made",
           "server=1", "token=" + tok2]
    args = []
    for kv in exp:
        args += ["--data-urlencode", kv]
    t0 = time.perf_counter()
    code, body, _, err = curl(J + args + ["-D", os.path.join(TMP, "exp_headers.txt"), base + "/index.php?route=/export"],
                              "POST export db as SQL", out="export.sql")
    hdr = open(os.path.join(TMP, "exp_headers.txt"), "rb").read().decode("latin-1")
    with open(os.path.join(RES, "pma_export_%s.sql" % label), "wb") as f:
        f.write(body)
    ok = (code == 200 and b"CREATE TABLE" in body and re.search(rb"INSERT INTO `?students`?", body) is not None
          and "attachment" in hdr.lower() and "phpMyAdmin wrote this".encode() in body)
    check("export database as SQL (attachment, CREATE+INSERT, utf8mb4 intact)", ok and "🎓".encode() in body,
          "%s hdr=%s body=%s" % (code, hdr[:300], body[:400]))
    print("   export size bytes:", len(body))

    code, body, _, err = curl(J + [base + "/index.php?route=/server/status&ajax_request=true&server=1"], "GET /server/status")
    check("server status page", code == 200, code)
    code, body, _, err = curl(J + [base + "/index.php?route=/database/structure&db=paritytest&server=1"], "GET db structure")
    check("database structure page lists students", code == 200 and b"students" in body, code)

    # logout
    code, body, redir, err = curl(J + ["--data-urlencode", "token=" + tok2, base + "/index.php?route=/logout"], "POST logout")
    check("logout", code in (200, 302), code)

    print("TIMINGS (curl time_total, seconds):")
    for lbl, t in timings:
        print("   %-28s %.4f" % (lbl, t))
    ok = sum(1 for _, r in results if r)
    print("SUMMARY %d/%d checks passed" % (ok, len(results)))
    sys.exit(0 if ok == len(results) else 1)


if __name__ == "__main__":
    main()
