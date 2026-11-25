<#
  Builds the three trimmed engine sets and the app, ready for Inno Setup:

    out\stage\app\                  LuminaDB.exe and its DLLs (Release)
    out\stage\engines\modern\       Windows 10/11 64-bit: MariaDB 11.8, PHP 8.5, Caddy + Apache 2.4.71
    out\stage\engines\legacy\       Windows 7/8/8.1 64-bit: MariaDB 10.4.34, PHP 8.2 x64, Apache 2.4.66 VS17 x64
    out\stage\engines\x86\          32-bit Windows: MariaDB 10.4.22 win32, PHP 8.2 x86, Apache 2.4.66 VS17 win32
    out\stage\shared\               phpMyAdmin, Composer and the WordPress zip (one copy, installed into the chosen set)

  The third-party files are not in git. Pass the folders they were unpacked into (the defaults are the developer ones).
  Each set holds: mariadb\, php\, apache\, (caddy\ for modern), and gets phpmyadmin\, composer\, templates\ at install.
#>
param(
    [string]$Out = (Join-Path $PSScriptRoot 'out'),
    [string]$EngineSpike = (Join-Path $PSScriptRoot '..\..\EngineSpike\engines'),
    [string]$WebBundle = (Join-Path $PSScriptRoot '..\..\ParityTests\web\bundle'),
    [Parameter(Mandatory = $true)][string]$Downloads,   # folder holding the unpacked downloads (see packaging\README.md)
    [switch]$SkipApp
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

function Join-Full([string]$a, [string]$b) { [IO.Path]::GetFullPath((Join-Path $a $b)) }
function Need([string]$p) { if (-not (Test-Path -LiteralPath $p)) { throw "Missing: $p" }; (Resolve-Path -LiteralPath $p).Path }
function Size-MB([string]$p) { '{0:N1} MB' -f (((Get-ChildItem -LiteralPath $p -Recurse -File | Measure-Object Length -Sum).Sum) / 1MB) }

function Copy-Tree([string]$from, [string]$to, [string[]]$skipDirs = @(), [string[]]$skipFiles = @()) {
    New-Item -ItemType Directory -Force $to | Out-Null
    $xd = @(); foreach ($d in $skipDirs) { $xd += (Join-Path $from $d) }
    $args = @($from, $to, '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/R:1', '/W:1')
    if ($xd.Count) { $args += '/XD'; $args += $xd }
    if ($skipFiles.Count) { $args += '/XF'; $args += $skipFiles }
    & robocopy.exe @args | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE): $from -> $to" }
}

# MariaDB: the programs LuminaDB runs (EngineSet probes the 11.x name first, then the 10.4 name), every DLL beside
# them, the plugins and share\ (error messages, charsets). No debug symbols, static libraries, headers or test tools.
function Copy-MariaDb([string]$from, [string]$to) {
    $from = Need $from
    $bin = Join-Path $to 'bin'
    New-Item -ItemType Directory -Force $bin | Out-Null
    $pairs = @(
        @('mariadbd.exe', 'mysqld.exe'),
        @('mariadb-install-db.exe', 'mysql_install_db.exe'),
        @('mariadb.exe', 'mysql.exe'),
        @('mariadb-dump.exe', 'mysqldump.exe'),
        @('mariadb-check.exe', 'mysqlcheck.exe'),
        @('mariadb-upgrade.exe', 'mysql_upgrade.exe'),
        @('aria_chk.exe'),
        @('myisamchk.exe')
    )
    foreach ($pair in $pairs) {
        $hit = $pair | Where-Object { Test-Path (Join-Path "$from\bin" $_) } | Select-Object -First 1
        if (-not $hit) { throw "MariaDB in $from has none of: $($pair -join ', ')" }
        Copy-Item (Join-Path "$from\bin" $hit) $bin
    }
    # mariadb-install-db (11.x too) starts the server as mysqld.exe, so the server keeps its old name beside the new one.
    if (Test-Path "$from\bin\mysqld.exe") { Copy-Item "$from\bin\mysqld.exe" $bin -Force }
    Get-ChildItem "$from\bin" -Filter *.dll | Copy-Item -Destination $bin
    Copy-Tree "$from\lib\plugin" (Join-Path $to 'lib\plugin') @() @('*.pdb', '*.lib', '*.exp', '*.ilk', 'ha_rocksdb.dll', 'ha_mroonga.dll')
    Copy-Tree "$from\share" (Join-Path $to 'share')
    foreach ($f in 'COPYING', 'THIRDPARTY', 'CREDITS') { if (Test-Path "$from\$f") { Copy-Item "$from\$f" $to } }
}

