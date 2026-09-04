@echo off
title Install FFmpeg for SR MUSIX HD Playout
echo Installing FFmpeg through Windows Package Manager...
where winget >nul 2>nul || (echo Winget is unavailable. Install App Installer from Microsoft Store.& pause & exit /b 1)
winget install --id Gyan.FFmpeg --exact --accept-package-agreements --accept-source-agreements
echo.
echo FFmpeg installation finished. Restart SR MUSIX HD Playout.
pause
