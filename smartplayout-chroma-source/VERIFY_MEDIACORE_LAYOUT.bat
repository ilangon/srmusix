@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "ROOT=%CD%\PLAYOUT\Runtime"
set "CORE=%ROOT%\MediaCore\FFmpeg"
set "LOG=%CD%\MEDIACORE_LAYOUT_AUDIT.txt"
>"%LOG%" echo SMART PLAYOUT SHARED MEDIACORE LAYOUT AUDIT
>>"%LOG%" echo Generated: %date% %time%

set "FAILED=0"
for %%F in (avcodec-63.dll avformat-63.dll avutil-61.dll avfilter-12.dll avdevice-63.dll swscale-10.dll swresample-7.dll ffmpeg.exe ffprobe.exe) do (
  if exist "%CORE%\%%F" (>>"%LOG%" echo [OK] %%F) else (>>"%LOG%" echo [MISSING] %%F& set "FAILED=1")
)
for %%D in (Decoder Encoder DirectShow) do if exist "%ROOT%\%%D" (>>"%LOG%" echo [DUPLICATE] Runtime\%%D& set "FAILED=1")
if exist "%ROOT%\DeckLink\FFmpeg" (>>"%LOG%" echo [DUPLICATE] Runtime\DeckLink\FFmpeg& set "FAILED=1")
if exist "%ROOT%\DeckLink\Native\SMARTPlayout.Device.BMD.x64.dll" >>"%LOG%" echo [OK] isolated DeckLink native bridge

if "%FAILED%"=="1" (
  echo MediaCore layout FAILED. See %LOG%
  exit /b 1
)
echo MediaCore layout verified. See %LOG%
exit /b 0
