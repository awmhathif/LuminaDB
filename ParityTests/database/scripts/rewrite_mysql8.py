"""Prototype of the automatic rewrites the app must do before feeding MySQL 8 /
Workbench / phpMyAdmin scripts to MariaDB.  Every rule here was verified by
running the rewritten script (see run_rewritten.sh / out/RW_*.txt).

usage: python -I rewrite_mysql8.py <target: 118|104> <in.sql> <out.sql>
"""
import re
import sys

target, src, dst = sys.argv[1], sys.argv[2], sys.argv[3]
sql = open(src, encoding="utf-8").read()

rules = [
    # auth plugins (both versions)
    (r"IDENTIFIED\s+WITH\s+caching_sha2_password\s+BY", "IDENTIFIED BY"),
    (r"IDENTIFIED\s+WITH\s+mysql_native_password\s+BY", "IDENTIFIED BY"),
    (r"IDENTIFIED\s+WITH\s+sha256_password\s+BY", "IDENTIFIED BY"),
    # per-account lockout options (MariaDB has only global max_password_errors)
    (r"\s+FAILED_LOGIN_ATTEMPTS\s+\d+", ""),
    (r"\s+PASSWORD_LOCK_TIME\s+(\d+|UNBOUNDED)", ""),
    # CHECK ... NOT ENFORCED: MariaDB cannot store an unenforced check -> drop it
    (r",\s*(CONSTRAINT\s+`?\w+`?\s+)?CHECK\s*\((?:[^()]|\([^()]*\))*\)\s+NOT\s+ENFORCED", ""),
    (r"(CHECK\s*\((?:[^()]|\([^()]*\))*\))\s+ENFORCED", r"\1"),
    # JSON operators
    (r"(`?\w+`?(?:\.`?\w+`?)?)\s*->>\s*('[^']*')", r"JSON_UNQUOTE(JSON_EXTRACT(\1, \2))"),
    (r"(`?\w+`?(?:\.`?\w+`?)?)\s*->\s*('[^']*')", r"JSON_EXTRACT(\1, \2)"),
    # REGEXP_LIKE(a, b) -> (a REGEXP b)   (2-arg form only)
    (r"REGEXP_LIKE\s*\(\s*([^,()]+?)\s*,\s*('[^']*')\s*\)", r"(\1 REGEXP \2)"),
    # EXPLAIN ANALYZE -> ANALYZE FORMAT=JSON
    (r"\bEXPLAIN\s+ANALYZE\b", "ANALYZE FORMAT=JSON"),
    # SET PERSIST / PERSIST_ONLY -> SET GLOBAL (app must also write my.ini)
    (r"\bSET\s+PERSIST(_ONLY)?\b", "SET GLOBAL"),
    # TABLE t  -> SELECT * FROM t (statement form only)
    (r"(?im)^\s*TABLE\s+(`?\w+`?)\s*;", r"SELECT * FROM \1;"),
    # VALUES ROW(...) -> VALUES (...)
    (r"\bROW\s*\(", "("),  # only safe inside VALUES statements; app should scope this
    # spatial SRID attribute
    (r"\b(POINT|GEOMETRY|LINESTRING|POLYGON|MULTIPOINT|MULTILINESTRING|MULTIPOLYGON|GEOMETRYCOLLECTION)\s+NOT\s+NULL\s+SRID\s+(\d+)",
     r"\1 REF_SYSTEM_ID=\2 NOT NULL"),
    (r"\b(POINT|GEOMETRY|LINESTRING|POLYGON|MULTIPOINT|MULTILINESTRING|MULTIPOLYGON|GEOMETRYCOLLECTION)\s+SRID\s+(\d+)",
     r"\1 REF_SYSTEM_ID=\2"),
    # roles have no host part in MariaDB
    (r"(CREATE|DROP)\s+ROLE\s+(IF\s+(NOT\s+)?EXISTS\s+)?('[^']+')@'[^']*'", r"\1 ROLE \2\4"),
]

if target == "104":
    rules += [
        # 10.4 has no 0900 collations
        (r"utf8mb4_0900_as_cs", "utf8mb4_bin"),
        (r"utf8mb4_0900_bin", "utf8mb4_bin"),
        (r"utf8mb4_0900_\w+", "utf8mb4_unicode_520_ci"),
        # 10.4 has no index visibility at all
        (r"\)\s+INVISIBLE\b", ")"),
        (r"\)\s+VISIBLE\b", ")"),
    ]
else:  # 11.8 accepts VISIBLE and the 0900 collations; INVISIBLE index -> IGNORED
    rules += [
        (r"\)\s+INVISIBLE\b(?=\s*[,)])", ") IGNORED"),
        (r"ALTER\s+INDEX\s+(`?\w+`?)\s+INVISIBLE", r"ALTER INDEX \1 IGNORED"),
        (r"ALTER\s+INDEX\s+(`?\w+`?)\s+VISIBLE", r"ALTER INDEX \1 NOT IGNORED"),
    ]

# 'ROW(' rule is too broad for general text; only apply it on lines starting with VALUES
lines = []
for line in sql.split("\n"):
    for pat, rep in rules:
        if pat == r"\bROW\s*\(" and not re.match(r"\s*VALUES\b", line, re.I):
            continue
        line = re.sub(pat, rep, line, flags=re.I)
    lines.append(line)
out = "\n".join(lines)
# multi-line CREATE TABLE: re-run the multi-line-sensitive rules on whole text
for pat, rep in rules:
    if "INVISIBLE" in pat or "VISIBLE" in pat or "ENFORCED" in pat:
        out = re.sub(pat, rep, out, flags=re.I)
open(dst, "w", encoding="utf-8", newline="\n").write(out)
print(f"rewrote {src} -> {dst} for {target}")
