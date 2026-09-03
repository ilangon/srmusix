# SR MUSIX HD Universal Web Playout

This is the Node/browser edition. FFmpeg decodes the source on the server and creates a live HLS preview, so playback is not limited to the codecs built into Chrome.

## Supported inputs

Any input recognized by the installed FFmpeg build, including **M2P**, M2V, MPEG/MPG, VOB, DAT, TS/M2TS/MTS, MXF, MOV, MKV, AVI, WMV, FLV, MP4, WebM, MP3, WAV, AAC, AC3 and FLAC. There is no hard-coded extension rejection list.

## Windows easy setup

1. Download the repository ZIP and extract it completely.
2. Open the extracted `playout-node` folder.
3. Double-click `install-windows.bat` once. It installs Node.js LTS and FFmpeg with Windows `winget`, installs the local components, and creates an **SR MUSIX HD Playout** Desktop shortcut.
4. After setup, use that Desktop shortcut. The control page opens at `http://localhost:3000`.

Do not run the BAT file while it is still inside the ZIP; extract the ZIP first.

## Manual Windows start

1. Install 64-bit Node.js LTS.
2. Install a full 64-bit FFmpeg build and add its `bin` folder to Windows PATH.
3. Double-click `start-windows.bat`.
4. The control page opens at `http://localhost:3000`.

Media selected in the page is copied into the local `media` directory. The 500 GB upload ceiling is intentional for long-form broadcast files. Keep the playout PC and browser on the same machine.

## Options included

- Drag/drop multi-file playlist; remove, clear, double-click play
- Play, browser pause, stop, next, loop, volume and native seek controls
- Schedule per item with automatic triggering
- Logo image, Now/Next, scrolling ticker and aspect selection
- RTMP or MPEG-TS destination
- NVIDIA H.264/H.265 or CPU H.264 encoding and bitrate control

The browser preview and transmission output are separate FFmpeg paths. For frame-accurate seamless switching and Blackmagic SDI fill/key output, a dedicated broadcast output pipeline is still required.
