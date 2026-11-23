# LuminaDB — Product Spec

One Windows app that replaces **XAMPP + MySQL Workbench + phpMyAdmin**. It is built around preventing the errors those tools cause, runs on every Windows PC from Windows 7 to 11, and ships as **one package**. Everything in this spec ships together; there is no "v1 now, rest later". The build order in §11 is internal only.

Evidence behind every technical choice is in:
- `EngineSpike/FINDINGS.md`: engine start, crash recovery, Windows 7 scan
- `ParityTests/PARITY-REPORT.md`: SQL parity, PHP/phpMyAdmin/web server
- `ParityTests/FEATURE-MAP.md`: every feature of the old tools mapped to LuminaDB

---

## 1. The promise

1. **Double-click and it works.** No installer choices, no admin rights, no Windows services, no VC++ to install, no editing config files, no ports to choose.
2. **It never leaves you stuck.** Every known failure is either prevented, fixed automatically (after a backup), or explained in plain English with a one-click fix.
3. **It does everything the three old tools did** for students and PHP developers, in one window with one look.
4. **Nothing is lost.** Automatic backups, snapshots before risky actions, and Undo.

---

## 2. Supported PCs and engine sets

The package carries three engine sets. On first start the app detects Windows and CPU and uses the right one; the user never chooses.

| Set | PCs | Database | PHP | Web server | Notes |
|---|---|---|---|---|---|
| **Modern** | Windows 10/11, 64-bit | MariaDB 11.8 LTS | PHP 8.5 (x64, NTS) | Caddy 2.11 + php-cgi | All verified on the dev PC |
| **Legacy** | Windows 7 SP1 / 8 / 8.1, 64-bit | MariaDB 10.4.34 | PHP 8.2 (x64) | Apache 2.4 (a Win7-capable VS16/17 build: **still to find and verify**) + php-cgi | MariaDB/PHP binary scans show no Windows 8+ calls; real Windows 7 not tested yet |
| **32-bit** | Any 32-bit Windows 7–10 | MariaDB 10.4.22 (last 32-bit release) | PHP 8.2 x86 on Win7/8, PHP 8.5 x86 on Win10 | Apache 2.4 Win32 | Builds exist (checked on the official archives); not run yet |

- **The app itself** is .NET Framework 4.8 WPF, compiled AnyCPU so one exe runs on 32-bit and 64-bit. .NET 4.8 is built into Windows 10 1903+ and 11. Windows 7/8.1 need it installed once; the installer includes the offline .NET 4.8 package and installs it only if missing (this is the one step that may need admin, only on those old PCs).
- **VC++ runtime:** each engine set ships its own `vcruntime140*.dll`/`msvcp140.dll` next to the programs that need them (MariaDB already does). Nothing goes system-wide.
- **Databases move between sets.** The Modern set is configured to write dumps the Legacy set can import (`character_set_collations`, utf8mb4 everywhere). A student can take a database from the lab's Windows 7 PC to their Windows 11 laptop and back.
- **Not supported:** Windows XP and Vista. Nothing modern runs there.
- **Honesty in the app:** on Legacy and 32-bit sets, Settings states that the engines no longer get security updates and that this is safe because they only accept connections from this PC.

---

## 3. Package and distribution

- **Two forms, same content:**
  - `LuminaDB-Setup.exe` (Inno Setup): per-user install into `%LOCALAPPDATA%\LuminaDB`, Start-menu shortcut, no admin.
  - `LuminaDB-Portable.zip`: unzip anywhere, including a USB stick. Everything (engines, data, projects, backups) lives in that folder.
- **Estimated size:** about 250 MB zipped with all three engine sets (Modern ≈ 120 MB, Legacy ≈ 90 MB, 32-bit ≈ 80 MB, app ≈ 5 MB). The installer only unpacks the set the PC needs; the portable zip keeps all three so the USB stick works on any PC.
- **Folder layout (portable and installed are the same):**
  ```
  LuminaDB\
    LuminaDB.exe
    app\            (.NET dlls, fonts, icons)
    engines\modern\  engines\legacy\  engines\x86\   (mariadb, php, web server, phpmyadmin, composer)
    data\mariadb-modern\  data\mariadb-legacy\  data\mariadb-x86\   (one per engine set, see rule below)
    projects\       (PHP projects, the "htdocs")
    backups\  snapshots\  mail\  logs\
    config\         (settings.json + generated my.ini, php.ini, Caddyfile / httpd.conf)
  ```
