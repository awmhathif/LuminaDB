@echo off
rem Double-click to start the dev build of LuminaDB (data in .devroot). "run-dev.cmd build" rebuilds first.
if /i "%~1"=="build" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-dev.ps1" -Build
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-dev.ps1"
)
if errorlevel 1 pause
