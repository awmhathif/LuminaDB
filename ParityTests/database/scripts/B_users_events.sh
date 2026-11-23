# Section B multi-session parts: events, users/grants/roles, root password.
# Usage: bash scripts/B_users_events.sh 118|104   (servers must be running, root without password)
cd /c/Users/Hp/Downloads/Feem/HNDIT/IT/softwareee/ParityTests/database; source lib.sh; setv $1
as() { u=$1; p=$2; shift 2; "$B/$CLI" -u"$u" -p"$p" -h127.0.0.1 -P$PORT -B -N "$@" 2>&1 | grep -v ssl-verify; }
echo "### events"
q -B -N -e "USE adv; DROP TABLE IF EXISTS evlog; CREATE TABLE evlog(id INT AUTO_INCREMENT PRIMARY KEY, at DATETIME(3) DEFAULT NOW(3));
SET GLOBAL event_scheduler=OFF;
CREATE EVENT ev_tick ON SCHEDULE EVERY 1 SECOND DO INSERT INTO adv.evlog() VALUES ();
CREATE EVENT ev_once ON SCHEDULE AT CURRENT_TIMESTAMP + INTERVAL 2 SECOND ON COMPLETION PRESERVE DO INSERT INTO adv.evlog() VALUES ();
SELECT 'B27 events created', GROUP_CONCAT(EVENT_NAME), @@event_scheduler FROM information_schema.events WHERE event_schema='adv';" 2>&1 | grep -v ssl-verify
sleep 4
q -B -N -e "SELECT 'B28 scheduler OFF -> rows after 4s (expect 0)', COUNT(*) FROM adv.evlog; SET GLOBAL event_scheduler=ON;" 2>&1 | grep -v ssl-verify
sleep 4
q -B -N -e "SELECT 'B29 scheduler ON -> rows after 4s (expect >=3)', COUNT(*), IF(COUNT(*)>=3,'PASS','FAIL') FROM adv.evlog;
SELECT 'B29b event thread in processlist', COUNT(*) FROM information_schema.processlist WHERE user='event_scheduler';
SET GLOBAL event_scheduler=OFF; DROP EVENT adv.ev_tick; SELECT 'B29c ev_once status', STATUS FROM information_schema.events WHERE event_name='ev_once';" 2>&1 | grep -v ssl-verify

echo "### users / grants"
q -B -N -e "DROP USER IF EXISTS 'stu'@'%', 'stu2'@'localhost'; DROP ROLE IF EXISTS r_read;
CREATE USER 'stu'@'%' IDENTIFIED BY 'Stu#123';
CREATE USER 'stu2'@'localhost' IDENTIFIED BY 'Stu2#123';
GRANT SELECT ON adv.* TO 'stu'@'%';
GRANT INSERT, UPDATE ON adv.emp TO 'stu'@'%';
GRANT SELECT (name, dept_id) ON school.student TO 'stu2'@'localhost';
SHOW GRANTS FOR 'stu'@'%'; SHOW GRANTS FOR 'stu2'@'localhost';" 2>&1 | grep -v ssl-verify | sed 's/^/B30 SHOW GRANTS: /'
echo "B31 stu wrong password:"; as stu wrong -e "SELECT 1"
echo "B32 stu select adv.emp:"; as stu 'Stu#123' -e "SELECT COUNT(*) FROM adv.emp"
echo "B33 stu insert adv.emp (granted at table level):"; as stu 'Stu#123' -e "INSERT INTO adv.emp(name,dept,salary) VALUES ('stu-ins','IT',1); SELECT 'ok', ROW_COUNT()"
echo "B34 stu insert adv.audit (expect denied):"; as stu 'Stu#123' -e "INSERT INTO adv.audit(msg) VALUES ('x')"
echo "B35 stu DROP TABLE (expect denied):"; as stu 'Stu#123' -e "DROP TABLE adv.emp"
echo "B36 stu other db (expect denied):"; as stu 'Stu#123' -e "SELECT * FROM school.dept"
echo "B37 stu SHOW DATABASES (only visible ones):"; as stu 'Stu#123' -e "SHOW DATABASES" | tr '\n' ' '; echo
echo "B38 stu2 column grant allowed col:"; as stu2 'Stu2#123' -e "SELECT name, dept_id FROM school.student LIMIT 1"
echo "B38b stu2 column grant denied col (expect ERROR 1143):"; as stu2 'Stu2#123' -e "SELECT age FROM school.student LIMIT 1"
q -B -N -e "REVOKE INSERT ON adv.emp FROM 'stu'@'%';" 2>&1 | grep -v ssl-verify
echo "B39 after REVOKE INSERT (expect denied):"; as stu 'Stu#123' -e "INSERT INTO adv.emp(name) VALUES ('x')"

