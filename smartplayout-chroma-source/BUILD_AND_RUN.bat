@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
title SMART PLAYOUT v0.6.8.50.57 - COMPLETE CG STUDIO

set "LOG=%CD%\BUILD_AND_RUN_v0.6.8.50.57.log"
>"%LOG%" echo SMART PLAYOUT v0.6.8.50.57 BUILD AND RUN LOG
>>"%LOG%" echo Started: %DATE% %TIME%

echo ================================================
echo SMART PLAYOUT v0.6.8.50.57 - COMPLETE CG STUDIO
echo ================================================

call "RESOLVE_DOTNET_X64.bat" "%LOG%"
if errorlevel 1 (
  echo ERROR: Compatible x64 .NET SDK preflight failed.
  >>"!LOG!" echo ERROR: Compatible x64 .NET SDK preflight failed.
  goto FAIL
)

call "PREPARE_FFMPEG_RUNTIME.bat" >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL
call "VERIFY_STREAMING_RUNTIME.bat" >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL
call "VERIFY_FFMPEG_CAPABILITIES.bat" >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL

echo.
echo [DECKLINK] Building/verifying REQUIRED native Blackmagic bridge...
call "BUILD_DECKLINK_NATIVE_BRIDGE.bat" /nopause >>"%LOG%" 2>&1
if errorlevel 1 (
  echo ERROR: Native DeckLink bridge is not complete.
  echo Report: !CD!\DECKLINK_BUILD_REPORT_v0.6.8.50.31.txt
  goto FAIL
)
if not exist "native_runtime\DeckLink\SMARTPlayout.Device.BMD.x64.dll" goto FAIL
echo [DECKLINK] READY - device + physical connector + mode capability bridge built.

rem Clean stale SDK/MSBuild outputs.
if exist "SmartPlayout\obj" rmdir /s /q "SmartPlayout\obj"
if exist "SmartPlayout\bin" rmdir /s /q "SmartPlayout\bin"
for /d %%D in ("Modules\*") do (
  if exist "%%~fD\obj" rmdir /s /q "%%~fD\obj"
  if exist "%%~fD\bin" rmdir /s /q "%%~fD\bin"
)
if exist "_publish" rmdir /s /q "_publish"
if exist "PLAYOUT" rmdir /s /q "PLAYOUT"
mkdir "_publish"

set "SMARTPLAYER_SKIP_RUNTIME_PREP=1"

echo.
echo [1/4] Restoring win-x64...
>>"%LOG%" echo ===== RESTORE =====
"%DOTNET%" restore "SmartPlayout\SmartPlayout.csproj" -r win-x64 >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL

echo.
echo [2/4] Building Release win-x64 first...
>>"%LOG%" echo ===== RELEASE BUILD =====
"%DOTNET%" build "SmartPlayout\SmartPlayout.csproj" -c Release -r win-x64 --self-contained true --no-restore --no-incremental -p:UseAppHost=true -p:GenerateRuntimeConfigurationFiles=true -p:GenerateDependencyFile=true -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL

set "BUILDOUT=%CD%\SmartPlayout\bin\Release\net8.0-windows\win-x64"
set "RUNTIMECONFIG=%BUILDOUT%\SMARTPlayout.runtimeconfig.json"

rem Extra BAT-level safety in case the installed SDK still skipped the MSBuild target.
if not exist "!RUNTIMECONFIG!" (
  echo.
  echo Runtimeconfig was not produced by SDK. Creating safe self-contained runtimeconfig...
  >"!RUNTIMECONFIG!" echo {
  >>"!RUNTIMECONFIG!" echo   "runtimeOptions": {
  >>"!RUNTIMECONFIG!" echo     "tfm": "net8.0",
  >>"!RUNTIMECONFIG!" echo     "includedFrameworks": [
  >>"!RUNTIMECONFIG!" echo       { "name": "Microsoft.NETCore.App", "version": "8.0.0" },
  >>"!RUNTIMECONFIG!" echo       { "name": "Microsoft.WindowsDesktop.App", "version": "8.0.0" }
  >>"!RUNTIMECONFIG!" echo     ]
  >>"!RUNTIMECONFIG!" echo   }
  >>"!RUNTIMECONFIG!" echo }
)

