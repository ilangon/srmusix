@echo off
setlocal EnableExtensions
cd /d "%~dp0"
powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0VERIFY_MODULE_WORKERS.ps1" -Root "%~dp0PLAYOUT"
exit /b %errorlevel%
