SR MUSIX HD PLAYOUT — WINDOWS DESKTOP V2
==========================================

V3 PROGRAM PLAYOUT:
- Playlist menu-ல் Program Name உருவாக்கி current songs-ஐ Program ஆக save/load/delete செய்யலாம்.
- Saved Program-ஐ date/time schedule செய்யலாம்.
- Filler Program தேர்வு செய்தால் manual/scheduled playlist முடிந்ததும் default songs automatic loop ஆகும்.
- ஒவ்வொரு media file-க்கும் SET IN / SET OUT marks மற்றும் -10/+10 second seek உள்ளது.
- Preview Mute output audio-ஐ பாதிக்காது; RTMP/UDP/DeckLink audio தொடர்ந்து செல்லும்.
- 16:9 Fit, Full Screen Crop மற்றும் Stretch preview/output modes.
- RTMP Server மற்றும் Stream Key தனித்தனி fields. பாதுகாப்புக்காக key profile-ல் save செய்யப்படாது.
- UDP-க்கு key கிடையாது; IP, Port, LAN/DVB/PID settings Saved UDP Profile ஆக சேமிக்கப்படும்.
- Active RTMP/UDP இருக்கும் போது CG Apply, Ticker Update, Color Save அல்லது next media செய்தால் output current playhead-ல் automatic refresh ஆகும்.
- Custom TTF/OTF/TTC fonts, image/animated logo, watermark, Now/Next மற்றும் ticker final MPEG-TS/RTMP frame-ல் composited ஆகும்.

V3.1 FINAL PROGRAM EDITOR:
- Playlist right-click: Play Now, Play Next, Insert, Edit/Replace, Explorer, Set/Clear Group, Shuffle, Remove, Up/Down, Save Playlist, Save Project.
- Toolbar-லும் Insert, Edit, Set Group, Shuffle, Save/Open Project buttons உள்ளன.
- Program Library-க்குள் Video Files, .srplaylist மற்றும் .srproject மூன்றையும் import செய்யலாம்.
- Project import current settings-ஐ அழிக்காமல் Programs மற்றும் media items-ஐ merge செய்கிறது.
- .srproject file-ல் Programs, playlist, schedules, filler, CG, color/audio, marks, groups மற்றும் output profiles save ஆகும்.
- Windows Auto Start option மூலம் login ஆனதும் 24x7 schedule/filler playout தொடரும்.
- CG image logo width மற்றும் height தனித்தனியாக மாற்றலாம். Height 0 வைத்தால் original aspect ratio பாதுகாக்கப்படும்; height கொடுத்தால் stretch/squeeze செய்யலாம்.
- அறியாத extension-களுக்கும் All Files தேர்வு உள்ளது; actual decode support FFprobe/FFmpeg codec detection மூலம் முடிவு செய்யப்படும்.

V2 முக்கிய திருத்தம்:
- பழைய MPEG-1/MPEG-2 video-ஐ Chromium player-க்கு அனுப்பாமல் FFmpeg compatibility bridge வழியாக app preview-க்குள்ளேயே H.264/AAC ஆக மாற்றுகிறது.
- அதனால் DAT, VOB, M2P, MPEG, MPG, MTS, M2TS, TS files audio-only ஆகாமல் video + audio preview ஆகும்.
- Color correction மற்றும் Audio EQ compatibility preview-லும் RTMP/UDP/DeckLink output-லும் apply ஆகும்.
- CG text values தனி UTF-8 text assets மூலம் FFmpeg-க்கு கொடுக்கப்படுவதால் colon, Tamil text மற்றும் file-title escaping காரணமான Invalid argument பிழை தவிர்க்கப்படுகிறது.

தொடங்குவது:
1. Windows கணினியில் Node.js LTS நிறுவவும்: https://nodejs.org
2. START_WINDOWS.bat கோப்பை double-click செய்யவும்.
3. முதல் முறை தேவையான Electron package install ஆகும்; பிறகு software திறக்கும்.

RTMP LIVE:
1. FFmpeg நிறுவி Windows PATH-ல் சேர்க்கவும்: https://ffmpeg.org
2. Playlist-ல் media சேர்த்து play செய்யவும்.
3. RTMP Server + Stream Key-ஐ ஒரே URL ஆக paste செய்யவும்.
4. GO LIVE அழுத்தவும்.

DVB / MULTICAST UDP CABLE OUTPUT:
1. Headend கொடுத்த Multicast IP மற்றும் Port உள்ளிடவும்.
2. Source / All Resolutions mode SD முதல் 4K வரை source resolution-ஐ வைத்திருக்கும்.
3. Receiver தேவைக்கேற்ப 576, 720p அல்லது 1080p preset தேர்வு செய்யலாம்.
4. START UDP அழுத்தவும். Output: MPEG-TS, H.264, AAC, pkt_size 1316.
5. Windows Firewall-ல் FFmpeg-க்கு Private network அனுமதி வழங்கவும்.

