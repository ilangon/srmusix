# SR MUSIX Playout

Native Windows 11 broadcast playout starter for **SR MUSIX HD**.

## Included in this first build

- .NET 8 WPF desktop application (`win-x64`)
- LibVLC-powered preview and playback
- Drag-and-drop playlist with Play / Pause / Stop / Next
- Schedule time and automatic item triggering
- Draggable logo, Now/Next and ticker overlays
- FFmpeg command builder for RTMP or multicast UDP MPEG-TS output
- NVIDIA NVENC H.264/H.265 encoder selection
- Blackmagic Intensity Pro 4K device/output preset placeholders
- Media validation and operator log
- Windows GitHub Actions build that publishes a self-contained `.exe`

## Download the EXE

Open **Actions → Build Windows EXE → latest successful run → Artifacts**, then download `SRMusixPlayout-win-x64`.

## Local development

Requirements: Windows 11, Visual Studio 2022 17.8+ with **.NET desktop development**, and .NET 8 SDK.

```powershell
dotnet restore
dotnet run --project src/SRMusix.Playout
```

FFmpeg is intentionally not committed. Put an official LGPL shared x64 build at `tools/ffmpeg/bin/ffmpeg.exe`, or select it from Settings in a future release. Keeping FFmpeg as external shared libraries avoids mixing GPL-only components into this repository.

## Hardware note

Blackmagic SDI/HDMI output needs Blackmagic Desktop Video and a DeckLink SDK-backed output module. The current first version detects/configures the intended output but uses the desktop preview until that module is added.


## Recommended: Node/Web edition

For the Windows desktop web interface with broad FFmpeg media support, including `.m2p`, use [`playout-node/`](playout-node/). Install Node.js LTS and FFmpeg, then double-click `playout-node/start-windows.bat`. It opens at `http://localhost:3000`.