- **Code signing:** buy a code-signing certificate before public release, so SmartScreen and antivirus don't flag the exe. Until then, the README explains "More info → Run anyway".
- **Updates:** a "Check for updates" button that downloads a new package when online. Lab PCs without internet keep working; the updater is never required.
- **Data folder compatibility (mandatory rule):**
  - Each engine set has its own data folder: `data\mariadb-modern`, `data\mariadb-legacy`, `data\mariadb-x86`.
  - A server never opens another set's folder. An older server can't read a newer folder, and a newer server silently upgrades an older one, after which the older PC can't read it any more.
  - When the USB moves to a PC that uses a different set, the app shows "Your databases from the other PC" and brings them across by automatic dump-and-import. Both copies stay intact.

---

## 4. Architecture

```
┌──────────────────────── LuminaDB.exe (WPF, .NET 4.8) ────────────────────────┐
│ UI (Ledger design)                                                            │
│  Today · Projects · Notebook · Tables · Diagram · Import/Export · Users ·      │
│  Server · Doctor · Backups · Mail · Settings                                  │
├───────────────────────────────────────────────────────────────────────────────┤
│ Core services (plain C#, unit-testable, no UI references)                     │
│  EngineSetSelector   PortManager       ConfigWriter     ProcessSupervisor     │
│  MariaDbService      WebStackService   ProjectService   BackupService         │
│  Doctor (checks+fixes) SqlGuard (live checks)  ErrorExplainer                 │
│  ImportRewriter (MySQL 8 / phpMyAdmin → MariaDB)  SchemaReader (info_schema)  │
│  MailCatcher (local SMTP)  SnapshotService  UpdateService  Telemetry: none    │
├───────────────────────────────────────────────────────────────────────────────┤
│ Child processes (started by us, never services, all bound to 127.0.0.1)       │
│  mariadbd/mysqld   php-cgi ×4 (FastCGI)   caddy / httpd                        │
└───────────────────────────────────────────────────────────────────────────────┘
```