FFmpeg INPUT SUPPORT:
MP4, MKV, AVI, MOV, MPEG/MPG, TS/M2TS, VOB, FLV, WebM,
H.264, H.265/HEVC மற்றும் FFmpeg decode செய்யும் பெரும்பாலான codecs.

WINDOWS EXE BUILD:
1. Node.js LTS நிறுவவும்.
2. BUILD_WINDOWS_EXE.bat double-click செய்யவும்.
3. dist folder-ல் Setup EXE மற்றும் Portable EXE கிடைக்கும்.
4. GitHub-ல் push செய்தால் Actions > Build Windows EXE > Artifacts-ல் EXE package கிடைக்கும்.
5. Offline FFmpeg வேண்டுமெனில் official x64 ffmpeg.exe, ffprobe.exe-ஐ bin folder-ல் build செய்வதற்கு முன் வைக்கவும்.

VERSION 2 CONTROLS:
- DAT, VOB, MPG, MPEG, MTS, M2P, M2TS, TS, MXF உள்ளிட்ட broadcast files.
- Embedded preview codec support இல்லையெனில் Full FFmpeg automatic fallback.
- Decoder / Processor Mode-ல் Auto Low CPU, GPU Auto, Microsoft D3D11VA, DXVA2, NVIDIA CUDA, Intel Quick Sync மற்றும் CPU Safe manual selection உள்ளது.
- TEST / DETECT DECODER தேர்ந்த file-க்கு உண்மையில் வேலை செய்யும் decoder/encoder-ஐ சோதித்து காட்டும்; hardware fail ஆனால் 2-thread CPU safe fallback தானாக இயங்கும்.
- Schedule, CG/Ticker, Advertisement மற்றும் Settings menu pages.
- Manual input resolution: SD, HD, Full HD, 2K, 4K அல்லது custom width/height.
- H.264/H.265 software, NVIDIA, Intel, AMD, MPEG-2 மற்றும் passthrough codec selection.
- Hardware encoder கிடைக்கவில்லை என்றால் software H.264/H.265 automatic fallback.
- Color: brightness, contrast, saturation, gamma, hue.
- Audio: gain, bass, mid, treble, normalization, mono/stereo.
- Graphics display selector மற்றும் Blackmagic DeckLink device/mode settings.

DECKLINK குறிப்பு:
Blackmagic Desktop Video driver மற்றும் DeckLink SDK support உள்ள FFmpeg build தேவை.
INSTALL_FFMPEG_WINDOWS.bat நிறுவும் பொதுவான FFmpeg build-ல் DeckLink support இல்லாமல் இருக்கலாம்.

PROGRAM OUTPUT COMPOSITOR:
- Logo, Now Playing, Next Track மற்றும் scrolling ticker FFmpeg video frame-ல் mix செய்யப்படும்.
- அதனால் RTMP, Multicast UDP/DVB MPEG-TS மற்றும் DeckLink output-ல் CG தெரியும்.
- CG/Ticker panel-ல் checkbox off செய்த layer output-ல் சேராது.
- Stream ஆரம்பித்த பிறகு title/ticker மாற்றினால் output-ஐ stop/start செய்தால் புதிய text apply ஆகும்.
- CHECK_FFMPEG_SUPPORT.bat மூலம் drawtext, GPU encoder மற்றும் DeckLink support சரிபார்க்கலாம்.

CG LOGO / LAYOUT IMPORT:
- Static: PNG, JPG, JPEG.
- Animated: GIF, WEBM, MOV, MP4 loop.
- Transparent numbered PNG sequence: logo_0001.png, logo_0002.png போன்ற files அனைத்தையும் select செய்யவும்.
- SWF: select செய்ததும் FFmpeg மூலம் ARGB/QTRLE MOV ஆக automatic conversion முயற்சி செய்யப்படும்.
- Logo width, opacity, corner position மற்றும் manual X/Y axis adjustment உள்ளது.
- Imported logo RTMP, UDP/DVB MPEG-TS மற்றும் DeckLink compositor-ல் overlay செய்யப்படும்.

ADDITIONAL VIDEO INPUTS:
DAT, TS, M2V, M2P, MTS, M2TS, M4V, MPEG, MPG, VOB file picker-ல் சேர்க்கப்பட்டுள்ளன.
M2V போன்ற audio இல்லாத elementary stream-களும் video-only ஆக output செய்யப்படும்.

