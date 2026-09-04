const $ = (s) => document.querySelector(s);
const video = $("#video");
let list = [],
  selected = -1,
  current = -1,
  editingSchedule = null,
  activeProgramName =
    localStorage.getItem("activeProgramName") || "Manual Playlist",
  usingCompat = false,
  mediaDuration = 0,
  previewOffset = 0,
  rtmpLive = false,
  udpLive = false,
  marks = JSON.parse(localStorage.getItem("mediaMarks") || "{}"),
  mediaGroups = JSON.parse(localStorage.getItem("mediaGroups") || "{}"),
  logoFile = JSON.parse(localStorage.getItem("cgLogoFile") || "null"),
  cgFontFile = localStorage.getItem("cgFontFile") || "",
  cgPositions = JSON.parse(
    localStorage.getItem("cgPositions") ||
      '{"logo":{"x":0.82,"y":0.04},"now":{"x":0.04,"y":0.68},"ticker":{"x":0,"y":0.92},"watermark":{"x":0.42,"y":0.08}}',
  );
let channels = ["SR MUSIX HD", "SR MUSIX MELODIES"];
$("header .status").insertAdjacentHTML(
  "beforebegin",
  '<div id="mainStreamMonitor" class="main-stream-monitor"><b>STREAM OUTPUT</b><span id="udpLiveLamp" class="stream-lamp off"><i></i>UDP / DVB OFF</span><span id="rtmpLiveLamp" class="stream-lamp off"><i></i>RTMP OFF</span></div>',
);
$("header .status").insertAdjacentHTML(
  "beforebegin",
  '<div class="cpu-monitor"><div><b id="cpuUsageText">CPU 0%</b><small id="memoryUsageText">RAM 0%</small></div><div id="cpuGraph" class="cpu-graph">' +
    Array(24).fill("<i></i>").join("") +
    "</div></div>",
);
function updateStreamIndicators() {
  let set = (id, on, label) => {
    let e = $("#" + id);
    e.classList.toggle("live", on);
    e.classList.toggle("off", !on);
    e.lastChild.nodeValue = label;
  };
  set("udpLiveLamp", udpLive, udpLive ? "UDP / DVB LIVE" : "UDP / DVB OFF");
  set("rtmpLiveLamp", rtmpLive, rtmpLive ? "RTMP LIVE" : "RTMP OFF");
  $("#mainStreamMonitor").classList.toggle(
    "has-live-stream",
    udpLive || rtmpLive,
  );
}
updateStreamIndicators();
document.body.insertAdjacentHTML(
  "afterbegin",
  '<svg width="0" height="0" style="position:absolute"><filter id="videoColorCorrection" color-interpolation-filters="sRGB"><feComponentTransfer id="gammaFilter"><feFuncR type="gamma" amplitude="1" exponent="1" offset="0"/><feFuncG type="gamma" amplitude="1" exponent="1" offset="0"/><feFuncB type="gamma" amplitude="1" exponent="1" offset="0"/></feComponentTransfer><feComponentTransfer id="levelFilter"><feFuncR type="linear" slope="1" intercept="0"/><feFuncG type="linear" slope="1" intercept="0"/><feFuncB type="linear" slope="1" intercept="0"/></feComponentTransfer><feColorMatrix id="saturationFilter" type="saturate" values="1"/><feColorMatrix id="hueFilter" type="hueRotate" values="0"/></filter></svg>',
);
document
  .querySelector("#videoColorCorrection")
  .insertAdjacentHTML(
    "beforeend",
    '<feColorMatrix id="uvWhiteFilter" type="matrix" values="1 0 0 0 0 0 1 0 0 0 0 0 1 0 0 0 0 0 1 0"/><feGaussianBlur id="noiseFilter" stdDeviation="0"/><feConvolveMatrix id="sharpnessFilter" order="3" kernelMatrix="0 0 0 0 1 0 0 0 0" divisor="1" preserveAlpha="true"/>',
  );
let audioGraph = null;
function correction() {
  return JSON.parse(localStorage.getItem("correctionSettings") || "{}");
}
function applyPreviewCorrections(settings = correction()) {
  const x = settings.color || {},
    q = settings.quality || {},
    s = settings.sound || {},
    auto = x.auto === true,
    gamma = Math.max(0.1, auto ? 1.02 : Number(x.gamma ?? 1)),
    contrast = Math.max(
      0,
      auto ? Math.max(1.05, Number(x.contrast ?? 1)) : Number(x.contrast ?? 1),
    ),
    brightness = Number(x.brightness || 0),
    intercept = brightness + (0.5 - 0.5 * contrast),
    uv = Math.max(0, Number(x.uvGain ?? 1)),
    vg = Math.max(0.1, Number(x.vGain ?? 1)),
    u = (Number(x.uChannel || 0) / 255) * 0.22,
    v = (Number(x.vChannel || 0) / 255) * 0.22,
    wr = Number(x.whiteRed || 0),
    wg = Number(x.whiteGreen || 0),
    wb = Number(x.whiteBlue || 0),
    sharp = q.auto === false ? 0 : Number(q.sharpness ?? 0.7);
  document
    .querySelectorAll(
      "#gammaFilter feFuncR,#gammaFilter feFuncG,#gammaFilter feFuncB",
    )
    .forEach((n) => n.setAttribute("exponent", String(1 / gamma)));
  document
    .querySelectorAll(
      "#levelFilter feFuncR,#levelFilter feFuncG,#levelFilter feFuncB",
    )
    .forEach((n) => {
      n.setAttribute("slope", String(contrast));
      n.setAttribute("intercept", String(intercept));
    });
  $("#saturationFilter").setAttribute(
    "values",
    String(
      Math.max(
        0,
        (auto
          ? Math.max(1.06, Number(x.saturation ?? 1))
          : Number(x.saturation ?? 1)) * uv,
      ),
    ),
  );
  $("#hueFilter").setAttribute("values", String(Number(x.hue || 0)));
  $("#uvWhiteFilter").setAttribute(
    "values",
    `${Math.max(0.1, 1 + wr) * vg} 0 0 0 ${v} 0 ${Math.max(0.1, 1 + wg)} 0 0 ${-(u + v) / 3} 0 0 ${Math.max(0.1, 1 + wb) * (2 - vg)} 0 ${u} 0 0 0 1 0`,
  );
  $("#noiseFilter").setAttribute(
    "stdDeviation",
    q.auto === false ? "0" : "0.18",
  );
  $("#sharpnessFilter").setAttribute(
    "kernelMatrix",
    `0 ${-sharp} 0 ${-sharp} ${1 + 4 * sharp} ${-sharp} 0 ${-sharp} 0`,
  );
  video.style.filter = "url(#videoColorCorrection)";
  video.volume = 1;
  if (audioGraph) {
    audioGraph.gain.gain.value = Math.max(0, Number(s.gain ?? 1));
    audioGraph.bass.gain.value = Number(s.bass || 0);
    audioGraph.mid.gain.value = Number(s.mid || 0);
    audioGraph.treble.gain.value = Number(s.treble || 0);
    Object.entries(s.bands || {}).forEach(([f, g]) => {
      if (audioGraph.bands[f]) audioGraph.bands[f].gain.value = Number(g) || 0;
    });
    audioGraph.compressor.ratio.value = s.normalize ? 8 : 1;
  }
}
function ensureAudioGraph() {
  if (audioGraph) return;
  try {
    const C = window.AudioContext || window.webkitAudioContext,
      ctx = new C(),
      src = ctx.createMediaElementSource(video),
      bass = ctx.createBiquadFilter(),
      mid = ctx.createBiquadFilter(),
      treble = ctx.createBiquadFilter(),
      compressor = ctx.createDynamicsCompressor(),
      gain = ctx.createGain(),
      split = ctx.createChannelSplitter(2),
      meterL = ctx.createAnalyser(),
      meterR = ctx.createAnalyser(),
      bands = {};
    bass.type = "lowshelf";
    bass.frequency.value = 100;
    mid.type = "peaking";
    mid.frequency.value = 1000;
    mid.Q.value = 1;
    treble.type = "highshelf";
    treble.frequency.value = 8000;
    compressor.threshold.value = -18;
    compressor.knee.value = 8;
    meterL.fftSize = meterR.fftSize = 256;
    let tail = src.connect(bass).connect(mid).connect(treble);
    [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000].forEach((f) => {
      let node = ctx.createBiquadFilter();
      node.type = "peaking";
      node.frequency.value = f;
      node.Q.value = 1;
      bands[f] = node;
      tail = tail.connect(node);
    });
    tail.connect(compressor).connect(gain).connect(ctx.destination);
    gain.connect(split);
    split.connect(meterL, 0);
    split.connect(meterR, 1);
    audioGraph = {
      ctx,
      bass,
      mid,
      treble,
      bands,
      compressor,
      gain,
      meterL,
      meterR,
    };
    applyPreviewCorrections();
    animateAudioMeters();
  } catch (e) {
    console.warn("Audio EQ unavailable", e);
  }
}
function animateAudioMeters() {
  if (!audioGraph?.meterL) return;
  let levels = [audioGraph.meterL, audioGraph.meterR].map((a) => {
      let d = new Uint8Array(a.fftSize);
      a.getByteTimeDomainData(d);
      let rms =
        Math.sqrt(d.reduce((s, v) => s + (v - 128) * (v - 128), 0) / d.length) /
        64;
      return Math.min(1, rms);
    }),
    bars = document.querySelectorAll(".meters i");
  bars.forEach((b, i) =>
    b.style.setProperty(
      "--audio-level",
      `${Math.max(0.02, levels[i] || 0) * 100}%`,
    ),
  );
  requestAnimationFrame(animateAudioMeters);
}
function cgConfig() {
  let lf = logoFile
    ? {
        ...logoFile,
        width: Number($("#logoWidth")?.value || 220),
        height: Number($("#logoHeight")?.value || 0),
        opacity: Number($("#logoOpacity")?.value || 1),
        position: $("#logoPosition")?.value || "tr",
        manual: !!$("#logoManual")?.checked,
        x: Number($("#logoX")?.value || 0),
        y: Number($("#logoY")?.value || 0),
        xPct: cgPositions.logo.x,
        yPct: cgPositions.logo.y,
      }
    : null;
  return {
    fontFile: cgFontFile,
    logo: $("#logoToggle").checked,
    logoText: "★ SR MUSIX HD",
    logoFile: $("#logoToggle").checked ? lf : null,
    watermark: $("#watermarkToggle")?.checked !== false,
    watermarkText: $("#watermarkText")?.value || "SR MUSIX HD",
    watermarkOpacity: Number($("#watermarkOpacity")?.value || 0.35),
    watermarkSize: Number($("#watermarkSize")?.value || 22),
    positions: cgPositions,
    now: $("#nowToggle").checked,
    nowText: current >= 0 ? title(list[current]) : "No media loaded",
    nextText: current + 1 < list.length ? title(list[current + 1]) : "—",
    ticker: $("#tickerToggle").checked,
    tickerText: $("#tickerInput").value,
  };
}
$(".screen").insertAdjacentHTML(
  "beforeend",
  '<div id="watermark">SR MUSIX HD</div>',
);
function placeCG(el, key) {
  let p = cgPositions[key] || { x: 0, y: 0 };
  el.style.left = p.x * 100 + "%";
  el.style.top = p.y * 100 + "%";
  el.style.right = "auto";
  el.style.bottom = "auto";
}
function draggable(el, key, lockX = false) {
  el.classList.add("cg-draggable");
  placeCG(el, key);
  el.onpointerdown = (e) => {
    e.preventDefault();
    el.setPointerCapture(e.pointerId);
    el.onpointermove = (ev) => {
      let r = $(".screen").getBoundingClientRect(),
        x = Math.max(0, Math.min(0.95, (ev.clientX - r.left) / r.width)),
        y = Math.max(0, Math.min(0.95, (ev.clientY - r.top) / r.height));
      if (lockX) x = 0;
      cgPositions[key] = { x, y };
      placeCG(el, key);
      localStorage.setItem("cgPositions", JSON.stringify(cgPositions));
    };
    el.onpointerup = () => {
      el.onpointermove = null;
    };
  };
}
draggable($("#logo"), "logo");
draggable($("#now"), "now");
draggable($("#ticker"), "ticker", true);
draggable($("#watermark"), "watermark");
$(".transport").insertAdjacentHTML(
  "beforeend",
  '<button id="back10">−10s</button><button id="forward10">+10s</button><button id="previewMute">🔊 PREVIEW</button>',
);
$(".screen").insertAdjacentHTML(
  "afterend",
  '<section id="playoutStoryboard" class="playout-storyboard"><div class="story-card now-story"><small>NOW PLAYING</small><b id="storyNowTitle">No media loaded</b><span id="storyNowGroup">PROGRAM: Manual Playlist</span><strong id="storyNowTiming">00:00 / 00:00</strong></div><div class="story-arrow">▶</div><div class="story-card next-story"><small>NEXT PLAYING</small><b id="storyNextTitle">—</b><span id="storyNextGroup">PROGRAM: Manual Playlist</span><strong id="storyNextTiming">DURATION 00:00</strong></div></section>',
);
let seekConsole = document.createElement("section");
seekConsole.id = "videoSeekConsole";
seekConsole.className = "video-seek-console";
seekConsole.innerHTML =
  '<div class="seek-console-title"><b>VIDEO SEEK</b><span>Drag the large cursor to preview any exact position</span></div><div class="seek-fine-controls"><button id="seekBack10" class="action-cyan">−10s</button><button id="seekBack1" class="action-purple">−1s</button><div id="seekTrackMount"></div><button id="seekForward1" class="action-purple">+1s</button><button id="seekForward10" class="action-cyan">+10s</button></div>';
$(".screen").insertAdjacentElement("afterend", seekConsole);
$("#seekTrackMount").appendChild($(".timebar"));
$("#seekBack10").onclick = () => seekTo(playhead() - 10);
$("#seekBack1").onclick = () => seekTo(playhead() - 1);
$("#seekForward1").onclick = () => seekTo(playhead() + 1);
$("#seekForward10").onclick = () => seekTo(playhead() + 10);
$(".quick").insertAdjacentHTML(
  "beforeend",
  '<label>Video Fit<select id="aspectMode"><option value="contain">16:9 Fit</option><option value="cover">Full Screen Crop</option><option value="fill">Stretch</option></select></label><button id="setIn">SET IN</button><button id="setOut">SET OUT</button><span id="markStatus">IN 00:00 • OUT END</span>',
);
function playhead() {
  return Math.max(0, previewOffset + (Number(video.currentTime) || 0));
}
function selectedMark() {
  return current >= 0 ? marks[list[current]] || {} : {};
}
function applyAspect() {
  let mode = $("#aspectMode").value;
  video.style.objectFit = mode === "4:3" ? "contain" : mode;
  video.style.aspectRatio = mode === "4:3" ? "4 / 3" : "auto";
  video.style.margin = mode === "4:3" ? "0 auto" : "";
  localStorage.setItem("aspectMode", mode);
}
$("#aspectMode").insertAdjacentHTML(
  "beforeend",
  '<option value="4:3">4:3 Pillarbox</option>',
);
$("#aspectMode").value = localStorage.getItem("aspectMode") || "contain";
applyAspect();
$("#aspectMode").onchange = applyAspect;
$("#previewMute").onclick = () => {
  video.muted = !video.muted;
  $("#previewMute").textContent = video.muted
    ? "🔇 PREVIEW MUTED"
    : "🔊 PREVIEW";
};
$("#back10").onclick = () => seekTo(playhead() - 10);
$("#forward10").onclick = () => seekTo(playhead() + 10);
$("#setIn").onclick = () => {
  if (current < 0) return;
  let m = marks[list[current]] || {};
  m.in = playhead();
  marks[list[current]] = m;
  localStorage.setItem("mediaMarks", JSON.stringify(marks));
  updateMarkStatus();
};
$("#setOut").onclick = () => {
  if (current < 0) return;
  let m = marks[list[current]] || {};
  m.out = playhead();
  marks[list[current]] = m;
  localStorage.setItem("mediaMarks", JSON.stringify(marks));
  updateMarkStatus();
};
function updateMarkStatus() {
  let m = selectedMark();
  $("#markStatus").textContent =
    `IN ${fmt(Number(m.in) || 0)} • OUT ${m.out ? fmt(Number(m.out)) : "END"}`;
}
function makeOutputFilter(useUdp = true) {
  let ir = $("#inputRes")?.value || "source",
    w = 0,
    h = 0;
  if (ir === "custom") {
    w = Number($("#customW").value);
    h = Number($("#customH").value);
  } else if (ir !== "source") {
    [w, h] = ir.split("x").map(Number);
  } else if (useUdp) {
    let m = String($("#udpRes").value).match(/scale=(\d+):(\d+)/);
    if (m) {
      w = Number(m[1]);
      h = Number(m[2]);
    }
  }
  if (!w || !h) return "source";
  let mode = $("#aspectMode").value;
  if (mode === "fill") return `scale=${w}:${h}`;
  if (mode === "cover")
    return `scale=${w}:${h}:force_original_aspect_ratio=increase,crop=${w}:${h}`;
  if (mode === "4:3") {
    let iw = Math.floor(Math.min(w, (h * 4) / 3) / 2) * 2;
    return `scale=${iw}:${h}:force_original_aspect_ratio=decrease,pad=${w}:${h}:(ow-iw)/2:(oh-ih)/2`;
  }
  return `scale=${w}:${h}:force_original_aspect_ratio=decrease,pad=${w}:${h}:(ow-iw)/2:(oh-ih)/2`;
}
async function refreshLiveOutputs() {
  let r = rtmpLive,
    u = udpLive;
  if (r) {
    await window.playoutAPI.stopRTMP();
    rtmpLive = false;
    await $("#goLive").onclick();
  }
  if (u) {
    await window.playoutAPI.stopUDP();
    udpLive = false;
    await $("#startUdp").onclick();
  }
  if (r || u) {
    $("#onAir").textContent = u ? "DVB / UDP LIVE + CG" : "RTMP LIVE + CG";
  }
}
$(".toolbar").insertAdjacentHTML(
  "beforeend",
  '<button id="savePlaylist">💾 Save Playlist</button><button id="loadPlaylist">↥ Load Playlist</button><button id="schedulePlaylist">◷ Schedule</button>',
);
$(".toolbar").insertAdjacentHTML(
  "beforeend",
  '<button id="probeMedia">ⓘ Codec Info</button><button id="openFolder">📁 Open Folder</button>',
);
$(".toolbar").insertAdjacentHTML(
  "beforeend",
  '<button id="addNetwork" class="primary">🌐 ADD NETWORK / YOUTUBE URL</button>',
);
$(".toolbar").insertAdjacentHTML(
  "afterend",
  '<div class="network-entry"><label>NETWORK / YOUTUBE PLAYBACK URL<input id="networkUrlInput" type="url" spellcheck="false" placeholder="https://... link-ஐ இங்கே type அல்லது paste செய்யவும்"></label><button id="addNetworkFromBox" class="action-purple">＋ ADD URL TO PLAYLIST</button></div>',
);
$(".network-entry").insertAdjacentHTML(
  "beforebegin",
  '<div id="playlistDropZone" class="playlist-drop-zone">⬇ DROP VIDEO / AUDIO FILES HERE FROM WINDOWS EXPLORER</div>',
);
$(".table-head").insertAdjacentHTML(
  "beforebegin",
  '<div class="front-program-summary"><span>LOADED PROGRAM <b id="frontProgramName">Manual Playlist</b></span><span>FILES <b id="frontProgramFileCount">0</b></span></div>',
);
$("#networkUrlInput").insertAdjacentHTML(
  "afterend",
  '<button type="button" class="paste-url" data-paste-target="networkUrlInput">📋 PASTE LINK</button>',
);
document.body.insertAdjacentHTML(
  "beforeend",
  '<aside id="networkPreviewDock" class="source-preview network-preview-dock"><div><b>NETWORK / YOUTUBE PREVIEW</b><select id="previewDecoder"><option value="safe">Safe Auto / CPU</option><option value="cpu">CPU</option><option value="gpu">NVIDIA GPU</option></select><button id="toggleNetworkPreview" class="action-orange" title="Minimize preview">—</button><button id="previewNetworkUrl" class="action-cyan">▶ PREVIEW</button><button id="stopNetworkPreview" class="action-red">■ STOP</button></div><video id="networkPreviewPlayer" controls></video><small id="networkPreviewStatus">Enter a URL and press Preview</small></aside>',
);
$(".center.card .network-entry").insertAdjacentHTML(
  "afterend",
  '<section id="playlistPreviewPanel" class="source-preview playlist-preview-panel"><div><b>PLAYLIST ITEM PREVIEW — DOUBLE CLICK A SONG</b><button id="stopPlaylistPreview" class="action-red">■ STOP PREVIEW</button></div><video id="playlistPreviewPlayer" controls></video><small id="playlistPreviewStatus">Preview is independent from the On-Air player</small></section>',
);
$(".toolbar").insertAdjacentHTML(
  "beforeend",
  '<button id="installYouTube">⬇ INSTALL YOUTUBE SUPPORT</button>',
);
$(".toolbar").insertAdjacentHTML(
  "beforeend",
  '<button id="insertMedia">INSERT</button><button id="editMedia">EDIT</button><button id="setGroup">SET GROUP</button><button id="shufflePlaylist">SHUFFLE</button><button id="saveProject">SAVE PROJECT</button><button id="openProject">OPEN PROJECT</button>',
);
document.body.insertAdjacentHTML(
  "beforeend",
  '<div id="playlistContext" class="playlist-context"><button data-cmd="play">▶ Play Now</button><button data-cmd="next">Play Next</button><button data-cmd="insert">Insert Media</button><button data-cmd="edit">Edit / Replace Song</button><button data-cmd="folder">Open in Explorer</button><button data-cmd="group">Set Group</button><button data-cmd="shuffle">Shuffle</button><button data-cmd="clearGroup">Clear Group</button><button data-cmd="remove">Remove</button><button data-cmd="up">Move Up</button><button data-cmd="down">Move Down</button><button data-cmd="savePlaylist">Save Playlist</button><button data-cmd="saveProject">Save Project</button></div>',
);
$("#rtmp").type = "text";
$("#rtmp").placeholder = "rtmp://server/live/stream-key  or  rtmps://...";
$("#rtmp").autocomplete = "off";
$("#rtmp").spellcheck = false;
$("#rtmp").parentElement.firstChild.textContent =
  "Full RTMP / RTMPS Streaming URL (including stream key)";
