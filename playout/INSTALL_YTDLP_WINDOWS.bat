@echo off
title Install YouTube Network Playback Support
echo Installing yt-dlp for SR MUSIX HD Playout...
winget install --id yt-dlp.yt-dlp --exact --accept-package-agreements --accept-source-agreements
echo.
echo Installation finished. Restart SR MUSIX HD Playout.
pause

