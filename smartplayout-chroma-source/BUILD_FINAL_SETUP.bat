@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
title SMART PLAYOUT v0.6.8.50.48 - FINAL BUILD + SETUP

set "LOG=%CD%\BUILD_FINAL_v0.6.8.50.48.log"
>"%LOG%" echo SMART PLAYOUT v0.6.8.50.48 FINAL BUILD LOG
>>"%LOG%" echo Started: %DATE% %TIME%

echo ============================================================
echo SMART PLAYOUT v0.6.8.50.48 - FINAL BUILD + SETUP
echo ============================================================

call "RESOLVE_DOTNET_X64.bat" "%LOG%"
if errorlevel 1 (
  echo ERROR: Compatible x64 .NET SDK preflight failed.
  >>"%LOG%" echo ERROR: Compatible x64 .NET SDK preflight failed.
  goto FAIL
)

call "PREPARE_FFMPEG_RUNTIME.bat" >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL
call "VERIFY_STREAMING_RUNTIME.bat" >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL
call "VERIFY_FFMPEG_CAPABILITIES.bat" >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL

echo [NATIVE] Attempting Blackmagic DeckLink SDK bridge build if SDK is available...
>>"%LOG%" echo ===== NATIVE DECKLINK SDK BRIDGE AUTO-BUILD =====
call "BUILD_DECKLINK_NATIVE_BRIDGE.bat" /nopause >>"%LOG%" 2>&1
if exist "DECKLINK_BUILD_REPORT_v0.6.8.50.31.txt" >>"%LOG%" echo DeckLink report: %CD%\DECKLINK_BUILD_REPORT_v0.6.8.50.31.txt
if errorlevel 1 (
  echo ERROR: Native BMD/DeckLink bridge build failed.
  echo This build is intentionally stopped so SDI is never shipped as NOT READY.
  >>"%LOG%" echo ERROR: REQUIRED Native BMD bridge unavailable.
  goto FAIL
)
if not exist "native_runtime\DeckLink\SMARTPlayout.Device.BMD.x64.dll" (
  >>"%LOG%" echo ERROR: Required SMARTPlayout.Device.BMD.x64.dll missing after bridge build.
  goto FAIL
)
echo Native BMD/DeckLink bridge READY.
>>"%LOG%" echo DECKLINK NATIVE OUTPUT: READY
>>"%LOG%" echo PIPELINE: CARD - PHYSICAL OUTPUT - SUPPORTED MODE - 8BIT YUV - EMBEDDED PCM

if exist "SmartPlayout\obj" rmdir /s /q "SmartPlayout\obj"
if exist "SmartPlayout\bin" rmdir /s /q "SmartPlayout\bin"
for /d /r "Modules" %%D in (obj) do @if exist "%%D" rmdir /s /q "%%D" 2>nul
for /d /r "Modules" %%D in (bin) do @if exist "%%D" rmdir /s /q "%%D" 2>nul
if exist "_publish" rmdir /s /q "_publish"
if exist "PLAYOUT" rmdir /s /q "PLAYOUT"
if exist "DIST" rmdir /s /q "DIST"
mkdir "_publish"
mkdir "DIST"
set "SMARTPLAYER_SKIP_RUNTIME_PREP=1"

echo [1/5] RESTORE...
"%DOTNET%" restore "SmartPlayout\SmartPlayout.csproj" -r win-x64 >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL

echo [2/5] RELEASE BUILD...
"%DOTNET%" build "SmartPlayout\SmartPlayout.csproj" -c Release -r win-x64 --self-contained true --no-restore -p:UseAppHost=true -p:GenerateRuntimeConfigurationFiles=true -p:GenerateDependencyFile=true -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL

echo [3/5] SELF-CONTAINED PUBLISH...
"%DOTNET%" publish "SmartPlayout\SmartPlayout.csproj" -c Release -r win-x64 --self-contained true --no-restore --no-build -p:UseAppHost=true -p:GenerateRuntimeConfigurationFiles=true -p:GenerateDependencyFile=true -o "%CD%\_publish" >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL

if not exist "_publish\SMARTPlayout.exe" (
  >>"%LOG%" echo ERROR: SMARTPlayout.exe missing after publish.
  goto FAIL
)

