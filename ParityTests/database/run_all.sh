# Reproduce everything. Ports 3410 (11.8.10) / 3420 (10.4.34), 127.0.0.1 only.
# Phase 1 runs with factory my.ini (from out/my_ini_original_<v>.ini); phase 2 with out/recommended_<v>.ini.
cd /c/Users/Hp/Downloads/Feem/HNDIT/IT/softwareee/ParityTests/database; source lib.sh
for v in 118 104; do setv $v; dbinit; cp out/my_ini_original_$v.ini "$DATA/my.ini"; dbstart; done
bash run.sh scripts/A_everyday.sql A
bash run.sh scripts/B_advanced.sql B
for v in 118 104; do bash scripts/B_users_events.sh $v > out/Busers_$v.txt 2>&1 </dev/null; done
bash scripts/run_C.sh
bash run.sh scripts/R_rewrites.sql R
for v in 118 104; do setv $v; q -e "DROP DATABASE IF EXISTS library_db"; q < scripts/D_phpmyadmin_export.sql; q --force -B -N < scripts/D_verify.sql; done > out/D_both.txt 2>&1
bash scripts/run_rewritten.sh
bash scripts/E_backup.sh > out/E.txt 2>&1 </dev/null
bash run.sh scripts/F_gui.sql F; bash scripts/F_kill.sh > out/F_kill.txt 2>&1 </dev/null; bash scripts/F_slowlog.sh > out/F_slowlog.txt 2>&1
(cd ConnTest && dotnet build -c Release) && for p in 3410 3420; do ConnTest/bin/Release/net48/ConnTest.exe $p > out/G_$p.txt 2>&1; done
for v in 118 104; do setv $v; dbstop; cp out/recommended_$v.ini "$DATA/my.ini"; dbstart; done
bash scripts/defaults_verify.sh > out/defaults_verify.txt 2>&1 </dev/null
for v in 118 104; do setv $v; dbstop; done
