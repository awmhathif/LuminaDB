# Section E: dump/restore, CSV out/in, cross-version moves. Both servers must be running (root, no password).
cd /c/Users/Hp/Downloads/Feem/HNDIT/IT/softwareee/ParityTests/database; source lib.sh
mkdir -p dumps csv
fp() { # fingerprint: row counts + routine/trigger/event/view names for adv and school
  q -B -N -e "
  SELECT CONCAT('rows adv.emp=',(SELECT COUNT(*) FROM adv.emp),' adv.audit=',(SELECT COUNT(*) FROM adv.audit),' adv.intl=',(SELECT COUNT(*) FROM adv.intl),' school.student=',(SELECT COUNT(*) FROM school.student),' adv.art=',(SELECT COUNT(*) FROM adv.art));
  SELECT CONCAT('routines=',GROUP_CONCAT(ROUTINE_NAME ORDER BY ROUTINE_NAME)) FROM information_schema.routines WHERE routine_schema IN ('adv','school');
  SELECT CONCAT('triggers=',GROUP_CONCAT(TRIGGER_NAME ORDER BY TRIGGER_NAME)) FROM information_schema.triggers WHERE trigger_schema IN ('adv','school');
  SELECT CONCAT('events=',GROUP_CONCAT(EVENT_NAME ORDER BY EVENT_NAME)) FROM information_schema.events WHERE event_schema IN ('adv','school');
  SELECT CONCAT('views=',GROUP_CONCAT(TABLE_NAME ORDER BY TABLE_NAME)) FROM information_schema.views WHERE table_schema IN ('adv','school');
  SELECT CONCAT('intl hex=',MD5(GROUP_CONCAT(HEX(s) ORDER BY id))) FROM adv.intl;
  SELECT CONCAT('fk count=',COUNT(*)) FROM information_schema.referential_constraints WHERE constraint_schema IN ('adv','school');" 2>&1 | grep -v ssl-verify
}
for v in 118 104; do setv $v
  echo "################ $v: dump/drop/restore"
  fp > out/E_fp_before_$v.txt
  "$B/$DUMP" -uroot -h127.0.0.1 -P$PORT --routines --triggers --events --single-transaction --default-character-set=utf8mb4 --databases adv school > dumps/dump_$v.sql 2> out/E_dumperr_$v.txt; echo "dump exit=$? size=$(wc -c < dumps/dump_$v.sql)"; grep -v ssl-verify out/E_dumperr_$v.txt
  head -c 300 dumps/dump_$v.sql | head -3
  q -e "DROP DATABASE adv; DROP DATABASE school;" 2>&1 | grep -v ssl-verify
  q < dumps/dump_$v.sql 2>&1 | grep -v ssl-verify; echo "restore exit=${PIPESTATUS[0]}"
  fp > out/E_fp_after_$v.txt
  if diff out/E_fp_before_$v.txt out/E_fp_after_$v.txt; then echo "E01 fingerprint identical after restore: PASS"; else echo "E01 FAIL"; fi
  cat out/E_fp_after_$v.txt
  q -B -N -e "USE adv; CALL raise_dept('IT',0,@c,@t); SELECT 'E02 restored proc works', @c; SELECT 'E02 restored function', tax(100000);" 2>&1 | grep -v ssl-verify
done

echo "################ CSV export (SELECT ... INTO OUTFILE)"
for v in 118 104; do setv $v
  W=$(cygpath -m "$ROOT/csv/emp_$v.csv"); rm -f "$ROOT/csv/emp_$v.csv"
  q -B -N -e "SELECT 'secure_file_priv', IFNULL(@@secure_file_priv,'NULL (= any dir the server process can write)');
  SELECT id,name,dept,salary INTO OUTFILE '$W' CHARACTER SET utf8mb4 FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '\"' LINES TERMINATED BY '\r\n' FROM adv.emp;
  SELECT id,s INTO OUTFILE '$(cygpath -m "$ROOT/csv/intl_$v.csv")' CHARACTER SET utf8mb4 FIELDS TERMINATED BY ',' ENCLOSED BY '\"' FROM adv.intl;" 2>&1 | grep -v ssl-verify
  echo "E03 $v outfile:"; head -3 "$ROOT/csv/emp_$v.csv"; echo "E03 $v intl csv:"; cat "$ROOT/csv/intl_$v.csv"
  echo "E03b $v INTO OUTFILE over existing file (expect ERROR 1086):"; q -e "SELECT 1 INTO OUTFILE '$W'" 2>&1 | grep -v ssl-verify
done

