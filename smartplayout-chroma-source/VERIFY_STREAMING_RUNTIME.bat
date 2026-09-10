@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "FF=ffmpeg_runtime\MediaCore\win-x64\ffmpeg.exe"
set "AUDIT=STREAM_RUNTIME_AUDIT.txt"
>"%AUDIT%" echo SMART PLAYOUT STREAMING RUNTIME AUDIT v0.6.8.50.30
>>"%AUDIT%" echo ============================================
if not exist "%FF%" (
  >>"%AUDIT%" echo ERROR: shared MediaCore ffmpeg.exe missing
  exit /b 1
)
"%FF%" -hide_banner -version >>"%AUDIT%" 2>&1
if errorlevel 1 exit /b 1
>>"%AUDIT%" echo.
>>"%AUDIT%" echo PROTOCOLS
"%FF%" -hide_banner -protocols >>"%AUDIT%" 2>&1
>>"%AUDIT%" echo.
>>"%AUDIT%" echo ENCODERS
"%FF%" -hide_banner -encoders >>"%AUDIT%" 2>&1
>>"%AUDIT%" echo.
>>"%AUDIT%" echo DEVICES
"%FF%" -hide_banner -devices >>"%AUDIT%" 2>&1
>>"%AUDIT%" echo.
>>"%AUDIT%" echo GPU ADAPTERS
powershell.exe -NoProfile -NonInteractive -Command "Get-CimInstance Win32_VideoController ^| Select-Object -ExpandProperty Name" >>"%AUDIT%" 2>&1
>>"%AUDIT%" echo.
>>"%AUDIT%" echo NOTE: encoder listing is capability discovery only. VERIFY_FFMPEG_CAPABILITIES.bat performs actual short encoder self-tests.
echo Streaming runtime audit saved: %AUDIT%
exit /b 0
