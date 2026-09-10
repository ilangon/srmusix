@echo off
setlocal EnableExtensions EnableDelayedExpansion
title SMART PLAYOUT v0.6.8.50.30 - Native DeckLink SDK Probe
cd /d "%~dp0"
set "OUT=%CD%\DECKLINK_NATIVE_SDK_PROBE.txt"
>"%OUT%" echo SMART PLAYOUT v0.6.8.50.30 - NATIVE DECKLINK SDK PROBE
>>"%OUT%" echo DATE: %DATE% %TIME%
>>"%OUT%" echo.

set "IDL="
if defined DECKLINK_SDK_DIR (
  for /r "%DECKLINK_SDK_DIR%" %%F in (DeckLinkAPI.idl) do if not defined IDL set "IDL=%%~fF"
)
if not defined IDL for %%R in ("%ProgramFiles%" "%ProgramFiles(x86)%" "%USERPROFILE%\Downloads" "%USERPROFILE%\Desktop") do (
  if exist "%%~R" for /f "delims=" %%F in ('dir /b /s "%%~R\DeckLinkAPI.idl" 2^>nul') do if not defined IDL set "IDL=%%F"
)
if not defined IDL (
  echo DeckLinkAPI.idl NOT FOUND.
  >>"%OUT%" echo RESULT: SDK IDL NOT FOUND
  >>"%OUT%" echo Install/extract the official Blackmagic DeckLink SDK, or set DECKLINK_SDK_DIR to its root.
  echo Report: %OUT%
  pause
  exit /b 2
)

echo SDK IDL: %IDL%
>>"%OUT%" echo SDK IDL: %IDL%
for %%I in ("%IDL%") do set "SDKINC=%%~dpI"
if "!SDKINC:~-1!"=="\" set "SDKINC=!SDKINC:~0,-1!"
>>"%OUT%" echo SDK INCLUDE: !SDKINC!

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
  echo Visual Studio vswhere.exe not found.
  >>"%OUT%" echo RESULT: VISUAL STUDIO BUILD TOOLS NOT FOUND
  pause
  exit /b 3
)
for /f "usebackq tokens=*" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%I"
if not defined VS (
  echo MSVC C++ build tools not installed.
  >>"%OUT%" echo RESULT: MSVC C++ BUILD TOOLS NOT FOUND
  pause
  exit /b 4
)
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul

set "GEN=%CD%\native_runtime\DeckLink\Generated"
set "BIN=%CD%\native_runtime\DeckLink"
if exist "%GEN%" rmdir /s /q "%GEN%"
mkdir "%GEN%" >nul 2>&1
mkdir "%BIN%" >nul 2>&1

where midl >>"%OUT%" 2>&1 || (echo MIDL not found & exit /b 5)
where cl >>"%OUT%" 2>&1 || (echo MSVC cl not found & exit /b 6)

pushd "%GEN%"
midl /nologo /env x64 /I "!SDKINC!" /h DeckLinkAPI_h.h /iid DeckLinkAPI_i.c /tlb DeckLinkAPI.tlb "%IDL%" >>"%OUT%" 2>&1
if errorlevel 1 (
  popd
  echo MIDL generation failed. See %OUT%
  exit /b 7
)
cl /nologo /EHsc /std:c++17 /I"%GEN%" "%CD%\..\..\..\NativeDeckLinkBridge\DeckLinkProbe.cpp" DeckLinkAPI_i.c /Fe:"%BIN%\DeckLinkProbe.exe" ole32.lib oleaut32.lib >>"%OUT%" 2>&1
if errorlevel 1 (
  popd
  echo Native probe compile failed. See %OUT%
  exit /b 8
)
popd

echo.>>"%OUT%"
echo ===== NATIVE DECKLINK ENUMERATION =====>>"%OUT%"
"%BIN%\DeckLinkProbe.exe" >>"%OUT%" 2>&1
set "RC=%ERRORLEVEL%"
echo RESULT CODE: %RC%>>"%OUT%"
echo.
echo Native DeckLink SDK probe finished. Report: %OUT%
if "%RC%"=="0" echo SUCCESS: DeckLink SDK COM can enumerate at least one device.
if not "%RC%"=="0" echo CHECK REPORT: native SDK/driver/device enumeration did not complete successfully.
pause
exit /b %RC%