Key behaviours, all already verified in the tests:
- **PortManager:** a port is "free" only if nothing listens on it at 127.0.0.1 **or** 0.0.0.0 (IPv4) and `[::]`/`[::1]` (IPv6). It prefers 3306, 80 and 9000, then the next free one. The chosen ports are saved and reused next time if still free, so project URLs stay stable.
- **ConfigWriter:** generates my.ini, php.ini and the Caddyfile/httpd.conf from templates at every start, with **absolute paths** (relative `extension_dir` breaks under Apache), utf8mb4 defaults, event scheduler on, `local-infile` under `[mysql]` (not `[client]`), `display_errors` handled by our own error page, `bind 127.0.0.1`. Users never edit these; an Advanced editor applies overrides on top, validates them, and rolls back if the server fails to start.
- **ProcessSupervisor:**
  - starts children hidden and captures their output to `logs\`
  - restarts php-cgi children (`PHP_FCGI_CHILDREN=4`)
  - shuts MariaDB down with SQL `SHUTDOWN`, waits, and kills only as a last resort
  - if the app crashes, the engines keep running and the next launch adopts them (detected by PID file and port) instead of starting duplicates
  - web and PHP processes are in a Job Object so they die with the app; MariaDB is not, so a crash doesn't cut it off mid-write
- **Never pass SQL as a command-line argument** (Unicode gets corrupted on 10.4). All SQL goes through MySqlConnector.
- **One root, four accounts:** "Set root password" updates every `root@` account the data-folder setup created.

---

## 5. Features (all in the package)

### 5.1 Today (home)
- Status sentence: "MariaDB is running on :3306", "Web server is serving 3 projects", or a plain sentence explaining why not.
- Activity line (queries/sec), Doctor summary, last backup, databases and projects lists, connection details with Copy.
- Start/stop each engine, or "Start everything".

### 5.2 Projects (the PHP workflow)
- **New project:** name → folder `projects\<name>`, a database with the same name, and `config.php` / `.env` with the right host, port, user and password. Templates:
  - empty PHP
  - CRUD starter
  - login/registration starter (sessions, password_hash)
  - REST API starter
  - Laravel (via bundled Composer)
  - WordPress
- **Open in browser:** `http://<name>.localhost:<port>` (works in Chrome, Edge and Firefox with no hosts-file edit). Also `http://localhost:<port>/<name>/` for old browsers.
- **Open in editor:** VS Code / Notepad++ / Explorer if installed, otherwise Explorer.
- **Friendly PHP error page:** errors, warnings and uncaught exceptions show the file, the line, the code around it and a plain-English explanation ("Undefined variable $user: it was never given a value on this path"). Raw logs are one click away.
- **Live log:** the PHP error log and access log per project.
- **PHP settings per project:** upload size, memory limit, timezone, enabled extensions, from a simple form.
- **Composer:** bundled, runs in the project folder from a button or the console.
- **`.htaccess` support:** projects that need it automatically use Apache; others use Caddy. The user never picks.
- **Demo mode:** temporarily serve one project to the local network (viva or presentation). Shows the LAN address and a QR code, and turns itself off after a set time. MariaDB is **never** exposed. This is the only feature that may trigger a Windows Firewall prompt; the app warns first.
- **Snapshot:** save code + database together and restore either or both.
- **Submission pack:** a zip of the project folder (without vendor/ and cache, optional) plus `<db>.sql` export plus a README with run instructions, named `StudentID_Project_YYYY-MM-DD.zip`.

### 5.3 Notebook (SQL editor; replaces the Workbench SQL editor)
- Tabs, syntax colours, line numbers, autocomplete for keywords, tables and columns.
- Run all / run selection / run the statement under the cursor (Ctrl+Enter, Ctrl+Shift+Enter).
- **Margin corrections while typing:**
  - misspelt commands, tables and columns, with did-you-mean
  - an extra comma before FROM
  - unclosed quotes and brackets
  - `UPDATE`/`DELETE` without `WHERE`
  - `DROP`
  - MySQL-8-only syntax, offering the MariaDB rewrite
- **Error explainer:** every MariaDB error code people commonly hit (1064, 1146, 1054, 1062, 1451, 1452, 1048, 1366, 1406, 1364, 1045, 2002/2003, 1273, 1005/150, 1215 and the rest of the top 50) shown as what happened, why, and a fix button. Raw error always visible.
- Result ledger: sort, filter, copy, NULL shown as a stamp, edit cells (generates UPDATE by primary key, shows it, applies on confirm, Undo available).
- Export results to CSV, JSON or Excel.
- Receipt log of everything run, with time and rows affected.
- Query history and saved snippets; an examples menu for students.
- **Safe updates:** a tear-off confirmation before UPDATE/DELETE without WHERE, DROP or TRUNCATE. A snapshot is taken first, so Undo works.
- Visual EXPLAIN (MariaDB `EXPLAIN FORMAT=JSON` rendered as a simple tree).

### 5.4 Tables (replaces phpMyAdmin browse/structure/insert and the Workbench table editor)
- Browse with paging, search, and filter builder (query by example).
- Insert/edit form generated from the table: date pickers, ENUM dropdowns, foreign-key lookups showing the referenced row, NOT NULL and length checks before sending.
- Structure editor: columns, types, PK/NN/UQ/AI/default, indexes, foreign keys (ON DELETE/UPDATE), triggers, CHECK constraints. Always shows the SQL it will run.
- Operations: rename, copy, move, empty, drop (all snapshotted), change charset/collation, optimize/repair/check.
- Views, procedures, functions, triggers and events: editors with templates and the same margin corrections.