echo "################ CSV import (LOAD DATA LOCAL INFILE)"
printf 'id,name,city\r\n1,Kamal,Colombo\r\n2,"Perera, Nimali",Kandy\r\n3,ශ්‍රී,යාපනය\r\n4,தமிழ்,Jaffna\r\n' > csv/people.csv
for v in 118 104; do setv $v; CSV=$(cygpath -m "$ROOT/csv/people.csv")
  q -e "DROP TABLE IF EXISTS adv.people; CREATE TABLE adv.people (id INT PRIMARY KEY, name VARCHAR(50), city VARCHAR(50)) CHARSET utf8mb4;" 2>&1 | grep -v ssl-verify
  L="LOAD DATA LOCAL INFILE '$CSV' INTO TABLE adv.people CHARACTER SET utf8mb4 FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '\"' LINES TERMINATED BY '\r\n' IGNORE 1 LINES"
  echo "E04 $v client default (no --local-infile flag):"; q -B -N -e "$L; SELECT 'loaded', COUNT(*) FROM adv.people;" 2>&1 | grep -v ssl-verify
  q -e "TRUNCATE adv.people" 2>/dev/null
  echo "E04b $v client --local-infile=0:"; q --local-infile=0 -B -N -e "$L; SELECT 'loaded', COUNT(*) FROM adv.people;" 2>&1 | grep -v ssl-verify
  q -e "TRUNCATE adv.people" 2>/dev/null
  echo "E04c $v client --local-infile=1:"; q --local-infile=1 -B -N -e "$L; SELECT 'loaded', COUNT(*) FROM adv.people; SELECT 'E04c row', id, name, city, HEX(name) FROM adv.people ORDER BY id;" 2>&1 | grep -v ssl-verify
  q -e "TRUNCATE adv.people" 2>/dev/null
  echo "E04d $v server SET GLOBAL local_infile=0 then client --local-infile=1:"; q -e "SET GLOBAL local_infile=0" 2>/dev/null; q --local-infile=1 -B -N -e "$L; SELECT 'loaded', COUNT(*) FROM adv.people;" 2>&1 | grep -v ssl-verify; q -e "SET GLOBAL local_infile=1" 2>/dev/null
  q -e "TRUNCATE adv.people" 2>/dev/null
  echo "E04e $v server-side LOAD DATA INFILE (no LOCAL):"; q -B -N -e "${L/LOCAL /}; SELECT 'loaded', COUNT(*) FROM adv.people;" 2>&1 | grep -v ssl-verify
  echo "E04f $v mariadb-import/mysqlimport tool:"; cp csv/people.csv csv/people2.csv; q -e "CREATE TABLE IF NOT EXISTS adv.people2 LIKE adv.people; TRUNCATE adv.people2;" 2>/dev/null
  IMP=$B/$( [ $v = 118 ] && echo mariadb-import.exe || echo mysqlimport.exe ); "$IMP" -uroot -h127.0.0.1 -P$PORT --local --fields-terminated-by=, --fields-optionally-enclosed-by='"' --lines-terminated-by='\r\n' --ignore-lines=1 --default-character-set=utf8mb4 adv "$(cygpath -w "$ROOT/csv/people2.csv")" 2>&1 | grep -v ssl-verify
done

echo "################ cross-version moves"
setv 104; q -e "DROP DATABASE IF EXISTS adv; DROP DATABASE IF EXISTS school;" 2>/dev/null
echo "E05 11.8 dump -> 10.4 server via 10.4 client:"; q < dumps/dump_118.sql 2>&1 | grep -v ssl-verify | head -5; echo "exit=${PIPESTATUS[0]}"
q -e "DROP DATABASE IF EXISTS adv; DROP DATABASE IF EXISTS school;" 2>/dev/null
echo "E05b 11.8 dump -> 10.4 server via 11.8 client:"; "$ENG/mariadb-11.8.10-winx64/bin/mariadb.exe" -uroot -h127.0.0.1 -P3420 < dumps/dump_118.sql 2>&1 | grep -v ssl-verify | head -5; echo "exit=${PIPESTATUS[0]}"
q -e "DROP DATABASE IF EXISTS adv; DROP DATABASE IF EXISTS school;" 2>/dev/null
echo "E05c 11.8 dump with rewrites (drop sandbox line, uca1400 -> unicode_520) -> 10.4 via 10.4 client:"
sed -e '1{/enable the sandbox mode/d}' -e 's/utf8mb4_uca1400_ai_ci/utf8mb4_unicode_520_ci/g' -e 's/utf8mb4_uca1400_\w*/utf8mb4_unicode_520_ci/g' dumps/dump_118.sql > dumps/dump_118_for_104.sql
q < dumps/dump_118_for_104.sql 2>&1 | grep -v ssl-verify | head -5; echo "exit=${PIPESTATUS[0]}"
fp > out/E_fp_118_into_104.txt; cat out/E_fp_118_into_104.txt
if diff <(grep -v '^rows' out/E_fp_after_118.txt) <(grep -v '^rows' out/E_fp_118_into_104.txt) >/dev/null; then echo "E05c objects identical to 11.8 source: PASS"; else echo "E05c objects differ:"; diff out/E_fp_after_118.txt out/E_fp_118_into_104.txt; fi
# restore 10.4's own data
q -e "DROP DATABASE IF EXISTS adv; DROP DATABASE IF EXISTS school;" 2>/dev/null; q < dumps/dump_104.sql 2>&1 | grep -v ssl-verify

setv 118; q -e "CREATE DATABASE IF NOT EXISTS x104; " 2>/dev/null
echo "E06 10.4 dump -> 11.8 server (into fresh names via sed of db names):"
sed -e 's/`adv`/`adv104`/g; s/`school`/`school104`/g; s/\badv\./adv104./g; s/\bschool\./school104./g' dumps/dump_104.sql > dumps/dump_104_renamed.sql
q -e "DROP DATABASE IF EXISTS adv104; DROP DATABASE IF EXISTS school104; DROP DATABASE IF EXISTS x104;" 2>/dev/null
q < dumps/dump_104_renamed.sql 2>&1 | grep -v ssl-verify | head -5; echo "exit=${PIPESTATUS[0]}"
q -B -N -e "SELECT 'E06 rows adv104.emp', COUNT(*) FROM adv104.emp; SELECT 'E06 routines', GROUP_CONCAT(ROUTINE_NAME) FROM information_schema.routines WHERE routine_schema='adv104'; SELECT 'E06 triggers', COUNT(*) FROM information_schema.triggers WHERE trigger_schema='adv104'; SELECT 'E06 intl md5', MD5(GROUP_CONCAT(HEX(s) ORDER BY id)) FROM adv104.intl; USE adv104; SELECT 'E06 function', tax(100000);" 2>&1 | grep -v ssl-verify
q -e "DROP DATABASE IF EXISTS adv104; DROP DATABASE IF EXISTS school104;" 2>/dev/null
