# Section F: slow query log toggled at runtime, observed from a NEW connection (file + table output)
cd /c/Users/Hp/Downloads/Feem/HNDIT/IT/softwareee/ParityTests/database; source lib.sh
for v in 118 104; do setv $v
  q -e "TRUNCATE mysql.slow_log; SET GLOBAL log_output='TABLE,FILE'; SET GLOBAL long_query_time=0.2; SET GLOBAL slow_query_log=1;" 2>&1 | grep -v ssl-verify
  q -B -N -e "SELECT SLEEP(0.5) AS slow_probe_$v" >/dev/null 2>&1
  q -B -N -e "SELECT 'F16 $v slow log rows (new connection)', COUNT(*), MAX(LEFT(sql_text,40)), @@slow_query_log_file FROM mysql.slow_log WHERE sql_text LIKE '%slow_probe%';
  SELECT 'F16 $v Slow_queries status', VARIABLE_VALUE FROM information_schema.GLOBAL_STATUS WHERE VARIABLE_NAME='SLOW_QUERIES';" 2>&1 | grep -v ssl-verify
  F=$(q -B -N -e "SELECT @@slow_query_log_file" 2>/dev/null); echo "F16 $v slow log file ($F) grep:"; grep -c slow_probe "$DATA/$F" 2>/dev/null || ls "$DATA" | grep -i slow
  q -e "SET GLOBAL slow_query_log=0; SET GLOBAL log_output='FILE';" 2>&1 | grep -v ssl-verify
  q -B -N -e "SET SESSION long_query_time=0.2; SET SESSION slow_query_log=1; SELECT 'F16b $v session-level slow_query_log settable', @@session.slow_query_log;" 2>&1 | grep -v ssl-verify
done
