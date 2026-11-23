# Usage: source lib.sh; setv 118|104
ROOT=/c/Users/Hp/Downloads/Feem/HNDIT/IT/softwareee/ParityTests/database
ENG=/c/Users/Hp/Downloads/Feem/HNDIT/IT/softwareee/EngineSpike/engines
setv() {
  V=$1
  if [ "$V" = 118 ]; then B=$ENG/mariadb-11.8.10-winx64/bin; SRV=mariadbd.exe; INIT=mariadb-install-db.exe; CLI=mariadb.exe; DUMP=mariadb-dump.exe; PORT=3410
  else B=$ENG/mariadb-10.4.34-winx64/bin; SRV=mysqld.exe; INIT=mysql_install_db.exe; CLI=mysql.exe; DUMP=mysqldump.exe; PORT=3420; fi
  DATA=$ROOT/data/d$V
  WDATA=$(cygpath -w "$DATA")
}
dbinit() { [ -d "$DATA/mysql" ] || "$B/$INIT" --datadir="$WDATA" --port=$PORT; }
dbstart() {
  "$B/$SRV" --defaults-file="$WDATA\my.ini" --datadir="$WDATA" --port=$PORT --bind-address=127.0.0.1 --console $EXTRA > "$ROOT/out/server$V.log" 2>&1 &
  echo $! > "$ROOT/out/pid$V"
  for i in $(seq 1 60); do "$B/$CLI" -uroot -h127.0.0.1 -P$PORT $ROOTPW -e "select 1" >/dev/null 2>&1 && return 0; sleep 1; done; echo "START FAILED"; return 1
}
q() { "$B/$CLI" -uroot -h127.0.0.1 -P$PORT $ROOTPW --default-character-set=utf8mb4 "$@"; }
dbstop() { q -e "SHUTDOWN" ; sleep 3; }
