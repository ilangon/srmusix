@echo off
setlocal
cd /d "%~dp0"
echo SR MUSIX HD Playout - Windows EXE Builder
where node >nul 2>nul || (echo ERROR: Install Node.js LTS first. & pause & exit /b 1)
call npm install || (echo ERROR: npm install failed. & pause & exit /b 1)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0PREPARE_FULL_FFMPEG_WINDOWS.ps1" || (echo ERROR: Full FFmpeg bundle preparation failed. & pause & exit /b 1)
call npm run check || (echo ERROR: Source validation failed. & pause & exit /b 1)
call npm run dist:win || (echo ERROR: Windows EXE build failed. & pause & exit /b 1)
echo.
echo BUILD COMPLETE. Open the dist folder.
start "" "%~dp0dist"
pause
