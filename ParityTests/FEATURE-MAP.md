# Feature map: XAMPP + MySQL Workbench + phpMyAdmin → LuminaDB

Every feature people use in the three tools, and how LuminaDB covers it.
**Engine** = the bundled MariaDB/PHP can already do it (see `database/RESULTS.md`, `web/RESULTS.md`); our app only needs a screen or a button.
**Build** = our app has to implement it.
Priority: **P1** first release · **P2** soon after · **P3** later · **Skip** not worth it (reason given).

## 1. XAMPP Control Panel

| Feature | How LuminaDB covers it | Priority |
|---|---|---|
| Start/stop MySQL | Engine + Build: start/stop button, clean `SHUTDOWN` on exit (tested) | P1 |
| Start/stop Apache | Engine + Build: web server start/stop (see web results) | P1 |
| Change ports when busy | Build: automatic free-port pick (tested), shown in the UI | P1 |
| "Port 80/3306 in use by…" diagnosis | Build: Doctor names the owning program | P1 |
| View logs (error log, access log) | Build: log viewer with plain-English explanations | P1 |
| Edit my.ini / php.ini / httpd.conf | Build: managed config + "advanced" editor with validation and rollback | P2 |
| Open htdocs folder | Build: "Open project folder" button | P1 |
| Shell (mysql command line) | Engine: bundled `mariadb.exe` client; Build: "Open console" button | P2 |
| Install as Windows service | **Skip**: needs admin and causes the conflicts we avoid. Optional "start with Windows" via a per-user startup entry instead | P3 |
| FileZilla FTP, Mercury mail, Tomcat, Perl | **Skip** for students (rarely used); revisit if lecturers ask | Skip |
| Netstat view | Build: part of Doctor (ports and owners) | P2 |

## 2. MySQL Workbench: SQL development

| Feature | How LuminaDB covers it | Priority |
|---|---|---|
| Connection manager (several servers) | Build: local server built in; "connect to other server" (host/port/user) | P2 |
| SSH tunnel connections | Build | P3 |
| Schema navigator (dbs, tables, views, routines, triggers) | Engine (information_schema) + Build: index tabs / tree | P1 |
| SQL editor: tabs, syntax colours, line numbers | Build (AvalonEdit) | P1 |
| Autocomplete (tables, columns, keywords) | Build, from information_schema | P1 |
| Run all / run selection / run current statement | Build | P1 |
| Beautify / format SQL | Build | P1 |
| Live error marking before running | Build (LuminaDB's own feature: margin corrections) | P1 |
| Plain-English error explanations + fixes | Build: map MariaDB error codes → explanation + fix | P1 |
| Result grid, sort, filter, copy | Build | P1 |
| Edit results in the grid and apply | Build: generates UPDATE by primary key, with preview | P2 |
| Export result to CSV / JSON / Excel | Build (CSV/JSON P1, Excel P2) | P1 |
| Action Output log with durations | Build (receipt tape) | P1 |
| Query history | Build | P2 |
| Snippets / templates | Build (examples menu) | P1 |
| Visual EXPLAIN | Engine (`EXPLAIN FORMAT=JSON`) + Build: simple diagram | P3 |
| Safe updates mode | Build (tear-off confirmation + snapshot) | P1 |
| Undo of data changes | Build (snapshot before risky statements); Workbench has no such feature | P1 |

## 3. MySQL Workbench: modelling

| Feature | How LuminaDB covers it | Priority |
|---|---|---|
| ER diagram of an existing database (reverse engineer) | Engine (FKs from information_schema) + Build: auto-laid-out diagram | P1 (students are marked on ER diagrams) |
| Draw a new ER diagram, then generate SQL (forward engineer) | Build: diagram editor → CREATE script | P2 |
| Synchronize model with database | Build: diff model vs database → ALTER script | P3 |
| Export diagram as PNG / PDF | Build | P2 |
| Table editor GUI (columns, types, PK/NN/UQ/AI, indexes, FKs, triggers) | Build: form that writes the ALTER/CREATE for you and shows it | P1 |

## 4. MySQL Workbench: server administration

| Feature | How LuminaDB covers it | Priority |
|---|---|---|
| Server status (uptime, connections, traffic) | Engine (`SHOW GLOBAL STATUS`) + Build: heartbeat strip and status page | P1 |
| Client connections, kill a query | Engine (`SHOW PROCESSLIST`, `KILL`) + Build | P2 |
| Users and privileges | Engine (CREATE USER / GRANT / roles) + Build: user editor | P2 |
| Status and system variables | Engine + Build: searchable list with explanations | P3 |
| Options file editor | Build (managed my.ini) | P2 |
| Data export (mysqldump) | Engine (bundled dump tool) + Build: export wizard | P1 |
| Data import / restore | Engine + Build: import wizard with automatic MySQL 8 / phpMyAdmin rewrites | P1 |
| Automatic backups + restore points | Build (LuminaDB feature; Workbench has none) | P1 |
| Performance dashboard / reports | Engine (status, performance_schema if on) + Build | P3 |
| Migration wizard (from SQL Server, Access, …) | **Skip** for v1 | Skip |

## 5. phpMyAdmin

| Feature | How LuminaDB covers it | Priority |
|---|---|---|
| Browse table rows, paging | Build | P1 |
| Edit / delete a row inline | Build (by primary key, Undo available) | P1 |
| Insert form generated from the table | Build: form with type hints (date picker, enum dropdown, FK lookup) | P1 |
| Structure tab (columns, indexes) | Build: table editor (above) | P1 |
| Search / query by example | Build: filter builder → WHERE | P2 |
| Import .sql / CSV | Engine + Build: wizard (handles phpMyAdmin dump quirks) | P1 |
| Export SQL / CSV / JSON / Excel / PDF | Build (SQL/CSV/JSON P1; Excel/PDF P2) | P1 |
| Operations: rename/copy database, change collation | Build | P2 |
| Routines, triggers, events editors | Engine + Build: editor with templates | P2 |
| Designer (relations view) | Same as ER diagram | P1 |
| Charts from a query | Build | P3 |
| Running phpMyAdmin itself (for teachers who demand it) | Engine: depends on bundled PHP (see web results) | P2 |

## 6. LuminaDB-only (why people switch)

| Feature | Priority |
|---|---|
| Doctor: port owner, crash repair, antivirus quarantine, VC++/UCRT check, path check, disk space | P1 |
| Margin corrections while typing, with one-click fixes | P1 |
| Plain-English errors for the top ~50 MySQL error codes | P1 |
| Undo for DELETE/UPDATE/DROP via snapshots | P1 |
| Automatic backups (every 30 min, before risky actions, on close) | P1 |
| One portable folder: copy to USB and it works | P1 |
| Windows 7 build with its own engine | P1 |