### 5.5 Diagram (replaces Workbench EER and phpMyAdmin Designer)
- **Reverse engineer** any database into an auto-laid-out ER diagram (tables, PK/FK, crow's-foot notation). Drag to arrange; the layout is saved.
- **Forward engineer:** draw tables and relations, then generate the CREATE script, review it, run it.
- **Synchronize:** diff the diagram against the database and produce an ALTER script.
- Export as PNG, SVG or PDF (for assignments).

### 5.6 Import / Export (replaces Workbench Data Import/Export and phpMyAdmin Import/Export)
- **Import .sql:** detects the source (MySQL 8, Workbench forward-engineer, phpMyAdmin dump, MariaDB dump) and applies the verified rewrites (R01–R25) automatically. Shows exactly what it changed and why. Handles large files by streaming with a progress bar, and keeps going past harmless errors with a summary at the end.
- **Import CSV/Excel** into a new or existing table with column mapping and type guessing.
- **Export:** SQL (choose "for MariaDB", "for MySQL 8" or "for phpMyAdmin/XAMPP" compatibility), CSV, JSON, Excel; single table, whole database, or all.
- Copy a database between the Modern and Legacy engines safely (§3).

### 5.7 Users (replaces Workbench Users & Privileges and phpMyAdmin Privileges)
- List accounts; create with password; grant per database/table/column with checkboxes; roles; show the generated GRANT.
- Root password set/clear (all root accounts).
- Explains `caching_sha2_password` scripts: "MariaDB uses a different login method; the user was created with a normal password instead".

### 5.8 Server (replaces Workbench Server Status/Administration and the XAMPP control panel)
- Status: uptime, connections, queries/sec, traffic, buffer pool.
- Client connections with Kill.
- Variables: searchable, with explanations; change at runtime or permanently (written to our managed my.ini).
- Logs: error log, slow query log (toggle), general log (toggle), PHP and web logs.
- Console: the `mariadb` command line in a tab, connected to the right port.

### 5.9 Doctor (LuminaDB's core)
Runs at every start and on demand. Each check has a status, a plain explanation, and a fix that always takes a backup first if it touches data.

| Check | Prevention / fix |
|---|---|
| Port busy (3306, 80, 9000) | Names the program holding it (PID, exe); moves our service to a free port automatically; offers "free the port" when it's ours or a stale process |
| Stale process from a crash | Adopts or cleanly stops leftover mariadbd/php-cgi/caddy started by us |
| Unclean shutdown | InnoDB recovers itself (verified). Doctor checks Aria/MyISAM system tables and repairs them (`aria_chk`/`mysqlcheck`) after a backup |
| Data folder from a newer engine | Blocks starting; offers export/import (§3) |
| Antivirus quarantined an engine file | Detects missing/zeroed exes, restores them from the package, explains how to add an exclusion (shows the exact folder) |
| Missing VC++/UCRT DLL | Should never happen (we ship them); if a DLL is missing or the wrong version, restores it from the package |
| .NET 4.8 missing (Win7) | Installer handles it; Doctor explains if it was skipped |
| Path problems | Warns about non-ASCII or very long paths, OneDrive-synced folders, and network drives; offers to move the folder |
| Disk space / read-only folder | Warns below 500 MB; detects read-only USB sticks and lab PCs that reset on reboot ("deep freeze") and suggests where to keep data |
| Config broken | Regenerates from templates; user overrides are kept aside and reported |
| Clock / timezone mismatch | PHP and MariaDB timezone set consistently |
| Browser can't open `*.localhost` | Falls back to `localhost:<port>/<project>` |
| Firewall | Explains that nothing needs firewall access except Demo mode |

### 5.10 Backups and snapshots
- Automatic: every 30 minutes while there are changes, before every DROP / TRUNCATE / delete-all / repair / import, and on close. Keeps the last 20 plus one per day for 7 days.
- Backups are per database dumps (portable between engine sets) plus project-code snapshots on demand.
- Restore always backs up the current state first.

### 5.11 Mail catcher (replaces XAMPP's Mercury)
- A tiny SMTP server inside the app on 127.0.0.1. php.ini's `SMTP`/`smtp_port` point at it.
- Every email a project sends appears in the Mail tab (HTML preview, raw source, attachments). Nothing leaves the PC.

### 5.12 phpMyAdmin (for lecturers who require it)
- Bundled phpMyAdmin 5.2.x, preconfigured, at `http://phpmyadmin.localhost:<port>`, auto-login to the local server. Off by default; one switch to turn on.

### 5.13 Settings
- Ports (auto by default), start with Windows (per-user startup entry, not a service), root password, backup schedule, data folder location (move = stop, copy, verify, restart), theme (Ledger light and a dark variant), font size, language (English first; Sinhala and Tamil later), engine set info.

---

## 6. Error-prevention catalogue (what XAMPP users hit → what LuminaDB does)

| Classic error | LuminaDB |
|---|---|
| "Port 3306/80 in use" / Apache won't start | Auto free port; Doctor names the culprit |
| "MySQL shutdown unexpectedly" | Clean SHUTDOWN always; InnoDB recovery; Aria repair after backup |
| "MSVCR/VCRUNTIME140.dll missing" | Shipped next to each engine |
| "Access denied for user root@localhost" | App manages root; reset-password button sets all root accounts |
| "#1064 syntax error" | Margin correction before running, plus a plain explanation |
| "Table doesn't exist" / "Unknown column" | Did-you-mean from the live schema |
| "#1452 / #1451 foreign key fails" | Explains which parent/child row is the problem, with a lookup |
| "Unknown collation utf8mb4_0900_ai_ci" | Import rewriter |
| Sinhala/Tamil shows as `????` | utf8mb4 everywhere; never SQL on the command line |
| "Maximum upload size" in phpMyAdmin | Our importer streams any size; PHP limits are set sensibly |
| Lost work after a power cut | Committed data survives (verified); backups and snapshots |
| Accidental DELETE without WHERE | Confirmation + snapshot + Undo |
| Config edited and broken | Regenerated; overrides validated with rollback |
| XAMPP in Program Files permission errors | Lives in a user-writable folder only |

---

## 7. UI: Ledger design system

Source of truth: `LuminaDB-UI-Prototype.html`.

- **Look:** warm paper (#F2EDE1), ink (#1C1A16), one action blue (#1F3FBF), vermillion (#D83A1E) for corrections and danger, green/amber for status. No gradients, no blur, no drop-shadow effects (fast on old integrated GPUs); "ink" offset shadows on buttons are plain rectangles.
- **Fonts, embedded in the exe (no internet):** Bricolage Grotesque (display), IBM Plex Sans (UI), IBM Plex Mono (code/data), Instrument Serif italic (margin notes). All OFL-licensed.
- **Signature elements:**
  - status sentence in the masthead
  - binder index tabs on the right
  - notebook editor with a red margin line and handwritten-style corrections
  - ruled ledger tables
  - receipt-tape output log
  - Doctor inspection tags with a FIXED stamp
  - tear-off confirmation slips
- **Icons:** our own small SVG/Path icon set (Segoe MDL2 isn't on Windows 7).
- **Accessibility:** full keyboard use (F1–F12 tabs, Ctrl+Enter), visible focus, contrast AA, honours Windows text scaling, reduced motion when animations are turned off in Windows.
- **Performance budget:** cold start under 3 s on a 4 GB dual-core HDD PC; the result grid virtualized (100k rows scroll smoothly); idle CPU near 0.

---

## 8. Verified configuration (summary; full files in the test folders)

- **my.ini (both MariaDB versions):** `bind-address=127.0.0.1`, `port=<chosen>`, `character-set-server=utf8mb4`, `collation-server=utf8mb4_unicode_ci`, `event_scheduler=ON`, `local_infile=ON`, `secure_file_priv=<data\files>`, sensible `sql_mode`; on 11.8 also `character_set_collations=utf8mb4=utf8mb4_unicode_ci` so dumps import on 10.4. `[mysql]` section: `local-infile=1`. Exact lines: `ParityTests/database/RESULTS.md` §3.
- **php.ini:** from php.ini-development with an absolute `extension_dir`; mysqli, pdo_mysql, mbstring, openssl, curl, fileinfo, gd, intl, zip, sodium; `display_errors=Off` + our error handler; `SMTP=127.0.0.1`, `smtp_port=<mail catcher>`; upload and post size 256M.
- **Web:** Caddy with `bind 127.0.0.1`, admin endpoint pinned to 127.0.0.1, `php_fastcgi` to php-cgi (`PHP_FCGI_CHILDREN=4`, `PHP_FCGI_MAX_REQUESTS=500`). Apache: `Define SRVROOT` on the command line, `mod_proxy_fcgi` + `ProxyFCGISetEnvIf`, `PATH` set for the httpd process only. Exact commands: `ParityTests/web/RESULTS.md` §4.

---

## 9. Quality and testing

- **Unit tests** (core services, no engines): SqlGuard, ErrorExplainer, ImportRewriter (every R01–R25 case plus the sample dumps), PortManager logic, ConfigWriter output.
- **Integration tests** against the real engines, extending EngineSpike and the parity scripts: start/stop, crash recovery, every rewrite, PHP project create → browse → save → edit, phpMyAdmin login, mail capture, backup/restore, cross-engine-set export/import.
- **Clean-PC test:** Windows Sandbox (on Windows 10/11 Pro) for "nothing installed, no VC++".
- **Windows 7 and 32-bit:** a Windows 7 VM if one can be obtained legally (e.g. an existing licence or a lab PC image). Until then those sets are marked "verified by binary scan only".
- **Real-user test:** the beginner-tester agent clicks through every screen and writes a report.
- **Release checklist:** signed exe, antivirus scan (VirusTotal), size check, smoke test of each engine set.

---

## 10. Out of scope (explicit)

- FileZilla FTP, Tomcat and Perl from XAMPP (rarely used in coursework).
- Remote MySQL administration beyond "connect to another server" (no SSH tunnels).
- Migration from SQL Server, Oracle or Access.
- macOS and Linux.

---

## 11. Internal build order (one release at the end)

1. **Core:** EngineSetSelector, PortManager, ConfigWriter, ProcessSupervisor, MariaDbService (port EngineSpike code), plus unit and integration tests.
2. **Shell UI:** Ledger window, index tabs, masthead, Today, embedded fonts, icon set.
3. **Notebook:** editor, SqlGuard, ErrorExplainer, results, receipt, safe updates and Undo.
4. **Tables:** browse/insert/edit, structure editor, object editors.
5. **Projects + web stack:** WebStackService (Caddy/Apache + php-cgi), templates, error page, logs, Composer, mail catcher, demo mode, submission pack.
6. **Doctor, backups, snapshots.**
7. **Import/Export:** ImportRewriter (with an SQL tokenizer/parser for the nine hard rewrites), CSV/Excel.
8. **Diagram:** reverse, forward, sync, export.
9. **Users, Server, Settings, phpMyAdmin switch.**
10. **Packaging:** three engine sets trimmed, Inno Setup + portable zip, .NET 4.8 offline installer for Win7, signing.
11. **Full test pass** (§9) and fixes, then release.

---

## 12. Open risks

| Risk | Plan |
|---|---|
| Windows 7 and 32-bit sets untested on real hardware | Get a VM or one real PC before release; until then label them clearly |
| Antivirus false positives (unsigned exe, bundled servers) | Code signing; submit to Microsoft and vendors as false positives; Doctor guidance |
| Lab PCs that wipe changes on reboot ("deep freeze") | Detect it; recommend running from USB / D:\ |
| Package size (≈250 MB with all sets) | The installer only unpacks the needed set; the portable zip stays full for USB use |
| MySQL-8-only scripts that can't be rewritten (real `caching_sha2_password`, `NOT ENFORCED`, …) | Clear explanation in the import summary |
| PHP 8.2 / MariaDB 10.4 out of security support | Local-only binding, stated openly in Settings |
| Scope is very large | Build order §11; each step has integration tests before moving on |