$("#rtmp").parentElement.insertAdjacentHTML(
  "afterend",
  '<label class="retired-rtmp-key">Legacy Stream Key<input id="rtmpKey" type="hidden" value=""></label>',
);
$("#rtmpKey").parentElement.insertAdjacentHTML(
  "afterend",
  '<label class="rtmp-codec-control">FFmpeg RTMP Video Encoder / Codec<select id="rtmpCodec"><option value="libx264" selected>H.264 FFmpeg CPU — Compatible</option><option value="h264_nvenc">H.264 NVIDIA NVENC GPU</option><option value="h264_qsv">H.264 Intel Quick Sync GPU</option><option value="h264_amf">H.264 AMD AMF GPU</option></select></label><div class="udp-grid"><label>Video Bitrate<input id="rtmpBitrate" value="4500k" placeholder="Example: 4500k"></label><label>GOP / Keyframe<input id="rtmpGop" type="number" value="50"></label></div>',
);
$("#rtmpCodec").parentElement.insertAdjacentHTML(
  "beforebegin",
  '<label>RTMP Network PCI / Ethernet Card<select id="streamNetworkCard"><option value="">Auto Route</option></select></label>',
);
$("#rtmpCodec").parentElement.insertAdjacentHTML(
  "afterend",
  '<div class="udp-grid"><label>RTMP Output Resolution<select id="rtmpOutputRes"><option value="source">Source / Auto</option><option value="576">720 × 576 SD</option><option value="720">1280 × 720 HD</option><option value="1080" selected>1920 × 1080 Full HD</option><option value="2160">3840 × 2160 4K</option></select></label><label>RTMP Audio Codec<select id="rtmpAudioCodec"><option value="aac" selected>AAC — Recommended</option><option value="mp2">MPEG Layer II</option><option value="ac3">AC-3</option><option value="copy">Audio Passthrough</option></select></label></div>',
);
$("#streamNetworkCard").parentElement.insertAdjacentHTML(
  "afterend",
  '<fieldset class="engine-grid"><legend>DECODER / PROCESSOR MODE</legend><label>Decoder — Manual or Auto<select id="decodeEngine"><option value="auto-lowcpu" selected>AUTO — Low CPU Recommended</option><option value="gpu">GPU Auto — Installed Hardware</option><option value="d3d11va">Microsoft D3D11VA</option><option value="dxva2">Microsoft DXVA2</option><option value="cuda">NVIDIA CUDA</option><option value="qsv">Intel Quick Sync</option><option value="cpu">CPU Safe — Maximum Compatibility</option></select></label><label>Encoder — CPU or GPU<select id="encodeEngine"><option value="auto" selected>AUTO — GPU First, CPU Fallback</option><option value="microsoft">Microsoft Media Foundation</option><option value="gpu">GPU — NVIDIA / Intel / AMD</option><option value="cpu">CPU — FFmpeg Safe (2 Threads)</option></select></label><button id="detectCodecEngine" type="button" class="action-cyan">TEST / DETECT DECODER</button><pre id="codecEngineStatus" class="codec-report">Select a video, then press TEST / DETECT.</pre></fieldset>',
);
for (const id of ["rtmpCodec", "udpCodec", "videoCodec"]) {
  const select = $("#" + id);
  if (select && !select.querySelector('[value="h264_mf"]'))
    select.insertAdjacentHTML(
      "beforeend",
      '<option value="h264_mf">H.264 — Microsoft Media Foundation</option><option value="hevc_mf">HEVC — Microsoft Media Foundation</option>',
    );
}
function engineConfig() {
  return {
    decodeEngine: $("#decodeEngine")?.value || "auto-lowcpu",
    encodeEngine: $("#encodeEngine")?.value || "auto",
  };
}
function applyEngineSelection() {
  let mode = $("#encodeEngine").value,
    codec =
      mode === "cpu"
        ? "libx264"
        : mode === "microsoft"
          ? "h264_mf"
          : "h264_nvenc";
  $("#rtmpCodec").value = [...$("#rtmpCodec").options].some(
    (o) => o.value === codec,
  )
    ? codec
    : "libx264";
  $("#udpCodec").value = [...$("#udpCodec").options].some(
    (o) => o.value === codec,
  )
    ? codec
    : "libx264";
  localStorage.setItem("engineConfig", JSON.stringify(engineConfig()));
  window.playoutAPI.setEngineConfig(engineConfig());
}
$("#encodeEngine").onchange = applyEngineSelection;
$("#decodeEngine").onchange = applyEngineSelection;
try {
  let e = JSON.parse(localStorage.getItem("engineConfig") || "{}");
  $("#decodeEngine").value = e.decodeEngine || "auto-lowcpu";
  $("#encodeEngine").value = e.encodeEngine || "auto";
} catch (e) {}
$("#detectCodecEngine").onclick = async () => {
  let i = selected >= 0 ? selected : current,
    status = $("#codecEngineStatus");
  if (i < 0) {
    status.textContent = "Select a playlist video first.";
    return;
  }
  status.textContent = "Testing Microsoft, GPU and FFmpeg decoders...";
  let r = await window.playoutAPI.detectCodecEngine({
    file: list[i],
    ...engineConfig(),
  });
  status.textContent = r.message || r.error || "Decoder test failed";
};
$("#previewDecoder").value =
  $("#decodeEngine").value === "gpu" ? "gpu" : "safe";
$("#previewDecoder").onchange = () => {
  $("#decodeEngine").value = $("#previewDecoder").value;
  applyEngineSelection();
};
$("#udpRes").parentElement.insertAdjacentHTML(
  "afterend",
  '<label>UDP / RTP / SRT Encoder Codec<select id="udpCodec"><option value="h264_nvenc">H.264 NVIDIA NVENC</option><option value="libx264">H.264 CPU</option><option value="hevc_nvenc">H.265 NVIDIA NVENC</option><option value="libx265">H.265 CPU</option><option value="mpeg2video">MPEG-2 Video</option></select></label><label>UDP / RTP / SRT Audio Codec<select id="udpAudioCodec"><option value="aac" selected>AAC</option><option value="mp2">MPEG Layer II</option><option value="ac3">AC-3</option><option value="copy">Audio Passthrough</option></select></label>',
);
$("#udpRes").parentElement.insertAdjacentHTML(
  "beforebegin",
  '<label>Output Protocol<select id="streamProtocol"><option value="udp">UDP MPEG-TS</option><option value="rtp">RTP MPEG-TS</option><option value="srt">SRT MPEG-TS</option></select></label><label id="srtUrlLabel" style="display:none">Complete SRT URL<input id="srtUrl" value="srt://127.0.0.1:9000?mode=caller&amp;latency=200000" placeholder="srt://host:port?mode=caller"></label><label>UDP / RTP / SRT Network PCI / Ethernet Card<select id="udpNetworkCard"><option value="">Auto Route</option></select></label>',
);
$("#streamProtocol").onchange = () => {
  $("#srtUrlLabel").style.display =
    $("#streamProtocol").value === "srt" ? "block" : "none";
};
window.playoutAPI
  .networkInterfaces()
  .then((rows) => {
    let options =
      '<option value="">Auto Route</option>' +
      rows
        .map((x) => `<option value="${x.address}">${x.label}</option>`)
        .join("");
    $("#streamNetworkCard").innerHTML = options;
    $("#udpNetworkCard").innerHTML = options;
    try {
      let u = JSON.parse(localStorage.getItem("udpCfg") || "{}");
      $("#udpNetworkCard").value = u.localAddress || "";
      $("#streamProtocol").value = u.protocol || "udp";
      $("#srtUrl").value = u.srtUrl || $("#srtUrl").value;
      $("#streamProtocol").onchange();
    } catch (e) {}
  })
  .catch(() => {});
$("#udpBitrate").outerHTML =
  '<input id="udpBitrate" value="8M" placeholder="Example: 8M">';
$("#udpTtl").parentElement.insertAdjacentHTML(
  "beforebegin",
  '<label>Buffer Size<input id="udpBuffer" value="16M"></label>',
);
applyEngineSelection();
$('[id="videoPid"]')
  .closest(".udp-grid")
  .insertAdjacentHTML(
    "beforeend",
    '<label>PMT PID<input id="pmtPid" value="4096"></label><label>PCR PID<input id="pcrPid" value="256" readonly title="PCR follows video PID"></label><label>Transport Stream ID<input id="tsId" value="1"></label><label>Original Network ID<input id="networkId" value="1"></label><label>Service Type<select id="serviceType"><option value="digital_tv">Digital TV</option><option value="advanced_codec_digital_hdtv">HD Digital TV</option><option value="hevc_digital_hdtv">HEVC Digital HDTV</option></select></label><label>Mux Rate (bits/s, 0=VBR)<input id="muxRate" value="0"></label><label>GOP<input id="gop" value="50"></label><label>Audio Bitrate<select id="audioBitrate"><option>128k</option><option selected>192k</option><option>256k</option><option>384k</option></select></label><label>PAT Period sec<input id="patPeriod" value="0.1"></label><label>SDT Period sec<input id="sdtPeriod" value="0.5"></label><label>PCR Period ms<input id="pcrPeriod" value="20"></label><label>Provider Name<input id="providerName" value="SR NETWORK"></label><label>Service Name<input id="serviceName" value="SR MUSIX HD"></label>',
  );
$("#videoPid").addEventListener("input", () => {
  $("#pcrPid").value = $("#videoPid").value;
});
$("#videoPid")
  .closest("details")
  .insertAdjacentHTML(
    "afterbegin",
    '<div class="pid-auto-bar"><label><input id="autoPidMode" type="checkbox" checked> Automatic DVB/DVP PID Assignment</label><button id="generatePids" class="action-purple">⚙ GENERATE PIDs</button></div>',
  );
$("#udpBitrate").parentElement.insertAdjacentHTML(
  "beforebegin",
  '<label>Bitrate Mode<select id="bitrateMode"><option value="cbr">CBR — Constant Bitrate</option><option value="vbr">VBR — Variable Bitrate</option></select></label>',
);
function generateBroadcastPids() {
  let service = Math.max(1, Number($("#serviceId").value) || 1),
    base = 256 + (service - 1) * 16;
  $("#videoPid").value = base;
  $("#audioPid").value = base + 1;
  $("#pcrPid").value = base;
  $("#pmtPid").value = 4096 + (service - 1);
  $("#serviceId").value = service;
  $("#tsId").value = Number($("#tsId").value) || 1;
  $("#networkId").value = Number($("#networkId").value) || 1;
}
function applyPidMode() {
  let auto = $("#autoPidMode").checked;
  ["videoPid", "audioPid", "pmtPid", "serviceId", "tsId", "networkId"].forEach(
    (id) => ($("#" + id).readOnly = auto),
  );
  if (auto) generateBroadcastPids();
}
$("#generatePids").onclick = generateBroadcastPids;
$("#autoPidMode").onchange = applyPidMode;
applyPidMode();
document.body.insertAdjacentHTML(
  "beforeend",
  '<dialog class="page-dialog" id="codecDialog"><h2>Media Codec Inspection</h2><pre id="codecReport" class="codec-report"></pre><button onclick="codecDialog.close()">Close</button><button id="applyRecommendedCodec" class="primary">Use Recommended Codec</button></dialog>',
);
document.body.insertAdjacentHTML(
  "beforeend",
  '<dialog id="playlistScheduleDialog"><h3>Schedule Playlist</h3><label>Saved Playlist<select id="scheduledPlaylistName"></select></label><label>Date & Time<input id="scheduledPlaylistTime" type="datetime-local"></label><div><button onclick="playlistScheduleDialog.close()">Cancel</button><button id="confirmPlaylistSchedule" class="primary">Schedule</button></div></dialog>',
);
$("#scheduledPlaylistTime").parentElement.style.display = "none";
$("#scheduledPlaylistName").parentElement.insertAdjacentHTML(
  "afterend",
  '<div class="schedule-date-grid"><label>📅 Program Date<input id="scheduledPlaylistDate" type="date"></label><label>🕒 Program Time<input id="scheduledPlaylistClock" type="time" step="1"></label></div><label>Network / YouTube URL (Optional)<input id="scheduledPlaylistUrl" type="url" spellcheck="false" placeholder="https://... type அல்லது paste செய்யவும்"></label>',
);
$("#scheduledPlaylistUrl").insertAdjacentHTML(
  "afterend",
  '<button type="button" class="paste-url" data-paste-target="scheduledPlaylistUrl">📋 PASTE LINK</button>',
);
let savedPlaylists = JSON.parse(localStorage.getItem("savedPlaylists") || "{}"),
  playlistSchedules = JSON.parse(
    localStorage.getItem("playlistSchedules") || "[]",
  );
