# Web stack parity test: replacing XAMPP's Apache + PHP + phpMyAdmin with portable pieces

Tested on 2026-11-23 on Windows 10 Pro 22H2 (10.0.19045), x64. Shell: Git Bash. Database: the portable MariaDB 11.8.10
already in `EngineSpike\engines\` (not modified), with a fresh data dir in `ParityTests\web\mariadb-data`, port 3510, bound to 127.0.0.1.

**Verdict: yes, we can replace it.** Every functional test passed with no installer, no admin rights, no Windows
service, no PATH or registry change, and loopback-only binding. There are three conditions:

1. **The VC++ runtime has to ship next to the binaries, and its version matters.** PHP 8.6.0 (VS18) refuses to start
   with the 14.44 runtime that is installed on this PC and bundled with MariaDB. It needs 14.51 or newer.
2. **Current PHP can't run on Windows 7.** Its imports require Windows 8 or later. Caddy needs Windows 10 or later.
3. **Don't use `php -S` for phpMyAdmin plus the app.** It is single-threaded and cannot fork on Windows.

My recommendation is **Caddy 2.11.9 + NTS `php-cgi.exe -b` with `PHP_FCGI_CHILDREN=4`**. The trimmed set zips to 58 MB.

---

## 1. Versions, URLs, SHA-256 (all verified)

| Component | File | Size (bytes) | Published checksum source | SHA-256 (matched) |
|---|---|---|---|---|
| PHP 8.6.0 NTS x64 (VS18) | https://downloads.php.net/~windows/releases/archives/php-8.6.0-nts-Win32-vs18-x64.zip | 39,660,643 | windows.php.net/download page + releases/sha256sum.txt | `d4783fffe1f4ab072600daff1622a7a2df936957a787a338c59e3dbd4d846d98` |
| PHP 8.6.0 TS x64 (VS18) | …/archives/php-8.6.0-Win32-vs18-x64.zip | 39,726,247 | same | `daf3639addc12197bfea7aff8797edc78939f5d8809e0cced49d899347c337b0` |
| PHP 8.5.13 NTS x64 (VS17) | https://downloads.php.net/~windows/releases/php-8.5.13-nts-Win32-vs17-x64.zip | 36,399,949 | releases/sha256sum.txt | `4ae6c72bc0ca4f6b8f3e9b107874bae0135699f3aabe6c26a1f6103d040eea97` |
| PHP 8.5.13 TS x64 (VS17) | …/releases/php-8.5.13-Win32-vs17-x64.zip | 36,539,776 | releases/sha256sum.txt | `792fe1553895ee98f9bc3c9d50c539910f7d0066676a6523207ca91429d4d475` |
| PHP 8.3.37 NTS x64 (VS16), for the Win7 check only | …/releases/php-8.3.37-nts-Win32-vs16-x64.zip | 34,185,054 | releases/sha256sum.txt | `38808a798485e1c0938dfcce7e942131415fc6c7834f83a5a85a4c4edb5539d6` |
| PHP 8.2.36 NTS x64 (VS16), for the Win7 check only | …/releases/php-8.2.36-nts-Win32-vs16-x64.zip | 33,785,611 | releases/sha256sum.txt | `0b541b7e9db3c55b9324f3f1f30f084a340657d1b57ca4f7a3acaca6b2e8a799` |
| phpMyAdmin 5.2.3 all-languages | https://files.phpmyadmin.net/phpMyAdmin/5.2.3/phpMyAdmin-5.2.3-all-languages.zip | 16,431,330 | `.zip.sha256` next to it | `2d2e13c735366d318425c78e4ee2cc8fc648d77faba3ddea2cd516e43885733f` |
| Apache 2.4.71 Win64 VS18 (Apache Lounge build 261112) | https://www.apachelounge.com/download/VS18/binaries/httpd-2.4.71-261112-Win64-VS18.zip | 14,518,924 | `…zip.txt` on apachelounge.com (SHA1/224/256/384/512 + `.asc`) | `44961a929dff16b767b3adbcea55760a942a00239b2891dc55806be34029e1fc` |
| Caddy 2.11.9 windows_amd64 (GitHub release, Go 1.27.3) | https://github.com/caddyserver/caddy/releases/download/v2.11.9/caddy_2.11.9_windows_amd64.zip | 18,384,117 | `caddy_2.11.9_checksums.txt` (SHA-512 only) | SHA-512 `98753d5d…b06871` matched (full value is in downloads\caddy_2.11.9_checksums.txt) |

Other notes on the downloads:

- **mod_fcgid 2.3.10 was skipped.** Apache Lounge publishes no checksum for its modules: `mod_fcgid-…zip.txt` returns an
  HTML page. I deleted it unextracted and used `mod_proxy_fcgi`, which ships inside the verified httpd zip.
- **The PHP signatures were not checked.** SPDX/CDX SBOMs and GPG `.asc` files were not used. Only SHA-256 was verified.
- **The VC++ 14.51 runtime DLLs have no download.** No VC++ redist was downloaded or installed. To *test* PHP 8.6 I
  copied the Microsoft-signed `vcruntime140.dll`, `vcruntime140_1.dll` and `msvcp140.dll` (14.51.36247.0) from the local
  Edge install into `runtime\vc14.51-from-edge\`. For shipping, take them from Microsoft's redistributable (see §3).
- **Each archive is in its own folder.** Every archive was extracted into its own `downloads\x_<name>\` folder. Working
  copies are in `bundle\` and the trimmed ship set is in `dist\`.

## 2. Results per test

| # | Test | Result | Evidence / exact error |
|---|---|---|---|
| 1a | `php -v`, `php -m` with PHP 8.6.0 as downloaded, on this PC (system runtime is 14.44) | **FAIL** | `PHP Warning:  'C:\WINDOWS\SYSTEM32\VCRUNTIME140.dll' 14.44 is not compatible with this PHP build linked with 14.51 in Unknown on line 0`, exit 1. Copying MariaDB's 14.44 DLLs next to php.exe gives the same result. |
| 1b | PHP 8.6.0 with the 14.51 runtime copied next to php.exe | PASS | `PHP 8.6.0 (cli) … (NTS Visual C++ 2026 x64)`. All 10 extensions load: mysqli, pdo_mysql, mbstring, openssl, curl, fileinfo, gd, intl, zip, sodium. `Process.Modules` shows VCRUNTIME140/140_1/MSVCP140 14.51 loaded from the PHP folder, and `ucrtbase.dll` from System32. |
| 1c | PHP 8.5.13 with MariaDB's 14.44 DLLs next to php.exe | PASS | Same 10 extensions load. The modules list shows the 14.44 DLLs loaded from the PHP folder. |
| 1d | `extension_dir = "ext"` (relative) | PASS for php.exe / php-cgi.exe; **FAIL for mod_php** | It works from any CWD (tested with `C:\` and `C:\Windows`): LoadLibrary searches the exe's folder first. Inside httpd.exe the exe folder is `apache\bin`, so every extension failed with `PHP Warning:  PHP Startup: Unable to load dynamic library 'curl' (tried: ext\\curl (The specified module could not be found), ext\\php_curl.dll (…))`. **The app must write an absolute `extension_dir`.** |
| 2 | Sample app on `php -S 127.0.0.1:8010` (PHP 8.6) | **PASS 27/27** | Covers CRUD with mysqli and PDO (prepared statements), the HTML form with POST-redirect-GET, and the JSON API (GET/POST/PUT/DELETE, 404/405/422). Sinhala (with ZWJ), Tamil and emoji written by one driver and read back by the other are byte-exact. MariaDB `HEX()` equals the Python UTF-8 encoding. Sessions: 401, then login, then counter 1→2→3→4, then 401 without a cookie. A 1 MiB binary and a PNG were uploaded multipart and the SHA-256 matched. The router.php pretty URLs worked. PHP 8.5.13 also passed 27/27. |
| 2t | php -S timings | — | Time to accepting connections: **229–258 ms** (median of 4 runs about 245 ms). Request time across 22 requests: avg 24 ms, max 50 ms. Non-DB pages take about 2 ms. DB pages take 20–40 ms because they open two connections. |
| 3 | phpMyAdmin 5.2.3 on `php -S 127.0.0.1:8020` | **PASS 9/9** (8.6 and 8.5) | Cookie login as root with an empty password gives a 302. The main page shows `11.8.10-MariaDB - MariaDB Server`. SQL through `/import` (ajax) worked, as did multi-statement DDL+DML. The database export as SQL arrives as an attachment with CREATE TABLE and INSERT, and the utf8mb4 text survives. Server status, database structure and logout also worked. |
| 3w | phpMyAdmin warnings and issues | PARTIAL | (a) With all 10 extensions there are no extension warnings, only "configuration storage is not completely configured" (an info notice). (b) With only mysqli: `The mbstring PHP extension was not found and you seem to be using a multibyte charset…`. (c) With no mysqli: `The mysqli extension is missing. Please check your PHP configuration.` (d) **PHP 8.6 + phpMyAdmin 5.2.3** logs `PHP Deprecated:  Function is_integer() is deprecated since 8.6, use is_int() instead in …vendor\slim\psr7\src\Uri.php on line 319` (10 times per test run). With `php -n` (PHP's default display_errors=1) the message is printed above `<!DOCTYPE` in the page. **Use `display_errors=Off`, or `error_reporting=E_ALL & ~E_DEPRECATED`.** PHP 8.5.13 logs 0 deprecations. (e) The "HTTPS mismatch" text in the HTML is a hidden JS template, not a real warning. (f) The login form needs the `set_session` hidden field, or the POST silently re-shows the login form. |
| 3b | phpMyAdmin under php -S (single-threaded) | **FAIL for real use** | While `slow.php` (sleep 2 s) was running, a request issued 0.1 s later took **1.93 s**, so the server is blocked. `PHP_CLI_SERVER_WORKERS=4` on Windows prints `forking is not supported on this platform` and stays single-process. A long phpMyAdmin import or export freezes the student's app as well. |
| 4a | Apache 2.4.71 + mod_php (PHP 8.6 TS), `Listen 127.0.0.1:8030`, run as a normal process | **PASS after 3 fixes** | Without the fixes: (1) `PHP Warning: 'C:\WINDOWS\SYSTEM32\VCRUNTIME140.dll' 14.44 is not compatible…`, then `AH00020: Configuration Failed, exiting`. **The 14.51 runtime must sit next to httpd.exe**, not next to php8ts.dll. (2) With an absolute `extension_dir`, `curl`, `intl`, `openssl` and `sodium` still failed with "The specified module could not be found". Their dependencies (libcrypto-4, icu*, libssh2, libsodium) are not on httpd's DLL search path. Fix: set `PATH=<php dir>;System32` **for the httpd child process only**. (3) A relative `<Directory "../../app">` gives 403, so I used `${SRVROOT}` supplied on the command line (`-C "Define SRVROOT …"`). After the fixes: app 27/27, .htaccess mod_rewrite pretty URLs PASS, phpMyAdmin at /phpmyadmin 9/9. 20 parallel requests: wall 0.23 s, 0 failures. 100 parallel: 1.02 s, 0 failures. No head-of-line blocking (0.026 s). PHP files in `/uploads` are not executed, and phpMyAdmin `libraries/` returns 403. Startup takes **0.9–1.1 s**. |
| 4b | Apache + mod_proxy_fcgi + NTS php-cgi | PASS after a Windows-specific fix | `SetHandler "proxy:fcgi://127.0.0.1:9001"` gives `500 Proxy Error … DNS lookup failure for: 127.0.0.1:9001c:`. With a trailing `/`, php-cgi answers `No input file specified.` because `SCRIPT_FILENAME` arrives as `proxy:fcgi://127.0.0.1:9001/C:/…`. Fixed with `ProxyFCGISetEnvIf "reqenv('SCRIPT_FILENAME') =~ m#^proxy:fcgi://[^/]+/([A-Za-z]:/.*)$#" SCRIPT_FILENAME "$1"`. Then app 27/27 and phpMyAdmin 9/9. With a single php-cgi the server is **blocked** (1.94 s). |
| 4c | Caddy 2.11.9 + `php_fastcgi` + `php-cgi.exe -b` | **PASS** | App 27/27 (Caddyfile rewrites for the pretty URLs) and phpMyAdmin 9/9 at `/phpmyadmin`. Startup takes **227–330 ms**. Graceful stop with `caddy stop --address 127.0.0.1:8089` takes 0.12 s. Re-run on PHP 8.5.13: also 27/27 and 9/9. |
| 4d | php-cgi `PHP_FCGI_MAX_REQUESTS` | Confirmed; solved | Default: **request 501 fails**. `{'200': 500, '503': 10}` comes back, the php-cgi process exits and port 9001 stops listening. With `PHP_FCGI_MAX_REQUESTS=0`: 1200/1200 OK. **`PHP_FCGI_CHILDREN=N` works on Windows**: the parent spawned 4 children and re-spawned a child I killed (13340, replaced by 2172). With `PHP_FCGI_CHILDREN=2` and the default 500 limit, 1300/1300 were OK because children recycle. Killing the parent kills its children too (port freed, Caddy returns 502). |
| 4e | Concurrency, 20 parallel curl | PASS | Caddy + php-cgi with 4 children: 20/20 OK, wall 0.24–0.31 s, 4 distinct PHP PIDs, slow request **not blocking** (0.027–0.030 s). 100 parallel: 100/100 in 1.09 s. php -S: 20/20 OK (0.25–0.30 s) but blocking. |
| 5 | Port conflict on 8010 | PASS (detection), with caveats | **Detection:** `netstat -ano` shows `TCP 127.0.0.1:8010 … LISTENING 16060`. `Get-NetTCPConnection -LocalPort 8010 -State Listen` gives PID and process name (`python`). A bind probe with `SO_EXCLUSIVEADDRUSE` (`scripts\launch.py port_free`) returns False. **Exact errors:** php -S: `Failed to listen on 127.0.0.1:8010 (reason: An attempt was made to access a socket in a way forbidden by its access permissions)`, exit 1. That message is misleading: it is a conflict, not a permissions problem. httpd: `(OS 10048)Only one usage of each socket address … AH00072: make_sock: could not bind to address 127.0.0.1:8010` / `AH00451: no listening sockets available, shutting down`, exit 1. php-cgi: `Cannot bind/listen socket - [9] Bad file descriptor.` / `Couldn't create FastCGI listen socket on port 127.0.0.1:8010`. Caddy with `bind 127.0.0.1`: `listen tcp 127.0.0.1:8010: bind: Only one usage of each socket address…`, exit 1. **Caveats:** (1) Caddy **without** `bind` listens on `:8010` (all interfaces) and **starts with no error** even though 127.0.0.1:8010 is taken, so always use `bind 127.0.0.1`. (2) If another program holds **0.0.0.0:8013**, php -S on 127.0.0.1:8013 **starts silently** and a plain 127.0.0.1 bind probe says "free". The probe must also try 0.0.0.0 (`port_free(strict=True)` returns False; verified). |
| 6 | Sizes of the trimmed set | PASS | See §4. php+phpMyAdmin+Caddy: **173.7 MB raw / 58.3 MB zipped**. php+phpMyAdmin+Apache: 140.0 MB / 47.2 MB. I rebuilt `dist\` and re-ran app 27/27, phpMyAdmin 9/9 and concurrency on it. |
| — | Re-run of the whole suite (`scripts\run_all.sh`) | PASS | php -S 27/27 + 9/9 (blocked), Apache mod_php 27/27 + 9/9, Caddy (dist) 27/27 + 9/9. Every process I started was stopped. `netstat` is clean on 3510, 80xx and 900x. MariaDB was stopped with SQL `SHUTDOWN`. |

One app bug was found and fixed while testing; it is a useful note for student code. `db_mysqli()->prepare(...)`,
with no variable holding the connection, throws `mysqli_sql_exception: MySQL server has gone away` on `execute()`.
The temporary `mysqli` object is destroyed, which closes the link that the statement still needs.

One test flake was fixed. The random upload body once started with `MZ`, and `finfo` reported
`application/x-dosexec`. The test file now has a fixed 16-byte prefix.

## 3. DLL dependency findings

I read the dependencies from the files: `scripts\peinfo.py` is a pure-Python PE import/delay-import parser, and
`scripts\crtcheck.py` checks every imported CRT function against the exports of a candidate runtime DLL. I did not
infer them from "it ran". Raw outputs are in `results\pe_*.txt` and `results\crtcheck_*.txt`.

**What PHP imports.** PHP 8.6.0 (all of `php*.exe`, `php8.dll`, `php8ts.dll`, `php8apache2_4.dll`, every `ext\*.dll`
and every dependency DLL) imports only these CRT pieces:
- `VCRUNTIME140.dll`: 60 binaries.
- `VCRUNTIME140_1.dll` and `MSVCP140.dll`: only `icuuc77`, `icuin77` (so intl) and `php_gd`.
- The `api-ms-win-crt-*` set, which is the UCRT. It is part of Windows 10/11. Windows 8.1 has it only with KB2999226.

**None of these runtime DLLs is inside the PHP zip.** The Apache Lounge zip doesn't contain them either. Every
PHP 8.6 binary has linker version **14.51**, and php8.dll enforces it at startup: 14.44 gives the "is not
compatible" error. PHP 8.5.13 is linked with 14.44 and PHP 8.2/8.3 with 14.29.

**Can MariaDB's copies be reused?** MariaDB's `bin\vcruntime140.dll`, `vcruntime140_1.dll` and `msvcp140.dll` are
**14.44.35211.0**, the same as System32 on this PC.
- They export every function that PHP 8.6, PHP 8.5 and Apache 2.4.71 import (crtcheck: "ALL CRT IMPORTS RESOLVED").
- But PHP 8.6's version check rejects them. So they are reusable for **PHP 8.5.x** (and older), not for **PHP 8.6**.

**What to ship next to php.exe and php-cgi.exe** on a PC with no VC++ installed:
- `vcruntime140.dll`, `vcruntime140_1.dll` and `msvcp140.dll`, at a version ≥ the PHP build's linker version (≥ 14.51
  for 8.6, ≥ 14.44 for 8.5).
- The newer runtime is backward compatible, so the simplest rule is to ship the newest one. MariaDB keeps its own
  14.44 copies in its own `bin`, and each exe loads from its own folder first. I verified that with the loaded-modules
  list.
- Nothing else is needed on Windows 10/11, because the UCRT is part of the OS.

**For Apache mod_php,** the runtime must be next to **httpd.exe**. The process loads VCRUNTIME140 before PHP. httpd
also needs `PATH=<php dir>` for the extension dependency DLLs.

**Watch for DLL name clashes inside httpd.** Apache's `bin` also contains `libcrypto-3-x64.dll`, `nghttp2.dll` (1.71
vs PHP's 1.70) and `brotli*.dll`. Inside httpd, the copies in the exe folder win. That is a hidden risk for mod_php
with PHP ≤ 8.5, which also uses `libcrypto-3`. With php-cgi under Caddy there is no shared process, so it isn't a
problem.

**Where to get the 14.51 DLLs legally.** Use Microsoft's redistributable files. These are the
`VC\Redist\MSVC\<ver>\x64\Microsoft.VC145.CRT\` folder of a Visual Studio 2026 / Build Tools install. App-local
deployment of those files is permitted by the VS redist terms. The alternative is to extract them from
`https://aka.ms/vc14/vc_redist.x64.exe` on a build machine. I used Edge's signed copies **only for testing**. Don't ship
files taken from Edge.

