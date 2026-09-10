@echo off
set "LOGDIR=D:\Smart Playout\Data\Logs\Streaming"
if not exist "%LOGDIR%" (
  echo No streaming logs have been created yet.
  echo Start RTMP / UDP / SRT once, then run this BAT again.
  pause
  exit /b 0
)
start "" explorer.exe "%LOGDIR%"