let fillerProgram = localStorage.getItem("fillerProgram") || "";
document.body.insertAdjacentHTML(
  "beforeend",
  '<dialog class="page-dialog" id="pagePrograms"><h2>▦ Program Playlist Library</h2><p>Program-க்குள் நேரடி Video Files, Playlist File அல்லது Project File import செய்யலாம்.</p><label>Program Name<input id="programName" placeholder="Example: Morning Songs"></label><div class="rtmp-buttons"><button id="addVideosProgram" class="primary">＋ ADD VIDEO FILES</button><button id="importPlaylistProgram">IMPORT PLAYLIST</button><button id="importProjectProgram">IMPORT PROJECT</button></div><div class="rtmp-buttons"><button id="createProgram" class="primary">SAVE CURRENT AS PROGRAM</button><button id="loadProgram">LOAD PROGRAM</button><button id="deleteProgram">DELETE</button></div><div id="programList" class="manager-list"></div><button onclick="pagePrograms.close()">Close</button></dialog><dialog class="page-dialog" id="pageFiller"><h2>● Automatic Filler</h2><p>Scheduled/Manual program முடிந்ததும் தேர்ந்தெடுத்த filler program automatic loop ஆகும்.</p><label>Default Filler Program<select id="fillerProgramSelect"></select></label><label><input id="fillerEnabled" type="checkbox" checked> Enable automatic filler</label><button id="saveFiller" class="primary">SAVE FILLER</button><button onclick="pageFiller.close()">Close</button></dialog>',
);
$("#fillerProgramSelect").parentElement.insertAdjacentHTML(
  "afterend",
  '<div id="fillerProgramList" class="filler-program-list"></div>',
);
const originalRenderPrograms = renderPrograms;
renderPrograms = function () {
  originalRenderPrograms();
  let names = Object.keys(savedPlaylists),
    box = $("#fillerProgramList"),
    cfg = JSON.parse(localStorage.getItem("weeklyFillerSettings") || "{}"),
    days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  box.innerHTML =
    '<div class="filler-schedule-head"><span>No</span><span>Show</span><span>Filler Name</span><span>Duration</span><span>Playlist</span><span>Remove</span><span>Time</span>' +
    days.map((d) => `<span>${d}</span>`).join("") +
    "</div>" +
    names
      .map((n, i) => {
        let key = encodeURIComponent(n),
          row = cfg[n] || { days: [0, 1, 2, 3, 4, 5, 6], time: "00:00" };
        return `<div class="filler-schedule-row schedule-color-${i % 5}" data-filler-name="${key}"><span>${i + 1}</span><input class="filler-show" type="checkbox" ${n === fillerProgram ? "checked" : ""}><b>${n}</b><span>${savedPlaylists[n].length} files</span><button class="action-cyan filler-show-playlist">SHOW</button><button class="action-red filler-remove">REMOVE</button><input class="filler-time" type="time" value="${row.time || "00:00"}">${days.map((d, di) => `<input class="filler-day" data-day="${di}" type="checkbox" ${row.days?.includes(di) ? "checked" : ""}>`).join("")}</div>`;
      })
      .join("");
  box.querySelectorAll(".filler-schedule-row").forEach((row) => {
    let name = decodeURIComponent(row.dataset.fillerName);
    row.querySelector(".filler-show").onchange = (e) => {
      if (e.target.checked) {
        fillerProgram = name;
        $("#fillerProgramSelect").value = name;
        box.querySelectorAll(".filler-show").forEach((x) => {
          if (x !== e.target) x.checked = false;
        });
      }
    };
    row.querySelector(".filler-show-playlist").onclick = () =>
      editSavedProgram(name);
    row.querySelector(".filler-remove").onclick = () => {
      delete cfg[name];
      if (fillerProgram === name) {
        fillerProgram = "";
        $("#fillerProgramSelect").value = "";
      }
      localStorage.setItem("weeklyFillerSettings", JSON.stringify(cfg));
      renderPrograms();
    };
    let saveRow = () => {
      cfg[name] = {
        time: row.querySelector(".filler-time").value,
        days: [...row.querySelectorAll(".filler-day:checked")].map((x) =>
          Number(x.dataset.day),
        ),
      };
      localStorage.setItem("weeklyFillerSettings", JSON.stringify(cfg));
    };
    row.querySelector(".filler-time").onchange = saveRow;
    row.querySelectorAll(".filler-day").forEach((x) => (x.onchange = saveRow));
  });
};
$("#programList").insertAdjacentHTML(
  "afterend",
  '<section class="program-editor"><h3>PROGRAM PLAYLIST EDITOR</h3><div class="program-edit-toolbar"><button id="programEditUp">↑ UP</button><button id="programEditDown">↓ DOWN</button><button id="programEditInsert">＋ INSERT</button><button id="programEditReplace">EDIT / REPLACE</button><button id="programEditShuffle">SHUFFLE</button><button id="programEditRemove">REMOVE</button><button id="programEditSave" class="primary">SAVE PROGRAM</button></div><div id="programEditList" class="manager-list">Select a program and click EDIT</div></section>',
);
$(".program-editor").insertAdjacentHTML(
  "beforeend",
  '<div class="source-preview program-source-preview"><div><b>SELECTED PROGRAM ITEM PREVIEW</b><button id="previewProgramItem" class="action-cyan">▶ PREVIEW SELECTED</button><button id="stopProgramPreview" class="action-red">■ STOP</button></div><video id="programPreviewPlayer" controls></video></div>',
);
let programWorkspace = document.createElement("div");
programWorkspace.className = "program-library-layout";
$("#programList").parentNode.insertBefore(programWorkspace, $("#programList"));
let programLibraryPane = document.createElement("section");
programLibraryPane.className = "program-project-pane";
programLibraryPane.innerHTML =
  "<h3>SAVED PROJECTS / PROGRAMS</h3><small>Select a project to view and edit its songs</small>";
programWorkspace.appendChild(programLibraryPane);
programLibraryPane.appendChild($("#programList"));
programWorkspace.appendChild($(".program-editor"));
$("#addVideosProgram").parentElement.insertAdjacentHTML(
  "beforeend",
  '<button id="addNetworkProgram">🌐 ADD URL / YOUTUBE</button>',
);
$("#addVideosProgram").parentElement.insertAdjacentHTML(
  "afterend",
  '<div class="network-entry program-url-entry"><label>PROGRAM NETWORK / YOUTUBE URL<input id="programNetworkUrl" type="url" spellcheck="false" placeholder="https://... link-ஐ type அல்லது paste செய்யவும்"></label><button id="addProgramUrlFromBox" class="action-purple">＋ ADD LINK TO PROGRAM</button></div>',
);
$("#programNetworkUrl").insertAdjacentHTML(
  "afterend",
  '<button type="button" class="paste-url" data-paste-target="programNetworkUrl">📋 PASTE LINK</button>',
);
document
  .querySelector('nav button[data-page="schedule"]')
  .insertAdjacentHTML(
    "afterend",
    '<button data-page="filler">● Filler</button>',
  );
let programDraft = [],
  programDraftName = "",
  programDraftSelected = -1;