# PHP: the whole build minus the SDK (dev\), the test/extras folders and the debug pack.
function Copy-Php([string]$from, [string]$to) {
    $from = Need $from
    Copy-Tree $from $to @('dev', 'extras') @('*.pdb', 'php.ini', 'php.ini-development', 'php.ini-production', 'deplister.exe', 'phpdbg.exe', 'php8phpdbg.dll', 'php8apache2_4.dll', 'snapshot.txt', 'php_pdo_firebird.dll')
}

# Apache: programs, modules, mime types and error pages. No manual, headers, import libraries or sample site.
function Copy-Apache([string]$from, [string]$to) {
    $from = Need $from
    Copy-Tree $from $to @('manual', 'include', 'lib', 'htdocs', 'cgi-bin', 'logs') @('*.pdb', '*.lib', '*.exp', 'ab.exe', 'abs.exe')
    New-Item -ItemType Directory -Force (Join-Path $to 'logs') | Out-Null
}

# The Visual C++ runtime ships beside every program that needs it (SPEC: no VC++ to install). Only these names.
function Copy-VcRuntime([string]$fromDir, [string[]]$toDirs) {
    $names = 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll', 'msvcp140_1.dll', 'msvcp140_2.dll', 'concrt140.dll'
    foreach ($t in $toDirs) {
        foreach ($n in $names) {
            $src = Join-Path $fromDir $n
            if ((Test-Path $src) -and -not (Test-Path (Join-Path $t $n))) { Copy-Item $src $t }
        }
    }
}

$stage = Join-Full $Out 'stage'
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
$dl = Need $Downloads

# ---------------------------------------------------------------- app
if (-not $SkipApp) {
    $sln = Join-Full $PSScriptRoot '..\src\LuminaDB.App\LuminaDB.App.csproj'
    $appOut = Join-Path $stage 'app\'
    & dotnet build $sln -c Release "-p:OutDir=$appOut" -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }
    Get-ChildItem $appOut -Recurse -Include *.pdb, *.xml | Where-Object { $_.Name -notlike '*.exe.config' } | Remove-Item -Force
}

