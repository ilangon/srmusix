@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

rem One complete version-matched FFmpeg family shared by decode, encode,
rem streaming and capture. Hardware-vendor native bridges remain separate.
set "ROOT=%CD%\ffmpeg_runtime"
set "MEDIACORE=%ROOT%\MediaCore\win-x64"
set "DATA=%ROOT%\Data"
for %%D in ("%MEDIACORE%" "%DATA%") do if not exist "%%~D" mkdir "%%~D"

call :CHECK_FULL "%MEDIACORE%"
if not errorlevel 1 goto MEDIACORE_READY
set "FOUND="
echo Searching for one complete matching FFmpeg 9.0.x runtime family...
call :SEARCHROOT "%CD%"
if not defined FOUND if exist "%USERPROFILE%\Desktop" call :SEARCHROOT "%USERPROFILE%\Desktop"
if not defined FOUND if exist "%USERPROFILE%\.nuget\packages" call :SEARCHROOT "%USERPROFILE%\.nuget\packages"
if not defined FOUND goto NOT_FOUND
echo Found complete FFmpeg runtime: !FOUND!
call :COPY_BUNDLE "!FOUND!" "%MEDIACORE%"
if errorlevel 1 exit /b 1

:MEDIACORE_READY
call :CHECK_FULL "%MEDIACORE%"
if errorlevel 1 exit /b 1

rem Remove only the obsolete generated runtime clones after MediaCore is valid.
for %%D in (Playback Streaming DirectShow DeckLink Decoder Encoder Approved) do if exist "%ROOT%\%%D" rmdir /s /q "%ROOT%\%%D"

"%MEDIACORE%\ffmpeg.exe" -hide_banner -version > "%DATA%\MEDIACORE_FFMPEG_VERSION.txt" 2>&1
"%MEDIACORE%\ffmpeg.exe" -hide_banner -buildconf > "%DATA%\MEDIACORE_FFMPEG_BUILDCONF.txt" 2>&1
>"%DATA%\MEDIACORE_RUNTIME_FILES.txt" echo SMART PLAYOUT SHARED MEDIACORE FILES
for %%F in (avcodec-63.dll avformat-63.dll avutil-61.dll avfilter-12.dll avdevice-63.dll swscale-10.dll swresample-7.dll ffmpeg.exe ffprobe.exe) do for %%S in ("%MEDIACORE%\%%F") do >>"%DATA%\MEDIACORE_RUNTIME_FILES.txt" echo %%~nxS ^| %%~zS bytes ^| %%~tS

"%MEDIACORE%\ffmpeg.exe" -hide_banner -devices 2>&1 | findstr /I /C:"dshow" >nul
if errorlevel 1 (>"%DATA%\DIRECTSHOW_RUNTIME_STATUS.txt" echo NOT COMPILED: dshow missing from shared MediaCore.) else (>"%DATA%\DIRECTSHOW_RUNTIME_STATUS.txt" echo WORKING-CANDIDATE: dshow uses shared Runtime\MediaCore\FFmpeg.)
"%MEDIACORE%\ffmpeg.exe" -hide_banner -devices 2>&1 | findstr /I /C:"decklink" >nul
if errorlevel 1 (>"%DATA%\DECKLINK_RUNTIME_STATUS.txt" echo NATIVE-BRIDGE: shared FFmpeg has no decklink backend; Runtime\DeckLink\Native remains isolated.) else (>"%DATA%\DECKLINK_RUNTIME_STATUS.txt" echo WORKING-CANDIDATE: decklink uses shared Runtime\MediaCore\FFmpeg.)
echo Shared FFmpeg MediaCore ready: %MEDIACORE%
exit /b 0

:SEARCHROOT
for /R "%~1" %%F in (avcodec-63.dll) do if not defined FOUND (
  set "CAND=%%~dpF"
  if exist "!CAND!avformat-63.dll" if exist "!CAND!avutil-61.dll" if exist "!CAND!avfilter-12.dll" if exist "!CAND!avdevice-63.dll" if exist "!CAND!swscale-10.dll" if exist "!CAND!swresample-7.dll" if exist "!CAND!ffmpeg.exe" if exist "!CAND!ffprobe.exe" set "FOUND=!CAND:~0,-1!"
)
exit /b 0

:COPY_BUNDLE
call :CLEAR_DIR "%~2"
xcopy "%~1\*.dll" "%~2\" /Y /I >nul
if errorlevel 1 exit /b 1
for %%E in (ffmpeg.exe ffprobe.exe ffplay.exe) do if exist "%~1\%%E" copy /y "%~1\%%E" "%~2\%%E" >nul
exit /b 0

:CLEAR_DIR
if exist "%~1" del /q "%~1\*" >nul 2>&1
if not exist "%~1" mkdir "%~1"
exit /b 0

:CHECK_FULL
for %%F in (avcodec-63.dll avformat-63.dll avutil-61.dll avfilter-12.dll avdevice-63.dll swscale-10.dll swresample-7.dll ffmpeg.exe ffprobe.exe) do if not exist "%~1\%%F" exit /b 1
exit /b 0

:NOT_FOUND
echo ERROR: Complete matching FFmpeg 9 x64 runtime not found.
echo SMART PLAYOUT will not combine DLLs from different folders or versions.
exit /b 1
