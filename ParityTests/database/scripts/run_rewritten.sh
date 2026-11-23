# Rewrites the MySQL-8 style scripts with rewrite_mysql8.py and runs them exactly (no --force, except C10 catalogue)
cd /c/Users/Hp/Downloads/Feem/HNDIT/IT/softwareee/ParityTests/database; source lib.sh; mkdir -p scripts/rewritten
for v in 118 104; do setv $v; : > out/RW_$v.txt
 q -e "DROP DATABASE IF EXISTS library_db" 2>/dev/null
 for f in scripts/C/C01_workbench_forward_engineer.sql scripts/C/C01b_invisible_index.sql scripts/C/C02_caching_sha2.sql scripts/C/C03_native_password.sql scripts/C/C04_utf8mb4_0900_ai_ci.sql scripts/C/C04b_db_0900.sql scripts/C/C08_regexp_like.sql scripts/C/C10_mysql8_misc.sql scripts/D_phpmyadmin_export.sql scripts/D_verify.sql; do
  o=scripts/rewritten/${v}_$(basename $f); python -I scripts/rewrite_mysql8.py $v $f $o >/dev/null
  F=""; case $f in *C10*|*verify*) F=--force;; esac
  echo "##### $(basename $f)" >> out/RW_$v.txt; q -B -N $F < $o >> out/RW_$v.txt 2>&1; echo "exit=$?" >> out/RW_$v.txt
 done; done
