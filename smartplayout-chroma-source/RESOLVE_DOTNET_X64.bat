@echo off
rem Shared .NET SDK preflight. Called by BUILD_AND_RUN and BUILD_FINAL_SETUP.
rem Intentionally does not download/install anything; it selects a real x64 SDK
rem already installed on Windows and writes enough evidence into the build log.

set "SDK_LOG=%~1"
set "DOTNET="
set "DOTNET_CANDIDATE="

if defined ProgramW6432 if exist "%ProgramW6432%\dotnet\dotnet.exe" set "DOTNET_CANDIDATE=%ProgramW6432%\dotnet\dotnet.exe"
if not defined DOTNET_CANDIDATE if exist "%ProgramFiles%\dotnet\dotnet.exe" set "DOTNET_CANDIDATE=%ProgramFiles%\dotnet\dotnet.exe"
if not defined DOTNET_CANDIDATE for /f "delims=" %%D in ('where dotnet.exe 2^>nul') do if not defined DOTNET_CANDIDATE set "DOTNET_CANDIDATE=%%~fD"

if not defined DOTNET_CANDIDATE goto SDK_NOT_FOUND

if defined SDK_LOG (
  >>"%SDK_LOG%" echo ===== DOTNET X64 SDK PREFLIGHT =====
  >>"%SDK_LOG%" echo Windows architecture: PROCESSOR_ARCHITECTURE=%PROCESSOR_ARCHITECTURE% PROCESSOR_ARCHITEW6432=%PROCESSOR_ARCHITEW6432%
  >>"%SDK_LOG%" echo Candidate host: %DOTNET_CANDIDATE%
  "%DOTNET_CANDIDATE%" --info >>"%SDK_LOG%" 2>&1
  >>"%SDK_LOG%" echo Installed SDKs:
  "%DOTNET_CANDIDATE%" --list-sdks >>"%SDK_LOG%" 2>&1
  >>"%SDK_LOG%" echo Installed runtimes:
  "%DOTNET_CANDIDATE%" --list-runtimes >>"%SDK_LOG%" 2>&1
)

set "SDK_LIST_FILE=%TEMP%\smart_playout_sdks_%RANDOM%_%RANDOM%.txt"
"%DOTNET_CANDIDATE%" --list-sdks >"%SDK_LIST_FILE%" 2>nul
for %%S in ("%SDK_LIST_FILE%") do if %%~zS EQU 0 goto SDK_ONLY_RUNTIME
findstr /R /C:"^8\." /C:"^9\." /C:"^10\." "%SDK_LIST_FILE%" >nul
if errorlevel 1 goto SDK_WRONG_VERSION
del /q "%SDK_LIST_FILE%" >nul 2>&1

set "DOTNET=%DOTNET_CANDIDATE%"
if defined SDK_LOG >>"%SDK_LOG%" echo DOTNET SDK PREFLIGHT: PASS - %DOTNET%
exit /b 0

:SDK_ONLY_RUNTIME
del /q "%SDK_LIST_FILE%" >nul 2>&1
echo ERROR SP-BUILD-DOTNET-SDK-MISSING: dotnet host/runtime exists, but no x64 SDK is installed.
if defined SDK_LOG >>"%SDK_LOG%" echo ERROR SP-BUILD-DOTNET-SDK-MISSING: dotnet host/runtime exists, but no x64 SDK is installed.
exit /b 21

:SDK_WRONG_VERSION
del /q "%SDK_LIST_FILE%" >nul 2>&1
echo ERROR SP-BUILD-DOTNET-SDK-VERSION: Install an x64 .NET 8, 9, or 10 SDK capable of targeting net8.0-windows.
if defined SDK_LOG >>"%SDK_LOG%" echo ERROR SP-BUILD-DOTNET-SDK-VERSION: No compatible x64 SDK was found.
exit /b 22

:SDK_NOT_FOUND
echo ERROR SP-BUILD-DOTNET-HOST-MISSING: x64 dotnet.exe was not found after the Windows update.
if defined SDK_LOG >>"%SDK_LOG%" echo ERROR SP-BUILD-DOTNET-HOST-MISSING: x64 dotnet.exe was not found.
exit /b 20
