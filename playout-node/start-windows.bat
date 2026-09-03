@echo off
title SR MUSIX HD Web Playout
cd /d "%~dp0"
where node >nul 2>nul || (echo Node.js is not installed.& pause & exit /b 1)
where ffmpeg >nul 2>nul || (echo FFmpeg is not in PATH. Set FFMPEG_PATH before starting.& pause & exit /b 1)
if not exist node_modules call npm install
start "" http://localhost:3000
node server.js
pause