mkdir "PLAYOUT"
xcopy "_publish\*" "PLAYOUT\" /E /I /Y >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL
if not exist "PLAYOUT\RuntimeData\Capabilities" mkdir "PLAYOUT\RuntimeData\Capabilities"
xcopy "ffmpeg_runtime\Data\Capabilities\*.*" "PLAYOUT\RuntimeData\Capabilities\" /Y /I >>"%LOG%" 2>&1
if exist "ffmpeg_runtime\Data\MEDIACORE_FFMPEG_VERSION.txt" copy /y "ffmpeg_runtime\Data\MEDIACORE_FFMPEG_VERSION.txt" "PLAYOUT\RuntimeData\" >>"%LOG%" 2>&1
if exist "native_runtime\DeckLink\DeckLinkProbe.exe" copy /y "native_runtime\DeckLink\DeckLinkProbe.exe" "PLAYOUT\Runtime\DeckLink\Native\" >>"%LOG%" 2>&1
if exist "native_runtime\DeckLink\SMARTPlayout.Device.BMD.x64.dll" copy /y "native_runtime\DeckLink\SMARTPlayout.Device.BMD.x64.dll" "PLAYOUT\Runtime\DeckLink\Native\" >>"%LOG%" 2>&1
if exist "DECKLINK_NATIVE_SDK_PROBE.txt" copy /y "DECKLINK_NATIVE_SDK_PROBE.txt" "PLAYOUT\RuntimeData\" >>"%LOG%" 2>&1
if exist "ffmpeg_runtime\Data\MEDIACORE_FFMPEG_BUILDCONF.txt" copy /y "ffmpeg_runtime\Data\MEDIACORE_FFMPEG_BUILDCONF.txt" "PLAYOUT\RuntimeData\" >>"%LOG%" 2>&1
if exist "ffmpeg_runtime\Data\APPROVED_RUNTIME_FILES.txt" copy /y "ffmpeg_runtime\Data\APPROVED_RUNTIME_FILES.txt" "PLAYOUT\RuntimeData\" >>"%LOG%" 2>&1
if exist "ffmpeg_runtime\Data\DIRECTSHOW_RUNTIME_STATUS.txt" copy /y "ffmpeg_runtime\Data\DIRECTSHOW_RUNTIME_STATUS.txt" "PLAYOUT\RuntimeData\" >>"%LOG%" 2>&1
if exist "ffmpeg_runtime\Data\DECKLINK_RUNTIME_STATUS.txt" copy /y "ffmpeg_runtime\Data\DECKLINK_RUNTIME_STATUS.txt" "PLAYOUT\RuntimeData\" >>"%LOG%" 2>&1
copy /y "SmartPlayout\SRMediaPlayer.ico" "PLAYOUT\SRMediaPlayer.ico" >>"%LOG%" 2>&1

>"PLAYOUT\RUN_SMART_PLAYOUT.bat" echo @echo off
>>"PLAYOUT\RUN_SMART_PLAYOUT.bat" echo cd /d "%%~dp0"
>>"PLAYOUT\RUN_SMART_PLAYOUT.bat" echo start "" "SMARTPlayout.exe"

if not exist "PLAYOUT\SMARTPlayout.exe" goto FAIL
if not exist "PLAYOUT\Runtime\MediaCore\FFmpeg\avcodec-63.dll" goto FAIL
if not exist "PLAYOUT\Runtime\MediaCore\FFmpeg\avdevice-63.dll" goto FAIL
if not exist "PLAYOUT\Runtime\MediaCore\FFmpeg\ffmpeg.exe" goto FAIL
if not exist "PLAYOUT\Runtime\MediaCore\FFmpeg\ffprobe.exe" goto FAIL
if not exist "PLAYOUT\SMARTPlayout.Engine.Worker.exe" goto FAIL
if not exist "PLAYOUT\SMARTPlayout.CG.Worker.exe" goto FAIL
if not exist "PLAYOUT\SMARTPlayout.Chroma.Worker.exe" goto FAIL
if not exist "PLAYOUT\SMARTPlayout.Output.Worker.exe" goto FAIL
if exist "PLAYOUT\Runtime\Decoder" goto FAIL
if exist "PLAYOUT\Runtime\Encoder" goto FAIL
if exist "PLAYOUT\Runtime\DirectShow" goto FAIL
if exist "PLAYOUT\Runtime\DeckLink\FFmpeg" goto FAIL
if not exist "PLAYOUT\Runtime\DeckLink\Native\SMARTPlayout.Device.BMD.x64.dll" goto FAIL

echo [VERIFY] MODULE WORKER EXE / IPC / HEARTBEAT...
call "VERIFY_MODULE_WORKERS.bat" >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL

echo [4/5] PORTABLE PACKAGE...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%CD%\PLAYOUT\*' -DestinationPath '%CD%\DIST\SMART_PLAYOUT_v0_6_8_50_48_PORTABLE.zip' -Force" >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL

echo [5/5] WINDOWS INSTALLER...
set "ISCC="
if exist "%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if defined ISCC (
  "%ISCC%" /DMySourceDir="%CD%\PLAYOUT" /DMyOutputDir="%CD%\DIST" "SMART_PLAYOUT_SETUP.iss" >>"%LOG%" 2>&1
  if errorlevel 1 goto FAIL
) else (
  echo Inno Setup 6 not found - portable ZIP is ready; installer skipped.
  >>"%LOG%" echo Inno Setup 6 not found - portable ZIP created, installer skipped.
)

call "BUILD_SOURCE_PACK.bat" >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL
echo.
echo ============================================================
echo FINAL BUILD READY
echo Portable: DIST\SMART_PLAYOUT_v0_6_8_50_48_PORTABLE.zip
if defined ISCC echo Setup:    DIST\SMART_PLAYOUT_v0_6_8_50_48_SETUP.exe
echo Source:   DIST\SMART_PLAYOUT_v0_6_8_50_48_SOURCE_PACK.zip
echo Log:      BUILD_FINAL_v0.6.8.50.48.log
echo DeckLink: DECKLINK_BUILD_REPORT_v0.6.8.50.31.txt
echo ============================================================
pause
exit /b 0

:FAIL
echo.
echo BUILD FAILED - see %LOG%
echo Do not delete the log; send it for correction.
pause
exit /b 1
