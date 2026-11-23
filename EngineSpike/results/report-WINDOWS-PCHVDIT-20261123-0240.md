# LuminaDB engine test — WINDOWS-PCHVDIT — 2026-11-23 02:39


## This PC
- Windows: Windows 10 Pro 22H2 (build 19045), 64-bit
- CPU cores: 4
- RAM: 8077 MB total, 1552 MB free
- .NET Framework: 4.8.1
- Running as admin: no (good: this is how students will run it)
- UCRT (ucrtbase.dll) in Windows: present
- VC++ 2015-2022 x64 installed: v14.44.35211.00
- Antivirus: Windows Defender
- Test folder: C:\Users\Hp\Downloads\Feem\HNDIT\IT\softwareee\EngineSpike\dist\LuminaEngineTest
- Port 3306: free

## mariadb-10.4.12-winx64

### Size
- Extracted, as downloaded: 143.5 MB
- After removing debug symbols (.pdb), headers, .lib, perl scripts: 143.5 MB
- Trimmed and zipped (≈ download size): 42.0 MB

### Will it start on Windows 7? (read from the binaries, no Windows 7 PC needed)
- Server binary: mysqld.exe (header says it needs Windows 6.0 or newer; 6.1 = Windows 7, 6.2 = Windows 8, 10.0 = Windows 10)
- Files checked: mysqld.exe, MSVCP140.dll, VCRUNTIME140.dll
- **Windows 7: no blockers found** in the server's startup imports.

### Does it need anything installed? (VC++ runtime)
- VC++ runtime: **shipped inside the engine** (vcruntime/msvcp found in bin). Users install nothing.
- Universal C Runtime: shipped inside the engine (works even on Windows 7 without updates).

### Run test (no installer, no service, normal user)
- PASS — Port pick: 3306 is busy (simulated) → chose 3307
- PASS — Create data folder with mysql_install_db.exe: exit 0, 2.5s
- PASS — Start server: ready in 1.0s → 10.4.12-MariaDB
- PASS — Listens on 127.0.0.1 only (no firewall prompt, no outside access)
-   Server memory after start: 69.3 MB
- PASS — Create table, insert 1000 rows, read back: 1000 rows in 224 ms
- PASS — Foreign keys enforced (error 1452 on a bad course_id)
- PASS — Clean shutdown with SHUTDOWN command: 1.6s
- PASS — Hard kill of the server (simulated power cut) after 500 committed inserts
- PASS — Restart after crash: ready in 1.1s
- PASS — No committed data lost: 1500/1500 rows
- PASS — CHECK TABLE after crash: OK
-   Server log mentions recovery: yes (InnoDB replayed its log, as designed)
- PASS — Final clean shutdown

## mariadb-10.4.34-winx64

### Size
- Extracted, as downloaded: 152.9 MB
- After removing debug symbols (.pdb), headers, .lib, perl scripts: 152.9 MB
- Trimmed and zipped (≈ download size): 46.3 MB

### Will it start on Windows 7? (read from the binaries, no Windows 7 PC needed)
- Server binary: mysqld.exe (header says it needs Windows 6.0 or newer; 6.1 = Windows 7, 6.2 = Windows 8, 10.0 = Windows 10)
- Files checked: mysqld.exe, MSVCP140.dll, VCRUNTIME140.dll, VCRUNTIME140_1.dll
- **Windows 7: no blockers found** in the server's startup imports.

### Does it need anything installed? (VC++ runtime)
- VC++ runtime: **shipped inside the engine** (vcruntime/msvcp found in bin). Users install nothing.
- Universal C Runtime: shipped inside the engine (works even on Windows 7 without updates).

### Run test (no installer, no service, normal user)
- PASS — Port pick: 3306 is busy (simulated) → chose 3307
- PASS — Create data folder with mysql_install_db.exe: exit 0, 2.9s
- PASS — Start server: ready in 0.5s → 10.4.34-MariaDB
- PASS — Listens on 127.0.0.1 only (no firewall prompt, no outside access)
-   Server memory after start: 70.5 MB
- PASS — Create table, insert 1000 rows, read back: 1000 rows in 137 ms
- PASS — Foreign keys enforced (error 1452 on a bad course_id)
- PASS — Clean shutdown with SHUTDOWN command: 1.3s
- PASS — Hard kill of the server (simulated power cut) after 500 committed inserts
- PASS — Restart after crash: ready in 1.0s
- PASS — No committed data lost: 1500/1500 rows
- PASS — CHECK TABLE after crash: OK
-   Server log mentions recovery: yes (InnoDB replayed its log, as designed)
- PASS — Final clean shutdown

## mariadb-11.8.10-winx64

### Size
- Extracted, as downloaded: 205.1 MB
- After removing debug symbols (.pdb), headers, .lib, perl scripts: 205.1 MB
- Trimmed and zipped (≈ download size): 60.2 MB

### Will it start on Windows 7? (read from the binaries, no Windows 7 PC needed)
- Server binary: mariadbd.exe (header says it needs Windows 6.0 or newer; 6.1 = Windows 7, 6.2 = Windows 8, 10.0 = Windows 10)
- Files checked: mariadbd.exe, server.dll, VCRUNTIME140.dll, MSVCP140.dll, VCRUNTIME140_1.dll
- **Windows 7: WILL NOT START** (4 blockers):
  - server.dll imports GetSystemTimePreciseAsFileTime from KERNEL32.dll (only exists on Windows 8+)
  - server.dll imports WakeByAddressAll from api-ms-win-core-synch-l1-2-0.dll (only exists on Windows 8+)
  - server.dll imports WakeByAddressSingle from api-ms-win-core-synch-l1-2-0.dll (only exists on Windows 8+)
  - server.dll imports WaitOnAddress from api-ms-win-core-synch-l1-2-0.dll (only exists on Windows 8+)

### Does it need anything installed? (VC++ runtime)
- VC++ runtime: **shipped inside the engine** (vcruntime/msvcp found in bin). Users install nothing.
- Universal C Runtime: not shipped. Built into Windows 10/11; Windows 7/8.1 need update KB2999226 (or we copy ucrtbase.dll + api-ms-win-crt-*.dll next to the server).

### Run test (no installer, no service, normal user)
- PASS — Port pick: 3306 is busy (simulated) → chose 3307
- PASS — Create data folder with mariadb-install-db.exe: exit 0, 4.0s
- PASS — Start server: ready in 1.0s → 11.8.10-MariaDB
- PASS — Listens on 127.0.0.1 only (no firewall prompt, no outside access)
-   Server memory after start: 116.6 MB
- PASS — Create table, insert 1000 rows, read back: 1000 rows in 154 ms
- PASS — Foreign keys enforced (error 1452 on a bad course_id)
- PASS — Clean shutdown with SHUTDOWN command: 0.5s
- PASS — Hard kill of the server (simulated power cut) after 500 committed inserts
- PASS — Restart after crash: ready in 2.3s
- PASS — No committed data lost: 1500/1500 rows
- PASS — CHECK TABLE after crash: OK
-   Server log mentions recovery: yes (InnoDB replayed its log, as designed)
- PASS — Final clean shutdown

## Overall
PASS: every engine tested worked on this PC.
