@echo off
setlocal
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%CD%\BUILD_SOURCE_PACK.ps1" -Version "0.6.8.50.57"
if errorlevel 1 (
  echo SOURCE PACKAGE FAILED
  exit /b 1
)
echo SOURCE PACKAGE READY IN DIST
exit /b 0
