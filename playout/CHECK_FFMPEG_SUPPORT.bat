@echo off
title SR MUSIX HD - FFmpeg Capability Check
echo === FFmpeg Version ===
ffmpeg -version
echo.
echo === CG DrawText Filter ===
ffmpeg -hide_banner -filters 2>&1 | findstr /i "drawtext drawbox eq hue"
echo.
echo === Video Encoders ===
ffmpeg -hide_banner -encoders 2>&1 | findstr /i "libx264 libx265 nvenc qsv amf mpeg2video"
echo.
echo === DeckLink Support ===
ffmpeg -hide_banner -devices 2>&1 | findstr /i "decklink"
echo.
echo If drawtext is listed, CG can be mixed into RTMP, UDP/DVB and DeckLink output.
pause
