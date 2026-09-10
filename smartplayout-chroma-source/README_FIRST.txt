SMART PLAYOUT v0.6.8.50.48

BUILD
1. Extract the source pack to a short local path, for example C:\SMART_PLAYOUT_50_48.
2. Run BUILD_AND_RUN.bat for a portable build and immediate launch.
3. Run BUILD_FINAL_SETUP.bat for portable ZIP and optional Inno Setup installer.

LOG FILES
- BUILD_AND_RUN_v0.6.8.50.48.log
- BUILD_FINAL_v0.6.8.50.48.log
- Logs are created beside the BAT file immediately, before runtime preparation or compilation begins.
- If the build fails, send the applicable complete log.

REQUIREMENTS
- Windows 10/11 x64.
- .NET 8 SDK or a later SDK capable of targeting net8.0-windows.
- One complete matching FFmpeg 9 Windows x64 runtime family.
- Inno Setup 6 is optional; without it the portable ZIP is still produced.

CG REFERENCES
- REFERENCE_DESIGN\CG_STUDIO_LOCKED_REFERENCE.png is the approved full CG Studio layout reference.
- REFERENCE_DESIGN\CHROMA_KEY_STUDIO_LOCKED_REFERENCE.png is the approved Chroma Key Studio reference.

IMPORTANT STATUS
- Locked CG and Chroma design colors/layouts are retained. CG Layers & Timeline, property cards, template actions, animation controls, text watermark and mouse transform/resize are present.
- Chroma source options include DeckLink/NDI/system capture/network URL/screen/test pattern, AUTO embedded audio, manual external audio and video-only operation. Background video audio is intentionally disabled.
- Program preview dispatch is decoupled from the output clock; shared Program BGRA/PCM buses feed the isolated Output Worker without changing media cadence.
- RTMP, DVB UDP and DVB SRT worker registrations carry the same validated FFmpeg command template as the embedded compatibility writer, including SAR, PIDs, service metadata, interface binding and SRT settings.
- BUILD_FINAL_SETUP.bat requires Windows x64 and a compatible .NET SDK. It creates the portable Build ZIP, optional Setup EXE, Source Code ZIP, SHA256 file and verification logs.
