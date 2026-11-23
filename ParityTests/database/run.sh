# run.sh <script.sql> <outprefix> : runs on both versions in batch mode with --force
cd /c/Users/Hp/Downloads/Feem/HNDIT/IT/softwareee/ParityTests/database; source lib.sh
for v in 118 104; do setv $v; q --force -B -N "${@:3}" < "$1" > out/${2}_$v.txt 2>&1; done
