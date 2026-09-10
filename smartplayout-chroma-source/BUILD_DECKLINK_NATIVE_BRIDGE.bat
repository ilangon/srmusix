@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
set "PROJECT_ROOT=%CD%"
set "BRIDGE_SRC=%PROJECT_ROOT%\NativeDeckLinkBridge"
title SMART PLAYOUT v0.6.8.50.17 - Native BMD Device Bridge
set "LOG=%CD%\BUILD_DECKLINK_NATIVE_BRIDGE_LOG.txt"
set "REPORT=%CD%\DECKLINK_BUILD_REPORT_v0.6.8.50.31.txt"
>"%LOG%" echo SMART PLAYOUT v0.6.8.50.31 - NATIVE BMD DEVICE BRIDGE
>>"%LOG%" echo DATE: %DATE% %TIME%
set "NOPAUSE=0"
if /I "%~1"=="/nopause" set "NOPAUSE=1"

set "IDL="
if defined DECKLINK_SDK_DIR if exist "%DECKLINK_SDK_DIR%" for /r "%DECKLINK_SDK_DIR%" %%F in (DeckLinkAPI.idl) do if not defined IDL set "IDL=%%~fF"
if not defined IDL for %%R in ("!PROJECT_ROOT!\Blackmagic DeckLink SDK 16.0" "!USERPROFILE!\Downloads" "!USERPROFILE!\Desktop") do (
 if exist "%%~R" for /f "delims=" %%F in ('dir /b /s "%%~R\DeckLinkAPI.idl" 2^>nul') do if not defined IDL set "IDL=%%F"
)

rem Convenience: if the user kept the official SDK as a ZIP, extract it locally for BUILD use only.
if not defined IDL (
 set "SDKZIP="
 for %%R in ("!PROJECT_ROOT!" "!USERPROFILE!\Downloads" "!USERPROFILE!\Desktop") do if exist "%%~R" (
  for /f "delims=" %%Z in ('dir /b /s "%%~R\*DeckLink*SDK*.zip" 2^>nul') do if not defined SDKZIP set "SDKZIP=%%Z"
 )
 if defined SDKZIP (
  echo [SDK] Found official SDK ZIP: !SDKZIP!
  >>"!LOG!" echo SDK ZIP: !SDKZIP!
  if exist "!PROJECT_ROOT!\_decklink_sdk_build" rmdir /s /q "!PROJECT_ROOT!\_decklink_sdk_build"
  rem Extract only the official Win/include SDK files. This avoids Expand-Archive failures on the SDK's very deep sample paths.
  set "SDKEXTRACT=!PROJECT_ROOT!\_decklink_sdk_build\Win\include"
  mkdir "!SDKEXTRACT!" >nul 2>&1
  powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; Add-Type -AssemblyName System.IO.Compression.FileSystem; $z=[IO.Compression.ZipFile]::OpenRead($env:SDKZIP); try { $entries=$z.Entries ^| Where-Object { $_.FullName -match '/Win/include/[^/]+$' }; foreach($e in $entries){ $dst=Join-Path $env:SDKEXTRACT $e.Name; [IO.Compression.ZipFileExtensions]::ExtractToFile($e,$dst,$true) }; if(-not (Test-Path (Join-Path $env:SDKEXTRACT 'DeckLinkAPI.idl'))){ throw 'DeckLinkAPI.idl was not extracted from Win/include' } } finally { $z.Dispose() }" >>"!LOG!" 2>&1
  if errorlevel 1 (
   echo ERROR: SDK ZIP include extraction failed. See !LOG!
   >>"!LOG!" echo RESULT: SDK ZIP EXTRACTION FAILED
  )
  if exist "!SDKEXTRACT!\DeckLinkAPI.idl" set "IDL=!SDKEXTRACT!\DeckLinkAPI.idl"
 )
)
if not defined IDL (
 echo ERROR: Official Blackmagic DeckLink SDK DeckLinkAPI.idl was not found.
 echo Extract SDK 16.0, place its ZIP beside this BAT, or set DECKLINK_SDK_DIR.
 >>"!LOG!" echo RESULT: SDK IDL NOT FOUND
 goto FAIL2
)
for %%I in ("%IDL%") do set "SDKINC=%%~dpI"
rem MIDL/C preprocessor can misparse a quoted /I path that ends in a backslash. Remove it.
if "!SDKINC:~-1!"=="\" set "SDKINC=!SDKINC:~0,-1!"
>>"%LOG%" echo SDK INCLUDE: !SDKINC!
>>"%LOG%" echo SDK IDL    : %IDL%
if not exist "!SDKINC!\DeckLinkAPIVersion.h" (echo ERROR: DeckLinkAPIVersion.h missing.&>>"!LOG!" echo RESULT: INCOMPLETE SDK&goto FAIL3)
findstr /c:"BLACKMAGIC_DECKLINK_API_VERSION_STRING" "%SDKINC%\DeckLinkAPIVersion.h" >>"%LOG%" 2>&1

