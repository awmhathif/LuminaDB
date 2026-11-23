# Portable MariaDB vs. Workbench + phpMyAdmin + XAMPP MySQL: parity test results

Tested on 2026-11-23, Windows 10 Pro 19045, as a normal user (no admin, no Windows service).

| Engine | Server exe | Port | Factory defaults seen at first boot |
|---|---|---|---|
| MariaDB **11.8.10** | `mariadbd.exe` | 3410 | utf8mb4 / `utf8mb4_uca1400_ai_ci`, event_scheduler OFF, local_infile 1, secure_file_priv NULL, performance_schema 0 |
| MariaDB **10.4.34** | `mysqld.exe` | 3420 | **latin1 / latin1_swedish_ci**, event_scheduler OFF, local_infile 1, secure_file_priv NULL, performance_schema 0 |

Both engines used `sql_mode = STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION`. Both bound to 127.0.0.1 only and were initialised with `<init> --datadir --port`. Both were shut down with `SHUTDOWN` at the end and neither port is listening now.

Every result below comes from a script in this folder. The raw output is in `out/`. To re-run everything, use `run_all.sh`.

Legend: **PASS** works as in MySQL 8. **FAIL** errors, with the exact text quoted. **PARTIAL** works with a caveat. "Expected error" means the server correctly rejected bad input, so that row counts as a PASS.

---

## 1. Results per section

### A. Everyday student SQL (`scripts/A_everyday.sql` -> `out/A_118.txt`, `out/A_104.txt`)

| Feature | 11.8.10 | 10.4.34 | Notes |
|---|---|---|---|
| CREATE DATABASE / USE | PASS | PASS | |
| AUTO_INCREMENT | PASS | PASS | ids 1..5 |
| PRIMARY / UNIQUE KEY | PASS | PASS | duplicate gives expected `ERROR 1062 (23000): Duplicate entry 'k@x.lk' for key 'email'` |
| FOREIGN KEY ... ON DELETE/UPDATE CASCADE | PASS | PASS | cascade delete verified. A bad parent gives expected `ERROR 1452 (23000): Cannot add or update a child row: a foreign key constraint fails (...)` |
| CHECK constraint **enforced** | PASS | PASS | Enforced: `ERROR 4025 (23000): CONSTRAINT \`student.age\` failed for \`school\`.\`student\`` |
| ENUM (+ default, invalid value in strict) | PASS | PASS | invalid gives `ERROR 1265 (01000): Data truncated for column 'gender' at row 1` |
| DEFAULT CURRENT_TIMESTAMP / ON UPDATE CURRENT_TIMESTAMP | PASS | PASS | 10.4 makes a TIMESTAMP column `NOT NULL` implicitly (old `explicit_defaults_for_timestamp=OFF` behaviour). 11.8 keeps it nullable like MySQL 8 |
| ALTER TABLE ADD / MODIFY / CHANGE / DROP COLUMN | PASS | PASS | MODIFY to NOT NULL while NULLs exist gives `ERROR 1265 ... Data truncated for column 'phone'` (strict mode, correct) |
| RENAME TABLE, TRUNCATE | PASS | PASS | |
| INSERT ... SELECT, CREATE TABLE LIKE | PASS | PASS | |
| UPDATE ... JOIN / DELETE ... JOIN | PASS | PASS | |
| INNER / LEFT / RIGHT / CROSS / self join | PASS | PASS | |
| FULL OUTER JOIN | n/a | n/a | `ERROR 1064 ... near 'FULL OUTER JOIN ...'`. MySQL 8 has no FULL OUTER JOIN either, so this is not a gap. LEFT UNION RIGHT emulation works |
| GROUP BY / HAVING, COUNT DISTINCT | PASS | PASS | |
| Subqueries (scalar, IN, EXISTS, derived) | PASS | PASS | |
| UNION / UNION ALL | PASS | PASS | |
| LIMIT n OFFSET m, LIMIT m,n | PASS | PASS | |
| LIKE / BETWEEN / IN | PASS | PASS | |
| NOW, CURDATE, DATEDIFF, DATE_FORMAT, DATE_ADD, TIMESTAMPDIFF | PASS | PASS | `DATE_FORMAT('2025-03-07 14:05','%d/%m/%Y %H:%i %W')` gives `07/03/2025 14:05 Friday` |
| CONCAT, CONCAT_WS, SUBSTRING, UPPER, LOWER, TRIM, REPLACE, LPAD... | PASS | PASS | |
| IFNULL / COALESCE / NULLIF | PASS | PASS | |
| CASE / IF() | PASS | PASS | |
| Strict mode: too long, divide by zero | PASS | PASS | `ERROR 1406 (22001): Data too long for column 'name'` and `ERROR 1365 (22012): Division by 0` |