### Windows 7 verdict

**Current PHP (8.6.0, and also 8.5.13) cannot run on Windows 7.**

- The PE header says OS/subsystem 6.0 (Vista), but `php8.dll` / `php8ts.dll` import
  `KERNEL32!GetSystemTimePreciseAsFileTime` (Win8+) and `KERNEL32!GetCurrentThreadStackLimits` (Win8+). They also
  import the API set `api-ms-win-core-path-l1-1-0.dll` (Win8+).
- The 8.6 `libpq.dll` also imports `GetSystemTimePreciseAsFileTime`.
- The 14.51 `msvcp140.dll` imports `GetSystemTimePreciseAsFileTime`, `CopyFile2` and `CreateFile2` (Win8+). The 14.44
  copy has none of those.
- **PHP 8.3.37** already imports `GetCurrentThreadStackLimits`. This matches php-src `PHP-8.3/UPGRADING` (fetched):
  *"Minimum supported Windows version has been bumped to Windows 8 or Windows Server 2012"*.
- **PHP 8.2.36 is the newest PHP that can target Windows 7.** Its whole tree (63 binaries, root + ext) has none of the
  flagged imports. It uses a 14.29 runtime, and the 14.44 copies scan clean.
- **This is UNVERIFIED on a real Windows 7 machine.** The import check uses a list of known Win8+ functions; it is not
  a full Win7 export diff.
