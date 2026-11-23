#!/bin/sh
# Re-run the whole web parity suite (Git Bash). Assumes downloads/ + bundle/ + mariadb-data/ already built
# (see RESULTS.md "Reproduce"). Starts MariaDB on 3510, each web server in turn, runs the curl test drivers,
# stops everything it started. Output: results/rerun_*.txt
set -u
W=C:/Users/Hp/Downloads/Feem/HNDIT/IT/softwareee/ParityTests/web
M=C:/Users/Hp/Downloads/Feem/HNDIT/IT/softwareee/EngineSpike/engines/mariadb-11.8.10-winx64/bin
PY="python -I"
cd "$W" || exit 1
L() { $PY scripts/launch.py "$@"; }
S() { $PY scripts/stop.py "$1" >/dev/null; }

# MariaDB (stopped with SQL SHUTDOWN at the end, never killed)
"$M/mariadbd.exe" --defaults-file="$W/mariadb-data/my.ini" --datadir="$W/mariadb-data" --port=3510 \
    --bind-address=127.0.0.1 --console >> results/mariadb.log 2>&1 &
for i in $(seq 1 30); do "$M/mariadb.exe" -h127.0.0.1 -P3510 -uroot -e "select 1" >/dev/null 2>&1 && break; sleep 1; done

# 1) php -S (PHP 8.6 + 14.51 runtime app-local)
L rr_phpS 8010 $W/results/rr_phpS.log -- $W/bundle/php86/php.exe -S 127.0.0.1:8010 -t $W/app $W/app/router.php
$PY scripts/test_app.py http://127.0.0.1:8010 --pretty > results/rerun_phpS_app.txt 2>&1
$PY scripts/concurrency.py http://127.0.0.1:8010 20 > results/rerun_phpS_conc.txt 2>&1
S rr_phpS
L rr_pmaS 8020 $W/results/rr_pmaS.log -- $W/bundle/php86/php.exe -S 127.0.0.1:8020 -t $W/bundle/phpmyadmin
$PY scripts/test_pma.py http://127.0.0.1:8020 --label rerun_phpS > results/rerun_phpS_pma.txt 2>&1
S rr_pmaS

# 2) Apache mod_php (TS build; PATH for the child only so ext dependency DLLs resolve)
WP="$(cygpath -w $W/bundle/php86ts);C:\\Windows\\System32;C:\\Windows"
L rr_httpd 8030 $W/results/rr_httpd.log --env "PATH=$WP" -- $W/bundle/apache/bin/httpd.exe -d $W/bundle/apache \
    -C "Define SRVROOT $W/bundle/apache" -f conf/httpd-parity.conf -D MODPHP
$PY scripts/test_app.py http://127.0.0.1:8030 --pretty > results/rerun_apache_app.txt 2>&1
$PY scripts/test_pma.py http://127.0.0.1:8030/phpmyadmin --label rerun_apache > results/rerun_apache_pma.txt 2>&1
$PY scripts/concurrency.py http://127.0.0.1:8030 20 > results/rerun_apache_conc.txt 2>&1
S rr_httpd

# 3) Caddy + php-cgi (NTS) with 4 FastCGI children (recommended bundle, trimmed dist/)
L rr_cgi 9007 $W/results/rr_cgi.log --env PHP_FCGI_CHILDREN=4 --env PHP_FCGI_MAX_REQUESTS=500 -- $W/dist/php/php-cgi.exe -b 127.0.0.1:9007
L rr_caddy 8040 $W/results/rr_caddy.log --env PHP_UPSTREAMS=127.0.0.1:9007 -- $W/dist/caddy/caddy.exe run --config Caddyfile --adapter caddyfile
$PY scripts/test_app.py http://127.0.0.1:8040 --pretty > results/rerun_caddy_app.txt 2>&1
$PY scripts/test_pma.py http://127.0.0.1:8040/phpmyadmin --label rerun_caddy > results/rerun_caddy_pma.txt 2>&1
$PY scripts/concurrency.py http://127.0.0.1:8040 20 > results/rerun_caddy_conc.txt 2>&1
$W/dist/caddy/caddy.exe stop --address 127.0.0.1:8089 >/dev/null 2>&1; S rr_caddy
S rr_cgi

"$M/mariadb.exe" -h127.0.0.1 -P3510 -uroot -e "SHUTDOWN" 2>/dev/null
grep -H -E "SUMMARY|PARALLEL|HEAD-OF-LINE" results/rerun_*.txt