function renderProgramEditor() {
  programDraftName = $("#programName").value.trim() || programDraftName;
  $("#programEditList").innerHTML = programDraft.length
    ? programDraft
        .map(
          (x, i) =>
            `<div class="program-edit-item ${i === programDraftSelected ? "selected" : ""} ${current >= 0 && x === list[current] ? "program-playing" : ""}" data-program-index="${i}"><span>${i + 1}</span><b>${title(x)}</b><small>${current >= 0 && x === list[current] ? "● ON AIR" : x.split(".").pop().toUpperCase()}</small></div>`,
        )
        .join("")
    : "Program playlist is empty";
  document.querySelectorAll("[data-program-index]").forEach(
    (e) =>
      (e.onclick = () => {
        programDraftSelected = Number(e.dataset.programIndex);
        renderProgramEditor();
        previewSourceIn(
          $("#programPreviewPlayer"),
          programDraft[programDraftSelected],
        );
      }),
  );
}
function editSavedProgram(name) {
  programDraftName = name;
  programDraft = [...(savedPlaylists[name] || [])];
  programDraftSelected = programDraft.length ? 0 : -1;
  $("#programName").value = name;
  renderProgramEditor();
}
function renderPrograms() {
  let names = Object.keys(savedPlaylists),
    html = names
      .map(
        (n, i) =>
          `<div class="program-row ${n === programDraftName ? "selected-project" : ""}" data-program-project="${encodeURIComponent(n)}"><span class="project-number">${i + 1}</span><b>${n}<small>${savedPlaylists[n].length} songs / files</small></b><button data-edit-program="${encodeURIComponent(n)}">EDIT</button><button data-load-program="${encodeURIComponent(n)}">PLAY</button></div>`,
      )
      .join("");
  $("#programList").innerHTML = html || "No saved projects";
  $("#fillerProgramSelect").innerHTML =
    '<option value="">None</option>' +
    names
      .map((n) => `<option value="${n.replace(/"/g, "&quot;")}">${n}</option>`)
      .join("");
  $("#fillerProgramSelect").value = fillerProgram;
  document.querySelectorAll("[data-edit-program]").forEach(
    (b) =>
      (b.onclick = (e) => {
        e.stopPropagation();
        editSavedProgram(decodeURIComponent(b.dataset.editProgram));
      }),
  );
  document.querySelectorAll("[data-load-program]").forEach(
    (b) =>
      (b.onclick = (e) => {
        e.stopPropagation();
        activateProgram(decodeURIComponent(b.dataset.loadProgram), true);
      }),
  );
  document
    .querySelectorAll("[data-program-project]")
    .forEach(
      (row) =>
        (row.onclick = () =>
          editSavedProgram(decodeURIComponent(row.dataset.programProject))),
    );
}
function activateProgram(name, play = false) {
  if (!savedPlaylists[name]) return false;
  activeProgramName = name;
  localStorage.setItem("activeProgramName", name);
  list = [...savedPlaylists[name]];
  current = selected = -1;
  render();
  if (list.length) load(0, play);
  $("#pagePrograms").close();
  return true;
}
$("#createProgram").onclick = () => {
  let name = $("#programName").value.trim();
  if (!name) return alert("Program name தேவை");
  if (!list.length) return alert("Current playlist காலியாக உள்ளது");
  savedPlaylists[name] = [...list];
  localStorage.setItem("savedPlaylists", JSON.stringify(savedPlaylists));
  renderPrograms();
  alert("Program saved: " + name);
};
$("#addVideosProgram").onclick = async () => {
  let name = $("#programName").value.trim();
  if (!name) return alert("முதலில் Program Name எழுதவும்");
  let files = await window.playoutAPI.pickMedia();
  if (!files.length) return;
  savedPlaylists[name] = [...(savedPlaylists[name] || []), ...files];
  localStorage.setItem("savedPlaylists", JSON.stringify(savedPlaylists));
  renderPrograms();
  alert(`${files.length} video files added to ${name}`);
};
function askNetworkUrl() {
  let url = prompt("Network media / HLS / YouTube URL");
  if (!url) return "";
  url = url.trim();
  if (!/^https?:\/\//i.test(url)) {
    alert("Valid http:// அல்லது https:// URL கொடுக்கவும்");
    return "";
  }
  return url;
}
function addProgramNetworkUrl() {
  let name = $("#programName").value.trim();
  if (!name) return alert("முதலில் Program Name எழுதவும்");
  let url = $("#programNetworkUrl").value.trim();
  if (!/^https?:\/\//i.test(url))
    return alert(
      "Network / YouTube URL box-ல் valid link type அல்லது paste செய்யவும்",
    );
  savedPlaylists[name] = [...(savedPlaylists[name] || []), url];
  localStorage.setItem("savedPlaylists", JSON.stringify(savedPlaylists));
  $("#programNetworkUrl").value = "";
  renderPrograms();
}
$("#addNetworkProgram").onclick = () => $("#programNetworkUrl").focus();
$("#addProgramUrlFromBox").onclick = addProgramNetworkUrl;
$("#importPlaylistProgram").onclick = async () => {
  let r = await window.playoutAPI.loadPlaylistFile();
  if (!r.ok) {
    if (!r.canceled) alert(r.message);
    return;
  }
  let name = $("#programName").value.trim() || r.name;
  savedPlaylists[name] = [...(savedPlaylists[name] || []), ...r.items];
  localStorage.setItem("savedPlaylists", JSON.stringify(savedPlaylists));
  $("#programName").value = name;
  renderPrograms();
  alert(`Playlist imported: ${name}`);
};
$("#importProjectProgram").onclick = async () => {
  let r = await window.playoutAPI.loadProjectFile();
  if (!r.ok) {
    if (!r.canceled) alert(r.message);
    return;
  }
  let state = r.data?.state || {},
    count = 0;
  try {
    let programs = JSON.parse(state.savedPlaylists || "{}");
    Object.entries(programs).forEach(([n, items]) => {
      if (Array.isArray(items)) {
        savedPlaylists[n] = [...(savedPlaylists[n] || []), ...items];
        count += items.length;
      }
    });
  } catch {}
  try {
    let items = JSON.parse(state.playlist || "[]");
    if (Array.isArray(items) && items.length) {
      let name = $("#programName").value.trim() || "Imported Project Playlist";
      savedPlaylists[name] = [...(savedPlaylists[name] || []), ...items];
      count += items.length;
    }
  } catch {}
  localStorage.setItem("savedPlaylists", JSON.stringify(savedPlaylists));
  renderPrograms();
  alert(`${count} media items imported from project`);
};
$("#loadProgram").onclick = () => {
  let name = $("#programName").value.trim();
  if (!activateProgram(name, false)) alert("Program கிடைக்கவில்லை");
};
$("#deleteProgram").onclick = () => {
  let name = $("#programName").value.trim();
  if (!savedPlaylists[name]) return;
  delete savedPlaylists[name];
  localStorage.setItem("savedPlaylists", JSON.stringify(savedPlaylists));
  if (fillerProgram === name) {
    fillerProgram = "";
    localStorage.removeItem("fillerProgram");
  }
  renderPrograms();
};
$("#programEditUp").onclick = () => {
  if (programDraftSelected > 0) {
    [
      programDraft[programDraftSelected - 1],
      programDraft[programDraftSelected],
    ] = [
      programDraft[programDraftSelected],
      programDraft[programDraftSelected - 1],
    ];
    programDraftSelected--;
    renderProgramEditor();
  }
};
$("#programEditDown").onclick = () => {
  if (
    programDraftSelected >= 0 &&
    programDraftSelected < programDraft.length - 1
  ) {
    [
      programDraft[programDraftSelected + 1],
      programDraft[programDraftSelected],
    ] = [
      programDraft[programDraftSelected],
      programDraft[programDraftSelected + 1],
    ];
    programDraftSelected++;
    renderProgramEditor();
  }
};
$("#programEditInsert").onclick = async () => {
  let files = await window.playoutAPI.pickMedia();
  if (!files.length) return;
  let at =
    programDraftSelected < 0 ? programDraft.length : programDraftSelected + 1;
  programDraft.splice(at, 0, ...files);
  programDraftSelected = at;
  renderProgramEditor();
};
$("#programEditReplace").onclick = async () => {
  if (programDraftSelected < 0) return alert("Program item தேர்வு செய்யவும்");
  let files = await window.playoutAPI.pickMedia();
  if (files.length) {
    programDraft.splice(programDraftSelected, 1, ...files);
    renderProgramEditor();
  }
};
$("#programEditShuffle").onclick = () => {
  for (let i = programDraft.length - 1; i > 0; i--) {
    let j = Math.floor(Math.random() * (i + 1));
    [programDraft[i], programDraft[j]] = [programDraft[j], programDraft[i]];
  }
  programDraftSelected = programDraft.length ? 0 : -1;
  renderProgramEditor();
};
$("#programEditRemove").onclick = () => {
  if (programDraftSelected < 0) return;
  programDraft.splice(programDraftSelected, 1);
  programDraftSelected = Math.min(
    programDraftSelected,
    programDraft.length - 1,
  );
  renderProgramEditor();
};
$("#programEditSave").onclick = () => {
  let name = $("#programName").value.trim() || programDraftName;
  if (!name) return alert("Program name தேவை");
  savedPlaylists[name] = [...programDraft];
  localStorage.setItem("savedPlaylists", JSON.stringify(savedPlaylists));
  programDraftName = name;
  renderPrograms();
  alert("Program playlist updated: " + name);
};
$("#saveFiller").onclick = () => {
  fillerProgram = $("#fillerProgramSelect").value;
  localStorage.setItem("fillerProgram", fillerProgram);
  localStorage.setItem(
    "fillerEnabled",
    $("#fillerEnabled").checked ? "1" : "0",
  );
  $("#pageFiller").close();
};
$("#fillerEnabled").checked = localStorage.getItem("fillerEnabled") !== "0";
renderPrograms();
let lastWeeklyFillerSlot = "";
setInterval(() => {
  if (localStorage.getItem("fillerEnabled") === "0") return;
  let now = new Date(),
    day = now.getDay(),
    time = now.toTimeString().slice(0, 5),
    slot = `${now.toDateString()}-${time}`,
    cfg = JSON.parse(localStorage.getItem("weeklyFillerSettings") || "{}"),
    name = Object.keys(cfg).find(
      (n) =>
        cfg[n]?.time === time &&
        cfg[n]?.days?.includes(day) &&
        savedPlaylists[n],
    );
  if (name && slot !== lastWeeklyFillerSlot) {
    lastWeeklyFillerSlot = slot;
    fillerProgram = name;
    localStorage.setItem("fillerProgram", name);
    activateProgram(name, true);
    setAirState(true);
  }
}, 15000);
function refreshPlaylistSchedules() {
  let box = $("#scheduleList"),
    programSchedules = JSON.parse(
      localStorage.getItem("scheduleItems") || "[]",
    ),
    rows = [
      ...playlistSchedules.map((s) => ({
        source: "playlist",
        ...s,
        title: `PLAYLIST: ${s.name}`,
        detail: s.url || s.name,
      })),
      ...programSchedules.map((s) => ({
        source: "program",
        ...s,
        title: `${s.type || "PROGRAM"}: ${s.title}`,
        detail: s.program || s.url || "Current Playlist",
      })),
    ];
  if (!rows.length) {
    box.textContent = "No schedules";
    return;
  }
  box.innerHTML = rows
    .sort((a, b) => new Date(a.when) - new Date(b.when))
    .map(
      (s) =>
        `<div class="schedule-manage-row"><div><b>${s.title}</b><span>${new Date(s.when).toLocaleString()} • ${s.fired ? "PLAYED" : "WAITING"}</span><small>${s.detail}</small></div><button class="action-red" data-remove-schedule="${s.source}:${s.id}">REMOVE</button></div>`,
    )
    .join("");
  box.querySelectorAll("[data-remove-schedule]").forEach(
    (b) =>
      (b.onclick = () => {
        let [source, id] = b.dataset.removeSchedule.split(":");
        if (source === "playlist") {
          playlistSchedules = playlistSchedules.filter(
            (s) => String(s.id) !== id,
          );
          localStorage.setItem(
            "playlistSchedules",
            JSON.stringify(playlistSchedules),
          );
        } else {
          let items = JSON.parse(
            localStorage.getItem("scheduleItems") || "[]",
          ).filter((s) => String(s.id) !== id);
          localStorage.setItem("scheduleItems", JSON.stringify(items));
        }
        refreshPlaylistSchedules();
      }),
  );
}
$("#savePlaylist").onclick = async () => {
  if (!list.length) return alert("Playlist காலியாக உள்ளது");
  let r = await window.playoutAPI.savePlaylistFile(list);
  if (!r.ok) {
    if (!r.canceled) alert("Playlist save error: " + r.message);
    return;
  }
  savedPlaylists[r.name] = [...list];
  localStorage.setItem("savedPlaylists", JSON.stringify(savedPlaylists));
  alert("Playlist saved: " + r.path);
};
$("#loadPlaylist").onclick = async () => {
  let r = await window.playoutAPI.loadPlaylistFile();
  if (!r.ok) {
    if (!r.canceled) alert("Playlist open error: " + r.message);
    return;
  }
  list = [...r.items];
  savedPlaylists[r.name] = [...list];
  localStorage.setItem("savedPlaylists", JSON.stringify(savedPlaylists));
  current = selected = -1;
  render();
  if (list.length) load(0);
};
let lastProbe = null;
$("#probeMedia").onclick = async () => {
  let i = selected >= 0 ? selected : current;
  if (i < 0) return alert("Playlist-ல் file தேர்வு செய்யவும்");
  $("#codecReport").textContent = "Checking with FFprobe...";
  $("#codecDialog").showModal();
  let r = await window.playoutAPI.probeMedia(list[i]);
  lastProbe = r;
  if (!r.ok) {
    $("#codecReport").textContent = "ERROR: " + r.error;
    return;
  }
  let v = r.video,
    a = r.audio,
    f = r.format || {};
  $("#codecReport").textContent =
    `FILE: ${list[i]}\nCONTAINER: ${f.format_name || "Unknown"}\nDURATION: ${f.duration || "—"} sec\n\nVIDEO: ${v ? v.codec_long_name + " (" + v.codec_name + ")" : "NO VIDEO STREAM"}\nRESOLUTION: ${v ? v.width + "x" + v.height : "—"}\nFRAME RATE: ${v ? v.r_frame_rate : "—"}\nPIXEL FORMAT: ${v ? v.pix_fmt : "—"}\nFIELD ORDER: ${v ? v.field_order : "—"}\n\nAUDIO: ${a ? a.codec_long_name + " (" + a.codec_name + ")" : "NO AUDIO STREAM"}\n\nPREVIEW: ${r.embeddedPreviewSupported ? "Embedded preview supported" : "Use FFplay preview"}\nRECOMMENDATION: ${r.recommendation}`;
};
$("#applyRecommendedCodec").onclick = () => {
  if (!lastProbe || !lastProbe.video) return;
  videoCodec.value =
    lastProbe.video.codec_name === "hevc" ? "libx265" : "libx264";
  $("#codecDialog").close();
  $("#pageSettings").showModal();
};
$("#openFolder").onclick = () => {
  let i = selected >= 0 ? selected : current;
  if (i < 0) return alert("File தேர்வு செய்யவும்");
  window.playoutAPI.openMediaFolder(list[i]);
};
$("#schedulePlaylist").onclick = () => {
  let names = Object.keys(savedPlaylists);
  if (!names.length) return alert("முதலில் playlist save செய்யவும்");
  $("#scheduledPlaylistName").innerHTML = names
    .map((n) => `<option>${n}</option>`)
    .join("");
  $("#playlistScheduleDialog").showModal();
};
$("#confirmPlaylistSchedule").onclick = () => {
  let name = $("#scheduledPlaylistName").value,
    date = $("#scheduledPlaylistDate").value,
    time = $("#scheduledPlaylistClock").value,
    url = $("#scheduledPlaylistUrl").value.trim(),
    when = date && time ? `${date}T${time}` : "";
  if (!when) return alert("Program Date மற்றும் Time select செய்யவும்");
  if (url && !/^https?:\/\//i.test(url))
    return alert("Network / YouTube URL சரியாக இல்லை");
  playlistSchedules.push({
    id: Date.now(),
    name,
    when: new Date(when).toISOString(),
    url,
    fired: false,
  });
  localStorage.setItem("playlistSchedules", JSON.stringify(playlistSchedules));
  refreshPlaylistSchedules();
  $("#playlistScheduleDialog").close();
};
setInterval(() => {
  let changed = false,
    now = Date.now();
  playlistSchedules.forEach((s) => {
    if (
      !s.fired &&
      now >= new Date(s.when).getTime() &&
      (s.url || savedPlaylists[s.name])
    ) {
      list = s.url ? [s.url] : [...savedPlaylists[s.name]];
      current = selected = -1;
      render();
      load(0, true);
      setAirState(true);
      s.fired = true;
      changed = true;
    }
  });
  if (changed) {
    localStorage.setItem(
      "playlistSchedules",
      JSON.stringify(playlistSchedules),
    );
    refreshPlaylistSchedules();
  }
  let items = JSON.parse(localStorage.getItem("scheduleItems") || "[]"),
    dirty = false;
  items.forEach((s) => {
    if (!s.fired && now >= new Date(s.when).getTime()) {
      if (s.url) list = [s.url];
      else if (s.program && savedPlaylists[s.program])
        list = [...savedPlaylists[s.program]];
      if (list.length) {
        current = selected = -1;
        render();
        load(0, true);
        setAirState(true);
      }
      s.fired = true;
      dirty = true;
    }
  });
  if (dirty) localStorage.setItem("scheduleItems", JSON.stringify(items));
}, 1000);
refreshPlaylistSchedules();
function fmt(n) {
  if (!isFinite(n)) return "00:00";
  return (
    String(Math.floor(n / 60)).padStart(2, "0") +
    ":" +
    String(Math.floor(n % 60)).padStart(2, "0")
  );
}
setInterval(
  () =>
    ($("#clock").textContent = new Date().toLocaleString("en-IN", {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
      hour12: true,
    })),
  500,
);
function title(p) {
  return p
    .split(/[\\/]/)
    .pop()
    .replace(/\.[^.]+$/, "");
}
const durationCache = {};
async function updateStoryboard() {
  let now = current >= 0 ? list[current] : null,
    next =
      current + 1 < list.length
        ? list[current + 1]
        : $("#loop").checked
          ? list[0]
          : null;
  $("#storyNowTitle").textContent = now ? title(now) : "No media loaded";
  $("#storyNextTitle").textContent = next ? title(next) : "—";
  $("#storyNowGroup").textContent = `PROGRAM: ${activeProgramName}`;
  $("#storyNextGroup").textContent = `PROGRAM: ${activeProgramName}`;
  if (next && !durationCache[next]) {
    let source = next;
    if (/^https?:\/\//i.test(next)) {
      let r = await window.playoutAPI.resolveNetworkMedia(next);
      if (r.ok) source = r.url;
    }
    let p = await window.playoutAPI.probeMedia(source);
    durationCache[next] = Number(p?.format?.duration) || 0;
  }
  $("#storyNextTiming").textContent =
    `DURATION ${fmt(durationCache[next] || 0)}`;
}
function render() {
  const p = $("#playlist");
  $("#count").textContent = `${list.length} items`;
  p.innerHTML = list.length
    ? ""
    : `<div class="empty">Media files சேர்க்க “ADD MEDIA” அழுத்தவும்</div>`;
  list.forEach((x, i) => {
    let d = document.createElement("div"),
      group = mediaGroups[x] || "—";
    d.className =
      "item" +
      (i === selected ? " selected" : "") +
      (i === current ? " playing" : "");
    d.innerHTML = `<span>${i + 1}</span><b>${title(x)}<small class="item-group">${group}</small></b><span>${x.split(".").pop().toUpperCase()}</span><span>${i === current ? "ON AIR" : "READY"}</span>`;
    d.onclick = () => {
      selected = i;
      render();
    };
    d.ondblclick = () => previewPlaylistItem(i);
    d.oncontextmenu = (e) => {
      e.preventDefault();
      selected = i;
      render();
      let m = $("#playlistContext");
      m.style.left = Math.min(e.clientX, window.innerWidth - 210) + "px";
      m.style.top = Math.min(e.clientY, window.innerHeight - 430) + "px";
      m.classList.add("show");
    };
    p.appendChild(d);
  });
  localStorage.setItem("playlist", JSON.stringify(list));
  updateNow();
}
function updateNow() {
  $("#nowTitle").textContent =
    current >= 0 ? title(list[current]) : "No media loaded";
  let n =
    current + 1 < list.length
      ? list[current + 1]
      : $("#loop").checked
        ? list[0]
        : null;
  $("#nextTitle").textContent = "NEXT: " + (n ? title(n) : "—");
  if ($("#activeProgramDisplay"))
    $("#activeProgramDisplay").textContent = activeProgramName;
  if ($("#activeSongDisplay"))
    $("#activeSongDisplay").textContent =
      current >= 0 ? title(list[current]) : "No song on air";
  if ($("#frontProgramName"))
    $("#frontProgramName").textContent = activeProgramName;
  if ($("#frontProgramFileCount"))
    $("#frontProgramFileCount").textContent = String(list.length);
  updateStoryboard();
}
async function load(i, play = false, startAt = null) {
  if (i < 0 || i >= list.length) return;
  await window.playoutAPI.stopPreview();
  current = i;
  usingCompat = false;
  let original = list[i],
    network = /^https?:\/\//i.test(original),
    playable = original;
  if (network) {
    let rr = await window.playoutAPI.resolveNetworkMedia(original);
    if (!rr.ok) {
      alert(rr.error);
      render();
      return;
    }
    playable = rr.url;
  }
  let probe = await window.playoutAPI.probeMedia(playable);
  mediaDuration = Number(probe?.format?.duration) || 0;
  let mark = selectedMark(),
    begin =
      startAt == null ? Number(mark.in || 0) : Math.max(0, Number(startAt)),
    end = Number(mark.out || 0);
  previewOffset = begin;
  let ext = original.split("?")[0].split(".").pop().toLowerCase(),
    legacy = [
      "vob",
      "vop",
      "mpg",
      "mpeg",
      "mpe",
      "dat",
      "ts",
      "mts",
      "m2ts",
      "m2p",
      "m2b",
      "m2v",
      "m4v",
      "mxf",
      "avi",
      "wmv",
      "asf",
      "divx",
      "flv",
      "3gp",
      "ogv",
      "b80",
      "bop",
    ];
  if (network || legacy.includes(ext) || probe?.embeddedPreviewSupported === false) {
    video.pause();
    video.removeAttribute("src");
    if (play) {
      let r = await window.playoutAPI.startCompatPreview({
        file: playable,
        startAt: begin,
        endAt: end,
        ...engineConfig(),
      });
      if (!r.ok) {
        alert(
          "FFmpeg compatibility preview error: " +
            (r.error || "Install Full FFmpeg from Settings"),
        );
        render();
        return;
      }
      usingCompat = true;
      video.src = r.url;
      video.load();
      ensureAudioGraph();
      applyPreviewCorrections();
      await video.play().catch(() => {});
    }
    updateMarkStatus();
    render();
    if (play && (rtmpLive || udpLive)) await refreshLiveOutputs();
    return;
  }
  previewOffset = 0;
  video.src = "file:///" + original.replace(/\\/g, "/");
  video.load();
  ensureAudioGraph();
  applyPreviewCorrections();
  await new Promise((resolve) => {
    if (video.readyState >= 1) return resolve();
    video.addEventListener("loadedmetadata", resolve, { once: true });
    setTimeout(resolve, 1500);
  });
  try {
    video.currentTime = begin;
  } catch {}
  if (play) await video.play().catch(() => {});
  updateMarkStatus();
  render();
  if (play && (rtmpLive || udpLive)) await refreshLiveOutputs();
}
async function seekTo(seconds) {
  if (current < 0) return;
  let target = Math.max(
    Number(selectedMark().in || 0),
    Math.min(
      Number(selectedMark().out || mediaDuration || seconds),
      Number(seconds) || 0,
    ),
  );
  if (usingCompat) await load(current, !video.paused, target);
  else video.currentTime = target;
}
const opaqueMediaLoad = load;
load = async function (...args) {
  $(".screen").classList.add("media-active");
  let result = await opaqueMediaLoad(...args);
  applyAspect();
  return result;
};
video.onerror = async () => {
  if (current >= 0 && !usingCompat) {
    let r = await window.playoutAPI.startCompatPreview({ file: list[current] });
    if (r.ok) {
      usingCompat = true;
      video.src = r.url;
      video.load();
      applyPreviewCorrections();
      await video.play().catch(() => {});
      $("#nowTitle").textContent =
        title(list[current]) + " • FFmpeg compatibility";
    }
  }
};
$("#add").onclick = async () => {
  const f = await window.playoutAPI.pickMedia();
  list.push(...f);
  render();
  if (current < 0 && list.length) load(0);
};
function addFrontNetworkUrl() {
  let url = $("#networkUrlInput").value.trim();
  if (!/^https?:\/\//i.test(url))
    return alert(
      "Network / YouTube URL box-ல் valid link type அல்லது paste செய்யவும்",
    );
  list.push(url);
  $("#networkUrlInput").value = "";
  render();
  if (current < 0) load(0);
}
$("#addNetwork").onclick = () => $("#networkUrlInput").focus();
$("#addNetworkFromBox").onclick = addFrontNetworkUrl;
$("#networkUrlInput").addEventListener("keydown", (e) => {
  if (e.key === "Enter") addFrontNetworkUrl();
});
$("#installYouTube").onclick = async () => {
  let r = await window.playoutAPI.installYtDlp();
  alert(r.message);
};
async function previewSourceIn(player, source) {
  if (!source) return alert("Select a media item or enter a URL first.");
  let network = /^https?:\/\//i.test(source),
    youtube = /(youtube\.com|youtu\.be)/i.test(source),
    playable = source,
    status =
      player.id === "networkPreviewPlayer" ? $("#networkPreviewStatus") : null;
  if (status) status.textContent = "Resolving media link…";
  if (network) {
    let resolved = await window.playoutAPI.resolveNetworkMedia(source);
    if (!resolved.ok) {
      if (status) status.textContent = "Unable to resolve URL";
      return alert(resolved.error);
    }
    playable = resolved.url;
  }
  if (youtube) {
    if (status) status.textContent = "Starting YouTube compatibility preview…";
    let r = await window.playoutAPI.startCompatPreview({
      ...engineConfig(),
      file: playable,
    });
    if (!r.ok) {
      if (status)
        status.textContent = "YouTube preview failed — check FFmpeg / yt-dlp";
      return alert(r.error || "YouTube preview failed");
    }
    player.src = r.url;
    player.load();
    await player.play().catch(() => {});
    if (status) status.textContent = "YouTube preview playing";
    return;
  }
  player.src = network ? playable : "file:///" + source.replace(/\\/g, "/");
  player.load();
  let fallback = async () => {
    let r = await window.playoutAPI.startCompatPreview({
      ...engineConfig(),
      file: playable,
    });
    if (r.ok) {
      player.src = r.url;
      player.load();
      await player.play().catch(() => {});
      if (status) status.textContent = "FFmpeg compatibility preview playing";
    } else if (status) status.textContent = "Preview failed";
  };
  player.onerror = fallback;
  await player
    .play()
    .then(() => {
      if (status) status.textContent = "Preview playing";
    })
    .catch(fallback);
}
$("#previewNetworkUrl").onclick = () =>
  previewSourceIn(
    $("#networkPreviewPlayer"),
    $("#networkUrlInput").value.trim(),
  );
$("#stopNetworkPreview").onclick = () => {
  $("#networkPreviewPlayer").pause();
  $("#networkPreviewPlayer").removeAttribute("src");
  $("#networkPreviewStatus").textContent = "Preview stopped";
};
$("#toggleNetworkPreview").onclick = () => {
  $("#networkPreviewDock").classList.toggle("dock-minimized");
  $("#toggleNetworkPreview").textContent = $(
    "#networkPreviewDock",
  ).classList.contains("dock-minimized")
    ? "□"
    : "—";
};
$("#previewProgramItem").onclick = () =>
  previewSourceIn(
    $("#programPreviewPlayer"),
    programDraft[programDraftSelected],
  );
$("#stopProgramPreview").onclick = () => {
  $("#programPreviewPlayer").pause();
  $("#programPreviewPlayer").removeAttribute("src");
};
async function previewPlaylistItem(i) {
  if (i < 0 || i >= list.length) return;
  $("#playlistPreviewStatus").textContent = `Previewing: ${title(list[i])}`;
  await previewSourceIn($("#playlistPreviewPlayer"), list[i]);
}
$("#stopPlaylistPreview").onclick = () => {
  let p = $("#playlistPreviewPlayer");
  p.pause();
  p.removeAttribute("src");
  p.load();
  $("#playlistPreviewStatus").textContent =
    "Preview stopped — On-Air playback was not changed";
};
$("#remove").onclick = () => {
  if (selected >= 0) {
    list.splice(selected, 1);
    selected = -1;
    if (current >= list.length) current = list.length - 1;
    render();
  }
};
$("#clear").onclick = () => {
  video.pause();
  video.removeAttribute("src");
  list = [];
  selected = current = -1;
  render();
};
$("#clear").addEventListener("click", () =>
  $(".screen").classList.remove("media-active"),
);
$("#up").onclick = () => {
  if (selected > 0) {
    [list[selected - 1], list[selected]] = [list[selected], list[selected - 1]];
    selected--;
    render();
  }
};
$("#down").onclick = () => {
  if (selected >= 0 && selected < list.length - 1) {
    [list[selected + 1], list[selected]] = [list[selected], list[selected + 1]];
    selected++;
    render();
  }
};
async function insertMedia() {
  let f = await window.playoutAPI.pickMedia();
  if (!f.length) return;
  let at = selected >= 0 ? selected + 1 : list.length;
  list.splice(at, 0, ...f);
  selected = at;
  render();
}
async function editMedia() {
  if (selected < 0) return alert("முதலில் song select செய்யவும்");
  let f = await window.playoutAPI.pickMedia();
  if (!f.length) return;
  let old = list[selected];
  list[selected] = f[0];
  if (mediaGroups[old]) mediaGroups[f[0]] = mediaGroups[old];
  render();
}
function setSelectedGroup() {
  if (selected < 0) return alert("Song select செய்யவும்");
  let g = prompt("Group name", mediaGroups[list[selected]] || "");
  if (g === null) return;
  if (g.trim()) mediaGroups[list[selected]] = g.trim();
  else delete mediaGroups[list[selected]];
  localStorage.setItem("mediaGroups", JSON.stringify(mediaGroups));
  render();
}
function shuffleList() {
  for (let i = list.length - 1; i > 0; i--) {
    let j = Math.floor(Math.random() * (i + 1));
    [list[i], list[j]] = [list[j], list[i]];
  }
  selected = -1;
  render();
}
function projectState() {
  return {
    state: Object.fromEntries(
      Array.from({ length: localStorage.length }, (_, i) => {
        let k = localStorage.key(i);
        return [k, localStorage.getItem(k)];
      }),
    ),
  };
}
$("#insertMedia").onclick = insertMedia;
$("#editMedia").onclick = editMedia;
$("#setGroup").onclick = setSelectedGroup;
$("#shufflePlaylist").onclick = shuffleList;
$("#saveProject").onclick = async () => {
  let r = await window.playoutAPI.saveProjectFile(projectState());
  if (!r.ok && !r.canceled) alert(r.message);
  else if (r.ok) alert("Project saved: " + r.path);
};
$("#openProject").onclick = async () => {
  let r = await window.playoutAPI.loadProjectFile();
  if (!r.ok) {
    if (!r.canceled) alert(r.message);
    return;
  }
  if (!r.data?.state) return alert("Invalid project");
  localStorage.clear();
  Object.entries(r.data.state).forEach(([k, v]) => localStorage.setItem(k, v));
  location.reload();
};
document.addEventListener("click", () =>
  $("#playlistContext").classList.remove("show"),
);
$("#playlistContext").onclick = async (e) => {
  e.stopPropagation();
  let c = e.target.dataset.cmd;
  if (!c) return;
  if (c === "play" && selected >= 0) load(selected, true);
  if (c === "next" && selected >= 0) {
    let [x] = list.splice(selected, 1),
      at = Math.min(current + 1, list.length);
    list.splice(at, 0, x);
    selected = at;
    render();
  }
  if (c === "insert") await insertMedia();
  if (c === "edit") await editMedia();
  if (c === "folder") $("#openFolder").click();
  if (c === "group") setSelectedGroup();
  if (c === "shuffle") shuffleList();
  if (c === "clearGroup" && selected >= 0) {
    delete mediaGroups[list[selected]];
    localStorage.setItem("mediaGroups", JSON.stringify(mediaGroups));
    render();
  }
  if (c === "remove") $("#remove").click();
  if (c === "up") $("#up").click();
  if (c === "down") $("#down").click();
  if (c === "savePlaylist") $("#savePlaylist").click();
  if (c === "saveProject") $("#saveProject").click();
  $("#playlistContext").classList.remove("show");
};
$("#play").onclick = () => {
  ensureAudioGraph();
  if (current < 0 && list.length) load(0, true);
  else if (!video.src) load(current, true);
  else if (video.paused) video.play();
  else video.pause();
};
video.onplay = () => ($("#play").textContent = "❚❚ PAUSE");
video.onpause = () => ($("#play").textContent = "▶ PLAY");
$("#stop").onclick = () => {
  video.pause();
  if (!usingCompat) video.currentTime = 0;
  else window.playoutAPI.stopPreview();
};
$("#next").onclick = () => {
  if (current + 1 < list.length) load(current + 1, true);
  else if ($("#loop").checked) load(0, true);
  else if (
    localStorage.getItem("fillerEnabled") !== "0" &&
    fillerProgram &&
    savedPlaylists[fillerProgram]
  )
    activateProgram(fillerProgram, true);
};
$("#prev").onclick = () => {
  if (current > 0) load(current - 1, true);
};
const preloadVideo = document.createElement("video");
preloadVideo.preload = "auto";
preloadVideo.muted = true;
let preloadedIndex = -1;
function prepareNextMedia() {
  let ni =
    current + 1 < list.length ? current + 1 : $("#loop").checked ? 0 : -1;
  if (ni < 0 || ni === preloadedIndex || /^https?:\/\//i.test(list[ni])) return;
  let ext = list[ni].split(".").pop().toLowerCase();
  if (
    [
      "vob",
      "mpg",
      "mpeg",
      "mpe",
      "dat",
      "ts",
      "mts",
      "m2ts",
      "m2p",
      "m2v",
      "m4v",
      "mxf",
    ].includes(ext)
  )
    return;
  preloadVideo.src = "file:///" + list[ni].replace(/\\/g, "/");
  preloadVideo.load();
  preloadedIndex = ni;
}
video.ontimeupdate = () => {
  let now = playhead(),
    end = Number(selectedMark().out || mediaDuration || video.duration || 0);
  $("#elapsed").textContent = fmt(now);
  $("#duration").textContent = fmt(end);
  $("#seek").value = end ? (now / end) * 100 : 0;
  $("#storyNowTiming").textContent =
    `${fmt(now)} / ${fmt(end)} • LEFT ${fmt(Math.max(0, end - now))}`;
  if (current >= 0) durationCache[list[current]] = end;
  if (end - now < 12) prepareNextMedia();
};
let seekTimer = 0;
$("#seek").oninput = (e) => {
  let end = mediaDuration || video.duration || 0,
    target = (end * Number(e.target.value)) / 100;
  $("#elapsed").textContent = fmt(target);
  $("#storyNowTiming").textContent = `SEEK ${fmt(target)} / ${fmt(end)}`;
  clearTimeout(seekTimer);
  seekTimer = setTimeout(() => seekTo(target), 120);
};
video.onended = async () => {
  if (!$("#autoPlay").checked) return;
  let ni =
    current + 1 < list.length ? current + 1 : $("#loop").checked ? 0 : -1;
  if (ni < 0) return;
  if (ni === preloadedIndex && preloadVideo.readyState >= 2) {
    current = ni;
    selected = ni;
    video.src = preloadVideo.src;
    video.load();
    await video.play().catch(() => {});
    preloadedIndex = -1;
    render();
    prepareNextMedia();
  } else $("#next").click();
};
$("#logoToggle").onchange = (e) =>
  ($("#logo").style.display = e.target.checked ? "block" : "none");
$("#nowToggle").onchange = (e) =>
  ($("#now").style.display = e.target.checked ? "block" : "none");
$("#tickerToggle").onchange = (e) =>
  ($("#ticker").style.display = e.target.checked ? "block" : "none");
$("#updateTicker").onclick = async () => {
  $("#tickerText").textContent = $("#tickerInput").value;
  await refreshLiveOutputs();
};
$("#fullscreen").onclick = () => $(".screen").requestFullscreen();
function renderChannels() {
  let c = $("#channels");
  c.innerHTML = "";
  channels.forEach((x, i) => {
    let d = document.createElement("div");
    d.className = "channel" + (i === 0 ? " active" : "");
    d.innerHTML = `<b>${x}</b><i>● READY</i>`;
    d.onclick = () => {
      document
        .querySelectorAll(".channel")
        .forEach((x) => x.classList.remove("active"));
      d.classList.add("active");
    };
    c.appendChild(d);
  });
}
$("#addChannel").onclick = () => {
  let n = prompt("Channel name");
  if (n) {
    channels.push(n);
    renderChannels();
  }
};
$("#scheduleDialog").classList.add("schedule-editor-dialog");
$("#schTime").parentElement.style.display = "none";
$("#schTitle").parentElement.insertAdjacentHTML(
  "afterend",
  '<div class="schedule-date-grid"><label>Schedule Date<input id="schDate" type="date"></label><label>Schedule Time<input id="schClock" type="time" step="1"></label></div><label>Program / Playlist<select id="schProgram"><option value="">Current Playlist</option></select></label><label>Network / YouTube URL (Optional)<input id="schNetworkUrl" type="url" spellcheck="false" placeholder="Type or paste https:// link"></label>',
);
$("#schNetworkUrl").insertAdjacentHTML(
  "afterend",
  '<button type="button" class="paste-url" data-paste-target="schNetworkUrl">📋 PASTE LINK</button>',
);
function openScheduleDialog() {
  let now = new Date(Date.now() - new Date().getTimezoneOffset() * 60000),
    iso = now.toISOString();
  $("#schDate").disabled = false;
  $("#schClock").disabled = false;
  $("#schProgram").disabled = false;
  $("#schNetworkUrl").disabled = false;
  $("#schDate").value = $("#schDate").value || iso.slice(0, 10);
  $("#schClock").value = $("#schClock").value || iso.slice(11, 19);
  $("#schProgram").innerHTML =
    '<option value="">Current Playlist</option>' +
    Object.keys(savedPlaylists)
      .map((n) => `<option value="${n.replace(/"/g, "&quot;")}">${n}</option>`)
      .join("");
  $("#scheduleDialog").showModal();
  setTimeout(() => $("#schDate").focus(), 50);
}
$("#scheduleBtn").onclick = () => {
  editingSchedule = null;
  openScheduleDialog();
};
$("#saveSchedule").onclick = () => {
  let t = $("#schTitle").value || $("#schProgram").value || "Scheduled item",
    date = $("#schDate").value,
    time = $("#schClock").value,
    type = $("#schType").value,
    url = $("#schNetworkUrl").value.trim(),
    when = date && time ? `${date}T${time}` : "";
  if (!when) return alert("Select both Schedule Date and Time");
  if (url && !/^https?:\/\//i.test(url))
    return alert("Enter a valid Schedule URL");
  let entry = {
      id:
        editingSchedule?.source === "program"
          ? Number(editingSchedule.id)
          : Date.now(),
      title: t,
      when: new Date(when).toISOString(),
      type,
      program: $("#schProgram").value,
      url,
    },
    schedules = JSON.parse(localStorage.getItem("scheduleItems") || "[]");
  if (editingSchedule?.source === "program")
    schedules = schedules.map((s) =>
      String(s.id) === editingSchedule.id ? entry : s,
    );
  else schedules.push(entry);
  editingSchedule = null;
  localStorage.setItem("scheduleItems", JSON.stringify(schedules));
  $(".schedule").innerHTML =
    `<b>${t}</b><span>${new Date(entry.when).toLocaleString()}</span>`;
  refreshPlaylistSchedules();
  $("#scheduleDialog").close();
};
refreshPlaylistSchedules = function () {
  let box = $("#scheduleList"),
    programSchedules = JSON.parse(
      localStorage.getItem("scheduleItems") || "[]",
    ),
    rows = [
      ...playlistSchedules.map((s) => ({
        source: "playlist",
        ...s,
        title: `PLAYLIST: ${s.name}`,
        detail: s.url || s.name,
      })),
      ...programSchedules.map((s) => ({
        source: "program",
        ...s,
        title: `${s.type || "PROGRAM"}: ${s.title}`,
        detail: s.program || s.url || "Current Playlist",
      })),
    ];
  if (!rows.length) {
    box.textContent = "No schedules";
    return;
  }
  box.innerHTML = rows
    .sort((a, b) => new Date(a.when) - new Date(b.when))
    .map(
      (s, i) =>
        `<div class="schedule-manage-row schedule-color-${i % 5}"><div><b>${s.title}</b><span>${new Date(s.when).toLocaleString()} • ${s.fired ? "PLAYED" : "WAITING"}</span><small>${s.detail}</small></div><button class="action-orange" data-edit-schedule="${s.source}:${s.id}">EDIT</button><button class="action-red" data-remove-schedule="${s.source}:${s.id}">REMOVE</button></div>`,
    )
    .join("");
  box.querySelectorAll("[data-edit-schedule]").forEach(
    (b) =>
      (b.onclick = () => {
        let [source, id] = b.dataset.editSchedule.split(":"),
          item =
            source === "playlist"
              ? playlistSchedules.find((s) => String(s.id) === id)
              : programSchedules.find((s) => String(s.id) === id);
        if (!item) return;
        editingSchedule = { source, id };
        let local = new Date(
          new Date(item.when).getTime() -
            new Date().getTimezoneOffset() * 60000,
        ).toISOString();
        if (source === "playlist") {
          $("#scheduledPlaylistName").innerHTML = Object.keys(savedPlaylists)
            .map((n) => `<option>${n}</option>`)
            .join("");
          $("#scheduledPlaylistName").value = item.name;
          $("#scheduledPlaylistDate").value = local.slice(0, 10);
          $("#scheduledPlaylistClock").value = local.slice(11, 19);
          $("#scheduledPlaylistUrl").value = item.url || "";
          $("#playlistScheduleDialog").showModal();
        } else {
          openScheduleDialog();
          $("#schTitle").value = item.title || "";
          $("#schType").value = item.type || "Program";
          $("#schProgram").value = item.program || "";
          $("#schNetworkUrl").value = item.url || "";
          $("#schDate").value = local.slice(0, 10);
          $("#schClock").value = local.slice(11, 19);
        }
      }),
  );
  box.querySelectorAll("[data-remove-schedule]").forEach(
    (b) =>
      (b.onclick = () => {
        let [source, id] = b.dataset.removeSchedule.split(":");
        if (source === "playlist") {
          playlistSchedules = playlistSchedules.filter(
            (s) => String(s.id) !== id,
          );
          localStorage.setItem(
            "playlistSchedules",
            JSON.stringify(playlistSchedules),
          );
        } else {
          let items = JSON.parse(
            localStorage.getItem("scheduleItems") || "[]",
          ).filter((s) => String(s.id) !== id);
          localStorage.setItem("scheduleItems", JSON.stringify(items));
        }
        refreshPlaylistSchedules();
      }),
  );
};
$("#confirmPlaylistSchedule").onclick = () => {
  let name = $("#scheduledPlaylistName").value,
    date = $("#scheduledPlaylistDate").value,
    time = $("#scheduledPlaylistClock").value,
    url = $("#scheduledPlaylistUrl").value.trim(),
    when = date && time ? `${date}T${time}` : "";
  if (!when) return alert("Select Program Date and Time");
  if (url && !/^https?:\/\//i.test(url))
    return alert("Enter a valid Network / YouTube URL");
  let entry = {
    id:
      editingSchedule?.source === "playlist"
        ? Number(editingSchedule.id)
        : Date.now(),
    name,
    when: new Date(when).toISOString(),
    url,
    fired: false,
  };
  if (editingSchedule?.source === "playlist")
    playlistSchedules = playlistSchedules.map((s) =>
      String(s.id) === editingSchedule.id ? entry : s,
    );
  else playlistSchedules.push(entry);
  editingSchedule = null;
  localStorage.setItem("playlistSchedules", JSON.stringify(playlistSchedules));
  refreshPlaylistSchedules();
  $("#playlistScheduleDialog").close();
};
$("#scheduleList").addEventListener(
  "click",
  (e) => {
    if (!e.target.closest("[data-remove-schedule]")) return;
    setTimeout(() => {
      if (
        localStorage.getItem("fillerEnabled") !== "0" &&
        fillerProgram &&
        savedPlaylists[fillerProgram]
      )
        activateProgram(fillerProgram, true);
    }, 50);
  },
  true,
);
$("#pageSchedule h2").insertAdjacentHTML(
  "afterend",
  '<button id="refreshSchedules" class="action-cyan">↻ REFRESH SCHEDULES</button>',
);
$("#pageFiller h2").insertAdjacentHTML(
  "afterend",
  '<button id="refreshFillers" class="action-cyan">↻ REFRESH FILLERS</button>',
);
$("#refreshSchedules").onclick = () => {
  refreshPlaylistSchedules();
  let pending = [
    ...playlistSchedules,
    ...JSON.parse(localStorage.getItem("scheduleItems") || "[]"),
  ].some((s) => !s.fired && new Date(s.when).getTime() <= Date.now());
  if (
    !pending &&
    localStorage.getItem("fillerEnabled") !== "0" &&
    fillerProgram &&
    savedPlaylists[fillerProgram]
  )
    activateProgram(fillerProgram, true);
};
$("#refreshFillers").onclick = () => renderPrograms();
function selectedOutputFilter(id) {
  let v = $("#" + id)?.value || "source",
    sizes = {
      576: [720, 576],
      720: [1280, 720],
      1080: [1920, 1080],
      2160: [3840, 2160],
    };
  if (v === "source" || !sizes[v]) return "source";
  let [w, h] = sizes[v];
  return `scale=${w}:${h}:force_original_aspect_ratio=decrease,pad=${w}:${h}:(ow-iw)/2:(oh-ih)/2`;
}
$("#goLive").onclick = async () => {
  if (current < 0) return alert("Please select a media file first");
  let url = $("#rtmp").value.trim(),
    codec = $("#rtmpCodec").value;
  if (!/^rtmps?:\/\//i.test(url)) {
    alert(
      "Paste the complete RTMP or RTMPS URL, including the stream key, in the single URL field.",
    );
    $("#rtmp").focus();
    return { ok: false };
  }
  if (!codec) return alert("Select an FFmpeg RTMP encoder / codec");
  $("#rtmpStatus").textContent =
    `CONNECTING • ${codec} • ${$("#rtmpBitrate").value}`;
  let correction = JSON.parse(
      localStorage.getItem("correctionSettings") || "{}",
    ),
    m = selectedMark(),
    r = await window.playoutAPI.startRTMP({
      file: list[current],
      ...engineConfig(),
      url,
      localAddress: $("#streamNetworkCard").value,
      startAt: playhead(),
      endAt: m.out || 0,
      resolution: selectedOutputFilter("rtmpOutputRes"),
      encoder: codec,
      bitrate: $("#rtmpBitrate").value,
      gop: $("#rtmpGop").value,
      audioCodec: $("#rtmpAudioCodec").value,
      cg: cgConfig(),
      color: correction.color,
      sound: correction.sound,
      quality: correction.quality,
    });
  $("#rtmpStatus").textContent = r.ok
    ? `LIVE • ${codec} • ${$("#rtmpBitrate").value} • ${$("#rtmpOutputRes").selectedOptions[0].textContent}`
    : r.message;
  rtmpLive = !!r.ok;
  updateStreamIndicators();
  if (r.ok) {
    $("#onAir").textContent = "RTMP LIVE + CG";
    $(".status .dot").style.background = "#ff355d";
  }
  return r;
};
$("#endLive").onclick = async () => {
  await window.playoutAPI.stopRTMP();
  rtmpLive = false;
  updateStreamIndicators();
  $("#onAir").textContent = udpLive ? "DVB / UDP / SRT LIVE + CG" : "PREVIEW";
  $("#rtmpStatus").textContent = "RTMP stream stopped";
};
$("#startUdp").onclick = async () => {
  if (current < 0) return alert("Please select a media file first");
  if ($("#autoPidMode").checked) generateBroadcastPids();
  let correction = JSON.parse(
      localStorage.getItem("correctionSettings") || "{}",
    ),
    m = selectedMark();
  let cfg = {
    file: list[current],
    ...engineConfig(),
    protocol: $("#streamProtocol").value,
    srtUrl: $("#srtUrl").value.trim(),
    localAddress: $("#udpNetworkCard").value,
    startAt: playhead(),
    endAt: m.out || 0,
    ip: $("#udpIp").value,
    port: $("#udpPort").value,
    resolution: makeOutputFilter(true),
    bitrateMode: $("#bitrateMode").value,
    bitrate: $("#udpBitrate").value,
    bufferSize: $("#udpBuffer").value,
    ttl: $("#udpTtl").value,
    autoPidMode: $("#autoPidMode").checked,
    videoPid: $("#videoPid").value,
    audioPid: $("#audioPid").value,
    pmtPid: $("#pmtPid").value,
    pcrPid: $("#pcrPid").value,
    tsId: $("#tsId").value,
    networkId: $("#networkId").value,
    serviceId: $("#serviceId").value,
    serviceType: $("#serviceType").value,
    muxRate: $("#muxRate").value,
    gop: $("#gop").value,
    audioBitrate: $("#audioBitrate").value,
    patPeriod: $("#patPeriod").value,
    sdtPeriod: $("#sdtPeriod").value,
    pcrPeriod: $("#pcrPeriod").value,
    providerName: $("#providerName").value,
    serviceName: $("#serviceName").value,
    encoder: $("#udpCodec").value,
    audioCodec: $("#udpAudioCodec").value,
    fps: $("#inputFps").value,
    color: correction.color,
    sound: correction.sound,
    quality: correction.quality,
    cg: cgConfig(),
    autoFallback: true,
  };
  if (cfg.protocol === "srt" && !/^srt:\/\//i.test(cfg.srtUrl))
    return alert(
      "Enter a complete SRT URL, for example srt://host:9000?mode=caller",
    );
  localStorage.setItem("udpCfg", JSON.stringify(cfg));
  let r = await window.playoutAPI.startUDP(cfg);
  $("#udpStatus").textContent = r.message;
  udpLive = !!r.ok;
  updateStreamIndicators();
  if (r.ok) {
    $("#onAir").textContent = `${cfg.protocol.toUpperCase()} LIVE + CG`;
    $(".status .dot").style.background = "#ff355d";
  }
  return r;
};
$("#stopUdp").onclick = async () => {
  await window.playoutAPI.stopUDP();
  udpLive = false;
  updateStreamIndicators();
  $("#onAir").textContent = rtmpLive ? "RTMP LIVE + CG" : "PREVIEW";
  $("#udpStatus").textContent = "UDP output stopped";
};
try {
  let u = JSON.parse(localStorage.getItem("udpCfg"));
  if (u) {
    const vals = {
      udpIp: u.ip || "239.1.1.1",
      udpPort: u.port || 1234,
      udpRes: u.resolution || "source",
      udpCodec: u.encoder || "h264_nvenc",
      bitrateMode: u.bitrateMode || "cbr",
      udpBitrate: u.bitrate || "8M",
      udpBuffer: u.bufferSize || "16M",
      decodeEngine: u.decodeEngine || "auto",
      encodeEngine: u.encodeEngine || "gpu",
      videoPid: u.videoPid || 256,
      audioPid: u.audioPid || 257,
      pmtPid: u.pmtPid || 4096,
      pcrPid: u.pcrPid || u.videoPid || 256,
      tsId: u.tsId || 1,
      networkId: u.networkId || 1,
      serviceId: u.serviceId || 1,
      serviceType: u.serviceType || "digital_tv",
      muxRate: u.muxRate || 0,
      gop: u.gop || 50,
      audioBitrate: u.audioBitrate || "192k",
      patPeriod: u.patPeriod || 0.1,
      sdtPeriod: u.sdtPeriod || 0.5,
      pcrPeriod: u.pcrPeriod || 20,
      providerName: u.providerName || "SR NETWORK",
      serviceName: u.serviceName || "SR MUSIX HD",
    };
    Object.entries(vals).forEach(([id, v]) => {
      let el = $("#" + id);
      if (el) el.value = v;
    });
    $("#autoPidMode").checked = u.autoPidMode !== false;
    applyPidMode();
    if (u.audioCodec) $("#audioCodec").value = u.audioCodec;
  }
} catch (e) {}
let rtmpProfiles = JSON.parse(localStorage.getItem("rtmpProfiles") || "{}"),
  udpProfiles = JSON.parse(localStorage.getItem("udpProfiles") || "{}");
$("#rtmp")
  .closest("section")
  .insertAdjacentHTML(
    "beforeend",
    '<div class="profile-box"><select id="rtmpProfile"></select><button id="saveRtmpProfile">SAVE RTMP PROFILE</button><button id="loadRtmpProfile">LOAD</button></div>',
  );
$("#udpIp")
  .closest("section")
  .insertAdjacentHTML(
    "beforeend",
    '<div class="profile-box"><select id="udpProfile"></select><button id="saveUdpProfile">SAVE UDP PROFILE</button><button id="loadUdpProfile">LOAD</button></div>',
  );
function refreshProfiles() {
  let options = (o) =>
    '<option value="">Select saved profile</option>' +
    Object.keys(o)
      .map((n) => `<option value="${n.replace(/"/g, "&quot;")}">${n}</option>`)
      .join("");
  $("#rtmpProfile").innerHTML = options(rtmpProfiles);
  $("#udpProfile").innerHTML = options(udpProfiles);
}
$("#saveRtmpProfile").onclick = () => {
  let n = prompt("RTMP profile name");
  if (!n) return;
  rtmpProfiles[n] = {
    url: $("#rtmp").value,
    rtmpCodec: $("#rtmpCodec").value,
    rtmpBitrate: $("#rtmpBitrate").value,
    rtmpGop: $("#rtmpGop").value,
    audio: $("#audioCodec").value,
    streamNetworkCard: $("#streamNetworkCard").value,
    decodeEngine: $("#decodeEngine").value,
    encodeEngine: $("#encodeEngine").value,
  };
  localStorage.setItem("rtmpProfiles", JSON.stringify(rtmpProfiles));
  refreshProfiles();
  alert(
    "Full RTMP URL, FFmpeg codec, bitrate and network card profile saved on this PC.",
  );
};
$("#loadRtmpProfile").onclick = () => {
  let p = rtmpProfiles[$("#rtmpProfile").value];
  if (p) {
    $("#rtmp").value = p.url || "";
    $("#rtmpKey").value = "";
    $("#rtmpCodec").value = p.rtmpCodec || p.video || "h264_nvenc";
    $("#rtmpBitrate").value = p.rtmpBitrate || "4500k";
    $("#rtmpGop").value = p.rtmpGop || 50;
    $("#audioCodec").value = p.audio || "aac";
    $("#streamNetworkCard").value = p.streamNetworkCard || "";
    $("#decodeEngine").value = p.decodeEngine || "auto";
    $("#encodeEngine").value = p.encodeEngine || "gpu";
    applyEngineSelection();
  }
};
function udpControlValues() {
  let ids = [
    "udpIp",
    "udpPort",
    "udpRes",
    "udpCodec",
    "bitrateMode",
    "udpBitrate",
    "udpBuffer",
    "udpTtl",
    "streamProtocol",
    "udpNetworkCard",
    "decodeEngine",
    "encodeEngine",
    "videoPid",
    "audioPid",
    "pmtPid",
    "pcrPid",
    "tsId",
    "networkId",
    "serviceId",
    "serviceType",
    "muxRate",
    "gop",
    "audioBitrate",
    "patPeriod",
    "sdtPeriod",
    "pcrPeriod",
    "providerName",
    "serviceName",
  ];
  return {
    ...Object.fromEntries(ids.map((id) => [id, $("#" + id)?.value])),
    autoPidMode: $("#autoPidMode").checked,
  };
}
$("#saveUdpProfile").onclick = () => {
  let n = prompt("UDP / DVB profile name");
  if (!n) return;
  udpProfiles[n] = udpControlValues();
  localStorage.setItem("udpProfiles", JSON.stringify(udpProfiles));
  refreshProfiles();
};
$("#loadUdpProfile").onclick = () => {
  let p = udpProfiles[$("#udpProfile").value];
  if (p) {
    Object.entries(p).forEach(([id, v]) => {
      if ($("#" + id)) $("#" + id).value = v;
    });
    applyEngineSelection();
  }
};
refreshProfiles();
document.querySelectorAll("nav button[data-page]").forEach(
  (b) =>
    (b.onclick = () => {
      document
        .querySelectorAll("nav button")
        .forEach((x) => x.classList.remove("active"));
      b.classList.add("active");
      let p = b.dataset.page;
      if (p === "playlist") {
        renderPrograms();
        return $("#pagePrograms").showModal();
      }
      if (p === "filler") {
        renderPrograms();
        return $("#pageFiller").showModal();
      }
      if (p === "schedule") return $("#pageSchedule").showModal();
      if (p === "cg") return $("#pageCG").showModal();
      if (p === "ads") return $("#pageAds").showModal();
      if (p === "correction") return $("#pageCorrection").showModal();
      if (p === "settings") return $("#pageSettings").showModal();
    }),
);
$("#pageCG h2").insertAdjacentHTML(
  "afterend",
  `<fieldset><legend>Logo / Animated CG</legend><button id="pickLogo" class="primary">SELECT LOGO / SEQUENCE</button><small id="logoFileStatus">${logoFile ? logoFile.path : "No logo file selected"}</small><button id="pickFont">SELECT CG FONT</button><small id="fontFileStatus">${cgFontFile || "Automatic: Nirmala UI / Segoe UI / Arial"}</small><div class="udp-grid"><label>Width<input id="logoWidth" type="number" value="220"></label><label>Opacity<input id="logoOpacity" type="range" min="0" max="1" step="0.05" value="1"></label><label>Position<select id="logoPosition"><option value="tr">Top Right</option><option value="tl">Top Left</option><option value="br">Bottom Right</option><option value="bl">Bottom Left</option></select></label><label><input id="logoManual" type="checkbox"> Manual X/Y axis</label><label>X Axis<input id="logoX" type="number" value="30"></label><label>Y Axis<input id="logoY" type="number" value="30"></label></div><p class="hint">TTF, OTF, TTC Windows fonts support. PNG sequence தேர்வுக்கு numbered PNG files அனைத்தையும் select செய்யவும்.</p></fieldset>`,
);
$("#logoWidth").parentElement.insertAdjacentHTML(
  "afterend",
  '<label>Height (0 = Auto)<input id="logoHeight" type="number" min="0" value="0"></label>',
);
$("#pageCG fieldset").insertAdjacentHTML(
  "afterend",
  '<fieldset><legend>Watermark</legend><label><input id="watermarkToggle" type="checkbox" checked> Show Watermark</label><label>Watermark Text<input id="watermarkText" value="SR MUSIX HD"></label><div class="udp-grid"><label>Opacity<input id="watermarkOpacity" type="range" min="0" max="1" step="0.05" value="0.35"></label><label>Font Size<input id="watermarkSize" type="number" value="22"></label></div><p class="hint">Preview-ல் Logo, Watermark, Now/Next, Ticker-ஐ mouse-ஆல் drag செய்யலாம்.</p></fieldset>',
);
$("#pageCG h2").insertAdjacentHTML(
  "afterend",
  '<section class="cg-designer"><div class="cg-designer-head"><b>16:9 ON-AIR LAYOUT CANVAS</b><span>Elements-ஐ mouse-ஆல் drag செய்து position மாற்றவும்</span></div><div id="cgDesignerCanvas"><div class="cg-node logo-node" data-cg-key="logo">★ SR MUSIX HD</div><div class="cg-node watermark-node" data-cg-key="watermark">SR MUSIX HD</div><div class="cg-node now-node" data-cg-key="now"><small>NOW PLAYING</small><b>Program Title</b><span>NEXT: Upcoming Video</span></div><div class="cg-node ticker-node" data-cg-key="ticker">SR MUSIX HD • Feel the Music • Live the Vibe!</div></div></section>',
);
function syncDesigner() {
  document.querySelectorAll("#cgDesignerCanvas [data-cg-key]").forEach((el) => {
    let p = cgPositions[el.dataset.cgKey] || { x: 0, y: 0 };
    el.style.left = p.x * 100 + "%";
    el.style.top = p.y * 100 + "%";
  });
}
function updateDesignerLogo() {
  let node = $("#cgDesignerCanvas .logo-node"),
    w = Math.max(10, Number($("#logoWidth")?.value || 220)),
    h = Math.max(0, Number($("#logoHeight")?.value || 0)),
    op = Number($("#logoOpacity")?.value || 1);
  if (logoFile?.path) {
    let src = "file:///" + logoFile.path.replace(/\\/g, "/"),
      ext = logoFile.path.split(".").pop().toLowerCase();
    node.innerHTML = ["webm", "mov", "mp4", "gif"].includes(ext)
      ? `<video src="${src}" autoplay loop muted></video>`
      : `<img src="${src}">`;
  } else node.textContent = "★ SR MUSIX HD";
  let media = node.querySelector("img,video");
  if (media) {
    media.style.width = Math.min(w, 500) + "px";
    media.style.height = h > 0 ? Math.min(h, 280) + "px" : "auto";
    media.style.opacity = op;
  }
  node.style.opacity = op;
}
function renderMainLogo() {
  if (!logoFile?.path) return;
  let src = "file:///" + logoFile.path.replace(/\\/g, "/"),
    ext = logoFile.path.split(".").pop().toLowerCase(),
    w = Math.max(10, Number($("#logoWidth")?.value || 220)),
    h = Math.max(0, Number($("#logoHeight")?.value || 0)),
    op = Number($("#logoOpacity")?.value || 1);
  $("#logo").innerHTML = ["webm", "mov", "mp4", "gif"].includes(ext)
    ? `<video src="${src}" autoplay loop muted></video>`
    : `<img src="${src}">`;
  let media = $("#logo img,#logo video");
  media.style.width = w + "px";
  media.style.height = h > 0 ? h + "px" : "auto";
  media.style.maxWidth = "none";
  media.style.maxHeight = "none";
  media.style.opacity = op;
}
document.querySelectorAll("#cgDesignerCanvas [data-cg-key]").forEach((el) => {
  el.onpointerdown = (e) => {
    e.preventDefault();
    el.setPointerCapture(e.pointerId);
    el.onpointermove = (ev) => {
      let r = $("#cgDesignerCanvas").getBoundingClientRect(),
        key = el.dataset.cgKey,
        x = Math.max(0, Math.min(0.92, (ev.clientX - r.left) / r.width)),
        y = Math.max(0, Math.min(0.92, (ev.clientY - r.top) / r.height));
      if (key === "ticker") x = 0;
      cgPositions[key] = { x, y };
      syncDesigner();
      placeCG($("#" + key), key);
      localStorage.setItem("cgPositions", JSON.stringify(cgPositions));
    };
    el.onpointerup = () => (el.onpointermove = null);
  };
});
syncDesigner();
updateDesignerLogo();
renderMainLogo();
try {
  let l = JSON.parse(localStorage.getItem("cgLogoLayout"));
  if (l) {
    logoWidth.value = l.width || 220;
    logoHeight.value = l.height || 0;
    logoOpacity.value = l.opacity ?? 1;
    logoPosition.value = l.position || "tr";
    logoManual.checked = !!l.manual;
    logoX.value = l.x ?? 30;
    logoY.value = l.y ?? 30;
  }
} catch (e) {}
$("#pickLogo").onclick = async () => {
  let r = await window.playoutAPI.pickLogo();
  if (!r) return;
  if (r.error) {
    $("#logoFileStatus").textContent = r.error;
    return;
  }
  logoFile = r;
  localStorage.setItem("cgLogoFile", JSON.stringify(r));
  $("#logoFileStatus").textContent =
    (r.converted ? "SWF converted → " : "") + r.path;
  let ext = r.path.split(".").pop().toLowerCase();
  if (["webm", "mov", "mp4"].includes(ext))
    $("#logo").innerHTML =
      `<video src="file:///${r.path.replace(/\\/g, "/")}" autoplay loop muted style="max-width:220px;max-height:100px"></video>`;
  else
    $("#logo").innerHTML =
      `<img src="file:///${r.path.replace(/\\/g, "/")}" style="max-width:220px;max-height:100px">`;
  updateDesignerLogo();
};
["logoWidth", "logoHeight", "logoOpacity"].forEach((id) =>
  $("#" + id).addEventListener("input", updateDesignerLogo),
);
$("#pickFont").onclick = async () => {
  let r = await window.playoutAPI.pickFont();
  if (!r) return;
  cgFontFile = r;
  localStorage.setItem("cgFontFile", r);
  $("#fontFileStatus").textContent = r;
};
let correctionButton = document.createElement("button");
correctionButton.textContent = "🎛 COLOR & AUDIO CORRECTION";
correctionButton.className = "primary";
correctionButton.onclick = () => {
  $("#pageSettings").close();
  $("#pageCorrection").showModal();
};
$("#pageSettings").appendChild(correctionButton);
$("#pageCorrection h2").textContent = "🎛 Professional Color & Audio Correction";
let colorBox = $("#brightness").closest("fieldset");
colorBox.insertAdjacentHTML(
  "beforeend",
  '<fieldset class="white-balance"><legend>UV Channels & White Balance</legend><label>U Channel<input id="uChannel" type="range" min="-255" max="255" value="0"></label><label>V Channel<input id="vChannel" type="range" min="-255" max="255" value="0"></label><label>UV Gain<input id="uvGain" type="range" min="0" max="2" step="0.05" value="1"></label><label>V Gain<input id="vGain" type="range" min="0.5" max="1.5" step="0.01" value="1"></label><label>White Balance — Red<input id="whiteRed" type="range" min="-1" max="1" step="0.01" value="0"></label><label>White Balance — Green<input id="whiteGreen" type="range" min="-1" max="1" step="0.01" value="0"></label><label>White Balance — Blue<input id="whiteBlue" type="range" min="-1" max="1" step="0.01" value="0"></label></fieldset><label><input id="autoQuality" type="checkbox" checked> Auto HD Quality + Noise Reduction</label><label>Sharpness<input id="sharpness" type="range" min="0" max="1.5" step="0.05" value="0.7"></label>',
);
$("#autoQuality").parentElement.insertAdjacentHTML(
  "beforebegin",
  '<label><input id="autoColor" type="checkbox"> Auto Color Correction</label>',
);
$("#autoQuality").parentElement.insertAdjacentHTML(
  "afterend",
  '<label>Video Softness<input id="softness" type="range" min="0" max="2" step="0.05" value="0"></label><label><input id="autoVideoFix" type="checkbox" checked> Auto Video Fix</label>',
);
$("#pageSettings h2").insertAdjacentHTML(
  "afterend",
  '<fieldset><legend>Full FFmpeg Codec Engine</legend><div class="rtmp-buttons"><button id="checkFFmpeg" class="primary">CHECK CODECS</button><button id="installFFmpeg">INSTALL FULL FFMPEG</button></div><pre id="ffmpegStatus" class="codec-report">FFmpeg status not checked</pre></fieldset>',
);
$("#pageSettings h2").insertAdjacentHTML(
  "afterend",
  '<fieldset><legend>24×7 Automatic Playout</legend><label><input id="autoStartWindows" type="checkbox"> Start Playout automatically with Windows</label><p class="hint">Windows login ஆனதும் software திறந்து saved schedule மற்றும் filler automation தொடரும்.</p></fieldset>',
);
$("#autoStartWindows").checked =
  localStorage.getItem("autoStartWindows") === "1";
$("#autoStartWindows").onchange = async () => {
  let enabled = $("#autoStartWindows").checked,
    r = await window.playoutAPI.setAutoStart(enabled);
  if (r.ok) localStorage.setItem("autoStartWindows", enabled ? "1" : "0");
  else alert(r.message);
};
$("#checkFFmpeg").onclick = async () => {
  $("#ffmpegStatus").textContent =
    "Checking decoders, encoders and CG filters...";
  let r = await window.playoutAPI.checkFFmpeg();
  $("#ffmpegStatus").textContent = r.message;
};
$("#installFFmpeg").onclick = async () => {
  let r = await window.playoutAPI.installFFmpeg();
  $("#ffmpegStatus").textContent = r.message;
};
$("#newSchedule").onclick = () => {
  $("#pageSchedule").close();
  openScheduleDialog();
};
$("#newAd").onclick = () => {
  $("#pageAds").close();
  $("#schType").value = "Advertisement";
  openScheduleDialog();
};
$("#pageSchedule").insertAdjacentHTML(
  "beforeend",
  '<button id="scheduleSavedProgram" class="primary">◷ SCHEDULE SAVED PROGRAM</button>',
);
$("#scheduleSavedProgram").onclick = () => {
  $("#pageSchedule").close();
  $("#schedulePlaylist").click();
};
$("#applyCG").onclick = async () => {
  let savedCG = {
    width: logoWidth.value,
    height: logoHeight.value,
    opacity: logoOpacity.value,
    position: logoPosition.value,
    manual: logoManual.checked,
    x: logoX.value,
    y: logoY.value,
    tickerText: $("#pageTicker").value,
    logo: $("#pageLogoToggle").checked,
    now: $("#pageNowToggle").checked,
    ticker: $("#tickerToggle").checked,
    watermark: $("#watermarkToggle").checked,
    watermarkText: $("#watermarkText").value,
    watermarkOpacity: $("#watermarkOpacity").value,
    watermarkSize: $("#watermarkSize").value,
    positions: cgPositions,
  };
  localStorage.setItem("cgLogoLayout", JSON.stringify(savedCG));
  localStorage.setItem("cgSettings", JSON.stringify(savedCG));
  $("#tickerInput").value = savedCG.tickerText;
  $("#tickerText").textContent = savedCG.tickerText;
  $("#logoToggle").checked = savedCG.logo;
  $("#nowToggle").checked = savedCG.now;
  $("#logo").style.display = savedCG.logo ? "block" : "none";
  let media = $("#logo img,#logo video");
  if (media) {
    media.style.width = logoWidth.value + "px";
    media.style.height =
      Number(logoHeight.value) > 0 ? logoHeight.value + "px" : "auto";
    media.style.maxWidth = "none";
    media.style.maxHeight = "none";
    media.style.opacity = logoOpacity.value;
  }
  $("#now").style.display = savedCG.now ? "block" : "none";
  $("#ticker").style.display = savedCG.ticker ? "block" : "none";
  $("#watermark").style.display = savedCG.watermark ? "block" : "none";
  $("#watermark").textContent = savedCG.watermarkText;
  $("#watermark").style.opacity = savedCG.watermarkOpacity;
  $("#watermark").style.fontSize = savedCG.watermarkSize + "px";
  $("#pageCG").close();
  await refreshLiveOutputs();
};
try {
  let c = JSON.parse(localStorage.getItem("cgSettings") || "null");
  if (c) {
    $("#tickerInput").value = c.tickerText || $("#tickerInput").value;
    $("#pageTicker").value = $("#tickerInput").value;
    $("#tickerText").textContent = $("#tickerInput").value;
    $("#logoToggle").checked = $("#pageLogoToggle").checked = c.logo !== false;
    $("#nowToggle").checked = $("#pageNowToggle").checked = c.now !== false;
    $("#tickerToggle").checked = c.ticker !== false;
    $("#watermarkToggle").checked = c.watermark !== false;
    $("#watermarkText").value = c.watermarkText || "SR MUSIX HD";
    $("#watermarkOpacity").value = c.watermarkOpacity ?? 0.35;
    $("#watermarkSize").value = c.watermarkSize || 22;
    $("#logo").style.display = c.logo === false ? "none" : "block";
    $("#now").style.display = c.now === false ? "none" : "block";
    $("#ticker").style.display = c.ticker === false ? "none" : "block";
    $("#watermark").style.display = c.watermark === false ? "none" : "block";
    $("#watermark").textContent = $("#watermarkText").value;
    $("#watermark").style.opacity = $("#watermarkOpacity").value;
    $("#watermark").style.fontSize = $("#watermarkSize").value + "px";
    if (c.positions) {
      cgPositions = c.positions;
      localStorage.setItem("cgPositions", JSON.stringify(cgPositions));
    }
    updateDesignerLogo();
  }
} catch (e) {}
$("#saveSettings").onclick = () => {
  let s = {
    inputRes: $("#inputRes").value,
    w: $("#customW").value,
    h: $("#customH").value,
    fps: $("#inputFps").value,
    codec: $("#videoCodec").value,
    audio: $("#audioCodec").value,
    display: $("#displayDevice").value,
    deck: $("#deckDevice").value,
    deckMode: $("#deckMode").value,
  };
  localStorage.setItem("outputSettings", JSON.stringify(s));
  $("#pageSettings").close();
};
$("#resetColor").onclick = () => {
  brightness.value = 0;
  contrast.value = 1;
  saturation.value = 1;
  gamma.value = 1;
  hue.value = 0;
  uChannel.value = 0;
  vChannel.value = 0;
  uvGain.value = 1;
  vGain.value = 1;
  whiteRed.value = 0;
  whiteGreen.value = 0;
  whiteBlue.value = 0;
  autoColor.checked = false;
  autoQuality.checked = true;
  sharpness.value = 0.7;
  applyPreviewCorrections(correctionFromControls());
};
$("#resetAudio").onclick = () => {
  audioGain.value = 1;
  bass.value = 0;
  mid.value = 0;
  treble.value = 0;
  normalize.checked = false;
  mono.checked = false;
};
$("#pageCorrection .settings-grid").insertAdjacentHTML(
  "afterend",
  '<p id="correctionStatus" class="hint">Video restart இல்லாமல் Color மற்றும் Audio EQ preview உடனடியாக மாறும்.</p>',
);
function correctionFromControls() {
  return {
    color: {
      auto: autoColor.checked,
      brightness: brightness.value,
      contrast: contrast.value,
      saturation: saturation.value,
      gamma: gamma.value,
      hue: hue.value,
      uChannel: uChannel.value,
      vChannel: vChannel.value,
      uvGain: uvGain.value,
      vGain: vGain.value,
      whiteRed: whiteRed.value,
      whiteGreen: whiteGreen.value,
      whiteBlue: whiteBlue.value,
    },
    quality: {
      auto: autoQuality.checked,
      videoFix: autoVideoFix.checked,
      sharpness: sharpness.value,
      softness: softness.value,
    },
    sound: {
      gain: audioGain.value,
      bass: bass.value,
      mid: mid.value,
      treble: treble.value,
      normalize: normalize.checked,
      mono: mono.checked,
    },
  };
}
const applyBasePreviewCorrections = applyPreviewCorrections;
applyPreviewCorrections = function (settings = correction()) {
  applyBasePreviewCorrections(settings);
  let q = settings.quality || {},
    soft = Math.max(0, Number(q.softness || 0)),
    sharp = q.videoFix === false ? 0 : Math.max(0, Number(q.sharpness ?? 0.7));
  $("#noiseFilter").setAttribute(
    "stdDeviation",
    String((q.auto === false ? 0 : 0.18) + soft * 0.3),
  );
  $("#sharpnessFilter").setAttribute(
    "kernelMatrix",
    `0 ${-sharp} 0 ${-sharp} ${1 + 4 * sharp} ${-sharp} 0 ${-sharp} 0`,
  );
};
[
  "brightness",
  "contrast",
  "saturation",
  "gamma",
  "hue",
  "uChannel",
  "vChannel",
  "uvGain",
  "vGain",
  "whiteRed",
  "whiteGreen",
  "whiteBlue",
  "autoColor",
  "autoQuality",
  "autoVideoFix",
  "sharpness",
  "softness",
  "audioGain",
  "bass",
  "mid",
  "treble",
  "normalize",
  "mono",
].forEach((id) =>
  $("#" + id).addEventListener("input", () => {
    let temp = correctionFromControls();
    applyPreviewCorrections(temp);
    $("#correctionStatus").textContent =
      `LIVE PREVIEW • Auto Color ${autoColor.checked ? "ON" : "OFF"} • U ${uChannel.value} • V ${vChannel.value} • UV Gain ${uvGain.value} • Sharpness ${sharpness.value} • Softness ${softness.value} • Noise Reduction ${autoQuality.checked ? "ON" : "OFF"}`;
  }),
);
$("#saveCorrection").onclick = async () => {
  let btn = $("#saveCorrection");
  btn.disabled = true;
  btn.textContent = "APPLYING…";
  $("#correctionStatus").textContent =
    "Applying correction to Preview, UDP and RTMP outputs…";
  let settings = correctionFromControls();
  localStorage.setItem("correctionSettings", JSON.stringify(settings));
  applyPreviewCorrections(settings);
  await refreshLiveOutputs();
  btn.disabled = false;
  btn.textContent = "SAVE & APPLY CORRECTION";
  $("#correctionStatus").textContent =
    "SAVED • Preview மற்றும் live outputs-க்கு correction apply செய்யப்பட்டது";
  setTimeout(() => $("#pageCorrection").close(), 450);
};
try {
  let s = JSON.parse(localStorage.getItem("outputSettings"));
  if (s) {
    inputRes.value = s.inputRes || "source";
    customW.value = s.w || 1920;
    customH.value = s.h || 1080;
    inputFps.value = s.fps || "source";
    videoCodec.value = s.codec || "libx264";
    audioCodec.value = s.audio || "aac";
    deckDevice.value = s.deck || "";
    deckMode.value = s.deckMode || "1920x1080,25";
  }
} catch (e) {}
try {
  let c = JSON.parse(localStorage.getItem("correctionSettings"));
  if (c.color) {
    autoColor.checked = c.color.auto === true;
    [
      "brightness",
      "contrast",
      "saturation",
      "gamma",
      "hue",
      "uChannel",
      "vChannel",
      "uvGain",
      "vGain",
      "whiteRed",
      "whiteGreen",
      "whiteBlue",
    ].forEach((k) => {
      if (c.color[k] !== undefined && $("#" + k)) $("#" + k).value = c.color[k];
    });
  }
  if (c.quality) {
    autoQuality.checked = c.quality.auto !== false;
    sharpness.value = c.quality.sharpness ?? 0.7;
  }
  if (c.sound) {
    audioGain.value = c.sound.gain;
    bass.value = c.sound.bass;
    mid.value = c.sound.mid;
    treble.value = c.sound.treble;
    normalize.checked = !!c.sound.normalize;
    mono.checked = !!c.sound.mono;
  }
} catch (e) {}
try {
  let q =
    JSON.parse(localStorage.getItem("correctionSettings") || "{}").quality ||
    {};
  softness.value = q.softness ?? 0;
  autoVideoFix.checked = q.videoFix !== false;
} catch (e) {}
window.playoutAPI.outputInfo().then((x) => {
  $("#displayDevice").innerHTML = x.displays
    .map((d) => `<option value="${d.id}">${d.name} — ${d.size}</option>`)
    .join("");
});
$("#detectDeck").onclick = async () => {
  let r = await window.playoutAPI.probeDeckLink();
  $("#deckStatus").textContent = r.ok
    ? "DeckLink FFmpeg support detected"
    : "DeckLink support இல்லை — Desktop Video/DeckLink FFmpeg நிறுவவும்";
};
$("#startDeck").onclick = async () => {
  if (current < 0) return alert("முதலில் media தேர்வு செய்யவும்");
  let [size, fps] = $("#deckMode").value.split(",");
  let [width, height] = size.split("x"),
    correction = JSON.parse(localStorage.getItem("correctionSettings") || "{}");
  let r = await window.playoutAPI.startDeckLink({
    file: list[current],
    device: $("#deckDevice").value,
    width,
    height,
    fps,
    cg: cgConfig(),
    color: correction.color,
    sound: correction.sound,
  });
  $("#deckStatus").textContent = r.message;
};
$(".screen").insertAdjacentHTML(
  "afterbegin",
  '<div class="mcr-bars" aria-label="MCR standby colour bars"><i></i><i></i><i></i><i></i><i></i><i></i><i></i><strong>SR MUSIX HD • STANDBY</strong></div>',
);
$(".left.card .section-title").insertAdjacentHTML(
  "afterend",
  '<div class="active-program-strip"><span>ACTIVE PROGRAM <b id="activeProgramDisplay">Manual Playlist</b></span><span>ON-AIR SONG <b id="activeSongDisplay">No song on air</b></span></div>',
);
$(".transport").insertAdjacentHTML(
  "afterbegin",
  '<button id="playoutStart" class="action-green">● START PLAYOUT</button><button id="playoutStop" class="action-red">■ STOP PLAYOUT</button>',
);
function setAirState(on) {
  document.body.classList.toggle("on-air-active", on);
  $("#onAir").textContent = on ? "ON AIR" : "OFF AIR";
  $(".status .dot").style.background = on ? "#23e56f" : "#ff365e";
  $(".status .dot").style.boxShadow = `0 0 14px ${on ? "#23e56f" : "#ff365e"}`;
}
$("#playoutStart").onclick = async () => {
  if (!list.length) return alert("Playlist-ல் media சேர்க்கவும்");
  if (current < 0) current = 0;
  await load(current, true);
  setAirState(true);
};
$("#playoutStop").onclick = async () => {
  video.pause();
  await window.playoutAPI.stopPreview();
  setAirState(false);
};
setAirState(false);
let settingsNav = document.querySelector('nav button[data-page="settings"]');
settingsNav.insertAdjacentHTML(
  "beforebegin",
  '<button id="streamingPageBtn">📡 Streaming Output</button><button id="audioPageBtn">🎚 Audio EQ</button>',
);
document.body.insertAdjacentHTML(
  "beforeend",
  '<dialog class="page-dialog streaming-page" id="pageStreaming"><h2>📡 Streaming Output Control</h2><div class="stream-dashboard"><div><b id="streamState">OUTPUT IDLE</b><span id="streamInterface">Auto Route</span></div><div><b id="streamBitrateMeter">0.00 Mbps</b><span>Current Bitrate</span></div><div><b id="streamDataMeter">0.00 MB</b><span>Estimated Data Sent</span></div><div class="data-wave"><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i></div></div><div id="streamingCards" class="streaming-cards"></div><button onclick="pageStreaming.close()">CLOSE</button></dialog><dialog class="page-dialog audio-page" id="pageAudio"><h2>🎚 Audio / Digital Equalizer</h2><div class="audio-console"><div class="eq-wave"><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i><i></i></div><div class="audio-control-grid"><label>Master Volume / Gain<input id="audioMaster2" type="range" min="0" max="3" step="0.05"></label><label>Bass dB<input id="audioBass2" type="range" min="-12" max="12"></label><label>Mid dB<input id="audioMid2" type="range" min="-12" max="12"></label><label>Treble dB<input id="audioTreble2" type="range" min="-12" max="12"></label><label><input id="audioNormalize2" type="checkbox"> Digital Loudness / Auto Level</label><label>Channel Mode<select id="audioMode2"><option value="stereo">Stereo</option><option value="mono">Mono</option></select></label></div><button id="saveAudioConsole" class="action-green">SAVE & APPLY AUDIO</button></div></dialog>',
);
const eqFrequencies = [32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
$(".audio-control-grid").insertAdjacentHTML(
  "beforebegin",
  '<section class="graphic-equalizer"><h3>10-BAND GRAPHIC EQUALIZER</h3><div class="eq-band-grid">' +
    eqFrequencies
      .map(
        (f) =>
          `<label><output id="eqOut${f}">0</output><input id="eq${f}" type="range" min="-12" max="12" step="1" value="0" orient="vertical"><span>${f >= 1000 ? f / 1000 + "k" : f} Hz</span></label>`,
      )
      .join("") +
    '</div><button id="resetGraphicEq" class="action-orange">RESET ALL BANDS</button></section>',
);
let streamCards = $("#streamingCards");
document.querySelectorAll("aside .card").forEach((c) => {
  let t = c.querySelector(".section-title")?.textContent || "";
  if (/RTMP STREAMING|DVB \/ UDP/.test(t)) streamCards.appendChild(c);
});
let deckField = [...$("#pageSettings").querySelectorAll("fieldset")].find((f) =>
  /Blackmagic DeckLink/.test(f.textContent),
);
if (deckField) streamCards.appendChild(deckField);
$(".stream-dashboard").insertAdjacentHTML(
  "afterend",
  '<div class="auto-stream-controls"><label><input id="autoStartRtmp" type="checkbox"> Auto-start RTMP / YouTube with Playout or Schedule</label><label><input id="autoStartUdp" type="checkbox"> Auto-start UDP / RTP with Playout or Schedule</label></div>',
);
$("#autoStartRtmp").checked = localStorage.getItem("autoStartRtmp") === "1";
$("#autoStartUdp").checked = localStorage.getItem("autoStartUdp") === "1";
["autoStartRtmp", "autoStartUdp"].forEach(
  (id) =>
    ($("#" + id).onchange = () =>
      localStorage.setItem(id, $("#" + id).checked ? "1" : "0")),
);
$("#streamingPageBtn").onclick = () => {
  $("#streamInterface").textContent =
    $("#udpNetworkCard").selectedOptions[0]?.textContent || "Auto Route";
  $("#pageStreaming").showModal();
};
async function startAutomaticOutputs() {
  if ($("#autoStartRtmp").checked && !rtmpLive) await $("#goLive").onclick();
  if ($("#autoStartUdp").checked && !udpLive) await $("#startUdp").onclick();
}
const originalPlayoutStart = $("#playoutStart").onclick;
$("#playoutStart").onclick = async () => {
  await originalPlayoutStart();
  if (document.body.classList.contains("on-air-active"))
    await startAutomaticOutputs();
};
$("#pageCorrection fieldset:nth-of-type(2)").classList.add(
  "audio-controls-source-hidden",
);
document.querySelector("main > aside").classList.add("retired-main-sidebar");
document.querySelector("main").classList.add("main-two-column");
function graphicBands() {
  return Object.fromEntries(
    eqFrequencies.map((f) => [f, Number($("#eq" + f).value)]),
  );
}
const baseCorrectionFromControls = correctionFromControls;
correctionFromControls = function () {
  let c = baseCorrectionFromControls();
  c.sound.bands = graphicBands();
  return c;
};
$("#audioPageBtn").onclick = () => {
  let sound = correction().sound || {},
    bands = sound.bands || {};
  $("#audioMaster2").value = $("#audioGain").value;
  $("#audioBass2").value = $("#bass").value;
  $("#audioMid2").value = $("#mid").value;
  $("#audioTreble2").value = $("#treble").value;
  $("#audioNormalize2").checked = $("#normalize").checked;
  $("#audioMode2").value = $("#mono").checked ? "mono" : "stereo";
  eqFrequencies.forEach((f) => {
    $("#eq" + f).value = bands[f] ?? 0;
    $("#eqOut" + f).value = $("#eq" + f).value + " dB";
  });
  $("#pageAudio").showModal();
};
function syncAudioConsole() {
  $("#audioGain").value = $("#audioMaster2").value;
  $("#bass").value = $("#audioBass2").value;
  $("#mid").value = $("#audioMid2").value;
  $("#treble").value = $("#audioTreble2").value;
  $("#normalize").checked = $("#audioNormalize2").checked;
  $("#mono").checked = $("#audioMode2").value === "mono";
  eqFrequencies.forEach(
    (f) => ($("#eqOut" + f).value = $("#eq" + f).value + " dB"),
  );
  applyPreviewCorrections(correctionFromControls());
}
[
  "audioMaster2",
  "audioBass2",
  "audioMid2",
  "audioTreble2",
  "audioNormalize2",
  "audioMode2",
  ...eqFrequencies.map((f) => "eq" + f),
].forEach((id) => $("#" + id).addEventListener("input", syncAudioConsole));
$("#resetGraphicEq").onclick = () => {
  eqFrequencies.forEach((f) => ($("#eq" + f).value = 0));
  syncAudioConsole();
};
$("#saveAudioConsole").onclick = async () => {
  syncAudioConsole();
  localStorage.setItem(
    "correctionSettings",
    JSON.stringify(correctionFromControls()),
  );
  await refreshLiveOutputs();
  $("#pageAudio").close();
};
let streamBytes = 0,
  lastMetric = Date.now();
setInterval(() => {
  let live = udpLive || rtmpLive,
    raw = udpLive ? $("#udpBitrate").value : $("#rtmpBitrate").value,
    m = /([\d.]+)\s*([kKmM]?)/.exec(raw || "0"),
    mbps = m
      ? Number(m[1]) *
        (m[2].toLowerCase() === "k"
          ? 0.001
          : m[2].toLowerCase() === "m"
            ? 1
            : 1e-6)
      : 0,
    now = Date.now();
  if (live) streamBytes += (mbps * 125000 * (now - lastMetric)) / 1000;
  lastMetric = now;
  if ($("#streamState")) {
    $("#streamState").textContent = live
      ? udpLive
        ? "UDP/DVB LIVE"
        : "RTMP LIVE"
      : "OUTPUT IDLE";
    $("#streamBitrateMeter").textContent =
      (live ? mbps : 0).toFixed(2) + " Mbps";
    $("#streamDataMeter").textContent =
      (streamBytes / 1048576).toFixed(2) + " MB";
  }
}, 1000);
setInterval(async () => {
  try {
    let s = await window.playoutAPI.systemStats(),
      moved = $("#cpuGraph i"),
      high = s.cpu >= 80;
    $("#cpuGraph").appendChild(moved);
    moved.style.height = Math.max(3, s.cpu) + "%";
    $("#cpuUsageText").textContent = "CPU " + s.cpu.toFixed(0) + "%";
    $("#memoryUsageText").textContent = "RAM " + s.memory.toFixed(0) + "%";
    $(".cpu-monitor").classList.toggle("cpu-high", high);
    document.body.classList.toggle("cpu-protection", high);
  } catch (e) {}
}, 2000);
document.querySelectorAll("[data-paste-target]").forEach(
  (b) =>
    (b.onclick = async () => {
      let text = (await window.playoutAPI.readClipboardText()).trim(),
        target = $("#" + b.dataset.pasteTarget);
      target.value = text;
      target.focus();
    }),
);
function installFileDrop(zone, onFiles) {
  ["dragenter", "dragover"].forEach((type) =>
    zone.addEventListener(type, (e) => {
      e.preventDefault();
      e.stopPropagation();
      zone.classList.add("drag-active");
    }),
  );
  ["dragleave", "drop"].forEach((type) =>
    zone.addEventListener(type, (e) => {
      e.preventDefault();
      e.stopPropagation();
      zone.classList.remove("drag-active");
    }),
  );
  zone.addEventListener("drop", (e) => {
    let paths = [...e.dataTransfer.files]
      .map((f) => {
        try {
          return window.playoutAPI.droppedFilePath(f);
        } catch {
          return f.path || "";
        }
      })
      .filter(Boolean);
    if (paths.length) onFiles(paths);
  });
}
installFileDrop($("#playlistDropZone"), (paths) => {
  list.push(...paths);
  activeProgramName = "Manual Playlist";
  localStorage.setItem("activeProgramName", activeProgramName);
  render();
  if (current < 0) load(0);
});
$("#playlistDropZone").onclick = () => $("#add").click();
installFileDrop($("#programEditList"), (paths) => {
  programDraft.push(...paths);
  programDraftSelected = programDraft.length - 1;
  renderProgramEditor();
});
let resumeState = JSON.parse(
  localStorage.getItem("playoutResumeState") || "null",
);
list = resumeState?.list?.length
  ? [...resumeState.list]
  : JSON.parse(localStorage.getItem("playlist") || "[]");
if (resumeState?.activeProgramName)
  activeProgramName = resumeState.activeProgramName;
applyPreviewCorrections();
render();
renderChannels();
window.playoutAPI
  .checkFFmpeg()
  .then((r) => {
    if (!r.ok) {
      $(".center.card .section-title").insertAdjacentHTML(
        "afterend",
        '<div class="ffmpeg-required">Legacy formats (VOB, DAT, M2P, AVI, MPEG) require Full FFmpeg. Open Settings → INSTALL FULL FFMPEG.</div>',
      );
    }
  })
  .catch(() => {});
setInterval(() => {
  if (current < 0 || !list.length) return;
  localStorage.setItem(
    "playoutResumeState",
    JSON.stringify({
      list,
      activeProgramName,
      current,
      position: playhead(),
      playing: !video.paused,
      savedAt: Date.now(),
    }),
  );
}, 2000);
setTimeout(() => {
  if (resumeState?.list?.length && Number.isInteger(resumeState.current)) {
    current = Math.min(resumeState.current, list.length - 1);
    load(
      current,
      resumeState.playing !== false,
      Number(resumeState.position) || 0,
    ).then(() => {
      if (resumeState.playing !== false) setAirState(true);
    });
  } else if (
    !list.length &&
    localStorage.getItem("fillerEnabled") !== "0" &&
    fillerProgram
  )
    activateProgram(fillerProgram, true);
}, 800);
document.head.insertAdjacentHTML(
  "beforeend",
  '<link rel="stylesheet" href="v32.css">',
);
function installEnglishUI() {
  const tamil = /[\u0B80-\u0BFF]+/g,
    map = new Map([
      [
        "Media files சேர்க்க “ADD MEDIA” அழுத்தவும்",
        "Click ADD MEDIA to add broadcast files",
      ],
      [
        "Scheduled items இங்கே காட்டப்படும்",
        "Scheduled items will appear here",
      ],
      [
        "Advertisement video-ஐ playlist-ல் சேர்த்து நேரம் நிர்ணயிக்கலாம்.",
        "Add advertisement videos to the playlist and set their broadcast time.",
      ],
      ["Advertisement schedule காலியாக உள்ளது", "No advertisement schedules"],
      [
        "GPU codecs-க்கு அதற்கான graphics driver தேவை.",
        "GPU codecs require the appropriate graphics driver.",
      ],
      ["Program output monitor தேர்வு.", "Select the program output monitor."],
      ["Programs இல்லை", "No saved programs"],
      ["Program playlist காலியாக உள்ளது", "Program playlist is empty"],
      ["Playlist schedules இல்லை", "No playlist schedules"],
      [
        "Elements-ஐ mouse-ஆல் drag செய்து position மாற்றவும்",
        "Drag elements with the mouse to change position",
      ],
      [
        "Video restart இல்லாமல் Color மற்றும் Audio EQ preview உடனடியாக மாறும்.",
        "Color and Audio EQ update immediately in Preview.",
      ],
    ]);
  function preserve(el) {
    return el.closest(
      "#ticker,#logo,#nowTitle,#nextTitle,#cgDesignerCanvas,.playlist .item b,.program-edit-item b,.program-row b,#schProgram,#fillerProgramSelect,#scheduledPlaylistName",
    );
  }
  function clean(s) {
    if (!tamil.test(s)) return s;
    tamil.lastIndex = 0;
    if (map.has(s.trim())) return s.replace(s.trim(), map.get(s.trim()));
    let out = s
      .replace(tamil, " ")
      .replace(/\s{2,}/g, " ")
      .replace(/\s+([,.:;])/g, "$1")
      .trim();
    return out.length > 2
      ? out
      : "Please check the required selection or setting.";
  }
  function scan(root = document.body) {
    let w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
    for (let n; (n = w.nextNode());)
      if (!preserve(n.parentElement) && tamil.test(n.nodeValue)) {
        tamil.lastIndex = 0;
        n.nodeValue = clean(n.nodeValue);
      }
  }
  scan();
  new MutationObserver((ms) =>
    ms.forEach((m) =>
      m.addedNodes.forEach((n) => {
        if (n.nodeType === 3 && !preserve(n.parentElement))
          n.nodeValue = clean(n.nodeValue);
        else if (n.nodeType === 1) scan(n);
      }),
    ),
  ).observe(document.body, {
    childList: true,
    subtree: true,
    characterData: false,
  });
  const nativeAlert = window.alert.bind(window);
  window.alert = (m) => nativeAlert(clean(String(m)));
  document.querySelectorAll("input[placeholder]").forEach((i) => {
    if (tamil.test(i.placeholder)) {
      tamil.lastIndex = 0;
      i.placeholder = "Type or paste the required value here";
    }
  });
}
installEnglishUI();

// V3.5.0 multi-filler selection and multi-program scheduling.
let multiFillerPrograms = JSON.parse(
    localStorage.getItem("multiFillerPrograms") || "[]",
  ),
  fillerRotation = 0;
const renderProgramsV350 = renderPrograms;
renderPrograms = function () {
  renderProgramsV350();
  let box = $("#fillerProgramList");
  box.querySelectorAll(".filler-schedule-row").forEach((row) => {
    let name = decodeURIComponent(row.dataset.fillerName),
      check = row.querySelector(".filler-show");
    check.checked = multiFillerPrograms.includes(name);
    check.onchange = () => {
      if (check.checked && !multiFillerPrograms.includes(name))
        multiFillerPrograms.push(name);
      if (!check.checked)
        multiFillerPrograms = multiFillerPrograms.filter((x) => x !== name);
      fillerProgram = multiFillerPrograms[0] || "";
      localStorage.setItem(
        "multiFillerPrograms",
        JSON.stringify(multiFillerPrograms),
      );
      localStorage.setItem("fillerProgram", fillerProgram);
    };
  });
};
$("#fillerProgramSelect").multiple = true;
$("#fillerProgramSelect").size = 5;
$("#fillerProgramSelect").parentElement.firstChild.textContent =
  "Multi Filler Programs (Ctrl + Click)";
$("#saveFiller").onclick = () => {
  let chosen = [...$("#fillerProgramSelect").selectedOptions]
    .map((x) => x.value)
    .filter(Boolean);
  if (chosen.length) multiFillerPrograms = chosen;
  fillerProgram = multiFillerPrograms[0] || "";
  localStorage.setItem(
    "multiFillerPrograms",
    JSON.stringify(multiFillerPrograms),
  );
  localStorage.setItem("fillerProgram", fillerProgram);
  localStorage.setItem(
    "fillerEnabled",
    $("#fillerEnabled").checked ? "1" : "0",
  );
  $("#pageFiller").close();
};
const nextV350 = $("#next").onclick;
$("#next").onclick = () => {
  if (
    current + 1 >= list.length &&
    !$("#loop").checked &&
    multiFillerPrograms.length
  ) {
    fillerProgram =
      multiFillerPrograms[fillerRotation++ % multiFillerPrograms.length];
    localStorage.setItem("fillerProgram", fillerProgram);
  }
  return nextV350();
};
$("#scheduledPlaylistName").multiple = true;
$("#scheduledPlaylistName").size = 5;
$("#scheduledPlaylistName").parentElement.firstChild.textContent =
  "Saved Programs (Ctrl + Click for multiple)";
$("#confirmPlaylistSchedule").onclick = () => {
  let names = [...$("#scheduledPlaylistName").selectedOptions].map(
      (x) => x.value,
    ),
    date = $("#scheduledPlaylistDate").value,
    time = $("#scheduledPlaylistClock").value,
    url = $("#scheduledPlaylistUrl").value.trim(),
    when = date && time ? `${date}T${time}` : "";
  if (!when) return alert("Select Program Date and Time");
  if (url && !/^https?:\/\//i.test(url))
    return alert("Enter a valid Network / YouTube URL");
  if (!names.length) names = [""];
  let name = names[0] || "",
    multi = names.length > 1;
  if (multi) {
    name = `MULTI • ${names.join(" + ")}`;
    savedPlaylists[name] = names.flatMap((n) => savedPlaylists[n] || []);
    localStorage.setItem("savedPlaylists", JSON.stringify(savedPlaylists));
  }
  playlistSchedules.push({
    id: Date.now(),
    name,
    when: new Date(when).toISOString(),
    url,
    fired: false,
    multiPrograms: names,
  });
  localStorage.setItem("playlistSchedules", JSON.stringify(playlistSchedules));
  refreshPlaylistSchedules();
  $("#playlistScheduleDialog").close();
};
try {
  let u = JSON.parse(localStorage.getItem("udpCfg") || "{}");
  if (u.audioCodec) $("#udpAudioCodec").value = u.audioCodec;
  if (u.srtUrl) $("#srtUrl").value = u.srtUrl;
  $("#streamProtocol").onchange();
} catch (e) {}
renderPrograms();
document.querySelectorAll("button").forEach((b) => {
  let t = b.textContent.toUpperCase();
  if (/STOP|REMOVE|DELETE|CLEAR|CLOSE|END LIVE/.test(t))
    b.classList.add("action-red");
  else if (/START|PLAY|GO LIVE|APPLY|SAVE|UPDATE/.test(t))
    b.classList.add("action-green");
  else if (/ADD|INSERT|NEW|IMPORT/.test(t)) b.classList.add("action-purple");
  else if (/EDIT|REPLACE|SHUFFLE|SCHEDULE/.test(t))
    b.classList.add("action-orange");
  else b.classList.add("action-cyan");
});
function installPageWindowControls() {
  document.querySelectorAll("dialog.page-dialog").forEach((d) => {
    if (d.querySelector(".page-window-actions")) return;
    let title = d.querySelector("h2");
    if (!title) return;
    title.classList.add("page-window-title");
    title.insertAdjacentHTML(
      "beforeend",
      '<span class="page-window-actions"><button type="button" data-window-action="min" title="Minimize">—</button><button type="button" data-window-action="max" title="Maximize / Restore">□</button><button type="button" data-window-action="close" title="Close">×</button></span>',
    );
    title.querySelector('[data-window-action="min"]').onclick = () => {
      d.classList.toggle("window-minimized");
      d.classList.remove("window-maximized");
    };
    title.querySelector('[data-window-action="max"]').onclick = (e) => {
      d.classList.remove("window-minimized");
      d.classList.toggle("window-maximized");
      e.currentTarget.textContent = d.classList.contains("window-maximized")
        ? "❐"
        : "□";
    };
    title.querySelector('[data-window-action="close"]').onclick = () =>
      d.close();
    d.addEventListener("close", () => {
      d.classList.remove("window-minimized", "window-maximized");
      let m = d.querySelector('[data-window-action="max"]');
      if (m) m.textContent = "□";
    });
  });
}
installPageWindowControls();
function enforceEnglishDisplay() {
  const has = /[\u0B80-\u0BFF]/,
    all = /[\u0B80-\u0BFF]+/g,
    keep = (e) =>
      e?.closest(
        "#ticker,#logo,#nowTitle,#nextTitle,#cgDesignerCanvas,.playlist .item b,.program-edit-item b,.program-row b,#schProgram,#fillerProgramSelect,#scheduledPlaylistName",
      ),
    convert = (s) => {
      if (!has.test(s)) return s;
      let v = s
        .replace(all, " ")
        .replace(/\s{2,}/g, " ")
        .trim();
      return v.length > 2
        ? v
        : "Please check the required selection or setting.";
    },
    scan = (root) => {
      let w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      for (let n; (n = w.nextNode());)
        if (!keep(n.parentElement) && has.test(n.nodeValue))
          n.nodeValue = convert(n.nodeValue);
    };
  scan(document.body);
  new MutationObserver((ms) =>
    ms.forEach((m) =>
      m.addedNodes.forEach((n) => {
        if (n.nodeType === 3 && !keep(n.parentElement))
          n.nodeValue = convert(n.nodeValue);
        else if (n.nodeType === 1) scan(n);
      }),
    ),
  ).observe(document.body, { childList: true, subtree: true });
  const prior = window.alert;
  window.alert = (m) => prior(convert(String(m)));
}
enforceEnglishDisplay();
$("#applyCG").addEventListener("click", () => setTimeout(renderMainLogo, 0));
renderMainLogo();
