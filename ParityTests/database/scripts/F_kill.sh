# Section F: PROCESSLIST + KILL QUERY / KILL CONNECTION from a second session
cd /c/Users/Hp/Downloads/Feem/HNDIT/IT/softwareee/ParityTests/database; source lib.sh
for v in 118 104; do setv $v
  ( q -B -N -e "SELECT 'victim started'; SELECT SLEEP(30) AS r; SELECT 'victim continued after KILL QUERY';" > out/F_victim_$v.txt 2>&1 ) &
  VP=$!; sleep 2
  ID=$(q -B -N -e "SELECT ID FROM information_schema.PROCESSLIST WHERE INFO LIKE 'SELECT SLEEP(30)%'" 2>/dev/null)
  echo "F21 $v victim connection id=$ID"
  t0=$(date +%s); q -e "KILL QUERY $ID" 2>&1 | grep -v ssl-verify; wait $VP; t1=$(date +%s)
  echo "F21 $v KILL QUERY -> victim returned after $((t1-t0))s; victim output:"; cat out/F_victim_$v.txt | grep -v ssl-verify
  ( q -B -N -e "SELECT SLEEP(30); SELECT 'should not print';" > out/F_victim2_$v.txt 2>&1 ) & VP=$!; sleep 2
  ID=$(q -B -N -e "SELECT ID FROM information_schema.PROCESSLIST WHERE INFO LIKE 'SELECT SLEEP(30)%'" 2>/dev/null)
  q -e "KILL CONNECTION $ID" 2>&1 | grep -v ssl-verify; wait $VP
  echo "F22 $v KILL CONNECTION victim output:"; grep -v ssl-verify out/F_victim2_$v.txt
done
