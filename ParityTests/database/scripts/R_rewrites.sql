-- Verified rewrites for MySQL 8 / Workbench constructs that fail on MariaDB.
-- Run with --force on both versions; each line labelled R## says which failure it replaces.
CREATE DATABASE IF NOT EXISTS rw; USE rw;

-- R01 INDEX ... VISIBLE (fails on 10.4 only) -> drop the keyword
DROP TABLE IF EXISTS r01;
CREATE TABLE r01 (id INT NOT NULL, c INT, PRIMARY KEY (id), INDEX `fk_idx` (`c` ASC)) ENGINE = InnoDB;
SELECT 'R01 VISIBLE removed: OK';

-- R02 INDEX ... INVISIBLE (fails on both) -> 11.8: IGNORED ; 10.4: drop keyword (no equivalent)
DROP TABLE IF EXISTS r02;
CREATE TABLE r02 (id INT PRIMARY KEY, d DATE, INDEX date_idx (d DESC) IGNORED) ENGINE=InnoDB;
SELECT 'R02a INVISIBLE -> IGNORED (11.8 only): OK', IGNORED FROM information_schema.statistics WHERE table_schema='rw' AND table_name='r02' AND index_name='date_idx';
DROP TABLE IF EXISTS r02;
CREATE TABLE r02 (id INT PRIMARY KEY, d DATE, INDEX date_idx (d DESC)) ENGINE=InnoDB;
SELECT 'R02b INVISIBLE removed: OK';

-- R03 IDENTIFIED WITH caching_sha2_password BY 'p' -> IDENTIFIED BY 'p'
DROP USER IF EXISTS 'app8'@'%';
CREATE USER 'app8'@'%' IDENTIFIED BY 'App8#pass';
SELECT 'R03 caching_sha2 -> IDENTIFIED BY: OK', plugin FROM mysql.user WHERE user='app8';
-- R04 IDENTIFIED WITH mysql_native_password BY 'p' -> IDENTIFIED VIA mysql_native_password USING PASSWORD('p') (or plain IDENTIFIED BY)
DROP USER IF EXISTS 'appn'@'%';
CREATE USER 'appn'@'%' IDENTIFIED VIA mysql_native_password USING PASSWORD('AppN#pass');
SELECT 'R04 native -> VIA ... USING PASSWORD(): OK', plugin FROM mysql.user WHERE user='appn';
-- R04b ALTER USER form used by Workbench "Users and Privileges"
ALTER USER 'appn'@'%' IDENTIFIED BY 'AppN#pass2';
SELECT 'R04b ALTER USER IDENTIFIED BY: OK';

-- R05 utf8mb4_0900_ai_ci (fails on 10.4 only) -> utf8mb4_unicode_520_ci ; utf8mb4_0900_as_cs -> utf8mb4_bin
DROP TABLE IF EXISTS r05;
CREATE TABLE r05 (id INT PRIMARY KEY, s VARCHAR(20)) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_520_ci;
DROP TABLE IF EXISTS r05b;
CREATE TABLE r05b (s VARCHAR(5)) CHARSET=utf8mb4 COLLATE=utf8mb4_bin;
SELECT 'R05 0900 collations replaced: OK';

-- R06 GROUPING(col) -> (col IS NULL) when col itself has no NULLs ; ROLLUP + ORDER BY -> wrap in derived table
SELECT 'R06a GROUPING replacement', dept, (dept IS NULL) AS grouping_dept, SUM(salary) FROM adv.emp GROUP BY dept WITH ROLLUP;
SELECT 'R06b ROLLUP+ORDER BY via derived', x.* FROM (SELECT dept, SUM(salary) total FROM adv.emp GROUP BY dept WITH ROLLUP) x ORDER BY x.dept IS NULL, x.dept DESC;

-- R07 LATERAL derived table -> correlated subquery in select list (single value) or derived table + join
SELECT 'R07a LATERAL -> correlated scalar', d.name, (SELECT MAX(s.age) FROM school.student s WHERE s.dept_id=d.id) AS top FROM school.dept d;
SELECT 'R07b LATERAL -> grouped derived + LEFT JOIN', d.name, x.top FROM school.dept d LEFT JOIN (SELECT dept_id, MAX(age) top FROM school.student GROUP BY dept_id) x ON x.dept_id=d.id;

-- R08 REGEXP_LIKE(a, p) -> a REGEXP p ; REGEXP_LIKE(a,p,'c') -> a REGEXP BINARY p / COLLATE utf8mb4_bin
SELECT 'R08 REGEXP_LIKE -> REGEXP', name FROM school.student WHERE name REGEXP '^[KN]';
SELECT 'R08b REGEXP_LIKE(...,''i'') -> (?i)', name FROM school.student WHERE name REGEXP '(?i)^k';

-- R09 col->>'$.p' -> JSON_UNQUOTE(JSON_EXTRACT(col,'$.p')) ; col->'$.p' -> JSON_EXTRACT(col,'$.p')
SELECT 'R09 ->> and ->', JSON_UNQUOTE(JSON_EXTRACT(j,'$.name')), JSON_EXTRACT(j,'$.name') FROM adv.doc;

-- R10 JSON_ARRAYAGG (fails on 10.4 only) -> CONCAT('[', GROUP_CONCAT(JSON_QUOTE(x)), ']')
SELECT 'R10 JSON_ARRAYAGG emulation', CONCAT('[', GROUP_CONCAT(JSON_QUOTE(name) ORDER BY id), ']') FROM adv.emp WHERE dept='HR';
SELECT 'R10b JSON_OBJECTAGG emulation', CONCAT('{', GROUP_CONCAT(CONCAT(JSON_QUOTE(name),':',salary) ORDER BY id), '}') FROM adv.emp WHERE dept='HR';

