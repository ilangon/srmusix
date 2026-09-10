@echo off
setlocal EnableExtensions
cd /d "%~dp0"
set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"
set "LOG=%CD%\BUILD_DIAGNOSTIC_LOG.txt"
if exist "%LOG%" del /q "%LOG%"
call "PREPARE_FFMPEG_RUNTIME.bat" >>"%LOG%" 2>&1
if exist "SmartPlayout\obj" rmdir /s /q "SmartPlayout\obj"
if exist "SmartPlayout\bin" rmdir /s /q "SmartPlayout\bin"
for /d %%D in ("Modules\*") do (
  if exist "%%~fD\obj" rmdir /s /q "%%~fD\obj"
  if exist "%%~fD\bin" rmdir /s /q "%%~fD\bin"
)
set "SMARTPLAYER_SKIP_RUNTIME_PREP=1"
"%DOTNET%" restore "SmartPlayout\SmartPlayout.csproj" -r win-x64 >>"%LOG%" 2>&1
"%DOTNET%" build "SmartPlayout\SmartPlayout.csproj" -c Release -r win-x64 --self-contained true --no-restore --no-incremental -p:UseAppHost=true -p:GenerateRuntimeConfigurationFiles=true -p:GenerateDependencyFile=true >>"%LOG%" 2>&1 -m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false
type "%LOG%"
echo.
echo Full diagnostic log saved to:
echo %LOG%
pause
