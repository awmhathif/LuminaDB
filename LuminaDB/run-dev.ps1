# Starts the dev build of LuminaDB with the developer setup:
#   data in .devroot\ (git ignores it), MariaDB from ..\EngineSpike\engines, PHP/Apache/Caddy from ..\ParityTests\web\bundle.
# Use -Build to rebuild first (close LuminaDB before building: Windows locks a running .exe).
param([switch]$Build)

$here = $PSScriptRoot
$exe = Join-Path $here 'src\LuminaDB.App\bin\Debug\net48\LuminaDB.exe'

if ($Build) {
    if (Get-Process LuminaDB -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe }) {
        Write-Host 'LuminaDB is open. Close it first, then run this again.' -ForegroundColor Red
        exit 1
    }
    dotnet build (Join-Path $here 'LuminaDB.sln') -nologo -v q
    if ($LASTEXITCODE -ne 0) { Write-Host 'Build failed (see above).' -ForegroundColor Red; exit 1 }
}

if (-not (Test-Path $exe)) { Write-Host "Not built yet. Run: .\run-dev.ps1 -Build" -ForegroundColor Red; exit 1 }

$env:LUMINA_ROOT       = Join-Path $here '.devroot'
$env:LUMINA_ENGINE_DIR = [IO.Path]::GetFullPath((Join-Path $here '..\EngineSpike\engines\mariadb-11.8.10-winx64'))
$env:LUMINA_WEB_DIR    = [IO.Path]::GetFullPath((Join-Path $here '..\ParityTests\web\bundle'))
Start-Process $exe