if not exist "!RUNTIMECONFIG!" (
  echo.
  echo ERROR: Could not create SMARTPlayout.runtimeconfig.json
  goto FAIL
)

echo.
echo [3/4] Publishing from the already-built output...
>>"%LOG%" echo ===== SELF-CONTAINED PUBLISH =====
"%DOTNET%" publish "SmartPlayout\SmartPlayout.csproj" -c Release -r win-x64 --self-contained true --no-restore --no-build -p:UseAppHost=true -p:GenerateRuntimeConfigurationFiles=true -p:GenerateDependencyFile=true -o "%CD%\_publish" >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL

if not exist "_publish\SMARTPlayout.exe" (
  echo.
  echo ERROR: SMARTPlayout.exe was not generated.
  goto FAIL
)

if not exist "_publish\SMARTPlayout.runtimeconfig.json" (
  echo.
  echo Publish did not copy runtimeconfig. Copying verified build runtimeconfig manually...
  copy /y "!RUNTIMECONFIG!" "_publish\SMARTPlayout.runtimeconfig.json" >nul
  if errorlevel 1 goto FAIL
)

echo.
echo [4/4] Preparing PLAYOUT folder...
mkdir "PLAYOUT"
xcopy "_publish\*" "PLAYOUT\" /E /I /Y >nul
if errorlevel 1 goto FAIL

if not exist "PLAYOUT\RuntimeData\Capabilities" mkdir "PLAYOUT\RuntimeData\Capabilities"
if exist "ffmpeg_runtime\Data\Capabilities" xcopy "ffmpeg_runtime\Data\Capabilities\*.*" "PLAYOUT\RuntimeData\Capabilities\" /Y /I >nul
if not exist "PLAYOUT\Runtime\MediaCore\FFmpeg\avcodec-63.dll" goto FAIL
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
if not exist "PLAYOUT\Runtime\DeckLink\Native" mkdir "PLAYOUT\Runtime\DeckLink\Native"
copy /y "native_runtime\DeckLink\SMARTPlayout.Device.BMD.x64.dll" "PLAYOUT\Runtime\DeckLink\Native\" >nul
if exist "native_runtime\DeckLink\DeckLinkProbe.exe" copy /y "native_runtime\DeckLink\DeckLinkProbe.exe" "PLAYOUT\Runtime\DeckLink\Native\" >nul
if not exist "PLAYOUT\Runtime\DeckLink\Native\SMARTPlayout.Device.BMD.x64.dll" goto FAIL

echo Verifying isolated module workers...
call "VERIFY_MODULE_WORKERS.bat" >>"%LOG%" 2>&1
if errorlevel 1 goto FAIL

if not exist "PLAYOUT\SMARTPlayout.exe" (
  echo.
  echo ERROR: PLAYOUT\SMARTPlayout.exe missing.
  goto FAIL
)

if not exist "PLAYOUT\SMARTPlayout.runtimeconfig.json" (
  echo.
  echo ERROR: PLAYOUT\SMARTPlayout.runtimeconfig.json missing.
  goto FAIL
)

echo.
echo ================================================
echo SMART PLAYOUT READY
echo %CD%\PLAYOUT\SMARTPlayout.exe
echo Log: %LOG%
echo ================================================
>>"%LOG%" echo BUILD SUCCEEDED: %DATE% %TIME%
>>"%LOG%" echo EXE: %CD%\PLAYOUT\SMARTPlayout.exe
start "" "%CD%\PLAYOUT\SMARTPlayout.exe"
pause
exit /b 0

:FAIL
echo.
>>"%LOG%" echo BUILD FAILED: %DATE% %TIME%
echo BUILD FAILED. Please send this log:
echo %LOG%
pause
exit /b 1
