-- Section A: everyday student SQL. Run with: client --force -t < A_everyday.sql
DROP DATABASE IF EXISTS school;
CREATE DATABASE school;
USE school;
SELECT 'A01 create db/use' t, DATABASE() db;
CREATE TABLE dept (id INT AUTO_INCREMENT PRIMARY KEY, name VARCHAR(50) NOT NULL UNIQUE);
CREATE TABLE student (
  id INT AUTO_INCREMENT PRIMARY KEY,
  name VARCHAR(100) NOT NULL,
  email VARCHAR(100) UNIQUE,
  age INT CHECK (age >= 16),
  gender ENUM('M','F','O') DEFAULT 'O',
  dept_id INT,
  created_at DATETIME DEFAULT CURRENT_TIMESTAMP,
  updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP ON UPDATE CURRENT_TIMESTAMP,
  CONSTRAINT fk_dept FOREIGN KEY (dept_id) REFERENCES dept(id) ON DELETE CASCADE ON UPDATE CASCADE
);
INSERT INTO dept(name) VALUES ('IT'),('Business'),('English');
INSERT INTO student(name,email,age,gender,dept_id) VALUES
 ('Kamal','k@x.lk',20,'M',1),('Nimali','n@x.lk',22,'F',1),('Ravi','r@x.lk',19,'M',2),('Sita','s@x.lk',25,'F',NULL),('Ahmed','a@x.lk',30,'M',3);