- **PHP 8.2 security support ends 31 Dec 2026, which is 5 weeks from now** (php.net/supported-versions).
- **Caddy 2.11.9** (Go 1.27) has PE OS/subsystem version **10.0**, so Windows 10+ only.
- **Apache Lounge VS18**: its ReadMe lists "Windows 8 / 8.1" as the minimum client OS. No Win8+ imports were found in
  httpd or its modules.
- Practical Win7 answer: PHP 8.2.36 + `php -S`, or Apache Lounge VS18 + 14.44 runtime, is the only theoretical path. It
  is untested, and EOL soon. I recommend declaring **Windows 10+** as the requirement.

## 4. Recommended bundle

**Use Caddy + php-cgi (NTS) with `PHP_FCGI_CHILDREN`.**

| | php -S | Apache Lounge (mod_php) | Caddy + php-cgi |
|---|---|---|---|
| Concurrency | **single-threaded, blocks** (can't fork on Windows) | multi-threaded | 4+ PHP children, not blocked |
| Moving parts | 1 exe | httpd + TS PHP; runtime must be next to httpd.exe; PATH injection for ext DLLs; `${SRVROOT}`; DLL-name clashes | 2 exes, separate processes; PHP keeps its own DLL dir |
| Start time | ~0.25 s | ~0.9–1.1 s | ~0.25 s + ~0.25 s |
| .htaccess (student projects that use it) | ignored (needs router.php) | **native** | ignored; translate to Caddyfile rules |
| Size (trimmed, zipped) | 0 extra | 6.0 MB | 17.1 MB |
| OS | Win8+ (PHP) | Win8+ | **Win10+** |

**When to pick Apache instead.** XAMPP-style projects lean on `.htaccess` (Laravel's `public/.htaccess`, WordPress
permalinks, CodeIgniter). If that matters more than simplicity, Apache Lounge + mod_php works (4a). Use exactly the
config in `bundle\apache\conf\httpd-parity.conf` and the three fixes above. Apache + mod_proxy_fcgi + php-cgi with
children also works and avoids the in-process DLL issues.

**`php -S` is fine for a quick "run this one file" button only.**

**PHP version to ship.**
- **PHP 8.5.13 NTS** (supported until 2029-12-31) passes everything. It works with the same 14.44 runtime MariaDB
  already ships, and it causes no phpMyAdmin deprecations.
- **PHP 8.6.0** (released 2026-11-19) also passes everything, if you ship the 14.51+ runtime and `display_errors=Off`
  for phpMyAdmin.
- Either way, ship the newest VC runtime next to php.exe.

### Exact commands the app should run

All are normal user processes. Paths are relative to the app's install folder `<B>`.

```bat
:: 1) pick ports: probe 127.0.0.1 AND 0.0.0.0 with SO_EXCLUSIVEADDRUSE (or GetExtendedTcpTable); if busy, try next in range
:: 2) PHP FastCGI pool (inherits this env only; no system PATH change)
set PHP_FCGI_CHILDREN=4
set PHP_FCGI_MAX_REQUESTS=500
"<B>\php\php-cgi.exe" -b 127.0.0.1:9002        (tested form: php.ini is picked up from the exe folder)
:: 3) web server (working directory = <B>\caddy so relative roots resolve)
set PHP_UPSTREAMS=127.0.0.1:9002
cd /d "<B>\caddy" && caddy.exe run --config Caddyfile --adapter caddyfile
:: stop (graceful), then the PHP pool
caddy.exe stop --address 127.0.0.1:8089
taskkill /PID <php-cgi parent pid> /T /F     (children exit with the parent; better: put both in a Job Object
                                               with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE so a crash of our app cleans up)
```

Readiness check: poll a TCP connect to the web port. Both servers accept in about 250 ms.

**Generated `php.ini`** starts from `php.ini-development` (`scripts\make_phpini.sh` does this) and changes:
- `extension_dir = "<absolute B>\php\ext"`.
- `extension=` for the 10 extensions.
- `upload_tmp_dir` and `session.save_path` set to `<B>\tmp\…`.
- `upload_max_filesize` / `post_max_size = 64M`, `memory_limit = 256M`, `date.timezone = Asia/Colombo`.
- Add `display_errors = Off` and `log_errors = On` for the phpMyAdmin pool (or exclude `E_DEPRECATED`).

**Generated `config.inc.php` for phpMyAdmin.** The template is `scripts\config.inc.php.template`: cookie auth, host
`127.0.0.1`, port as generated, `connect_type tcp`, `AllowNoPassword true`, a random 32-char `blowfish_secret`
generated per install, `TempDir <B>\tmp\pma`, `VersionCheck false` and `SendErrorReports never` (no calls out to the
internet).

**Generated `Caddyfile`** (the tested version is in `bundle\caddy\Caddyfile`):
- `admin 127.0.0.1:<port>`, `auto_https off`, `persist_config off`, `storage file_system ./data`, logs in `./logs`.
- The site is `http://127.0.0.1:<port>` with **`bind 127.0.0.1`** and `request_body max_size 64MB`.
- `/phpmyadmin/*` uses root `..`, a 403 for `libraries/ templates/ vendor/ sql/ setup/lib/ config.inc.php`, and
  `php_fastcgi {$PHP_UPSTREAMS}` with `lb_policy least_conn` and `lb_try_duration 5s`.
- The app's handle has a 403 for `/uploads/*.php`, `path_regexp` rewrites for the pretty URLs, then `php_fastcgi` and
  `file_server`.
- The `respond` deny rules **must be inside the `handle` blocks**. At site level Caddy runs `handle` first, and
  `config.inc.php` returned 200.

### Trimmed size (`scripts\build_dist.py`, output in `dist\` and `dist-zips\`)

| Component | Contents | Raw | Zipped (deflate-9) |
|---|---|---|---|
| php | php.exe, php-cgi.exe, php8.dll, 10 ext DLLs, libcrypto-4/libssl-4/libssh2/nghttp2/brotli*/libsodium/icu*, VC 14.51 ×3, php.ini | 92.7 MB | 32.9 MB |
| phpmyadmin | 5.2.3 without setup/, doc/, examples/, js/src, 3 of 4 themes (kept pmahomme), locales except si/ta/en_GB | 31.4 MB | 8.2 MB |
| caddy | caddy.exe + Caddyfile | 49.6 MB | 17.1 MB |
| apache (alternative) | bin (minus tools), 12 modules, conf | 15.9 MB | 6.0 MB |
| **php + phpMyAdmin + Caddy** | | **173.7 MB** | **58.3 MB** |
| php + phpMyAdmin + Apache | | 140.0 MB | 47.2 MB |

**The big items:**
- intl's ICU data is 36.7 MB raw. Dropping `intl` saves about 37 MB raw and roughly 13 MB zipped.
- `php_gd.dll` is 17.0 MB and `php_fileinfo.dll` is 10.9 MB.
- For a mod_php Apache bundle, swap NTS for the TS PHP folder (about the same size).

## 5. XAMPP features not covered

| XAMPP piece | Covered? | Matters for HNDIT students? |
|---|---|---|
| Apache + mod_php, .htaccess | Covered by Caddy rules, or by Apache Lounge if .htaccess fidelity is needed | Medium. Many PHP tutorials and frameworks assume .htaccess. |
| PHP CLI (`php`, Composer) | php.exe is in the bundle (Composer not tested) | Medium. Composer is needed for Laravel. |
| phpMyAdmin | Covered | High |
| MySQL/MariaDB | Covered by the separate MariaDB engine (another agent) | High |
| HTTPS on localhost (XAMPP ships a self-signed Apache cert) | Not tested. Caddy can do `tls internal`, but that needs a root CA installed into the user store; we disabled it with `skip_install_trust`. | Low |
| FileZilla FTP server | No | Low. Local development doesn't need FTP. Hosting upload is done with the FileZilla *client*, which is a separate app. |
| Mercury/32 mail server; `sendmail` / `mail()` | No. PHP `mail()` has no SMTP to talk to. | Low-medium. "Contact form" exercises call `mail()`. Use PHPMailer with an external SMTP, or later a tiny catch-all SMTP sink such as Mailpit (single exe). |
| Tomcat (Java servlets/JSP) | No | Low for PHP courses. Relevant only if a module teaches JSP. |
| Perl (mod_perl, Strawberry Perl CGI) | No | Very low |
| XAMPP Control Panel (start/stop, ports, logs) | Our app replaces it with the commands above | High (UX). Port-conflict handling as in test 5. |
| Webalizer, `security/` page, `htdocs/dashboard` | No | Negligible |

## What I could not test or what remains unverified

- **No PC without the VC++ runtime was available.** The "no VC++ installed" claim rests on the import scans, the
  export cross-check, and the loaded-modules lists, which show every CRT DLL except the OS's `ucrtbase.dll` coming from
  the app folder. It was not tested on a clean machine.
- **Windows 7 and 8.1 were not tested on real machines.** The verdict comes from import scans and php-src
  documentation.
- **phpMyAdmin through curl only.** It was driven by curl, not a browser, so the JavaScript UI was not exercised.
- **Large phpMyAdmin imports (file upload to /import) were not tested.** Only SQL-text import was.
- **IPv6 conflicts were not tested.** A process on `[::]:port` was not tested as a port-conflict case.
- **During the port-conflict test,** a throw-away Caddyfile without a global `admin` line briefly opened Caddy's
  default admin endpoint on `localhost:2019`, outside the agreed range, for about 10 s. The real Caddyfile pins admin
  to 127.0.0.1:8089.

## Reproduce

Scripts are in `scripts\`:

| Script | Purpose |
|---|---|
| `peinfo.py` | PE imports, delay imports, Win8+ check |
| `crtcheck.py` | CRT import vs export check |
| `make_phpini.sh` | Generates php.ini |
| `launch.py` / `stop.py` | Start, wait for the port, record the PID; kill only recorded PIDs |
| `test_app.py`, `test_pma.py`, `concurrency.py`, `maxreq.py` | curl test drivers |
| `blocker.py` | Port holder for test 5 |
| `build_dist.py` | Trimmed bundle + sizes |
| `run_all.sh` | Full re-run |

Other folders:

| Folder | Contents |
|---|---|
| `app\` | The sample app |
| `bundle\` | Working configs (`apache\conf\httpd-parity.conf`, `caddy\Caddyfile`, `phpmyadmin\config.inc.php`) |
| `results\` | All logs, outputs, exports |
