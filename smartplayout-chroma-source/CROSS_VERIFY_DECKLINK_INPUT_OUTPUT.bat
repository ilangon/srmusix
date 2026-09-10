@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
set "REPORT=DECKLINK_INPUT_OUTPUT_REPORT.txt"
>"%REPORT%" echo SMART PLAYOUT v0.6.8.50.30 - DECKLINK INPUT / OUTPUT CROSS VERIFY
>>"%REPORT%" echo Generated: %date% %time%
>>"%REPORT%" echo ================================================================

>>"%REPORT%" echo.
>>"%REPORT%" echo [WINDOWS BLACKMAGIC PNP]
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-PnpDevice ^| Where-Object { $_.FriendlyName -match 'Blackmagic|DeckLink|Intensity' } ^| Format-Table -AutoSize Status,Class,FriendlyName,InstanceId" >>"%REPORT%" 2>&1

>>"%REPORT%" echo.
>>"%REPORT%" echo [BLACKMAGIC SERVICE]
powershell -NoProfile -ExecutionPolicy Bypass -Command "Get-Service dvhlp -ErrorAction SilentlyContinue ^| Format-Table -AutoSize Status,Name,DisplayName" >>"%REPORT%" 2>&1

>>"%REPORT%" echo.
>>"%REPORT%" echo [MEDIALOOKS MPLATFORM INSTALL]
if exist "C:\Program Files (x86)\Medialooks\MPlatform SDK" (echo FOUND: C:\Program Files (x86)\Medialooks\MPlatform SDK>>"%REPORT%") else (echo NOT FOUND: MPlatform SDK x86>>"%REPORT%")
if exist "C:\Program Files\Medialooks\MPlatform SDK" (echo FOUND: C:\Program Files\Medialooks\MPlatform SDK>>"%REPORT%") else (echo NOT FOUND: MPlatform SDK x64>>"%REPORT%")

set "CANDIDATES=%SMARTPLAYOUT_DECKLINK_FFMPEG%;%CD%\ffmpeg_runtime\MediaCore\win-x64\ffmpeg.exe;%CD%\PLAYOUT\Runtime\MediaCore\FFmpeg\ffmpeg.exe"
for %%F in (!CANDIDATES!) do call :probe "%%~F"

goto :done

:probe
set "F=%~1"
if "%F%"=="" goto :eof
if not exist "%F%" goto :eof
for %%Z in ("%F%") do set "KEY=%%~fZ"
if defined SEEN_!KEY! goto :eof
set "SEEN_!KEY!=1"
>>"%REPORT%" echo.
>>"%REPORT%" echo [FFMPEG] %F%
"%F%" -hide_banner -version >>"%REPORT%" 2>&1
>>"%REPORT%" echo --- DEVICES ---
"%F%" -hide_banner -devices >>"%REPORT%" 2>&1
>>"%REPORT%" echo --- DIRECTSHOW DEVICES ---
"%F%" -hide_banner -list_devices true -f dshow -i dummy >>"%REPORT%" 2>&1
>>"%REPORT%" echo --- DECKLINK INPUT SOURCES ---
"%F%" -hide_banner -sources decklink >>"%REPORT%" 2>&1
>>"%REPORT%" echo --- DECKLINK OUTPUT SINKS ---
"%F%" -hide_banner -sinks decklink >>"%REPORT%" 2>&1
>>"%REPORT%" echo --- DECKLINK LEGACY ENUMERATION ---
"%F%" -hide_banner -f decklink -list_devices 1 -i dummy >>"%REPORT%" 2>&1
goto :eof

:done
>>"%REPORT%" echo.
>>"%REPORT%" echo [INTERPRETATION]
>>"%REPORT%" echo D in ffmpeg -devices = input support. E = output support. DE = both.
>>"%REPORT%" echo Windows PnP detection alone does not prove that the selected FFmpeg exposes DeckLink I/O.
>>"%REPORT%" echo MPlatform renderer detection proves the SDK can see an output renderer, not automatically that FFmpeg DirectShow capture is available.
echo.
echo REPORT READY: %CD%\%REPORT%
start notepad "%REPORT%"
endlocal
