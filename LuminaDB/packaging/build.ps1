<#
  Builds both LuminaDB installers into packaging\out\:
    LuminaDB-Setup.exe               Windows 10/11 64-bit
    LuminaDB-Setup-Win7-32bit.exe    Windows 7/8/8.1 64-bit and any 32-bit Windows
  plus SHA256SUMS.txt for the release page.

  .\build.ps1 -Downloads <folder with the unpacked engine downloads>      (see README.md)
  .\build.ps1 -SkipStage                                                  (reuse out\stage)
#>
param(
    [string]$Downloads,
    [switch]$SkipStage,
    [string]$Iscc
)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'out'

if (-not $SkipStage) {
    if (-not $Downloads) { throw 'Pass -Downloads (the folder holding the unpacked engine downloads), or -SkipStage to reuse out\stage.' }
    & (Join-Path $PSScriptRoot 'build-sets.ps1') -Downloads $Downloads
    if ($LASTEXITCODE -ne 0) { throw 'build-sets.ps1 failed' }
}

# Every program must find its DLLs on a clean PC; the Windows 7 sets must not use anything newer than Windows 7.
$check = Join-Path $PSScriptRoot 'check-imports.ps1'
& $check -SetDir (Join-Path $out 'stage\engines\modern'); if ($LASTEXITCODE -ne 0) { throw 'modern set: import check failed' }
& $check -SetDir (Join-Path $out 'stage\engines\legacy') -Win7; if ($LASTEXITCODE -ne 0) { throw 'legacy set: import check failed' }
& $check -SetDir (Join-Path $out 'stage\engines\x86') -Win7; if ($LASTEXITCODE -ne 0) { throw 'x86 set: import check failed' }

if (-not $Iscc) {
    $Iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw 'Inno Setup 6 (ISCC.exe) not found. Install it from https://jrsoftware.org/isinfo.php' }

$iss = Join-Path $PSScriptRoot 'LuminaDB.iss'
foreach ($edition in 'modern', 'win7') {
    & $Iscc "/DEdition=$edition" /Q $iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed for $edition ($LASTEXITCODE)" }
}

$files = 'LuminaDB-Setup.exe', 'LuminaDB-Setup-Win7-32bit.exe' | ForEach-Object { Join-Path $out $_ }
$lines = foreach ($f in $files) { '{0}  {1}' -f (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $f -Leaf) }
[IO.File]::WriteAllLines((Join-Path $out 'SHA256SUMS.txt'), $lines)
foreach ($f in $files) { '{0,-32} {1,7:N1} MB' -f (Split-Path $f -Leaf), ((Get-Item $f).Length / 1MB) }
exit 0
