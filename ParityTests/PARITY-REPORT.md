# Can LuminaDB replace XAMPP + MySQL Workbench + phpMyAdmin?

**Answer: yes, for almost everything students and developers do.** The engines can do the work; most of what's left is screens our app must build. All of this was tested on the dev PC (Windows 10 22H2, normal user) on 2026-11-23.

Detailed evidence:
- `database/RESULTS.md`: MariaDB 11.8.10 and 10.4.34, sections A–G, scripts in `database/scripts`, raw output in `database/out`
- `web/RESULTS.md`: PHP, phpMyAdmin, Caddy and Apache, scripts and logs in `web/scripts` and `web/results`
- `FEATURE-MAP.md`: every XAMPP/Workbench/phpMyAdmin feature → what our app must build, with priority

## 1. Database (Workbench / phpMyAdmin / XAMPP's MySQL)

**Works on both 11.8 and 10.4, unchanged:**
- All everyday student SQL: keys and cascades, all JOIN types, GROUP BY/HAVING, subqueries, date and string functions, ALTER.
- Views, stored procedures (OUT parameters, cursors), functions, triggers, events.
- Transactions and savepoints.
- Users and grants (database, table and column level), root password, roles.
- Window functions, CTEs (including recursive), full-text search, EXPLAIN/ANALYZE.
- Sinhala, Tamil and emoji, stored and read back byte-exact.
- Dump and restore including routines, triggers and events; CSV export and import.
- Everything a GUI needs to read: information_schema, foreign keys for ER diagrams, PROCESSLIST/KILL, status counters, runtime SET GLOBAL.
- The .NET connector our app will use: 12/12 checks, including query cancel in 1.5 s.

**MySQL 8 files (Workbench exports, phpMyAdmin dumps, scripts from the internet):**
- **11.8 imports a stock Workbench forward-engineer script and a MySQL 8 phpMyAdmin dump without changes.**
- 10.4 fails on `VISIBLE` and `utf8mb4_0900_ai_ci`.
- Both fail on a list of MySQL-8-only syntax: `IDENTIFIED WITH caching_sha2_password`, `->`/`->>`, `LATERAL`, `REGEXP_LIKE`, `GROUPING()`, `INSERT … AS new`, functional indexes, `SET PERSIST`, and others. Each one has a **verified rewrite** (R01–R25, `database/scripts/R_rewrites.sql`). `rewrite_mysql8.py` is a working prototype of the automatic rewriting. About 9 of the rewrites need a real SQL parser, not text replacement.