# ---------------------------------------------------------------- engine sets
$sets = @(
    @{ Name = 'modern'; MariaDb = "$EngineSpike\mariadb-11.8.10-winx64"; Php = "$WebBundle\php85"; Apache = "$WebBundle\apache"; Caddy = "$WebBundle\caddy\caddy.exe"; Vc = "$env:SystemRoot\System32" },
    @{ Name = 'legacy'; MariaDb = "$EngineSpike\mariadb-10.4.34-winx64"; Php = "$dl\php-8.2.36-nts-Win32-vs16-x64"; Apache = "$dl\httpd-2.4.66-251206-Win64-VS17\Apache24"; Caddy = $null; Vc = "$env:SystemRoot\System32" },
    @{ Name = 'x86'; MariaDb = "$dl\mariadb-10.4.22-win32\mariadb-10.4.22-win32"; Php = "$dl\php-8.2.36-nts-Win32-vs16-x86"; Apache = "$dl\httpd-2.4.66-251206-win32-vs17\Apache24"; Caddy = $null; Vc = "$env:SystemRoot\SysWOW64" }
)
foreach ($s in $sets) {
    $root = Join-Path $stage ("engines\" + $s.Name)
    Copy-MariaDb $s.MariaDb (Join-Path $root 'mariadb')
    Copy-Php $s.Php (Join-Path $root 'php')
    Copy-Apache $s.Apache (Join-Path $root 'apache')
    if ($s.Caddy) { New-Item -ItemType Directory -Force (Join-Path $root 'caddy') | Out-Null; Copy-Item (Need $s.Caddy) (Join-Path $root 'caddy') }
    Copy-VcRuntime $s.Vc @((Join-Path $root 'mariadb\bin'), (Join-Path $root 'php'), (Join-Path $root 'apache\bin'))
    if ($s.Name -ne 'modern') {
        # Windows 7/8 without the Universal CRT update: MariaDB's app-local UCRT (Microsoft's, made for this) goes beside
        # PHP and Apache too. Windows 10/11 always use their own UCRT, so the modern set doesn't need it.
        $ucrt = Get-ChildItem (Join-Path $root 'mariadb\bin') -File | Where-Object { $_.Name -like 'api-ms-win-*.dll' -or $_.Name -eq 'ucrtbase.dll' }
        if (-not ($ucrt | Where-Object Name -eq 'ucrtbase.dll')) { throw "No app-local UCRT in $($s.MariaDb)" }
        foreach ($t in (Join-Path $root 'php'), (Join-Path $root 'apache\bin')) { $ucrt | Copy-Item -Destination $t -Force }
    }
    Write-Host ("{0,-7} {1}" -f $s.Name, (Size-MB $root))
}

# ---------------------------------------------------------------- shared by every set
$shared = Join-Path $stage 'shared'
Copy-Tree (Need "$WebBundle\phpmyadmin") (Join-Path $shared 'phpmyadmin') @('doc', 'examples', 'setup') @('config.inc.php', 'config.lumina-original.php', 'CONTRIBUTING.md', 'babel.config.json', 'package.json', 'yarn.lock', '.rtlcssrc.json')
New-Item -ItemType Directory -Force (Join-Path $shared 'composer'), (Join-Path $shared 'templates') | Out-Null
Copy-Item (Need "$WebBundle\composer\composer.phar") (Join-Path $shared 'composer')
Copy-Item (Need "$WebBundle\templates\wordpress.zip") (Join-Path $shared 'templates')
Write-Host ("shared  {0}" -f (Size-MB $shared))
if (-not $SkipApp) { Write-Host ("app     {0}" -f (Size-MB (Join-Path $stage 'app'))) }

# ---------------------------------------------------------------- licences (installed into {app}\licenses)
$lic = Join-Path $stage 'licenses'
New-Item -ItemType Directory -Force $lic | Out-Null
Copy-Item (Need (Join-Full $PSScriptRoot '..\..\LICENSE')) (Join-Path $lic 'LICENSE.txt')
Copy-Item (Need (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.txt')) $lic
Copy-Item (Need "$stage\engines\modern\mariadb\COPYING") (Join-Path $lic 'mariadb-COPYING.txt')
Copy-Item (Need "$stage\engines\modern\php\license.txt") (Join-Path $lic 'php-license.txt')
Copy-Item (Need "$stage\engines\modern\apache\LICENSE.txt") (Join-Path $lic 'apache-LICENSE.txt')
Copy-Item (Need "$stage\engines\modern\apache\NOTICE.txt") (Join-Path $lic 'apache-NOTICE.txt')
Copy-Item (Need "$shared\phpmyadmin\LICENSE") (Join-Path $lic 'phpmyadmin-LICENSE.txt')
Copy-Item (Need (Join-Path $PSScriptRoot 'composer-LICENSE.txt')) $lic
Write-Host ("licences {0} files" -f (Get-ChildItem $lic).Count)
exit 0
