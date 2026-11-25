# Packaging LuminaDB

One command builds both installers into `packaging\out\`:

| File | For | Engines |
|---|---|---|
| `LuminaDB-Setup.exe` (about 94 MB) | Windows 10 and 11, 64-bit | MariaDB 11.8.10, PHP 8.5.13, Caddy + Apache 2.4.71 |
| `LuminaDB-Setup-Win7-32bit.exe` (about 108 MB) | Windows 7 SP1, 8, 8.1 (64-bit) and any 32-bit Windows | 64-bit: MariaDB 10.4.34, PHP 8.2.36, Apache 2.4.66 (VS17) · 32-bit: MariaDB 10.4.22, PHP 8.2.36 x86, Apache 2.4.66 win32 |

```powershell
.\build.ps1 -Downloads <folder with the unpacked downloads>
```

`build.ps1` runs `build-sets.ps1` (Release build of the app, trimmed engine sets, licences), checks every program's
DLL imports with `check-imports.ps1` (the Windows 7 sets must not use anything newer than Windows 7), then runs Inno
Setup for both editions and writes `SHA256SUMS.txt`. `-SkipStage` reuses `out\stage`.

## What the installers do

- Per-user install into `%LOCALAPPDATA%\LuminaDB`. No administrator rights, no services, no choices but a desktop shortcut.
- Each installer checks the PC first. On the wrong PC it says which file to download instead and offers the release page.
- The Win7/32-bit installer puts in only the set the PC needs (legacy on 64-bit Windows 7/8, x86 on 32-bit Windows).
- .NET Framework 4.8 missing (Windows 7/8, early Windows 10): downloaded from Microsoft during setup and installed with
  one administrator prompt. Everything else ships inside: the Visual C++ runtime beside every engine program, and for
  the Windows 7 sets Microsoft's app-local Universal CRT too, so a Windows 7 PC without that update still works.
- Upgrades: `LuminaDB.exe --quit` asks the running copy to close the normal way (server stopped cleanly), then the
  engine folders are replaced. Data, projects, backups and settings stay.
- Uninstall: closes LuminaDB the same way, removes the program, shortcuts, "Open with" entries and "Start with Windows".
  It then asks whether to delete the databases, projects and backups too (default No; silent uninstalls keep them).
- "Open with LuminaDB" for .sql, .csv and .xlsx (never takes over the default program).
- Finish page and Settings › About carry one quiet link to buymeacoffee.com/awmhathif. Nothing else asks for support.

## Downloads (not in git)

Unpack each zip into its own folder (named after the zip) inside the `-Downloads` folder. The modern set comes from
the developer folders `EngineSpike\engines\mariadb-11.8.10-winx64` and `ParityTests\web\bundle`.

| File | From | SHA-256 |
|---|---|---|
| mariadb-10.4.22-win32.zip | archive.mariadb.org | 52e37e10218aa55b2fedaf7d9bc34e4bffff69b72ef15a805526443193fbaf7c |
| php-8.2.36-nts-Win32-vs16-x64.zip | windows.php.net | 0b541b7e9db3c55b9324f3f1f30f084a340657d1b57ca4f7a3acaca6b2e8a799 |
| php-8.2.36-nts-Win32-vs16-x86.zip | windows.php.net | 035e6bf30217a18896a2a6933ce2c28c438b10416b6a5fffee688cacacf0be21 |
| httpd-2.4.66-251206-Win64-VS17.zip | apachelounge.com | 2cd1f349b6705e43e784e876a233eeb1a859fa6b8abc693a91f87b35a368f7f9 |
| httpd-2.4.66-251206-win32-vs17.zip | apachelounge.com | 798b6bd7a4fcbb66ae3436b86bbc2e29f3e6165b970d471c1589dd782fff2b12 |
| innosetup-6.7.4.exe (the builder) | github.com/jrsoftware | a549929319f9120716677c685535ca4671e4b937b5fdc87e2585f32d8eb30e8c |

Why these versions: PHP 8.3+ and Apache Lounge's VS18 builds no longer run on Windows 7; MariaDB 10.4.22 is the last
32-bit Windows build. Every hash matched the publisher's published checksum.

## Tested on this PC (Windows 10 64-bit)

- Main installer: silent install, start, server on 3306, web (Caddy + PHP 8.5) serving a project that reads the database.
- Upgrade over a running copy (database and web running): closed cleanly, data kept. Uninstall: everything removed
  except data. Reinstall: the old data found again.
- Win7/32-bit installer on this PC: refuses with the right message.
- The legacy and x86 sets, installed with test builds (`/DTestSet=legacy|x86`, never used for releases): server, Apache
  and PHP 8.2 (64-bit and 32-bit) serving a project, Doctor all healthy.

Not tested yet: a real Windows 7 PC, a real 32-bit PC, a PC without .NET 4.8 (the download-and-install step), and the
"delete my data too" answer at uninstall (it needs the dialog). A Windows 7 virtual machine is the way to check the
first three before announcing Windows 7 support widely.

## Releasing

1. Run `build.ps1`. Upload both `.exe` files and `SHA256SUMS.txt` to a GitHub release (awmhathif/LuminaDB).
2. The installers aren't code-signed yet, so Windows SmartScreen shows "Windows protected your PC" the first times
   people run them (More info › Run anyway). Say so on the release page. Free signing for open-source projects:
   apply at signpath.org (needs the repository public and a release to point at), then sign both `.exe` files before
   uploading.
3. MariaDB and phpMyAdmin are GPLv2. THIRD-PARTY-NOTICES.txt links their source and offers it on request; keep that
   offer good (or attach the MariaDB source archives to the release).