**Truly impossible (MariaDB isn't MySQL):**
- real `caching_sha2_password` accounts
- CHECK `NOT ENFORCED`
- MySQL 8's exact `performance_schema`/`sys` layout
- `SET PERSIST`'s config file
- the `EXPLAIN ANALYZE` tree format

Students almost never need these. Our import wizard should explain them in plain words.

**Traps the app must handle (all reproduced):**
- **10.4 creates latin1 tables by default.** Set `character-set-server=utf8mb4`.
- **10.4's command-line client corrupts Sinhala passed as a `-e` argument.** Always send SQL through the connector or stdin.
- **`local-infile=1` must go under `[mysql]`, not `[client]`.** In `[client]` it breaks the dump tool.
- **The event scheduler and performance_schema are off by default.** Turn them on in my.ini (the exact lines are in `database/RESULTS.md` §3, verified).
- **The installer creates four root accounts.** "Set root password" must update all four.
- **11.8 dumps don't import into 10.4 as they are** (collation `uca1400` and a "sandbox mode" first line). Set `character_set_collations` on 11.8 so dumps stay portable.

## 2. Web stack (XAMPP's Apache + PHP + phpMyAdmin)

**Works, portable, no admin, no service:**
- PHP 8.5 / 8.6 with mysqli, PDO, mbstring, openssl, curl, gd, intl, zip and sodium.
- A sample CRUD app: 27/27 checks (prepared statements, Unicode, sessions, uploads, a JSON API).
- **phpMyAdmin 5.2.3: 9/9** (login, run SQL, export).
- Both Caddy and Apache work. Apache passes `.htaccess` pretty URLs. 20 to 100 parallel requests: 0 failures.

**Recommended setup:**
- **Caddy + `php-cgi`**: a single small web server exe, with 4 PHP worker processes that restart themselves.
- Offer **Apache** for projects that rely on `.htaccess`; it needs 3 extra config fixes, all documented.
- **Do not use `php -S`:** one slow page blocks every other page.

**Bundle size:** PHP + phpMyAdmin + Caddy is about **58 MB zipped**. MariaDB adds 46–60 MB.

**Problems found and their fixes:**
- **PHP 8.6 needs a newer VC++ runtime (14.51) than MariaDB ships (14.44).** As downloaded it refuses to start. Ship `vcruntime140.dll`, `vcruntime140_1.dll` and `msvcp140.dll` at version 14.51, taken from Microsoft's redistributable, next to php.exe and httpd.exe. PHP 8.5 works with MariaDB's 14.44 runtime.
- **php-cgi exits after 500 requests by default.** Fixed by setting `PHP_FCGI_CHILDREN`.
- **phpMyAdmin on PHP 8.6 prints deprecation warnings into the page.** Fixed with `display_errors=Off`.
- **The free-port check must also test 0.0.0.0.** Otherwise a port held by another program looks free.
- **Caddy must be told `bind 127.0.0.1`,** or it opens to the network.

## 3. Windows 7

| Piece | Newest version that can run on Windows 7 | Evidence |
|---|---|---|
| MariaDB | 10.4.34 (11.8 can't) | Binary scan; real Win7 not tested |
| PHP | 8.2.36 (8.3+ needs Windows 8; security support ends 31 Dec 2026) | Binary scan and PHP release notes; real Win7 not tested |
| VC++ runtime | 14.44 or older (14.51 needs Windows 8) | Binary scan |
| Caddy | none (needs Windows 10) | Caddy docs |
| Apache | an older Apache Lounge build is needed; not tested | — |

**Conclusion:** a Windows 7 edition is possible (MariaDB 10.4 + PHP 8.2 + an older Apache or plain php-cgi), but it is a separate, ageing, unsupported bundle. **Recommendation:** make Windows 10/11 the main product. Offer Windows 7 as a "legacy lab edition" only if the college's PCs really run Windows 7, and check that on one real PC first.

## 4. XAMPP pieces we deliberately leave out

FileZilla FTP, Mercury mail, Tomcat and Perl are rarely used in coursework. Revisit only if lecturers ask.

## 5. Still unverified

- A PC that has never had VC++ installed (this PC has it; the DLL lists come from reading the files)
- Real Windows 7 / 8.1
- phpMyAdmin clicked through in a real browser (it was tested with scripted requests only)
- Very large imports
- Python drivers (no pip allowed)
- Real Workbench or phpMyAdmin apps connecting to our MariaDB

## 6. What this means for the build

1. **Engines:** MariaDB 11.8 + PHP 8.5 (sharing one VC++ runtime), or PHP 8.6 with the 14.51 runtime shipped next to it + Caddy, with Apache as an option.
2. **First things the app must do:**
   - write my.ini and php.ini itself (verified defaults)
   - pick free ports, checking 127.0.0.1 and 0.0.0.0
   - manage worker processes
   - shut everything down cleanly
3. **Import wizard:** apply the MySQL 8 → MariaDB rewrites automatically and explain what changed.
4. **The rest is screens.** The P1 list in `FEATURE-MAP.md` is the first release.

## Disk space

The test downloads in `web/downloads` (about 1 GB) and the data folders can be deleted. The scripts, results and `web/dist` (the trimmed bundle, 197 MB) are what's worth keeping.
