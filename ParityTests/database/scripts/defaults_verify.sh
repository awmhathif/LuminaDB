# Verifies the recommended my.ini (out/recommended_<v>.ini copied to data/d<v>/my.ini) after a restart.
cd /c/Users/Hp/Downloads/Feem/HNDIT/IT/softwareee/ParityTests/database; source lib.sh
for v in 118 104; do setv $v; echo "################ $v"
  # use the [client] section of the data-dir my.ini, like the app would
  qq() { "$B/$CLI" --defaults-file="$WDATA\\my.ini" -uroot -h127.0.0.1 "$@" 2>&1 | grep -v ssl-verify; }
  qq -B -N -e "SELECT 'V01 vars', @@character_set_server, @@collation_server, @@event_scheduler, @@local_infile, @@secure_file_priv, @@max_allowed_packet, @@performance_schema, @@innodb_buffer_pool_size, @@lower_case_table_names;
  SELECT 'V02 client conn charset', @@character_set_client, @@character_set_connection, @@collation_connection, @@character_set_results;"
  [ $v = 118 ] && qq -B -N -e "SELECT 'V02b character_set_collations', @@character_set_collations;"
  qq -B -N --force < scripts/V03_unicode_default.sql
  echo "V04 event scheduler running at boot:"; qq -B -N -e "SELECT COUNT(*) FROM information_schema.processlist WHERE user='event_scheduler'"
  echo "V05 performance_schema usable:"; qq -B -N -e "SELECT 'events_statements_summary rows', COUNT(*) FROM performance_schema.events_statements_summary_by_digest; SELECT 'ps global_status table', COUNT(*) FROM performance_schema.global_status;"
  EXP=$(cygpath -m "$ROOT/data/exports$v")
  rm -f "$ROOT/data/exports$v/t.csv" "$ROOT/csv/forbidden_$v.csv"
  echo "V06 INTO OUTFILE inside secure_file_priv:"; qq -B -N -e "SELECT 1,'x' INTO OUTFILE '$EXP/t.csv' FIELDS TERMINATED BY ','; SELECT 'ok';"; cat "$ROOT/data/exports$v/t.csv"
  echo "V06b INTO OUTFILE outside secure_file_priv (expect ERROR 1290):"; qq -B -N -e "SELECT 1 INTO OUTFILE '$(cygpath -m "$ROOT/csv/forbidden_$v.csv")'"
  echo "V07 LOAD DATA LOCAL with [client] local-infile=1:"; qq -B -N -e "USE deflt; CREATE TABLE p (id INT, name VARCHAR(50), city VARCHAR(50)); LOAD DATA LOCAL INFILE '$(cygpath -m "$ROOT/csv/people.csv")' INTO TABLE p FIELDS TERMINATED BY ',' OPTIONALLY ENCLOSED BY '\"' LINES TERMINATED BY '\r\n' IGNORE 1 LINES; SELECT 'loaded', COUNT(*) FROM p;"
  echo "V08 phpMyAdmin dump (raw, unmodified) imports with these defaults:"; qq -e "DROP DATABASE IF EXISTS library_db"; qq < scripts/D_phpmyadmin_export.sql; qq -B -N -e "SELECT COUNT(*) AS library_db_tables FROM information_schema.tables WHERE table_schema='library_db'"
  echo "V09 mysqldump of a default-created db: collation clause emitted"; "$B/$DUMP" --defaults-file="$WDATA\\my.ini" -uroot -h127.0.0.1 --databases deflt 2>/dev/null | grep -E 'CREATE DATABASE|CHARSET' | head -3
  echo "V10 11.8 client -> this server with password and no extra flags (cert verification):"
  qq -e "ALTER USER 'root'@'localhost' IDENTIFIED BY 'Root#123'"
  "$ENG/mariadb-11.8.10-winx64/bin/mariadb.exe" -uroot -pRoot#123 -h127.0.0.1 -P$PORT -B -N -e "SELECT 'ok', VERSION()" 2>&1
  echo "V10b same with [client] ssl-verify-server-cert=0 via --defaults-extra-file:"
  printf '[client]\nssl-verify-server-cert=0\n' > out/client_noverify.ini
  "$ENG/mariadb-11.8.10-winx64/bin/mariadb.exe" --defaults-extra-file="$(cygpath -w "$ROOT/out/client_noverify.ini")" -uroot -pRoot#123 -h127.0.0.1 -P$PORT -B -N -e "SELECT 'ok', VERSION()" 2>&1
  "$B/$CLI" -uroot -pRoot#123 -h127.0.0.1 -P$PORT -e "ALTER USER 'root'@'localhost' IDENTIFIED BY ''" 2>&1 | grep -v ssl-verify
done