### B. "Advanced" Workbench work (`scripts/B_advanced.sql`, `scripts/B_users_events.sh` -> `out/B_*.txt`, `out/Busers_*.txt`)

| Feature | 11.8.10 | 10.4.34 | Notes |
|---|---|---|---|
| Views | PASS | PASS | |
| Updatable view + WITH CHECK OPTION | PASS | PASS | violation gives `ERROR 1369 (44000): CHECK OPTION failed \`adv\`.\`v_it2\``. Non-updatable view gives `ERROR 1288 (HY000): The target table v_agg of the UPDATE is not updatable` |
| Stored procedure with IN/OUT params + cursor + NOT FOUND handler | PASS | PASS | `@c=2, @t=132000.00` |
| Stored function | PASS | PASS | |
| Procedure returning 2 result sets | PASS | PASS | |
| SIGNAL SQLSTATE '45000' | PASS | PASS | `ERROR 1644 (45000): negative not allowed` |
| Triggers BEFORE/AFTER INSERT/UPDATE (incl. modifying NEW, SIGNAL to block) | PASS | PASS | |
| Events + event scheduler on/off | PASS | PASS | Scheduler is **OFF by default**: 0 rows in 4 s. After `SET GLOBAL event_scheduler=ON` there were 6 rows (11.8) and 5 rows (10.4) in 4 s. ONE TIME event ends `DISABLED` |
| Transactions ROLLBACK, SAVEPOINT / ROLLBACK TO, autocommit=0 | PASS | PASS | |
| CREATE USER, GRANT/REVOKE on db, table and **column** | PASS | PASS | |
| SHOW GRANTS | PASS | PASS | |
| Logging in as the user and being denied correctly | PASS | PASS | `ERROR 1142 (42000): INSERT command denied to user 'stu'@'localhost' for table \`adv\`.\`audit\``. Column denial gives `ERROR 1143 (42000): SELECT command denied ... for column 'age'`. Wrong password gives `ERROR 1045 (28000): Access denied ...` |
| Set root password and log in with it | PASS | PASS | The client without a password gets `ERROR 1045`. **Caveat:** the installer creates 4 root rows (`localhost`, `127.0.0.1`, `::1`, `<hostname>`). `ALTER USER root@localhost` only changes one of them. TCP from 127.0.0.1 resolved to `root@localhost` here, but the GUI should set the password on all root rows (or drop the extra ones) |
| CREATE ROLE / GRANT role / SET DEFAULT ROLE ... FOR / SET ROLE NONE | PASS | PASS | |
| MySQL-8 role syntax `SET DEFAULT ROLE ALL TO u`, `SET ROLE ALL` | FAIL | FAIL | `ERROR 1064 ... near 'ALL TO 'stu'@'%''` and `... near 'ALL'` (rewrite R22) |
| Window functions ROW_NUMBER, RANK, DENSE_RANK, SUM OVER, LAG, named WINDOW with frame | PASS | PASS | |
| CTE + recursive CTE | PASS | PASS | |
| JSON_EXTRACT, JSON_UNQUOTE, JSON_OBJECT, JSON_ARRAY, JSON_VALID | PASS | PASS | |
| JSON column type | PARTIAL | PARTIAL | `JSON` is stored as `LONGTEXT` with `CHECK (json_valid(j))`. information_schema says `longtext` and MySqlConnector reports `VARCHAR`. A GUI must detect JSON columns from the `json_valid` CHECK constraint |
| `col->'$.x'` / `col->>'$.x'` | FAIL | FAIL | `ERROR 1064 ... near '>>'$.name' FROM doc'` (rewrite R09) |
| JSON_ARRAYAGG | PASS | **FAIL** | `ERROR 1305 (42000): FUNCTION adv.JSON_ARRAYAGG does not exist` (rewrite R10) |
| JSON_TABLE | PASS | **FAIL** | `ERROR 1064 ... near '(j, '$.tags[*]' COLUMNS (rn FOR ORDINALITY, ...` (rewrite R11) |
| utf8mb4 Sinhala / Tamil / emoji (incl. ZWJ sequences) byte-exact | PASS | PASS | HEX matches the Python UTF-8 encoding for all 4 samples. **Caveat (10.4 client only):** SQL passed as a command-line argument (`mysql.exe -e "..."`) is converted to the ANSI code page and stored as `3F3F...` (`?????`). SQL sent by file or stdin is byte-exact. The 11.8 client handles `-e` correctly. The app must send SQL by stdin or connector, never in argv |
| Collation sorting / ci equality | PASS | PASS | `A a Á B b c`, and `'a'='A'='Á'` under unicode_ci |
| FULLTEXT MATCH ... AGAINST (natural + boolean) on InnoDB | PASS | PASS | |
| EXPLAIN, EXPLAIN FORMAT=JSON | PASS | PASS | |
| ANALYZE (MariaDB), ANALYZE FORMAT=JSON, ANALYZE TABLE | PASS | PASS | |
| MySQL `EXPLAIN ANALYZE` | FAIL | FAIL | `ERROR 1064 ... near 'ANALYZE SELECT * FROM emp'` (rewrite R12) |

