SR MUSIX HD AUTOMATION PLAYOUT V3.7.0
DESKTOP + WEB SHARED SOURCE

இந்த project-ல் Desktop EXE மற்றும் Web interface இரண்டுக்கும் ஒரே UI/source concept பயன்படுத்தப்படுகிறது.

SHARED WEB / UI SOURCE
  index.html       - முழு playout interface
  app.js           - playlist, program, schedule, CG, correction மற்றும் output control
  style.css        - main design
  udp.css, v32.css - broadcast/output design additions

WINDOWS DESKTOP BRIDGE
  main.js           - FFmpeg, decoder/encoder, RTMP, UDP/RTP/SRT, DeckLink
  preload.js        - பாதுகாப்பான Desktop API bridge

FULL CODECS
  bin/ffmpeg.exe
  bin/ffprobe.exe
  bin/ffplay.exe

முக்கிய குறிப்பு:
சாதாரண browser பாதுகாப்பு காரணமாக local Windows files, FFmpeg hardware decoder,
RTMP, multicast UDP மற்றும் DeckLink-ஐ நேரடியாக இயக்க முடியாது. Web UI-யை பயன்படுத்தும்போதும்
இந்த Desktop/localhost backend ஓட வேண்டும். அதனால் UI மற்றும் automation behaviour ஒரே மாதிரி
இருக்கும்; hardware/output வேலை main.js backend வழியாக நடக்கும்.

MAIN OUTPUT BUS
Main Resolution, FPS, Aspect மற்றும் Audio format ஒருமுறை தேர்வு செய்தால் Preview,
Fullscreen, RTMP, UDP/RTP/SRT மற்றும் DeckLink அனைத்தும் அதையே பயன்படுத்தும்.