echo "### roles"
q -B -N -e "CREATE ROLE r_read; GRANT SELECT ON school.* TO r_read; GRANT r_read TO 'stu'@'%'; SET DEFAULT ROLE r_read FOR 'stu'@'%'; SHOW GRANTS FOR 'stu'@'%';" 2>&1 | grep -v ssl-verify | sed 's/^/B40 /'
echo "B41 stu with default role reads school:"; as stu 'Stu#123' -e "SELECT CURRENT_ROLE(), COUNT(*) FROM school.dept"
echo "B42 stu SET ROLE NONE then denied:"; as stu 'Stu#123' -e "SET ROLE NONE; SELECT COUNT(*) FROM school.dept"
echo "B43 MySQL-8 syntax SET DEFAULT ROLE ALL TO user:"; q -B -N -e "SET DEFAULT ROLE ALL TO 'stu'@'%'" 2>&1 | grep -v ssl-verify
echo "B43b MySQL-8 syntax GRANT role with multiple roles / SET ROLE ALL:"; as stu 'Stu#123' -e "SET ROLE ALL; SELECT CURRENT_ROLE()"

echo "### root password"
q -B -N -e "ALTER USER 'root'@'localhost' IDENTIFIED BY 'Root#123';" 2>&1 | grep -v ssl-verify
echo "B44 root no password after set (expect denied):"; "$B/$CLI" -uroot -h127.0.0.1 -P$PORT -e "SELECT 1" 2>&1
echo "B45 root with password:"; as root 'Root#123' -e "SELECT 'logged in as', CURRENT_USER()"
echo "B45b root via ::1 / other root rows still passwordless?"; "$B/$CLI" -uroot -h::1 -P$PORT -B -N -e "SELECT 'still in via', CURRENT_USER()" 2>&1
echo "B45c 11.8 client to this server with password (SSL verification behaviour):"; "$ENG/mariadb-11.8.10-winx64/bin/mariadb.exe" -uroot -pRoot#123 -h127.0.0.1 -P$PORT -B -N -e "SELECT 'ok 11.8 client', VERSION(), (SELECT VARIABLE_VALUE FROM information_schema.SESSION_STATUS WHERE VARIABLE_NAME='Ssl_cipher')" 2>&1
echo "B45d 10.4 client to this server with password:"; "$ENG/mariadb-10.4.34-winx64/bin/mysql.exe" -uroot -pRoot#123 -h127.0.0.1 -P$PORT -B -N -e "SELECT 'ok 10.4 client', VERSION()" 2>&1
# reset root to passwordless for the rest of the suite
as root 'Root#123' -e "ALTER USER 'root'@'localhost' IDENTIFIED BY ''; SELECT 'root password reset to empty'"
echo "B45e 11.8 client with --skip-ssl to this server with password:"; q -e "ALTER USER 'root'@'localhost' IDENTIFIED BY 'Root#123'" 2>&1 | grep -v ssl-verify; "$ENG/mariadb-11.8.10-winx64/bin/mariadb.exe" --skip-ssl -uroot -pRoot#123 -h127.0.0.1 -P$PORT -B -N -e "SELECT 'ok 11.8 client --skip-ssl', VERSION()" 2>&1
"$ENG/mariadb-11.8.10-winx64/bin/mariadb.exe" --ssl-verify-server-cert=0 -uroot -pRoot#123 -h127.0.0.1 -P$PORT -B -N -e "SELECT 'ok 11.8 client --ssl-verify-server-cert=0', VERSION()" 2>&1
as root 'Root#123' -e "ALTER USER 'root'@'localhost' IDENTIFIED BY ''; SELECT 'root password reset to empty (2)'"
