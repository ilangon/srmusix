@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "LOG=%~dp0DECKLINK_OUTPUT_AUDIT.txt"
set "FF="
if not defined FF if exist "%~dp0ffmpeg_runtime\MediaCore\win-x64\ffmpeg.exe" set "FF=%~dp0ffmpeg_runtime\MediaCore\win-x64\ffmpeg.exe"
if defined SMARTPLAYOUT_DECKLINK_FFMPEG if exist "%SMARTPLAYOUT_DECKLINK_FFMPEG%" set "FF=%SMARTPLAYOUT_DECKLINK_FFMPEG%"
>"%LOG%" echo SMART PLAYOUT DeckLink / SDI runtime audit
>>"%LOG%" echo Date: %date% %time%
if not defined FF (
  >>"%LOG%" echo ERROR: No candidate DeckLink FFmpeg found.
  echo No candidate DeckLink FFmpeg found. See %LOG%
  pause
  exit /b 1
)
>>"%LOG%" echo FFmpeg: %FF%
>>"%LOG%" echo.
>>"%LOG%" echo ==== DEVICES ====
"%FF%" -hide_banner -devices >>"%LOG%" 2>&1
>>"%LOG%" echo.
>>"%LOG%" echo ==== DECKLINK SINKS ====
"%FF%" -hide_banner -sinks decklink >>"%LOG%" 2>&1
>>"%LOG%" echo.
>>"%LOG%" echo ==== LEGACY DEVICE ENUMERATION ====
"%FF%" -hide_banner -f decklink -list_devices 1 -i dummy >>"%LOG%" 2>&1
echo Audit saved: %LOG%
pause