CG FILTER HOTFIX:
FFmpeg drawtext Windows font path சரியான C\:/Windows/Fonts/Nirmala.ttf வடிவத்திற்கு மாற்றப்பட்டது.
முந்தைய C\\:/ path காரணமாக வந்த "Invalid argument" filtergraph error சரிசெய்யப்பட்டது.

DRAGGABLE CG PREVIEW:
- Preview screen-ல் Logo, Watermark, Now/Next மற்றும் Ticker-ஐ mouse cursor மூலம் drag செய்யலாம்.
- Position output resolution-ன் percentage ஆக save செய்யப்படும்; SD/HD/4K-ல் அதே relative position வரும்.
- Watermark text, opacity மற்றும் font size CG Layout-ல் மாற்றலாம்.
- Preview position RTMP மற்றும் UDP/DVB compositor-க்கு அனுப்பப்படும்.

SAVED PLAYLIST & SCHEDULER:
- Current playlist-ஐ பெயருடன் Save Playlist செய்யலாம்.
- Load Playlist மூலம் மீண்டும் திறக்கலாம்.
- Schedule மூலம் saved playlist-க்கு date/time நிர்ணயிக்கலாம்.
- App திறந்து இயங்கிக் கொண்டிருந்தால் scheduled time-ல் playlist load ஆகி auto-play தொடங்கும்.

DVB CODECS:
- H.264: libx264, NVIDIA NVENC, Intel QSV, AMD AMF.
- H.265/HEVC: libx265, NVIDIA NVENC, Intel QSV, AMD AMF.
- Output container: MPEG-TS over multicast UDP/DVB.

MEDIA CODEC INSPECTOR:
- Playlist item select செய்து Codec Info அழுத்தினால் FFprobe container, video/audio codec, resolution, FPS, pixel format காட்டும்.
- Recommended DVB output codec-ஐ Use Recommended Codec மூலம் apply செய்யலாம்.
- Open Folder மூலம் Windows Explorer-ல் அந்த file இருக்கும் folder திறக்கும்.
- VOB, MPG, MPEG, DAT, TS, MTS, M2TS, M2P, M2V embedded Chromium preview-க்கு பதிலாக FFplay-ல் திறக்கும்.
- இவை audio மட்டும் embedded preview-ல் வருவதற்கான பொதுவான காரணம் MPEG-2 video codec-ஐ Chromium decode செய்யாதது; file corrupt என்று அவசியமில்லை.

குறிப்பு:
- RTMP key யாரிடமும் பகிர வேண்டாம்.
- இந்த version Preview + playlist automation + CG/ticker + channel controls + schedule UI ஆகியவற்றுக்கான functional foundation.
- ஒரே media file RTMP-க்கு loop ஆக stream செய்யப்படும். Full mixed program output, capture-card/NDI மற்றும் database scheduler அடுத்த production build-ல் இணைக்கலாம்.
SR MUSIX HD AUTOMATIC PLAYOUT V3.3

V3.2 புதிய வசதிகள்:
- ஒவ்வொரு Program-க்கும் தனி Playlist Editor: Video / Playlist / Project import, Insert, Edit/Replace, Up, Down, Shuffle, Remove, Save.
- தனி CG Layout Designer: Logo drag, Width, Height, Opacity, X/Y, Watermark, Now/Next, Ticker.
- DVB/Multicast UDP பகுதியில் Encoder Codec, Custom Video Bitrate, Buffer, Resolution, PID/Mux controls.
- RTMP பகுதியில் தனி Encoder Codec, Custom Bitrate, GOP மற்றும் Stream Key.
- Fit Screen, Full Screen Crop, Stretch, 4:3 Pillarbox modes.
- Auto HD Quality, Sharpness, Noise Reduction.
- தனி Professional Color Correction page: U Channel, V Channel, UV Gain, V Gain, RGB White Balance.
- எல்லா தனி pages-க்கும் Minimize, Maximize/Restore, Close buttons.
- Premium 3D broadcast-console buttons and panels.
- Network URL / HLS / M3U8 playback மற்றும் YouTube direct link playback. YouTube support-க்கு INSTALL_YTDLP_WINDOWS.bat-ஐ ஒருமுறை Run as Administrator செய்யவும்.

V3.3 FINAL CONTROL UPDATE:
- Gapless preload transition மற்றும் MCR colour-bar standby background.
- Playout START / STOP, ON AIR green / OFF AIR red status.
- தனி Streaming Output layout: UDP/DVB, RTMP, DeckLink மற்றும் live data meter.
- தனி Audio / Digital Equalizer layout: Volume, Gain, Bass, Mid, Treble, Stereo/Mono.
- Schedule Date, Calendar, Time, Type, Program மற்றும் Network/YouTube URL fields.
- Live UV/White Balance/Auto Color/Sharpness/Softness/Noise Reduction preview.