### C. MySQL 8 / Workbench scripts run through the CLI exactly as given (`scripts/C/*.sql` -> `out/C_118.txt`, `out/C_104.txt`)

| Construct | 11.8.10 | 10.4.34 | Exact error |
|---|---|---|---|
| Workbench forward-engineer script: `SET @OLD_UNIQUE_CHECKS...`, `SQL_MODE='ONLY_FULL_GROUP_BY,...'`, `CREATE SCHEMA IF NOT EXISTS \`mydb\` DEFAULT CHARACTER SET utf8 ;`, `ENGINE = InnoDB`, `INDEX ... (col ASC) VISIBLE` (C01) | **PASS** | **FAIL** | 10.4: `ERROR 1064 (42000) at line 23: ... near ')\nENGINE = InnoDB' at line 7` (the `VISIBLE` keyword). 11.8 accepts `VISIBLE`. `utf8` becomes utf8mb3 |
| Index `INVISIBLE` (unticked "Visible" in Workbench) (C01b) | FAIL | FAIL | `ERROR 1064 ... near 'INVISIBLE)\nENGINE = InnoDB'` |
| `CREATE USER ... IDENTIFIED WITH caching_sha2_password BY '...'` (C02) | FAIL | FAIL | `ERROR 1064 (42000) at line 2: ... near 'BY 'App8#pass''` |
| `CREATE USER ... IDENTIFIED WITH mysql_native_password BY '...'` (C03) | FAIL | FAIL | `ERROR 1064 (42000) at line 2: ... near 'BY 'AppN#pass''` |
| `utf8mb4_0900_ai_ci` on table and on database (C04, C04b) | **PASS** | **FAIL** | `ERROR 1273 (HY000): Unknown collation: 'utf8mb4_0900_ai_ci'` |
| `utf8mb4_0900_as_cs` | PASS | FAIL | `ERROR 1273 ... Unknown collation: 'utf8mb4_0900_as_cs'` |
| `DEFAULT (expression)` incl. `(UUID())`, `(CURRENT_DATE)`, `(JSON_ARRAY())` on JSON/TEXT (C05) | PASS | PASS | |
| `GROUP BY ... WITH ROLLUP` (C06) | PASS | PASS | |
| `GROUPING()` | FAIL | FAIL | `ERROR 1305 (42000): FUNCTION adv.GROUPING does not exist` |
| `WITH ROLLUP ... ORDER BY` (C06c) | FAIL | FAIL | `ERROR 1221 (HY000): Incorrect usage of CUBE/ROLLUP and ORDER BY` |
| `LATERAL` derived table (C07) | FAIL | FAIL | `ERROR 1064 ... near '(SELECT MAX(s.age) top FROM student s WHERE s.dept_id=d.id) AS x'` |
| `REGEXP_LIKE()` (C08) | FAIL | FAIL | `ERROR 1305 (42000): FUNCTION school.REGEXP_LIKE does not exist`. `REGEXP_REPLACE/SUBSTR/INSTR` and `REGEXP` all PASS |
| Invisible columns `INVISIBLE` (C09) | PASS | PASS | `SELECT *` hides the column, explicit select works |
| `CHECK (...) NOT ENFORCED` (C10a) | FAIL | FAIL | `ERROR 1064 ... near 'NOT ENFORCED)'` |
| Functional index `INDEX ((LOWER(email)))` (C10b) | FAIL | FAIL | `ERROR 1064 ... near '(LOWER(email))))'` |
| `ALTER TABLE t ALTER INDEX i INVISIBLE` (C10c) | FAIL | FAIL | 11.8: `... near 'INVISIBLE'`. 10.4: `... near 'INDEX ia INVISIBLE'` |
| `INSERT ... AS new ON DUPLICATE KEY UPDATE a=new.a` (C10d) | FAIL | FAIL | `ERROR 1064 ... near 'AS new ON DUPLICATE KEY UPDATE a = new.a'`. Old `VALUES(a)` form PASS |
| `TABLE t` statement (C10f) | FAIL | FAIL | `ERROR 1064 ... near 'TABLE m3'` |
| `VALUES ROW(1,2), ROW(3,4)` (C10g) | FAIL | FAIL | `ERROR 1064 ... near 'ROW(1,2), ROW(3,4)'`. MariaDB form `VALUES (1,2),(3,4)` PASS |
| `CAST(x AS JSON)` (C10h) | FAIL | FAIL | `ERROR 1064 ... near 'JSON)'` |
| `SET PERSIST` (C10i) | FAIL | FAIL | `ERROR 1064 ... near 'max_connections = 151'` |
| `FAILED_LOGIN_ATTEMPTS n PASSWORD_LOCK_TIME d` (C10j) | FAIL | FAIL | `ERROR 1064 ... near 'FAILED_LOGIN_ATTEMPTS 3 PASSWORD_LOCK_TIME 1'` |
| `CREATE ROLE 'r'@'%'` (role with host) (C10m) | FAIL | FAIL | `ERROR 1064 ... near '@'%''` |
| `x MEMBER OF (json)` (C10q) | FAIL | FAIL | `ERROR 1064 ... near 'OF ('[1,2,3]')'` |
| `POINT NOT NULL SRID 4326` (C10r) | FAIL | FAIL | `ERROR 1064 ... near '4326, SPATIAL INDEX (p))'` |
| `utf8mb3`, `EXCEPT`, `INTERSECT`, `ROW_FORMAT=DYNAMIC`, `DEFINER ... SQL SECURITY` view, `GRANT role ... WITH ADMIN OPTION` | PASS | PASS | |

