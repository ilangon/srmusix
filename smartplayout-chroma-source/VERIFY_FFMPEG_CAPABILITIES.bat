@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"
set "FF=ffmpeg_runtime\MediaCore\win-x64\ffmpeg.exe"
set "FP=ffmpeg_runtime\MediaCore\win-x64\ffprobe.exe"
set "OUT=ffmpeg_runtime\Data\Capabilities"
if not exist "%OUT%" mkdir "%OUT%"
if not exist "%FF%" echo ERROR: Shared MediaCore FFmpeg runtime missing.& exit /b 1

for %%N in (VERSION BUILDCONF DECODERS ENCODERS CODECS FORMATS DEMUXERS MUXERS FILTERS BSFS PROTOCOLS DEVICES HWACCELS PIX_FMTS SAMPLE_FMTS) do if exist "%OUT%\%%N.txt" del /q "%OUT%\%%N.txt"

"%FF%" -hide_banner -version > "%OUT%\VERSION.txt" 2>&1
if errorlevel 1 exit /b 1
"%FF%" -hide_banner -buildconf > "%OUT%\BUILDCONF.txt" 2>&1
"%FF%" -hide_banner -decoders > "%OUT%\DECODERS.txt" 2>&1
"%FF%" -hide_banner -encoders > "%OUT%\ENCODERS.txt" 2>&1
"%FF%" -hide_banner -codecs > "%OUT%\CODECS.txt" 2>&1
"%FF%" -hide_banner -formats > "%OUT%\FORMATS.txt" 2>&1
"%FF%" -hide_banner -demuxers > "%OUT%\DEMUXERS.txt" 2>&1
"%FF%" -hide_banner -muxers > "%OUT%\MUXERS.txt" 2>&1
"%FF%" -hide_banner -filters > "%OUT%\FILTERS.txt" 2>&1
"%FF%" -hide_banner -bsfs > "%OUT%\BSFS.txt" 2>&1
"%FF%" -hide_banner -protocols > "%OUT%\PROTOCOLS.txt" 2>&1
"%FF%" -hide_banner -devices > "%OUT%\DEVICES.txt" 2>&1
"%FF%" -hide_banner -hwaccels > "%OUT%\HWACCELS.txt" 2>&1
"%FF%" -hide_banner -pix_fmts > "%OUT%\PIX_FMTS.txt" 2>&1
"%FF%" -hide_banner -sample_fmts > "%OUT%\SAMPLE_FMTS.txt" 2>&1
if exist "%FP%" "%FP%" -hide_banner -version > "%OUT%\FFPROBE_VERSION.txt" 2>&1

>"%OUT%\MODULE_STATUS.txt" echo SMART PLAYOUT FFmpeg MODULE STATUS
>>"%OUT%\MODULE_STATUS.txt" echo ==================================
call :CHECKFILE "%OUT%\DECODERS.txt" " h264 " "H264 DECODER"
call :CHECKFILE "%OUT%\DECODERS.txt" " hevc " "HEVC DECODER"
call :CHECKFILE "%OUT%\DECODERS.txt" " mpeg2video " "MPEG2 VIDEO DECODER"
call :CHECKFILE "%OUT%\ENCODERS.txt" " libx264 " "H264 CPU ENCODER libx264"
call :CHECKFILE "%OUT%\ENCODERS.txt" " h264_nvenc " "NVIDIA NVENC"
call :CHECKFILE "%OUT%\ENCODERS.txt" " h264_qsv " "INTEL QSV"
call :CHECKFILE "%OUT%\ENCODERS.txt" " h264_amf " "AMD AMF"
call :CHECKFILE "%OUT%\DEVICES.txt" "dshow" "DIRECTSHOW DEVICE BACKEND"
call :CHECKFILE "%OUT%\DEVICES.txt" "decklink" "DECKLINK DEVICE BACKEND"
call :CHECKFILE "%OUT%\PROTOCOLS.txt" "rtmp" "RTMP PROTOCOL"
call :CHECKFILE "%OUT%\PROTOCOLS.txt" "srt" "SRT PROTOCOL"
call :CHECKFILE "%OUT%\PROTOCOLS.txt" "udp" "UDP PROTOCOL"
call :CHECKFILE "%OUT%\PROTOCOLS.txt" "rist" "RIST PROTOCOL"
call :CHECKFILE "%OUT%\FILTERS.txt" " scale " "SCALE FILTER"
call :CHECKFILE "%OUT%\FILTERS.txt" " overlay " "OVERLAY FILTER"
call :CHECKFILE "%OUT%\FILTERS.txt" " chromakey " "CHROMAKEY FILTER"
call :CHECKFILE "%OUT%\FILTERS.txt" " aresample " "AUDIO RESAMPLE FILTER"

>"%OUT%\SELF_TEST_RESULTS.txt" echo SMART PLAYOUT FFmpeg SELF TESTS
>>"%OUT%\SELF_TEST_RESULTS.txt" echo ===============================
call :RUNTEST "CORE LAVFI PIPELINE" "-hide_banner -loglevel error -f lavfi -i testsrc2=size=320x180:rate=25 -f lavfi -i sine=frequency=1000:sample_rate=48000 -t 1 -map 0:v -map 1:a -c:v rawvideo -pix_fmt yuv420p -c:a pcm_s16le -f null NUL"
findstr /I /C:"libx264" "%OUT%\ENCODERS.txt" >nul && call :RUNTEST "LIBX264 ACTUAL ENCODE" "-hide_banner -loglevel error -f lavfi -i testsrc2=size=320x180:rate=25 -t 1 -c:v libx264 -pix_fmt yuv420p -f null NUL"
findstr /I /C:"h264_nvenc" "%OUT%\ENCODERS.txt" >nul && call :RUNTEST "NVENC ACTUAL ENCODE" "-hide_banner -loglevel error -f lavfi -i testsrc2=size=320x180:rate=25 -t 1 -c:v h264_nvenc -f null NUL"
findstr /I /C:"h264_qsv" "%OUT%\ENCODERS.txt" >nul && call :RUNTEST "QSV ACTUAL ENCODE" "-hide_banner -loglevel error -f lavfi -i testsrc2=size=320x180:rate=25 -t 1 -c:v h264_qsv -f null NUL"
findstr /I /C:"h264_amf" "%OUT%\ENCODERS.txt" >nul && call :RUNTEST "AMF ACTUAL ENCODE" "-hide_banner -loglevel error -f lavfi -i testsrc2=size=320x180:rate=25 -t 1 -c:v h264_amf -f null NUL"

echo Full FFmpeg capability audit saved under:
echo   %OUT%
exit /b 0

:CHECKFILE
findstr /I /C:%2 %1 >nul
if errorlevel 1 (>>"%OUT%\MODULE_STATUS.txt" echo [MISSING] %~3) else (>>"%OUT%\MODULE_STATUS.txt" echo [AVAILABLE] %~3)
exit /b 0

:RUNTEST
"%FF%" %~2 >>"%OUT%\SELF_TEST_DETAILS.txt" 2>&1
if errorlevel 1 (>>"%OUT%\SELF_TEST_RESULTS.txt" echo [FAILED] %~1) else (>>"%OUT%\SELF_TEST_RESULTS.txt" echo [WORKING] %~1)
exit /b 0
