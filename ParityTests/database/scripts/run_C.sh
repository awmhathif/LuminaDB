# Runs every scripts/C/*.sql through the CLI exactly as given (no --force, except C10 which is a catalogue run with --force)
cd /c/Users/Hp/Downloads/Feem/HNDIT/IT/softwareee/ParityTests/database; source lib.sh
for v in 118 104; do setv $v; : > out/C_$v.txt
 for f in scripts/C/*.sql; do F=""; case $f in *C10*|*R_*) F=--force;; esac
  echo "##### $(basename $f)" >> out/C_$v.txt
  q -B -N $F < $f >> out/C_$v.txt 2>&1; echo "exit=$?" >> out/C_$v.txt
 done; done