### D. phpMyAdmin 5.2 export from a MySQL 8 server (`scripts/D_phpmyadmin_export.sql`, check `scripts/D_verify.sql` -> `out/D_both.txt`, `out/RW_*.txt`)

The dump contains: header comments, `SET SQL_MODE = "NO_AUTO_VALUE_ON_ZERO"`, `START TRANSACTION`, `SET time_zone`, `/*!40101 ... */` conditional comments, CREATE TABLE without keys (one table uses `utf8mb4_0900_ai_ci`, MySQL-8 `int` without width, `year`, `enum`), multi-row INSERT including a row with **id 0**, `ALTER TABLE ... ADD PRIMARY KEY, ADD UNIQUE KEY, ADD KEY`, `ALTER TABLE ... MODIFY ... AUTO_INCREMENT, AUTO_INCREMENT=5`, `ALTER TABLE ... ADD CONSTRAINT ... FOREIGN KEY ... ON DELETE CASCADE`, and `COMMIT`.

| Check | 11.8.10 | 10.4.34 | Notes |
|---|---|---|---|
| Import unmodified via CLI | **PASS** (exit 0) | **FAIL** | 10.4: `ERROR 1273 (HY000) at line 23: Unknown collation: 'utf8mb4_0900_ai_ci'`. Import stops, nothing is created |
| Import after rewrite (0900 to `utf8mb4_unicode_520_ci`) | PASS | PASS | |
| Row counts 4/5/2, id 0 preserved (NO_AUTO_VALUE_ON_ZERO), next AUTO_INCREMENT 5/5/3, FKs with CASCADE/RESTRICT, Sinhala byte-exact, next insert id = 5 | PASS | PASS (after rewrite) | |

### E. Backup / restore and data tools (`scripts/E_backup.sh` -> `out/E.txt`)