-- R11 JSON_TABLE (fails on 10.4 only) -> recursive CTE over array index
WITH RECURSIVE idx AS (SELECT 0 n UNION ALL SELECT n+1 FROM idx WHERE n < 50)
SELECT 'R11 JSON_TABLE emulation', idx.n+1 AS rn, JSON_UNQUOTE(JSON_EXTRACT(d.j, CONCAT('$.tags[', idx.n, ']'))) AS tag
FROM adv.doc d JOIN idx ON idx.n < JSON_LENGTH(d.j, '$.tags') ORDER BY rn;

-- R12 EXPLAIN ANALYZE -> ANALYZE FORMAT=JSON
ANALYZE FORMAT=JSON SELECT * FROM adv.emp;

-- R13 CHECK (...) NOT ENFORCED -> drop the constraint (MariaDB always enforces); CHECK ... ENFORCED -> drop the word ENFORCED
DROP TABLE IF EXISTS r13;
CREATE TABLE r13 (id INT PRIMARY KEY, a INT);
SELECT 'R13 NOT ENFORCED check removed: OK';
DROP TABLE IF EXISTS r13b;
CREATE TABLE r13b (id INT PRIMARY KEY, a INT, CONSTRAINT chk_a CHECK (a > 0));
SELECT 'R13b ENFORCED word removed: OK';

-- R14 functional index INDEX ((LOWER(email))) -> virtual generated column + index
DROP TABLE IF EXISTS r14;
CREATE TABLE r14 (id INT PRIMARY KEY, email VARCHAR(100), email_lower VARCHAR(100) AS (LOWER(email)) VIRTUAL, INDEX idx_low (email_lower));
INSERT INTO r14(id,email) VALUES (1,'A@B.LK');
SELECT 'R14 functional index emulation', email_lower FROM r14 WHERE email_lower='a@b.lk';

-- R15 ALTER TABLE t ALTER INDEX i INVISIBLE -> 11.8: ALTER INDEX i IGNORED ; 10.4: none (skip statement)
DROP TABLE IF EXISTS r15; CREATE TABLE r15 (id INT PRIMARY KEY, a INT, INDEX ia (a));
ALTER TABLE r15 ALTER INDEX ia IGNORED;
SELECT 'R15 ALTER INDEX IGNORED (11.8 only): OK';

-- R16 INSERT ... AS new ON DUPLICATE KEY UPDATE a=new.a -> VALUES(a)
INSERT INTO r15 (id,a) VALUES (1,1) ON DUPLICATE KEY UPDATE a = VALUES(a);
INSERT INTO r15 (id,a) VALUES (1,7) ON DUPLICATE KEY UPDATE a = VALUES(a);
SELECT 'R16 upsert alias -> VALUES()', a FROM r15 WHERE id=1;

-- R17 TABLE t -> SELECT * FROM t ; VALUES ROW(1,2),ROW(3,4) -> VALUES (1,2),(3,4)
SELECT 'R17 TABLE ->', r15.* FROM r15;
VALUES (1,2),(3,4);

-- R18 CAST(x AS JSON) -> JSON_COMPACT(x) (validates & normalises) or just the string
SELECT 'R18 CAST AS JSON -> JSON_COMPACT', JSON_COMPACT('{"a": 1}');

-- R19 SET PERSIST v=x -> SET GLOBAL v=x  (+ app writes it into my.ini)
SET GLOBAL max_connections = 151;
SELECT 'R19 SET GLOBAL', @@global.max_connections;

-- R20 FAILED_LOGIN_ATTEMPTS n PASSWORD_LOCK_TIME d -> strip clause (MariaDB: global max_password_errors, 10.4+)
DROP USER IF EXISTS 'u10'@'%'; CREATE USER 'u10'@'%' IDENTIFIED BY 'x';
SELECT 'R20 user without FAILED_LOGIN_ATTEMPTS: OK', @@global.max_password_errors;

-- R21 CREATE ROLE 'r'@'%' -> CREATE ROLE 'r' (MariaDB roles have no host)
DROP ROLE IF EXISTS 'r11'; CREATE ROLE 'r11';
SELECT 'R21 role without host: OK';

-- R22 SET DEFAULT ROLE ALL TO u -> SET DEFAULT ROLE <role> FOR u (one role only) ; SET ROLE ALL -> SET ROLE <role>
GRANT r11 TO 'u10'@'%';
SET DEFAULT ROLE r11 FOR 'u10'@'%';
SELECT 'R22 SET DEFAULT ROLE r FOR u: OK';

-- R23 x MEMBER OF (json) -> JSON_CONTAINS(json, CAST(x AS CHAR))
SELECT 'R23 MEMBER OF -> JSON_CONTAINS', JSON_CONTAINS('[1,2,3]', '2');

-- R24 POINT SRID 4326 -> POINT REF_SYSTEM_ID=4326
DROP TABLE IF EXISTS g1; CREATE TABLE g1 (p POINT REF_SYSTEM_ID=4326 NOT NULL, SPATIAL INDEX (p));
SELECT 'R24 SRID -> REF_SYSTEM_ID: OK', SRID FROM information_schema.geometry_columns WHERE G_TABLE_SCHEMA='rw' AND G_TABLE_NAME='g1';

SELECT 'R99 END';
