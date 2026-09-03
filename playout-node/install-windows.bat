@echo off
setlocal
title SR MUSIX HD Playout Setup
cd /d "%~dp0"
echo.
echo ========================================
echo   SR MUSIX HD WEB PLAYOUT - SETUP
echo ========================================
echo.

where winget >nul 2>nul || (
  echo Windows App Installer / winget is required.
  echo Please update "App Installer" from Microsoft Store and run this again.
  pause
  exit /b 1
)

where node >nul 2>nul
if errorlevel 1 (
  echo Installing Node.js LTS...
  winget install OpenJS.NodeJS.LTS --accept-package-agreements --accept-source-agreements
  if errorlevel 1 goto :failed
)

where ffmpeg >nul 2>nul
if errorlevel 1 (
  echo Installing FFmpeg...
  winget install Gyan.FFmpeg --accept-package-agreements --accept-source-agreements
  if errorlevel 1 goto :failed
)

echo Installing playout components...
call npm install --omit=dev
if errorlevel 1 goto :failed

set "PLAYOUT_DIR=%CD%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$w=New-Object -ComObject WScript.Shell; $s=$w.CreateShortcut([Environment]::GetFolderPath('Desktop')+'\SR MUSIX HD Playout.lnk'); $s.TargetPath='%PLAYOUT_DIR%\start-windows.bat'; $s.WorkingDirectory='%PLAYOUT_DIR%'; $s.Description='SR MUSIX HD Universal Web Playout'; $s.Save()"
if errorlevel 1 goto :failed

echo.
echo SETUP COMPLETE.
echo A shortcut named "SR MUSIX HD Playout" is now on the Desktop.
echo Double-click it to start the playout.
pause
exit /b 0

:failed
echo.
echo Setup did not complete. Check the message above.
pause
exit /b 1