| Feature | 11.8.10 | 10.4.34 | Notes |
|---|---|---|---|
| `mariadb-dump` / `mysqldump --routines --triggers --events --single-transaction --databases adv school` | PASS | PASS | |
| Drop, restore, compare | PASS | PASS | Row counts, 4 routines, 4 triggers, event, 3 views, FK count and MD5 of the utf8mb4 data are identical before and after. Restored proc/function run |
| `SELECT ... INTO OUTFILE` | PASS | PASS | Default `secure_file_priv = NULL` means no restriction: any path the server process can write. Overwriting gives `ERROR 1086 (HY000): File '...' already exists`. utf8mb4 CSV is byte-correct |
| `LOAD DATA LOCAL INFILE`: does the client need `--local-infile=1`? | No | No | Both bundled clients allow LOCAL by default and server `local_infile=1` by default. Quoted fields with commas and Sinhala/Tamil load fine |
| Client `--local-infile=0` or server `SET GLOBAL local_infile=0` | expected error | expected error | 11.8: `ERROR 4166 (HY000): The used command is not allowed because the MariaDB server or client has disabled the local infile capability`. 10.4: `ERROR 1148 (42000): The used command is not allowed with this MariaDB version` |
| Server-side `LOAD DATA INFILE` (no LOCAL) | PASS | PASS | allowed because secure_file_priv is NULL |
| `mariadb-import` / `mysqlimport --local` | PASS | PASS | |
| **11.8 dump into 10.4 server, 10.4 client** | FAIL | | `ERROR at line 1: Unknown command '\-'.` from the 11.8 dump's first line `/*M!999999\- enable the sandbox mode */` |
| 11.8 dump into 10.4 server, 11.8 client | FAIL | | `ERROR 1273 (HY000) at line 23: Unknown collation: 'utf8mb4_uca1400_ai_ci'` (11.8's default collation) |
| 11.8 dump into 10.4 after rewrite (delete sandbox line 1, `utf8mb4_uca1400_*` to `utf8mb4_unicode_520_ci`) | PASS | | all objects and data MD5 identical to source |
| 11.8 dump into 10.4 when 11.8 runs the recommended defaults (section 3) via 11.8 client | PASS | | no uca1400 in dump, so only the sandbox line matters, and the 11.8 client understands it |
| **10.4 dump into 11.8 server** | | PASS | routines, triggers, data MD5, function call all OK |
| 11.8 dump tool against 10.4 server / 10.4 dump tool against 11.8 server | PASS | PASS | both exit 0 |

### F. What a GUI needs to read (`scripts/F_gui.sql`, `F_kill.sh`, `F_slowlog.sh` -> `out/F_*.txt`)

| Feature | 11.8.10 | 10.4.34 | Notes |
|---|---|---|---|
| information_schema.TABLES (type, engine, rows, size, collation, AUTO_INCREMENT) | PASS | PASS | |
| COLUMNS (COLUMN_TYPE, default, COLUMN_KEY, EXTRA, collation, GENERATION_EXPRESSION) | PASS | PASS | defaults come back as expressions (`current_timestamp()`), and string defaults are quoted (`'O'`) |
| FKs for ER diagram (KEY_COLUMN_USAGE JOIN REFERENTIAL_CONSTRAINTS) | PASS | PASS | referenced table/column plus UPDATE/DELETE rule |
| STATISTICS (indexes incl. FULLTEXT) | PASS | PASS | 11.8 also has an `IGNORED` column (= MySQL "invisible") |
| CHECK_CONSTRAINTS | PASS | PASS | also shows the implicit `json_valid()` checks |
| ROUTINES + PARAMETERS, TRIGGERS, VIEWS (definition, IS_UPDATABLE, CHECK_OPTION), EVENTS, SCHEMATA | PASS | PASS | |
| SHOW CREATE TABLE / VIEW / PROCEDURE / TRIGGER | PASS | PASS | |
| SHOW FULL PROCESSLIST / information_schema.PROCESSLIST | PASS | PASS | 10.4 also lists InnoDB daemon threads (`Command='Daemon'`), so the GUI should filter them |
| KILL QUERY / KILL CONNECTION from another session | PASS | PASS | victim gets `ERROR 1317 (70100): Query execution was interrupted` or `ERROR 2013 (HY000): Lost connection ...` within 0 s |
| GLOBAL STATUS: Questions, Threads_connected, Uptime, Bytes_sent (+ Com_*, Slow_queries...) | PASS | PASS | via `information_schema.GLOBAL_STATUS` or `SHOW GLOBAL STATUS` |
| MySQL-8 style `performance_schema.global_status` | PASS (empty while PS off) | FAIL | 10.4: `ERROR 1146 (42S02): Table 'performance_schema.global_status' doesn't exist`. Use information_schema.GLOBAL_STATUS |
| performance_schema on by default? | **No** (`@@performance_schema=0`) | **No** | `performance_schema=ON` in my.ini works on both after restart (digest table filled). It is read-only at runtime |
| `sys` schema | present | absent | |
| Slow query log toggled at runtime | PASS | PASS | `SET GLOBAL slow_query_log=1, long_query_time=0.2, log_output='TABLE,FILE'` works. **It applies to new connections only**: the same session logged 0 rows, a new connection logged 1 row in mysql.slow_log and the file |
| SET GLOBAL without restart (max_connections, event_scheduler, sql_mode, general_log, innodb_buffer_pool_size) | PASS | PARTIAL | 10.4 resizes the buffer pool in the background. A second resize within seconds fails with `ERROR 1210 (HY000): Another buffer pool resize is already in progress.` Read-only variables fail with `ERROR 1238 (HY000): Variable 'port' is a read only variable` (also innodb_log_file_size on 10.4. 11.8 accepts that one at runtime) |

### G. Connectors (`ConnTest/` net48 + MySqlConnector 2.4.0, built offline from the NuGet cache -> `out/G_3410.txt`, `out/G_3420.txt`)

| Feature | 11.8.10 | 10.4.34 | Notes |
|---|---|---|---|
| Connect as a password user / wrong password rejected | PASS | PASS | 1045 on a wrong password |
| TLS | auto-TLS on (`ECDHE-RSA-AES256-GCM-SHA384`, SslMode=Preferred) | no TLS (plain) | |
| `ServerVersion` | `11.8.10-MariaDB` | `5.5.5-10.4.34-MariaDB` | the app's version parser must strip the `5.5.5-` prefix |
| utf8mb4 round trip via parameters (Sinhala, Tamil, emoji ZWJ, accents) | PASS | PASS | string and HEX match |
| Prepared statements (`Prepare()`, 3 executions) | PASS | PASS | server `Com_stmt_execute` +3, so the binary protocol was used |
| Multiple result sets from `CALL` | PASS | PASS | 2 sets |
| OUT params with `CommandType.StoredProcedure` | PASS | PASS | |
| Cancel `SELECT SLEEP(30)`: `MySqlCommand.Cancel()`, KILL QUERY from another connection, CancellationToken, CommandTimeout=2 | PASS | PASS | returns in 1.5 s (2.0 s for the timeout). Connection is still usable afterwards |
| Multi-statement batch + transaction rollback | PASS | PASS | |
| Column schema (PK, AUTO_INCREMENT) | PASS | PASS | |
| JSON column | PARTIAL | PARTIAL | reported as `VARCHAR`/`String` (see B) |
| **Python** | NOT TESTED | NOT TESTED | No driver is installed (`pymysql`, `mysql.connector`, `MySQLdb`, `mariadb` all give `No module named ...`). It would need `pip install`, which the rules exclude |
| Bundled **11.8 CLI client to 10.4 server with a password** | n/a | FAIL | `ERROR 2026 (HY000): TLS/SSL error: SSL is required, but the server does not support it`. Fix: `--skip-ssl` or `--ssl-verify-server-cert=0` (both verified). The 11.8 client to the 11.8 server with a password works with no flags. Without a password the 11.8 client only warns `WARNING: option --ssl-verify-server-cert is disabled, because of an insecure passwordless login.` |

---

## 2. Rewrites our app must do

Every rewrite below was run on both engines. The manual forms are in `scripts/R_rewrites.sql` (output `out/R_118.txt`, `out/R_104.txt`).

`scripts/rewrite_mysql8.py <118|104> in.sql out.sql` is a regex prototype of the automatic ones. `scripts/run_rewritten.sh` checked it by re-running the rewritten C01, C01b, C02, C03, C04, C04b, C08 and the phpMyAdmin dump with no `--force`. All exit 0 on both versions, and the dump passes every D_verify check (`out/RW_*.txt`).

"Both" means the construct fails on both versions. "10.4" means it only fails there.

| # | Fails on | MySQL 8 / Workbench / phpMyAdmin input | Verified MariaDB rewrite |
|---|---|---|---|
| R01 | 10.4 | `INDEX \`i\` (\`c\` ASC) VISIBLE` | delete ` VISIBLE` (11.8 accepts it as is) |
| R02 | both | `INDEX i (...) INVISIBLE` | 11.8: `INDEX i (...) IGNORED`. 10.4: delete `INVISIBLE` (no equivalent) |
| R03 | both | `IDENTIFIED WITH caching_sha2_password BY 'p'` (also `sha256_password`) | `IDENTIFIED BY 'p'` (plugin ends up mysql_native_password) |
| R04 | both | `IDENTIFIED WITH mysql_native_password BY 'p'` | `IDENTIFIED BY 'p'` or `IDENTIFIED VIA mysql_native_password USING PASSWORD('p')` |
| R05 | 10.4 | `utf8mb4_0900_ai_ci` (and other `utf8mb4_0900_*_ci`) / `utf8mb4_0900_as_cs`, `utf8mb4_0900_bin` | `utf8mb4_unicode_520_ci` / `utf8mb4_bin` |
| R05b | 10.4 | 11.8-made dumps: `utf8mb4_uca1400_ai_ci` (any `uca1400`) | `utf8mb4_unicode_520_ci` (or avoid it with the 11.8 defaults in section 3) |
| R05c | 10.4 client | 11.8 dump line 1 `/*M!999999\- enable the sandbox mode */` | delete that line, or import with the 11.8 client |
| R06 | both | `GROUPING(col)` | `(col IS NULL)` (correct only when col has no real NULLs) |
| R06b | both | `... WITH ROLLUP ORDER BY x` | `SELECT * FROM (... WITH ROLLUP) t ORDER BY ...` |
| R07 | both | `FROM a, LATERAL (SELECT ... WHERE b.k=a.k) x` | correlated scalar subquery in the SELECT list, or `LEFT JOIN (SELECT k, agg ... GROUP BY k) x ON x.k=a.k` |
| R08 | both | `REGEXP_LIKE(a, 'p')` / `REGEXP_LIKE(a,'p','i')` | `a REGEXP 'p'` / `a REGEXP '(?i)p'` |
| R09 | both | `col->>'$.p'` / `col->'$.p'` | `JSON_UNQUOTE(JSON_EXTRACT(col,'$.p'))` / `JSON_EXTRACT(col,'$.p')` |
| R10 | 10.4 | `JSON_ARRAYAGG(x)` / `JSON_OBJECTAGG(k,v)` | `CONCAT('[',GROUP_CONCAT(JSON_QUOTE(x)),']')` / `CONCAT('{',GROUP_CONCAT(CONCAT(JSON_QUOTE(k),':',v)),'}')` |
| R11 | 10.4 | `JSON_TABLE(j,'$.a[*]' COLUMNS(...))` | recursive CTE of indexes 0..n joined with `JSON_EXTRACT(j, CONCAT('$.a[',n,']'))` while `n < JSON_LENGTH(j,'$.a')` |
| R12 | both | `EXPLAIN ANALYZE SELECT ...` | `ANALYZE FORMAT=JSON SELECT ...` |
| R13 | both | `CHECK (expr) NOT ENFORCED` / `CHECK (expr) ENFORCED` | delete the constraint (MariaDB always enforces) / delete the word `ENFORCED` |
| R14 | both | `INDEX idx ((LOWER(email)))` | `email_lower VARCHAR(100) AS (LOWER(email)) VIRTUAL, INDEX idx (email_lower)` |
| R15 | both | `ALTER TABLE t ALTER INDEX i INVISIBLE / VISIBLE` | 11.8: `ALTER INDEX i IGNORED / NOT IGNORED`. 10.4: skip |
| R16 | both | `INSERT ... VALUES (...) AS new ON DUPLICATE KEY UPDATE a=new.a` | `... ON DUPLICATE KEY UPDATE a=VALUES(a)` |
| R17 | both | `TABLE t;` / `VALUES ROW(1,2), ROW(3,4)` | `SELECT * FROM t;` / `VALUES (1,2),(3,4)` |
| R18 | both | `CAST(x AS JSON)` | `JSON_COMPACT(x)` |
| R19 | both | `SET PERSIST v = x` | `SET GLOBAL v = x`, and the app writes `v=x` into my.ini |
| R20 | both | `FAILED_LOGIN_ATTEMPTS n PASSWORD_LOCK_TIME d` | delete the clause (MariaDB only has the global `max_password_errors`) |
| R21 | both | `CREATE/DROP ROLE 'r'@'%'` | `CREATE/DROP ROLE 'r'` |
| R22 | both | `SET DEFAULT ROLE ALL TO u` / `SET ROLE ALL` | `SET DEFAULT ROLE r FOR u` (one role) / `SET ROLE r` |
| R23 | both | `x MEMBER OF (json)` | `JSON_CONTAINS(json, 'x')` |
| R24 | both | `POINT NOT NULL SRID 4326` | `POINT REF_SYSTEM_ID=4326 NOT NULL` (information_schema.GEOMETRY_COLUMNS.SRID = 4326) |
| R25 | 11.8 client to 10.4 server | password login fails with ERROR 2026 | pass `--skip-ssl` (or `--ssl-verify-server-cert=0`) to the 11.8 client |

Some of these cannot be done safely with a regex: R06, R06b, R07, R10, R11, R14, R16, R18, R23 and the multi-role part of R22. They need a statement parser. For them the app should catch the error number (1064/1305/1221) and offer the rewrite instead of applying it silently.

Semantic caveats:
- R03/R04 store a weaker hash.
- R05 sorting differs from 0900 in rare cases.
- R06 gives a wrong answer if the column has real NULLs.
- R13 drops the constraint definition.

---

## 3. Defaults our app should set (verified by restart: `out/recommended_*.ini`, `scripts/defaults_verify.sh` -> `out/defaults_verify.txt`)

```ini
[mysqld]
datadir=<data dir>
port=<port>
bind-address=127.0.0.1
character-set-server=utf8mb4
collation-server=utf8mb4_unicode_ci
# 11.8 ONLY (10.4 does not know this variable): stop explicit "CHARSET utf8mb4" from picking uca1400,
# so dumps stay importable into 10.4 / MySQL 8
character_set_collations=utf8mb4=utf8mb4_unicode_ci,utf8mb3=utf8mb3_unicode_ci
event_scheduler=ON
local_infile=ON
secure_file_priv="<app dir>/exports"
sql_mode="STRICT_TRANS_TABLES,ERROR_FOR_DIVISION_BY_ZERO,NO_AUTO_CREATE_USER,NO_ENGINE_SUBSTITUTION"
max_allowed_packet=256M
innodb_buffer_pool_size=256M
performance_schema=ON
slow_query_log=OFF
long_query_time=2
log_output=FILE
lower_case_table_names=1

[client]
port=<port>
default-character-set=utf8mb4
plugin-dir=<engine>/lib/plugin

[mysql]
local-infile=1
```

What the restart showed:
- **Character set:**
  - 11.8: a plain `CREATE TABLE` and an explicit `CHARSET utf8mb4` both get `utf8mb4_unicode_ci`.
  - 10.4: a plain table gets utf8mb4_unicode_ci (it was latin1 at factory default). An explicit `CHARSET utf8mb4` still gets `utf8mb4_general_ci` because 10.4 cannot remap it, so mixed collations remain possible there.
  - Without these lines, 10.4 makes latin1 tables. Strict mode then rejects Sinhala with `ERROR 1366 (22007): Incorrect string value: '\xE0\xB7\x81...'`.
- **event_scheduler** thread is running at boot.
- **performance_schema** is usable after restart.
- **secure_file_priv:** INTO OUTFILE works inside the folder. Outside it fails with `ERROR 1290 (HY000): The MariaDB server is running with the --secure-file-priv option so it cannot execute this statement`. Keep it unset (NULL) only if students must write CSVs anywhere.
- **local-infile** must go under `[mysql]` (the CLI), **not** `[client]`. In `[client]` it breaks the dump tools: `mysqldump.exe: unknown variable 'local-infile=1'` and the same for `mariadb-dump.exe`. The clients already default to LOCAL allowed.
- **sql_mode:** keep MariaDB's default. It is already strict, and MySQL 8's extra `ONLY_FULL_GROUP_BY` is still applied per script by Workbench headers, which worked.
- **Not set on purpose:** `skip-name-resolve`. With it, TCP logins from 127.0.0.1 would match the passwordless `root@127.0.0.1` row instead of `root@localhost`. This was not tested; it is inferred from the account list.
- **Raw phpMyAdmin MySQL-8 dump with these defaults:** imports on 11.8 (3 tables). On 10.4 it still needs R05, because no my.ini setting fixes it.

---

## 4. Verdict

Yes for 11.8.10, with a short, known rewrite list. 10.4.34 is close but needs more rewrites.

On both versions everything a student or developer does day to day worked the same as in Workbench, phpMyAdmin and XAMPP:
- all of section A
- views, procedures with cursors and OUT params, functions, triggers, events, transactions and savepoints
- users with database, table and column grants, correct denials, root password, roles
- window functions, CTEs, full-text search, EXPLAIN/ANALYZE
- byte-exact Sinhala, Tamil and emoji
- dump/restore with routines, triggers and events
- CSV in and out
- every information_schema view a Workbench-style GUI needs, PROCESSLIST/KILL, status counters, runtime SET GLOBAL
- the .NET connector: prepared statements, multiple result sets, cancellation

11.8 even runs a stock Workbench forward-engineer script and a MySQL-8 phpMyAdmin dump (with `utf8mb4_0900_ai_ci`) unmodified. 10.4 does not: it needs the `VISIBLE`/0900-collation rewrites. 10.4 also lacks JSON_TABLE and JSON_ARRAYAGG, uses latin1 unless configured, and its CLI corrupts Unicode passed in argv.

The real gaps are MySQL-8-only syntax: `caching_sha2_password` users, `->`/`->>`, `LATERAL`, `REGEXP_LIKE`, `GROUPING()`, ROLLUP with ORDER BY, functional indexes, `CHECK ... NOT ENFORCED`, `INSERT ... AS alias`, `TABLE`, `VALUES ROW`, `CAST AS JSON`, `MEMBER OF`, `SRID`, `SET PERSIST`, per-account lockout and host-qualified roles. Each one has a verified rewrite above, but some of them need a parser.

What is genuinely impossible:
- a real `caching_sha2_password` account. Any client that insists on that plugin cannot be served, and hashes copied from MySQL 8 `mysql.user` cannot be imported.
- true unenforced CHECK constraints
- invisible indexes on 10.4
- MySQL 8's exact 0900 sort order on 10.4
- the MySQL-8 `performance_schema`/`sys` layout on 10.4, plus `SET PERSIST`'s mysqld-auto.cnf
- `EXPLAIN ANALYZE`'s tree output format. MariaDB's `ANALYZE FORMAT=JSON` gives equivalent data in a different format.

### Not tested, and why
- **Python drivers:** none are installed and pip is excluded.
- **A real MySQL 8 server and a genuine mysqldump 8 file:** there is no MySQL 8 binary here and downloads are not allowed. The phpMyAdmin/Workbench scripts were hand-written to match their formats.
- **The Workbench/phpMyAdmin GUIs connecting to MariaDB:** not installed.
- **`skip-name-resolve` behaviour:** not tested.
- **The "Illegal mix of collations" error on 10.4 general_ci/unicode_ci joins:** predicted, not reproduced.
- **Very large imports:** not tested.