set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "!VSWHERE!" (echo ERROR: Visual Studio Build Tools not found.&goto FAIL4)
for /f "usebackq tokens=*" %%I in (`"%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%I"
if not defined VS (echo ERROR: MSVC C++ x64 tools are not installed.&goto FAIL5)
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
where midl >>"!LOG!" 2>&1 || (echo ERROR: MIDL not found.&goto FAIL6)
where cl >>"!LOG!" 2>&1 || (echo ERROR: cl.exe not found.&goto FAIL7)

set "GEN=%CD%\native_runtime\DeckLink\Generated"
set "BIN=%CD%\native_runtime\DeckLink"
if exist "%GEN%" rmdir /s /q "%GEN%"
mkdir "%GEN%" >nul 2>&1
mkdir "%BIN%" >nul 2>&1
if not exist "!BRIDGE_SRC!\SMARTPlayout.DeckLinkNative.cpp" (
 echo ERROR: Native bridge source missing: !BRIDGE_SRC!\SMARTPlayout.DeckLinkNative.cpp
 >>"!LOG!" echo RESULT: BRIDGE SOURCE MISSING - !BRIDGE_SRC!\SMARTPlayout.DeckLinkNative.cpp
 goto FAIL9
)
if not exist "!BRIDGE_SRC!\DeckLinkProbe.cpp" (
 echo ERROR: Native probe source missing: !BRIDGE_SRC!\DeckLinkProbe.cpp
 >>"!LOG!" echo RESULT: PROBE SOURCE MISSING - !BRIDGE_SRC!\DeckLinkProbe.cpp
 goto FAIL10
)
>>"%LOG%" echo PROJECT ROOT: %PROJECT_ROOT%
>>"%LOG%" echo BRIDGE SRC : %BRIDGE_SRC%
pushd "%GEN%"
midl /nologo /env x64 /I "%SDKINC%" /h DeckLinkAPI_h.h /iid DeckLinkAPI_i.c /tlb DeckLinkAPI.tlb "%IDL%" >>"%LOG%" 2>&1
if errorlevel 1 (popd&echo ERROR: MIDL failed. See !LOG!&goto FAIL8)
cl /nologo /LD /EHsc /std:c++17 /DUNICODE /D_UNICODE /DSMARTPLAYOUT_DECKLINK_NATIVE_EXPORTS ^
 /I"%GEN%" /I"%SDKINC%" /I"%BRIDGE_SRC%" ^
 "%BRIDGE_SRC%\SMARTPlayout.DeckLinkNative.cpp" DeckLinkAPI_i.c ^
 /link /OUT:"%BIN%\SMARTPlayout.Device.BMD.x64.dll" ole32.lib oleaut32.lib >>"%LOG%" 2>&1
if errorlevel 1 (popd&echo ERROR: Native BMD bridge compile failed. See !LOG!&goto FAIL9)
cl /nologo /EHsc /std:c++17 /DUNICODE /D_UNICODE /I"%GEN%" /I"%SDKINC%" /I"%BRIDGE_SRC%" ^
 "%BRIDGE_SRC%\DeckLinkProbe.cpp" DeckLinkAPI_i.c ^
 /Fe:"%BIN%\DeckLinkProbe.exe" ole32.lib oleaut32.lib >>"%LOG%" 2>&1
if errorlevel 1 (popd&echo ERROR: Native probe compile failed. See !LOG!&goto FAIL10)
popd
if not exist "!BIN!\SMARTPlayout.Device.BMD.x64.dll" (echo ERROR: BMD DLL missing after compiler success.&goto FAIL11)
>>"%LOG%" echo ===== NATIVE DEVICE PROBE =====
"%BIN%\DeckLinkProbe.exe" >>"%LOG%" 2>&1
set "PROBERC=%ERRORLEVEL%"
echo SUCCESS: Native BMD DLL CREATED.
echo DLL: %BIN%\SMARTPlayout.Device.BMD.x64.dll
if "%PROBERC%"=="0" (echo SUCCESS: DeckLink SDK device enumeration passed.) else (echo WARNING: DLL built, but no output device enumerated. Check Desktop Video/card state.)
>>"%LOG%" echo CONNECTOR ENUMERATION: SDK BMDDeckLinkVideoOutputConnections
>>"%LOG%" echo MODE FILTER: IDeckLinkOutput::DoesSupportVideoMode(connection, mode, bmdFormat8BitYUV, ...)
>>"%LOG%" echo RESULT: BMD DLL BUILD PASS
copy /y "%LOG%" "%REPORT%" >nul 2>&1
echo REPORT: %REPORT%
if "%NOPAUSE%"=="0" pause
exit /b 0
:FAIL2
set RC=2&goto FAIL
:FAIL3
set RC=3&goto FAIL
:FAIL4
set RC=4&goto FAIL
:FAIL5
set RC=5&goto FAIL
:FAIL6
set RC=6&goto FAIL
:FAIL7
set RC=7&goto FAIL
:FAIL8
set RC=8&goto FAIL
:FAIL9
set RC=9&goto FAIL
:FAIL10
set RC=10&goto FAIL
:FAIL11
set RC=11&goto FAIL
:FAIL
>>"%LOG%" echo RESULT: FAIL %RC%
copy /y "%LOG%" "%REPORT%" >nul 2>&1
echo REPORT: %REPORT%
if "%NOPAUSE%"=="0" pause
exit /b %RC%
