# Engine test: findings

Tested on 2026-11-23 on the dev PC (Windows 10 22H2, 4 cores, 8 GB, normal user, Windows Defender).
Full reports: `results/`.

## Result: the core idea works

For all three MariaDB versions, with **no installer, no admin rights and no Windows service**:

| Step | 10.4.12 | 10.4.34 | 11.8.10 |
|---|---|---|---|
| Pick another port when 3306 is busy | ✅ 3307 | ✅ 3307 | ✅ 3307 |
| Create data folder | ✅ 1.7–2.5 s | ✅ 2.9–3.1 s | ✅ 4.0 s |
| Start and accept connections | ✅ 0.5–1.0 s | ✅ 0.5 s | ✅ 1.0–1.8 s |
| Listens on 127.0.0.1 only | ✅ | ✅ | ✅ |
| 1000 inserts + read back | ✅ | ✅ | ✅ |
| Foreign keys enforced | ✅ | ✅ | ✅ |
| Clean shutdown (`SHUTDOWN`) | ✅ ~1 s | ✅ ~1.3 s | ✅ 0.5 s |
| Power-cut simulation (hard kill) → restart | ✅ 1.1 s | ✅ 1.0 s | ✅ 1.5–2.3 s |
| Committed rows kept after crash | ✅ 1500/1500 | ✅ 1500/1500 | ✅ 1500/1500 |
| `CHECK TABLE` after crash | ✅ OK | ✅ OK | ✅ OK |
| RAM after start | 69 MB | 70 MB | 115 MB |
| **Download size (trimmed, zipped)** | **42 MB** | **46 MB** | **60 MB** |
| **Windows 7** (read from the binaries) | ✅ no blockers | ✅ no blockers | ❌ won't start |
| VC++ runtime | ships inside | ships inside | ships inside |
| Universal C Runtime | ships inside | ships inside | not shipped (in Win10/11; Win7 needs KB2999226) |

## What this settles

1. **"VC++ missing" errors can't happen.** All three versions ship `vcruntime140.dll`/`msvcp140.dll` in `bin`.
2. **The "MySQL shutdown unexpectedly" fear is mostly InnoDB doing its job.** After a hard kill, every version replayed its log and lost nothing that was committed. Doctor's "repair" mainly needs to handle Aria/MyISAM system tables and a missing or locked data folder.
3. **The port conflict is solved** by checking for listeners before starting and choosing the next free port.
4. **11.8 cannot run on Windows 7.** Its `server.dll` imports `GetSystemTimePreciseAsFileTime`, `WaitOnAddress`, `WakeByAddressSingle` and `WakeByAddressAll`, which only exist on Windows 8 and later. This confirms Gemini's point about newer versions.
5. **Gemini's claim that 10.4.12 is the last version for Windows 7 is not confirmed.** 10.4.34 (the final 10.4 release) shows no Windows 8+ imports either. Only a real Windows 7 PC can settle it, because a static scan can't catch every runtime problem.
6. **Size is fine.** Removing debug symbols, headers and similar files cuts the download to 42–60 MB.

## Proposed engine plan (to confirm on lab PCs)

- **Windows 10/11:** MariaDB 11.8 (current long-term-support release).
- **Windows 7/8.1:** MariaDB 10.4.34, the newest version without Windows 8 imports. Note: 10.4 reached end of support in 2024, so it gets no more security fixes. Acceptable for a local-only server bound to 127.0.0.1, but say so in the app.
- The app picks the engine by Windows version at first run, or the installer ships only the right one.

## Not tested yet: needs the lab PCs

- Real **Windows 7** behaviour, including whether .NET Framework 4.8 is present (the app needs it).
- Lab antivirus quarantining `mysqld.exe`.
- PCs with **no VC++ installed** (this dev PC has it, so the "ships inside" result comes from reading the files, not from a clean machine).
- Running from a **path with spaces** or non-English characters.
- Slow HDDs and 2–4 GB RAM.

**How:** copy `dist/LuminaEngineTest/` (150 MB) to each lab PC, run `run-test.bat` as a normal user, and bring back the `report-*.md` files.
