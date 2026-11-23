-- Section B (single-session parts). Run with client --force -B -N
DROP DATABASE IF EXISTS adv; CREATE DATABASE adv; USE adv;
CREATE TABLE emp (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50), dept VARCHAR(20), salary DECIMAL(10,2), mgr INT NULL);
INSERT INTO emp(name,dept,salary,mgr) VALUES ('Anu','IT',100000,NULL),('Bimal','IT',80000,1),('Chamari','HR',60000,1),('Dinesh','HR',60000,3),('Eshan','IT',90000,2);
-- views
CREATE VIEW v_it AS SELECT id,name,salary FROM emp WHERE dept='IT' WITH CHECK OPTION;
SELECT 'B01 view select', COUNT(*) FROM v_it;
UPDATE v_it SET salary=salary+1 WHERE name='Bimal';
SELECT 'B02 updatable view', IF(salary=80001,'PASS','FAIL') FROM emp WHERE name='Bimal';
CREATE VIEW v_agg AS SELECT dept, SUM(salary) s FROM emp GROUP BY dept;
SELECT 'B03 next: update non-updatable view (expect ERROR 1288)';
UPDATE v_agg SET s=0;
CREATE OR REPLACE VIEW v_it2 AS SELECT id,name,dept FROM emp WHERE dept='IT' WITH CHECK OPTION;
SELECT 'B04 next: WITH CHECK OPTION violation (expect ERROR 1369)';
UPDATE v_it2 SET dept='HR' WHERE name='Eshan';
-- procedures
DELIMITER //
CREATE PROCEDURE raise_dept(IN p_dept VARCHAR(20), IN p_pct INT, OUT p_count INT, OUT p_total DECIMAL(12,2))
BEGIN
  DECLARE done INT DEFAULT 0;
  DECLARE v_id INT; DECLARE v_sal DECIMAL(10,2);
  DECLARE cur CURSOR FOR SELECT id, salary FROM emp WHERE dept=p_dept;
  DECLARE CONTINUE HANDLER FOR NOT FOUND SET done=1;
  SET p_count=0; SET p_total=0;
  OPEN cur;
  read_loop: LOOP
    FETCH cur INTO v_id, v_sal;
    IF done THEN LEAVE read_loop; END IF;
    UPDATE emp SET salary = v_sal * (100+p_pct)/100 WHERE id=v_id;
    SET p_count = p_count + 1; SET p_total = p_total + v_sal * (100+p_pct)/100;
  END LOOP;
  CLOSE cur;
END//
CREATE FUNCTION tax(s DECIMAL(10,2)) RETURNS DECIMAL(10,2) DETERMINISTIC
BEGIN
  RETURN CASE WHEN s > 85000 THEN s*0.18 ELSE s*0.06 END;
END//
CREATE PROCEDURE two_sets()
BEGIN
  SELECT 'B07 set1', COUNT(*) FROM emp;
  SELECT 'B07 set2', MAX(salary) FROM emp;
END//
CREATE PROCEDURE sig(IN x INT)
BEGIN
  IF x < 0 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='negative not allowed'; END IF;
  SELECT 'B08 signal ok path', x;