SELECT 'A02 auto_increment' t, GROUP_CONCAT(id ORDER BY id) ids, IF(GROUP_CONCAT(id ORDER BY id)='1,2,3,4,5','PASS','FAIL') r FROM student;
SELECT 'A03 default current_timestamp' t, IF(COUNT(*)=5,'PASS','FAIL') r FROM student WHERE created_at IS NOT NULL AND updated_at IS NOT NULL;
INSERT INTO student(name,email,age) VALUES ('NoGender','ng@x.lk',21); SELECT 'A04 enum default' t, gender, IF(gender='O','PASS','FAIL') FROM student WHERE name='NoGender'; DELETE FROM student WHERE name='NoGender';
SELECT 'A05 next: CHECK violation (expect ERROR 4025)' t;
INSERT INTO student(name,age) VALUES ('Kid',10);
SELECT 'A05b check enforced?' t, IF(COUNT(*)=0,'PASS enforced','FAIL not enforced') r FROM student WHERE name='Kid';
SELECT 'A06 next: UNIQUE violation (expect ERROR 1062)' t;
INSERT INTO student(name,email,age) VALUES ('Dup','k@x.lk',20);
SELECT 'A07 next: FK violation (expect ERROR 1452)' t;
INSERT INTO student(name,age,dept_id) VALUES ('Bad',20,99);
SELECT 'A08 next: ENUM invalid in strict (expect ERROR 1265)' t;
INSERT INTO student(name,age,gender) VALUES ('BadEnum',20,'X');
-- ON DELETE CASCADE
DELETE FROM dept WHERE id=3;
SELECT 'A09 on delete cascade' t, IF(COUNT(*)=0,'PASS','FAIL') r FROM student WHERE name='Ahmed';
-- ALTER TABLE
ALTER TABLE student ADD COLUMN phone VARCHAR(15) AFTER email;
UPDATE student SET phone='000'; ALTER TABLE student MODIFY COLUMN phone VARCHAR(20) NOT NULL DEFAULT '';
ALTER TABLE student CHANGE COLUMN phone mobile VARCHAR(20);
ALTER TABLE student DROP COLUMN mobile;
SELECT 'A10 alter add/modify/change/drop' t, IF(COUNT(*)=0,'PASS','FAIL') r FROM information_schema.columns WHERE table_schema='school' AND table_name='student' AND column_name IN ('phone','mobile');
CREATE TABLE tmp1 (x INT);
RENAME TABLE tmp1 TO tmp2;
INSERT INTO tmp2 VALUES (1),(2);
TRUNCATE TABLE tmp2;
SELECT 'A11 rename+truncate' t, IF(COUNT(*)=0,'PASS','FAIL') r FROM tmp2;
CREATE TABLE student_archive LIKE student;
INSERT INTO student_archive SELECT * FROM student WHERE age > 20;
SELECT 'A12 insert..select' t, COUNT(*) n FROM student_archive;
UPDATE student s JOIN dept d ON s.dept_id=d.id SET s.age=s.age+1 WHERE d.name='IT';
SELECT 'A13 update join' t, GROUP_CONCAT(age ORDER BY id) ages, IF(GROUP_CONCAT(age ORDER BY id)='21,23,19,25','PASS','FAIL') r FROM student;
DELETE s FROM student_archive s JOIN dept d ON s.dept_id=d.id WHERE d.name='IT';
SELECT 'A14 delete join' t, COUNT(*) remaining FROM student_archive;
SELECT 'A15 inner join' t, s.name, d.name dept FROM student s INNER JOIN dept d ON s.dept_id=d.id ORDER BY s.id;
SELECT 'A16 left join' t, s.name, d.name dept FROM student s LEFT JOIN dept d ON s.dept_id=d.id ORDER BY s.id;
SELECT 'A17 right join' t, s.name, d.name dept FROM student s RIGHT JOIN dept d ON s.dept_id=d.id ORDER BY d.id;
SELECT 'A18 cross join count' t, COUNT(*) n FROM student CROSS JOIN dept;
SELECT 'A19 self join' t, a.name, b.name FROM student a JOIN student b ON a.dept_id=b.dept_id AND a.id<b.id;
SELECT 'A20 full outer join emulated via UNION' t, s.name, d.name FROM student s LEFT JOIN dept d ON s.dept_id=d.id UNION SELECT 'A20 full outer join emulated via UNION', s.name, d.name FROM student s RIGHT JOIN dept d ON s.dept_id=d.id;
SELECT 'A20b native FULL OUTER JOIN (not in MySQL either, expect error)' t;
SELECT s.name, d.name FROM student s FULL OUTER JOIN dept d ON s.dept_id=d.id;
SELECT 'A21 group by having' t, dept_id, COUNT(*) c, AVG(age) a FROM student GROUP BY dept_id HAVING COUNT(*)>=1 ORDER BY dept_id;
SELECT 'A22 subquery scalar/IN/EXISTS' t, name FROM student WHERE age > (SELECT AVG(age) FROM student) AND dept_id IN (SELECT id FROM dept) AND EXISTS (SELECT 1 FROM dept WHERE id=student.dept_id);
SELECT 'A23 derived table' t, x.mx FROM (SELECT MAX(age) mx FROM student) x;
SELECT 'A24 union / union all' t, name FROM student UNION ALL SELECT 'A24 union / union all', name FROM dept;
SELECT 'A25 limit offset' t, name FROM student ORDER BY id LIMIT 2 OFFSET 1;
SELECT 'A25b limit a,b' t, name FROM student ORDER BY id LIMIT 1,2;
SELECT 'A26 like/between/in' t, name FROM student WHERE name LIKE '%a%' AND age BETWEEN 18 AND 30 AND gender IN ('M','F');
SELECT 'A27 date funcs' t, NOW() IS NOT NULL n, CURDATE() IS NOT NULL c, DATEDIFF('2025-03-01','2025-02-01') dd, DATE_FORMAT('2025-03-07 14:05:00','%d/%m/%Y %H:%i %W') f, DATE_ADD('2025-01-31', INTERVAL 1 MONTH) da, YEAR(NOW())>2000 y, TIMESTAMPDIFF(YEAR,'2000-05-01','2025-04-30') td;
SELECT 'A28 string funcs' t, CONCAT('a','-','b') c, CONCAT_WS(',','x','y') cw, SUBSTRING('database',1,4) s, UPPER('abc') u, LOWER('ABC') l, LENGTH('abc') len, TRIM('  x ') tr, REPLACE('aXb','X','-') rp, LEFT('hello',2) lf, LPAD('5',3,'0') lp;
SELECT 'A29 ifnull/coalesce/nullif' t, IFNULL(NULL,'d') a, COALESCE(NULL,NULL,'c') b, NULLIF(1,1) c;
SELECT 'A30 case' t, name, CASE WHEN age<20 THEN 'teen' WHEN age<25 THEN 'young' ELSE 'adult' END grp, IF(age>20,'Y','N') iff FROM student ORDER BY id;
SELECT 'A31 order by multiple, distinct, count distinct' t, COUNT(DISTINCT dept_id) FROM student;
SELECT 'A32 strict mode: string too long (expect ERROR 1406)' t;
INSERT INTO dept(name) VALUES (REPEAT('x',60));
SELECT 'A33 division by zero (strict + ERROR_FOR_DIVISION_BY_ZERO: insert expect ERROR 1365)' t;
CREATE TABLE dz (v INT); INSERT INTO dz VALUES (1/0);
SELECT 'A34 ON UPDATE CURRENT_TIMESTAMP present' t, EXTRA FROM information_schema.columns WHERE table_schema='school' AND table_name='student' AND column_name='updated_at';
SELECT 'A99 END' t;
