SMART PLAYOUT v0.6.8.50.30 - NATIVE DECKLINK SDK MODULE

PURPOSE
- Separate physical Blackmagic I/O from FFmpeg encoding and DirectShow.
- Final Program SDI/HDMI output target is uncompressed video frames + PCM audio.
- No H.264/H.265 video encoder is required for the Native DeckLink output branch.
- File playback still needs decoders; network streaming/recording can still need encoders/muxers.

THIS PACK INCLUDES
1. A Windows-native DeckLink SDK probe source (DeckLinkProbe.cpp).
2. BUILD_DECKLINK_NATIVE_SDK_PROBE.bat at package root.
3. A stable C ABI contract for the companion SMARTPlayout.Device.BMD.x64.dll.
4. Managed DeckLinkNativeOutputEngine wiring in SMART PLAYOUT.

IMPORTANT
The Blackmagic DeckLink SDK itself is vendor software and is NOT bundled in this package.
Install/download it from Blackmagic Design. The build script locates DeckLinkAPI.idl and uses
Microsoft MIDL from Visual Studio/Windows SDK to generate the Windows COM header/GUID source.

CURRENT GATE
- Native SDK/driver/device enumeration can be independently verified now.
- SMART PLAYOUT already prefers the native bridge when SMARTPlayout.Device.BMD.x64.dll exists.
- FFmpeg DeckLink remains fallback/diagnostic only.
- The production scheduled-playback bridge DLL must implement the C ABI in
  SMARTPlayout.DeckLinkNative.h using IDeckLinkOutput scheduled video/audio APIs and hardware timing.
  Do not substitute DirectShow audio for that final native path.