END//
DELIMITER ;
CALL raise_dept('HR', 10, @c, @t);
SELECT 'B05 proc IN/OUT + cursor', @c, @t, IF(@c=2 AND @t=132000,'PASS','FAIL');
SELECT 'B06 stored function', tax(100000), tax(50000), IF(tax(100000)=18000,'PASS','FAIL');
CALL two_sets();
CALL sig(1);
SELECT 'B08 next: SIGNAL (expect ERROR 1644)';
CALL sig(-1);
-- triggers
CREATE TABLE audit (id INT AUTO_INCREMENT PRIMARY KEY, msg VARCHAR(200), at DATETIME DEFAULT NOW());
DELIMITER //
CREATE TRIGGER emp_bi BEFORE INSERT ON emp FOR EACH ROW BEGIN SET NEW.name = UPPER(NEW.name); IF NEW.salary < 0 THEN SET NEW.salary = 0; END IF; END//
CREATE TRIGGER emp_ai AFTER INSERT ON emp FOR EACH ROW INSERT INTO audit(msg) VALUES (CONCAT('ins ',NEW.name))//
CREATE TRIGGER emp_bu BEFORE UPDATE ON emp FOR EACH ROW BEGIN IF NEW.salary < OLD.salary THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='salary cut blocked'; END IF; END//
CREATE TRIGGER emp_au AFTER UPDATE ON emp FOR EACH ROW INSERT INTO audit(msg) VALUES (CONCAT('upd ',OLD.salary,'->',NEW.salary))//
DELIMITER ;
INSERT INTO emp(name,dept,salary) VALUES ('fathima','IT',-5);
SELECT 'B09 BEFORE INSERT trigger', name, salary, IF(name='FATHIMA' AND salary=0,'PASS','FAIL') FROM emp WHERE id=LAST_INSERT_ID();
UPDATE emp SET salary=salary+5 WHERE name='Anu';
SELECT 'B10 AFTER INSERT/UPDATE triggers', GROUP_CONCAT(msg ORDER BY id SEPARATOR ' | ') FROM audit;
SELECT 'B11 next: BEFORE UPDATE trigger blocks (expect ERROR 1644)';
UPDATE emp SET salary=1 WHERE name='Anu';
-- transactions
CREATE TABLE acct (id INT PRIMARY KEY, bal INT) ENGINE=InnoDB;
INSERT INTO acct VALUES (1,100),(2,100);
START TRANSACTION; UPDATE acct SET bal=bal-50 WHERE id=1; ROLLBACK;
SELECT 'B12 rollback', bal, IF(bal=100,'PASS','FAIL') FROM acct WHERE id=1;
START TRANSACTION; UPDATE acct SET bal=bal-10 WHERE id=1; SAVEPOINT s1; UPDATE acct SET bal=bal-20 WHERE id=1; ROLLBACK TO SAVEPOINT s1; COMMIT;
SELECT 'B13 savepoint', bal, IF(bal=90,'PASS','FAIL') FROM acct WHERE id=1;
SET autocommit=0; UPDATE acct SET bal=0; ROLLBACK; SET autocommit=1;
SELECT 'B14 autocommit=0 rollback', SUM(bal), IF(SUM(bal)=190,'PASS','FAIL') FROM acct;
-- window functions
SELECT 'B15 window', name, dept, salary, ROW_NUMBER() OVER (PARTITION BY dept ORDER BY salary DESC) rn, RANK() OVER (ORDER BY salary DESC) rk, DENSE_RANK() OVER (ORDER BY salary DESC) drk, SUM(salary) OVER (PARTITION BY dept) dept_total, LAG(salary) OVER (ORDER BY id) prev FROM emp ORDER BY id;
-- CTE
WITH it AS (SELECT * FROM emp WHERE dept='IT') SELECT 'B16 CTE', COUNT(*) FROM it;
WITH RECURSIVE chain AS (SELECT id,name,mgr,0 lvl FROM emp WHERE mgr IS NULL UNION ALL SELECT e.id,e.name,e.mgr,c.lvl+1 FROM emp e JOIN chain c ON e.mgr=c.id) SELECT 'B17 recursive CTE', name, lvl FROM chain ORDER BY lvl, id;
WITH RECURSIVE n AS (SELECT 1 x UNION ALL SELECT x+1 FROM n WHERE x<10) SELECT 'B17b recursive numbers', SUM(x) FROM n;
-- JSON
CREATE TABLE doc (id INT PRIMARY KEY, j JSON);
INSERT INTO doc VALUES (1,'{"name":"Kamal","tags":["a","b"],"addr":{"city":"Kandy"}}');
SELECT 'B18 json_extract', JSON_EXTRACT(j,'$.addr.city'), JSON_UNQUOTE(JSON_EXTRACT(j,'$.name')), JSON_LENGTH(j,'$.tags') FROM doc;
SELECT 'B18b ->> operator (MySQL syntax)';
SELECT 'B18b', j->>'$.name' FROM doc;
SELECT 'B18c -> operator (MySQL syntax)';
SELECT 'B18c', j->'$.name' FROM doc;
SELECT 'B19 json_object/array/valid/arrayagg', JSON_OBJECT('a',1,'b','x'), JSON_ARRAY(1,2), JSON_VALID('{bad'), JSON_ARRAYAGG(name) FROM emp WHERE dept='HR';
SELECT 'B19b next: invalid json insert (expect ERROR 4025 CHECK json_valid)';
INSERT INTO doc VALUES (2,'{bad json');
SELECT 'B20 next: JSON_TABLE (10.6+ only)';
SELECT 'B20 json_table', jt.* FROM doc, JSON_TABLE(j, '$.tags[*]' COLUMNS (rn FOR ORDINALITY, tag VARCHAR(10) PATH '$')) AS jt;
SELECT 'B20c json column type reported in information_schema', DATA_TYPE, COLUMN_TYPE FROM information_schema.columns WHERE table_schema='adv' AND table_name='doc' AND column_name='j';
-- utf8mb4
CREATE TABLE intl (id INT PRIMARY KEY, s VARCHAR(100)) DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci;
INSERT INTO intl VALUES (1,'ශ්‍රී ලංකාව'),(2,'தமிழ் நாடு'),(3,'😀🎉👨‍👩‍👧'),(4,'Ünïcödé');
SELECT 'B21 utf8mb4 hex', id, HEX(s), CHAR_LENGTH(s), LENGTH(s) FROM intl ORDER BY id;
SELECT 'B21 utf8mb4 text', id, s FROM intl ORDER BY id;
-- collation
CREATE TABLE c (w VARCHAR(10)) CHARSET utf8mb4 COLLATE utf8mb4_unicode_ci;
INSERT INTO c VALUES ('b'),('B'),('a'),('Á'),('A'),('c');
SELECT 'B22 collation ci order', GROUP_CONCAT(w ORDER BY w, BINARY w SEPARATOR ' ') FROM c;
SELECT 'B22b ci equality a=A=Á', COUNT(*) FROM c WHERE w='a';
SELECT 'B22c bin order', GROUP_CONCAT(w ORDER BY w COLLATE utf8mb4_bin SEPARATOR ' ') FROM c;
-- fulltext
CREATE TABLE art (id INT AUTO_INCREMENT PRIMARY KEY, title VARCHAR(200), body TEXT, FULLTEXT KEY ft (title, body)) ENGINE=InnoDB;
INSERT INTO art(title,body) VALUES ('MariaDB tutorial','Learn databases with MariaDB quickly'),('Cooking rice','How to cook rice and curry'),('MySQL vs MariaDB','Comparing database engines'),('Gardening','Plants and flowers'),('Java','Programming language');
SELECT 'B23 fulltext natural', id, title FROM art WHERE MATCH(title,body) AGAINST ('mariadb') ORDER BY id;
SELECT 'B23b fulltext boolean', id FROM art WHERE MATCH(title,body) AGAINST ('+mariadb -tutorial' IN BOOLEAN MODE);
-- explain
SELECT 'B24 explain'; EXPLAIN SELECT * FROM emp WHERE id=1;
SELECT 'B25 explain format=json'; EXPLAIN FORMAT=JSON SELECT * FROM emp WHERE salary>1000;
SELECT 'B26 ANALYZE (MariaDB)'; ANALYZE SELECT * FROM emp WHERE salary>1000;
SELECT 'B26b ANALYZE FORMAT=JSON'; ANALYZE FORMAT=JSON SELECT COUNT(*) FROM emp;
SELECT 'B26c next: EXPLAIN ANALYZE (MySQL 8.0.18 syntax)'; EXPLAIN ANALYZE SELECT * FROM emp;
SELECT 'B26d ANALYZE TABLE'; ANALYZE TABLE emp;
SELECT 'B99 END';
