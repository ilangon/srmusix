const {
  app,
  BrowserWindow,
  ipcMain,
  dialog,
  shell,
  clipboard,
} = require("electron");
const { spawn } = require("child_process");
const path = require("path");
const os = require("os");
const fs = require("fs");
const http = require("http");
const crypto = require("crypto");
let ffmpegProcess = null,
  udpProcess = null,
  previewProcess = null;
const previewProcesses = new Set();
let activeEngineConfig = { decodeEngine: "auto-lowcpu", encodeEngine: "auto" };
const decodeChoiceCache = new Map(),
  encoderChoiceCache = new Map();
let cpuSnapshot = os.cpus().map((c) => ({ ...c.times }));
let previewServer = null,
  previewPort = 0,
  pendingPreview = new Map();

function toolPath(name, requested = "") {
  if (requested) return requested;
  const exe = process.platform === "win32" ? `${name}.exe` : name;
  const roots = app.isPackaged
    ? [
        path.join(process.resourcesPath, "bin", exe),
        path.join(process.resourcesPath, exe),
      ]
    : [path.join(__dirname, "bin", exe)];
  return roots.find(fs.existsSync) || exe;
}

function startChild(tool, args, options = {}) {
  const env = { ...process.env };
  if (process.platform === "win32") {
    const fc = app.isPackaged
      ? path.join(process.resourcesPath, "fontconfig")
      : path.join(__dirname, "fontconfig");
    env.FONTCONFIG_PATH = fc;
    env.FONTCONFIG_FILE = path.join(fc, "fonts.conf");
  }
  return spawn(toolPath(tool, options.requested), args, {
    windowsHide: true,
    stdio: options.stdio || ["ignore", "ignore", "pipe"],
    env,
  });
}
function capture(tool, args, requested = "") {
  return new Promise((resolve) => {
    let out = "",
      err = "";
    const p = startChild(tool, args, {
      requested,
      stdio: ["ignore", "pipe", "pipe"],
    });
    p.stdout.on("data", (d) => (out += String(d)));
    p.stderr.on("data", (d) => (err += String(d)));
    p.on("error", (e) => resolve({ ok: false, text: e.message }));
    p.on("close", (code) => resolve({ ok: code === 0, text: out + err }));
  });
}
async function resolveNetworkInput(input = "") {
  const value = String(input).trim();
  if (!/^https?:\/\//i.test(value))
    return { ok: true, url: value, original: value };
  if (!/(youtube\.com|youtu\.be)/i.test(value))
    return { ok: true, url: value, original: value };
  const selectors = [
    "best[protocol^=http][vcodec!=none][acodec!=none]/best[vcodec!=none][acodec!=none]/best",
    "b[protocol^=http]/b",
    null,
  ];
  let errors = [];
  for (const selector of selectors) {
    let args = [
      "--no-playlist",
      "--no-warnings",
      "--get-url",
      "--extractor-args",
      "youtube:player_client=android,web",
    ];
    if (selector) args.push("-f", selector);
    args.push(value);
    const r = await capture("yt-dlp", args),
      urls = r.text
        .split(/\r?\n/)
        .map((x) => x.trim())
        .filter((x) => /^https?:\/\//i.test(x));
    if (r.ok && urls.length)
      return {
        ok: true,
        url: urls[0],
        urls,
        original: value,
        type: "youtube",
        formatSelector: selector || "automatic",
      };
    errors.push(r.text.trim());
  }
  return {
    ok: false,
    error:
      "YouTube URL could not be resolved. Update yt-dlp, then retry. " +
      errors.filter(Boolean).pop(),
  };
}

function filterPath(p = "") {
  return String(p)
    .replace(/\\/g, "/")
    .replace(/:/g, "\\:")
    .replace(/'/g, "\\'");
}
function textAsset(name, value = "") {
  const dir = path.join(app.getPath("userData"), "overlay-text");
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `${name}.txt`);
  fs.writeFileSync(file, String(value).replace(/[\r\n]+/g, " "), "utf8");
  return filterPath(file);
}
function broadcastFont(custom = "") {
  const win = process.env.WINDIR || "C:\\Windows";
  const candidates = [
    custom,
    path.join(win, "Fonts", "Nirmala.ttf"),
    path.join(win, "Fonts", "seguisym.ttf"),
    path.join(win, "Fonts", "segoeui.ttf"),
    path.join(win, "Fonts", "arial.ttf"),
    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
  ].filter(Boolean);
  const found = candidates.find(fs.existsSync);
  return found
    ? filterPath(found)
    : filterPath(candidates[0] || "C:/Windows/Fonts/arial.ttf");
}

function ffText(value = "") {
  return String(value)
    .replace(/\\/g, "\\\\")
    .replace(/:/g, "\\:")
    .replace(/'/g, "\\'")
    .replace(/,/g, "\\,")
    .replace(/%/g, "\\%")
    .replace(/[\r\n]+/g, " ");
}
function n(value, fallback) {
  const x = Number(value);
  return Number.isFinite(x) ? x : fallback;
}
function decodeArgs(cfg = {}) {
  const engine = cfg.decodeEngine || "auto-lowcpu";
  if (["cpu", "safe", "third-party"].includes(engine)) return ["-threads", "2"];
  if (engine === "d3d11va" || engine === "windows-mf")
    return ["-hwaccel", "d3d11va"];
  if (engine === "dxva2") return ["-hwaccel", "dxva2"];
  if (engine === "qsv") return ["-hwaccel", "qsv"];
  if (engine === "cuda") return ["-hwaccel", "cuda"];
  if (engine === "gpu") return ["-hwaccel", "auto"];
  return ["-hwaccel", "auto"];
}
function decodeCandidates(mode = "auto-lowcpu") {
  if (mode === "cpu" || mode === "safe" || mode === "third-party")
    return [{ name: "FFmpeg CPU Safe", args: ["-threads", "2"] }];
  const map = {
    d3d11va: ["-hwaccel", "d3d11va"],
    dxva2: ["-hwaccel", "dxva2"],
    cuda: ["-hwaccel", "cuda"],
    qsv: ["-hwaccel", "qsv"],
    gpu: ["-hwaccel", "auto"],
    "windows-mf": ["-hwaccel", "d3d11va"],
  };
  if (map[mode])
    return [
      { name: mode.toUpperCase(), args: map[mode] },
      { name: "FFmpeg CPU Safe", args: ["-threads", "2"] },
    ];
  return process.platform === "win32"
    ? [
        { name: "Microsoft D3D11VA", args: ["-hwaccel", "d3d11va"] },
        { name: "Microsoft DXVA2", args: ["-hwaccel", "dxva2"] },
        { name: "Intel Quick Sync", args: ["-hwaccel", "qsv"] },
        { name: "NVIDIA CUDA", args: ["-hwaccel", "cuda"] },
        { name: "FFmpeg CPU Safe", args: ["-threads", "2"] },
      ]
    : [
        { name: "Hardware Auto", args: ["-hwaccel", "auto"] },
        { name: "FFmpeg CPU Safe", args: ["-threads", "2"] },
      ];
}
async function testDecoder(file, choice) {
  const nullOut = process.platform === "win32" ? "NUL" : "/dev/null",
    r = await capture("ffmpeg", [
      "-v",
      "error",
      "-t",
      "0.8",
      ...choice.args,
      "-i",
      file,
      "-map",
      "0:v:0",
      "-an",
      "-f",
      "null",
      nullOut,
    ]);
  return r.ok;
}
async function selectDecoder(file, mode = "auto-lowcpu") {
  const key = `${mode}|${file}`;
  if (decodeChoiceCache.has(key)) return decodeChoiceCache.get(key);
  for (const choice of decodeCandidates(mode))
    if (await testDecoder(file, choice)) {
      decodeChoiceCache.set(key, choice);
      return choice;
    }
  const cpu = { name: "FFmpeg CPU Safe", args: ["-threads", "2"] };
  decodeChoiceCache.set(key, cpu);
  return cpu;
}
async function availableEncoderNames() {
  const r = await capture("ffmpeg", ["-hide_banner", "-encoders"]);
  if (!r.ok) return [];
  return r.text
    .split(/\r?\n/)
    .map((x) => (x.match(/^\s*[VAS\.]{6}\s+(\S+)/) || [])[1])
    .filter(Boolean);
}
function encoderFallbacks(requested = "libx264") {
  const hevc = /hevc|265/.test(requested),
    software = hevc ? "libx265" : "libx264";
  const windows = hevc
    ? ["hevc_mf", "hevc_qsv", "hevc_nvenc", "hevc_amf"]
    : ["h264_mf", "h264_qsv", "h264_nvenc", "h264_amf"];
  return [...new Set([requested, ...windows, software])];
}
async function selectEncoder(requested = "libx264") {
  if (encoderChoiceCache.has(requested))
    return encoderChoiceCache.get(requested);
  const available = await availableEncoderNames(),
    candidates = encoderFallbacks(requested);
  for (const encoder of candidates) {
    if (!available.includes(encoder)) continue;
    const nullOut = process.platform === "win32" ? "NUL" : "/dev/null",
      r = await capture("ffmpeg", [
        "-v",
        "error",
        "-f",
        "lavfi",
        "-i",
        "color=size=128x72:rate=25",
        "-frames:v",
        "3",
        "-pix_fmt",
        "yuv420p",
        "-c:v",
        encoder,
        "-f",
        "null",
        nullOut,
      ]);
    if (r.ok) {
      encoderChoiceCache.set(requested, encoder);
      return encoder;
    }
  }
  encoderChoiceCache.set(requested, "libx264");
  return "libx264";
}
function videoFilters(cfg = {}, includeCG = true) {
  const f = [];
  if (cfg.resolution && cfg.resolution !== "source") f.push(cfg.resolution);
  if (cfg.quality?.auto)
    f.push(
      "hqdn3d=0.7:0.7:2:2",
      `unsharp=3:3:${n(cfg.quality.sharpness, 0.55)}:3:3:0`,
    );
  if (n(cfg.quality?.softness, 0) > 0)
    f.push(`gblur=sigma=${n(cfg.quality.softness, 0)}`);
  if (cfg.color) {
    const c = cfg.color,
      uv = n(c.uvGain, 1),
      vg = n(c.vGain, 1),
      auto = c.auto === true,
      ct = auto ? Math.max(1.05, n(c.contrast, 1)) : n(c.contrast, 1),
      sat = auto
        ? Math.max(1.06, n(c.saturation, 1) * uv)
        : n(c.saturation, 1) * uv,
      gm = auto ? 1.02 : n(c.gamma, 1);
    f.push(
      `eq=brightness=${n(c.brightness, 0)}:contrast=${ct}:saturation=${sat}:gamma=${gm}`,
      `hue=h=${n(c.hue, 0)}`,
      `chromashift=cbh=${n(c.uChannel, 0)}:crh=${n(c.vChannel, 0)}`,
      `colorbalance=rs=${n(c.whiteRed, 0)}:gs=${n(c.whiteGreen, 0)}:bs=${n(c.whiteBlue, 0)}`,
      `colorchannelmixer=rr=${vg}:gg=1:bb=${2 - vg}`,
    );
  }
  if (!includeCG) return f.length ? ["-vf", f.join(",")] : [];
  const c = cfg.cg || {},
    p = c.positions || {},
    font = broadcastFont(c.fontFile);
  const lx = p.logo?.x ?? 0.82,
    ly = p.logo?.y ?? 0.04,
    nx = p.now?.x ?? 0.04,
    ny = p.now?.y ?? 0.68,
    ty = p.ticker?.y ?? 0.92,
    wx = p.watermark?.x ?? 0.42,
    wy = p.watermark?.y ?? 0.08;
  if (c.logo !== false && !c.logoFile)
    f.push(
      `drawtext=fontfile='${font}':textfile='${textAsset("logo", c.logoText || "★ SR MUSIX HD")}':fontcolor=white:fontsize=30:borderw=2:bordercolor=black@0.8:x=w*${lx}:y=h*${ly}`,
    );
  if (c.watermark !== false)
    f.push(
      `drawtext=fontfile='${font}':textfile='${textAsset("watermark", c.watermarkText || "SR MUSIX HD")}':fontcolor=white@${c.watermarkOpacity ?? 0.35}:fontsize=${c.watermarkSize || 22}:x=w*${wx}:y=h*${wy}`,
    );
  if (c.now !== false)
    f.push(
      `drawbox=x=iw*${nx}:y=ih*${ny}:w=iw*0.58:h=82:color=0x071a35@0.82:t=fill`,
      `drawbox=x=iw*${nx}:y=ih*${ny}:w=5:h=82:color=0x2ee9ff:t=fill`,
      `drawtext=fontfile='${font}':text='NOW PLAYING':fontcolor=0x2ee9ff:fontsize=15:x=w*${nx}+18:y=h*${ny}+10`,
      `drawtext=fontfile='${font}':textfile='${textAsset("now", c.nowText || "No media loaded")}':fontcolor=white:fontsize=27:x=w*${nx}+18:y=h*${ny}+31`,
      `drawtext=fontfile='${font}':textfile='${textAsset("next", "NEXT: " + (c.nextText || "—"))}':fontcolor=0x7be8ff:fontsize=16:x=w*${nx}+18:y=h*${ny}+64`,
    );
  if (c.ticker !== false)
    f.push(
      `drawbox=x=0:y=ih*${ty}:w=iw:h=42:color=0x087bdd@0.92:t=fill`,
      `drawtext=fontfile='${font}':textfile='${textAsset("ticker", c.tickerText || "SR MUSIX HD • Feel the Music • Live the Vibe!")}':fontcolor=white:fontsize=23:x=w-mod(t*120\\,w+tw):y=h*${ty}+9`,
    );
  return f.length ? ["-vf", f.join(",")] : [];
}
function audioFilters(cfg = {}) {
  const s = cfg.sound;
  if (!s) return [];
  const defaults = {
      32: s.bass,
      64: s.bass,
      125: s.bass,
      250: 0,
      500: s.mid,
      1000: s.mid,
      2000: s.mid,
      4000: s.treble,
      8000: s.treble,
      16000: s.treble,
    },
    bands = s.bands || {};
  let chain = [`volume=${n(s.gain, 1)}`];
  for (const [f, g] of Object.entries(defaults))
    chain.push(`equalizer=f=${f}:t=q:w=1:g=${n(bands[f], n(g, 0))}`);
  if (s.normalize) chain.push("dynaudnorm=f=150:g=15");
  return ["-af", chain.join(",")];
}
function composition(cfg = {}) {
  const logo = cfg.cg && cfg.cg.logoFile;
  if (!logo)
    return {
      inputs: [],
      filters: videoFilters(cfg),
      maps: ["-map", "0:v:0", "-map", "0:a:0?"],
    };
  const base = videoFilters({ ...cfg, cg: { ...cfg.cg, logo: false } });
  const baseText = base.length ? base[1] : "null";
  let inputs = [];
  if (logo.type === "sequence")
    inputs = [
      "-stream_loop",
      "-1",
      "-framerate",
      String(logo.fps || 25),
      "-start_number",
      String(logo.start || 0),
      "-i",
      logo.pattern,
    ];
  else if (logo.type === "static") inputs = ["-loop", "1", "-i", logo.path];
  else inputs = ["-stream_loop", "-1", "-i", logo.path];
  const normalized =
    Number.isFinite(Number(logo.xPct)) && Number.isFinite(Number(logo.yPct));
  const manual =
    logo.manual === true &&
    Number.isFinite(Number(logo.x)) &&
    Number.isFinite(Number(logo.y));
  const pos = normalized
    ? `W*${Number(logo.xPct)}:H*${Number(logo.yPct)}`
    : manual
      ? `${Number(logo.x)}:${Number(logo.y)}`
      : { tl: "30:30", tr: "W-w-30:30", bl: "30:H-h-60", br: "W-w-30:H-h-60" }[
          logo.position || "tr"
        ];
  const graph = `[0:v]${baseText}[base];[1:v]format=rgba,scale=${logo.width || 220}:${Number(logo.height) > 0 ? Number(logo.height) : -1},colorchannelmixer=aa=${logo.opacity ?? 1}[lg];[base][lg]overlay=${pos}:shortest=0[vout]`;
  return {
    inputs,
    filters: ["-filter_complex", graph],
    maps: ["-map", "[vout]", "-map", "0:a:0?"],
  };
}

async function ensurePreviewServer() {
  if (previewServer) return previewPort;
  previewServer = http.createServer(async (req, res) => {
    const token = req.url.replace(/^\/preview\//, "").split("?")[0],
      cfg = pendingPreview.get(token);
    if (!cfg) {
      res.writeHead(404);
      return res.end("Preview expired");
    }
    pendingPreview.delete(token);
    res.writeHead(200, {
      "Content-Type": "video/mp4",
      "Cache-Control": "no-store",
      Connection: "close",
    });
    const vf = videoFilters(cfg, false),
      seek = Number(cfg.startAt) > 0 ? ["-ss", String(cfg.startAt)] : [],
      limit =
        Number(cfg.endAt) > Number(cfg.startAt || 0)
          ? ["-t", String(Number(cfg.endAt) - Number(cfg.startAt || 0))]
          : [],
      requested =
        cfg.encodeEngine === "cpu"
          ? "libx264"
          : cfg.encodeEngine === "microsoft"
            ? "h264_mf"
            : "h264_nvenc",
      enc = await selectEncoder(requested),
      decoder = await selectDecoder(cfg.file, cfg.decodeEngine),
      preset =
        enc === "libx264"
          ? ["-preset", "ultrafast", "-tune", "zerolatency", "-threads", "2"]
          : [];
    const args = [
      "-hide_banner",
      "-loglevel",
      "warning",
      "-fflags",
      "+genpts+discardcorrupt",
      "-err_detect",
      "ignore_err",
      ...seek,
      ...decoder.args,
      "-i",
      cfg.file,
      "-map",
      "0:v:0",
      "-map",
      "0:a:0?",
      ...vf,
      ...audioFilters(cfg),
      ...limit,
      "-c:v",
      enc,
      ...preset,
      "-pix_fmt",
      "yuv420p",
      "-g",
      "25",
      "-c:a",
      "aac",
      "-b:a",
      "128k",
      "-ar",
      "48000",
      "-ac",
      "2",
      "-f",
      "mp4",
      "-movflags",
      "frag_keyframe+empty_moov+default_base_moof",
      "pipe:1",
    ];
    const processForClient = startChild("ffmpeg", args, {
      requested: cfg.ffmpeg,
      stdio: ["ignore", "pipe", "pipe"],
    });
    previewProcess = processForClient;
    previewProcesses.add(processForClient);
    processForClient.stdout.pipe(res);
    res.on("close", () => {
      if (previewProcesses.has(processForClient)) {
        processForClient.kill();
        previewProcesses.delete(processForClient);
        if (previewProcess === processForClient) previewProcess = null;
      }
    });
    processForClient.on("close", () => {
      previewProcesses.delete(processForClient);
      if (previewProcess === processForClient) previewProcess = null;
      try {
        res.end();
      } catch {}
    });
    processForClient.on("error", (e) => {
      previewProcesses.delete(processForClient);
      if (previewProcess === processForClient) previewProcess = null;
      try {
        res.end(String(e.message));
      } catch {}
    });
  });
  await new Promise((resolve, reject) =>
    previewServer
      .listen(0, "127.0.0.1", () => {
        previewPort = previewServer.address().port;
        resolve();
      })
      .once("error", reject),
  );
  return previewPort;
}

function createWindow() {
  const win = new BrowserWindow({
    width: 1600,
    height: 960,
    minWidth: 1180,
    minHeight: 720,
    backgroundColor: "#07111f",
    title: "SR MUSIX HD Playout",
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
    },
  });
  win.setMenuBarVisibility(false);
  win.loadFile("index.html");
}
app.whenReady().then(createWindow);
app.on("before-quit", () => {
  [ffmpegProcess, udpProcess, previewProcess].forEach((p) => {
    try {
      p && p.kill();
    } catch {}
  });
  try {
    previewServer && previewServer.close();
  } catch {}
});
app.on("window-all-closed", () => {
  if (process.platform !== "darwin") app.quit();
});

ipcMain.handle("pick-media", async () => {
  const result = await dialog.showOpenDialog({
    properties: ["openFile", "multiSelections"],
    filters: [
      {
        name: "All Broadcast Media",
        extensions: [
          "mp4",
          "mkv",
          "mov",
          "avi",
          "webm",
          "dat",
          "vob",
          "vop",
          "mpg",
          "mpeg",
          "mpe",
          "mts",
          "m2ts",
          "m2p",
          "m2b",
          "m2v",
          "m4v",
          "ts",
          "flv",
          "wmv",
          "asf",
          "divx",
          "mxf",
          "mp3",
          "wav",
          "m4a",
          "aac",
          "ac3",
          "mka",
          "3gp",
          "ogv",
          "b80",
          "bop",
        ],
      },
      { name: "All Files — FFmpeg auto detect", extensions: ["*"] },
    ],
  });
  return result.canceled ? [] : result.filePaths;
});
ipcMain.handle("resolve-network-media", async (_, url) =>
  resolveNetworkInput(url),
);
ipcMain.handle("save-playlist-file", async (_, items) => {
  if (!Array.isArray(items) || !items.length)
    return { ok: false, message: "Playlist காலியாக உள்ளது" };
  const r = await dialog.showSaveDialog({
    title: "Save SR MUSIX Playlist",
    defaultPath: `SR_MUSIX_${new Date().toISOString().slice(0, 10)}.srplaylist`,
    filters: [
      { name: "SR MUSIX Playlist", extensions: ["srplaylist"] },
      { name: "JSON", extensions: ["json"] },
    ],
  });
  if (r.canceled || !r.filePath) return { ok: false, canceled: true };
  try {
    const data = {
      format: "SR MUSIX Playlist",
      version: 1,
      name: path.basename(r.filePath, path.extname(r.filePath)),
      savedAt: new Date().toISOString(),
      items,
    };
    fs.writeFileSync(r.filePath, JSON.stringify(data, null, 2), "utf8");
    return { ok: true, path: r.filePath, name: data.name };
  } catch (e) {
    return { ok: false, message: e.message };
  }
});
ipcMain.handle("load-playlist-file", async () => {
  const r = await dialog.showOpenDialog({
    title: "Open SR MUSIX Playlist",
    properties: ["openFile"],
    filters: [
      { name: "SR MUSIX Playlist", extensions: ["srplaylist", "json"] },
    ],
  });
  if (r.canceled || !r.filePaths.length) return { ok: false, canceled: true };
  try {
    const data = JSON.parse(fs.readFileSync(r.filePaths[0], "utf8")),
      items = Array.isArray(data) ? data : data.items;
    if (!Array.isArray(items)) throw new Error("Invalid playlist file");
    return {
      ok: true,
      path: r.filePaths[0],
      name:
        data.name ||
        path.basename(r.filePaths[0], path.extname(r.filePaths[0])),
      items: items.filter((x) => typeof x === "string"),
    };
  } catch (e) {
    return { ok: false, message: e.message };
  }
});
ipcMain.handle("save-project-file", async (_, data) => {
  const r = await dialog.showSaveDialog({
    title: "Save SR MUSIX Project",
    defaultPath: `SR_MUSIX_PROJECT_${new Date().toISOString().slice(0, 10)}.srproject`,
    filters: [{ name: "SR MUSIX Project", extensions: ["srproject"] }],
  });
  if (r.canceled || !r.filePath) return { ok: false, canceled: true };
  try {
    fs.writeFileSync(
      r.filePath,
      JSON.stringify(
        {
          format: "SR MUSIX Project",
          version: 1,
          savedAt: new Date().toISOString(),
          ...data,
        },
        null,
        2,
      ),
      "utf8",
    );
    return { ok: true, path: r.filePath };
  } catch (e) {
    return { ok: false, message: e.message };
  }
});
ipcMain.handle("load-project-file", async () => {
  const r = await dialog.showOpenDialog({
    title: "Open SR MUSIX Project",
    properties: ["openFile"],
    filters: [{ name: "SR MUSIX Project", extensions: ["srproject", "json"] }],
  });
  if (r.canceled || !r.filePaths.length) return { ok: false, canceled: true };
  try {
    return {
      ok: true,
      path: r.filePaths[0],
      data: JSON.parse(fs.readFileSync(r.filePaths[0], "utf8")),
    };
  } catch (e) {
    return { ok: false, message: e.message };
  }
});
ipcMain.handle("set-auto-start", async (_, enabled) => {
  try {
    app.setLoginItemSettings({
      openAtLogin: !!enabled,
      path: process.execPath,
    });
    return { ok: true, enabled: !!enabled };
  } catch (e) {
    return { ok: false, message: e.message };
  }
});
ipcMain.handle(
  "probe-media",
  async (_, file) =>
    new Promise((resolve) => {
      const p = startChild(
        "ffprobe",
        [
          "-v",
          "error",
          "-show_entries",
          "format=format_name,duration,bit_rate:stream=index,codec_type,codec_name,codec_long_name,width,height,pix_fmt,r_frame_rate,field_order,bit_rate",
          "-of",
          "json",
          file,
        ],
        { stdio: ["ignore", "pipe", "pipe"] },
      );
      let out = "",
        err = "";
      p.stdout.on("data", (d) => (out += String(d)));
      p.stderr.on("data", (d) => (err += String(d)));
      p.on("error", (e) => resolve({ ok: false, error: e.message }));
      p.on("close", (code) => {
        if (code !== 0)
          return resolve({ ok: false, error: err || "FFprobe failed" });
        try {
          let data = JSON.parse(out),
            v = data.streams.find((s) => s.codec_type === "video"),
            a = data.streams.find((s) => s.codec_type === "audio");
          let recommendation = !v
            ? "No video stream detected"
            : [
                  "mpeg1video",
                  "mpeg2video",
                  "mpeg4",
                  "msmpeg4v2",
                  "msmpeg4v3",
                ].includes(v.codec_name)
              ? "Compatibility decoder: FFmpeg. DVB output: H.264 NVENC or libx264"
              : v.codec_name === "hevc"
                ? "Input: HEVC/H.265. HEVC DVB receiver இல்லையெனில் H.264 output தேர்வு செய்யவும்"
                : "Recommended DVB output: H.264 NVENC / libx264";
          resolve({
            ok: true,
            format: data.format,
            video: v || null,
            audio: a || null,
            recommendation,
            embeddedPreviewSupported: v
              ? ["h264", "vp8", "vp9", "av1"].includes(v.codec_name)
              : false,
          });
        } catch (e) {
          resolve({ ok: false, error: e.message });
        }
      });
    }),
);
ipcMain.handle("set-engine-config", async (_, cfg) => {
  activeEngineConfig = { ...activeEngineConfig, ...cfg };
  return { ok: true, ...activeEngineConfig };
});
ipcMain.handle("detect-codec-engine", async (_, cfg = {}) => {
  const file = cfg.file;
  if (!file)
    return {
      ok: false,
      message: "Select a playlist video first, then press TEST / DETECT.",
    };
  const probe = await capture("ffprobe", [
    "-v",
    "error",
    "-select_streams",
    "v:0",
    "-show_entries",
    "stream=codec_name,width,height",
    "-of",
    "json",
    file,
  ]);
  if (!probe.ok)
    return {
      ok: false,
      message:
        "Full FFmpeg could not read this file: " + probe.text.slice(-300),
    };
  const decoder = await selectDecoder(file, cfg.decodeEngine || "auto-lowcpu"),
    encoder = await selectEncoder(
      cfg.encodeEngine === "cpu"
        ? "libx264"
        : cfg.encodeEngine === "microsoft"
          ? "h264_mf"
          : "h264_nvenc",
    );
  let codec = "unknown";
  try {
    codec = JSON.parse(probe.text).streams?.[0]?.codec_name || codec;
  } catch {}
  return {
    ok: true,
    decoder: decoder.name,
    encoder,
    codec,
    message: `INPUT CODEC: ${codec}\nWORKING DECODER: ${decoder.name}\nWORKING ENCODER: ${encoder}\nCPU LIMIT: ${encoder === "libx264" ? "2 threads" : "Hardware acceleration active"}\nNo separate codec pack is required.`,
  };
});
ipcMain.handle("start-compat-preview", async (_, cfg) => {
  if (!cfg || !cfg.file) return { ok: false, error: "Media file required" };
  try {
    const port = await ensurePreviewServer(),
      token = crypto.randomBytes(16).toString("hex"),
      requested = cfg.decodeEngine || activeEngineConfig.decodeEngine;
    pendingPreview.set(token, {
      ...activeEngineConfig,
      ...cfg,
      decodeEngine: requested || "auto-lowcpu",
    });
    return { ok: true, url: `http://127.0.0.1:${port}/preview/${token}` };
  } catch (e) {
    return { ok: false, error: e.message };
  }
});
ipcMain.handle("stop-preview", async () => {
  for (const p of previewProcesses) {
    try {
      p.kill();
    } catch {}
  }
  previewProcesses.clear();
  previewProcess = null;
  return { ok: true };
});
ipcMain.handle("check-ffmpeg", async () => {
  const [v, d, e, f] = await Promise.all([
    capture("ffmpeg", ["-version"]),
    capture("ffmpeg", ["-hide_banner", "-decoders"]),
    capture("ffmpeg", ["-hide_banner", "-encoders"]),
    capture("ffmpeg", ["-hide_banner", "-filters"]),
  ]);
  if (!v.ok)
    return {
      ok: false,
      message:
        "FFmpeg கிடைக்கவில்லை. Install Full FFmpeg அழுத்தவும்.\n" + v.text,
    };
  const all = d.text + e.text + f.text,
    need = [
      "mpeg1video",
      "mpeg2video",
      "h264",
      "hevc",
      "libx264",
      "libx265",
      "h264_nvenc",
      "hevc_nvenc",
      "drawtext",
      "overlay",
    ],
    missing = need.filter((x) => !all.toLowerCase().includes(x));
  return {
    ok: missing.length === 0,
    message:
      (v.text.split(/\r?\n/)[0] || "FFmpeg detected") +
      "\n" +
      (missing.length
        ? "Missing/Unavailable: " + missing.join(", ")
        : "MPEG/H.264/HEVC/NVENC/CG capability detected"),
  };
});
ipcMain.handle("install-full-ffmpeg", async () => {
  if (process.platform !== "win32")
    return {
      ok: false,
      message: "இந்த installer Windows 11-ல் மட்டும் இயங்கும்",
    };
  try {
    const p = spawn(
      "powershell.exe",
      [
        "-NoProfile",
        "-Command",
        'Start-Process winget -Verb RunAs -ArgumentList @("install","--id","Gyan.FFmpeg","--exact","--accept-package-agreements","--accept-source-agreements")',
      ],
      { detached: true, windowsHide: false, stdio: "ignore" },
    );
    p.unref();
    return {
      ok: true,
      message:
        "Full FFmpeg installer தொடங்கப்பட்டது. முடிந்ததும் app-ஐ restart செய்யவும்.",
    };
  } catch (e) {
    return { ok: false, message: e.message };
  }
});
ipcMain.handle("install-ytdlp", async () => {
  if (process.platform !== "win32")
    return {
      ok: false,
      message: "YouTube installer is available on Windows 11 only",
    };
  try {
    const script = `$installed = winget list --id yt-dlp.yt-dlp --exact | Select-String 'yt-dlp'; $action = if ($installed) {'upgrade'} else {'install'}; Start-Process winget -Verb RunAs -ArgumentList @($action,'--id','yt-dlp.yt-dlp','--exact','--accept-package-agreements','--accept-source-agreements')`,
      p = spawn("powershell.exe", ["-NoProfile", "-Command", script], {
        detached: true,
        windowsHide: false,
        stdio: "ignore",
      });
    p.unref();
    return {
      ok: true,
      message:
        "YouTube support install/update started. Restart the app after it completes.",
    };
  } catch (e) {
    return { ok: false, message: e.message };
  }
});
ipcMain.handle("open-media-folder", async (_, file) => {
  shell.showItemInFolder(file);
  return { ok: true };
});
ipcMain.handle("pick-logo", async () => {
  const result = await dialog.showOpenDialog({
    properties: ["openFile", "multiSelections"],
    filters: [
      {
        name: "Logo / CG Media",
        extensions: ["png", "jpg", "jpeg", "gif", "webm", "mov", "mp4", "swf"],
      },
    ],
  });
  if (result.canceled || !result.filePaths.length) return null;
  const files = result.filePaths,
    first = files[0],
    ext = path.extname(first).toLowerCase();
  if (files.length > 1) {
    const m = path.basename(first).match(/^(.*?)(\d+)(\.[^.]+)$/);
    if (!m)
      return { error: "PNG sequence filenames numbered ஆக இருக்க வேண்டும்" };
    return {
      type: "sequence",
      path: first,
      pattern: path.join(
        path.dirname(first),
        `${m[1]}%0${m[2].length}d${m[3]}`,
      ),
      start: Number(m[2]),
      fps: 25,
    };
  }
  if (ext === ".swf") {
    const out = path.join(os.tmpdir(), `sr-musix-logo-${Date.now()}.mov`);
    return await new Promise((resolve) => {
      const p = startChild("ffmpeg", [
        "-y",
        "-i",
        first,
        "-an",
        "-c:v",
        "qtrle",
        "-pix_fmt",
        "argb",
        out,
      ]);
      let err = "";
      p.stderr.on("data", (d) => (err = String(d).slice(-500)));
      p.on("error", (e) => resolve({ error: e.message }));
      p.on("close", (code) =>
        resolve(
          code === 0
            ? { type: "animated", path: out, source: first, converted: true }
            : { error: err || "SWF conversion failed" },
        ),
      );
    });
  }
  return {
    type: [".png", ".jpg", ".jpeg"].includes(ext) ? "static" : "animated",
    path: first,
  };
});
ipcMain.handle("pick-font", async () => {
  const r = await dialog.showOpenDialog({
    title: "Select CG Font",
    properties: ["openFile"],
    defaultPath: process.env.WINDIR
      ? path.join(process.env.WINDIR, "Fonts")
      : undefined,
    filters: [
      { name: "TrueType / OpenType Font", extensions: ["ttf", "otf", "ttc"] },
    ],
  });
  return r.canceled || !r.filePaths.length ? null : r.filePaths[0];
});
ipcMain.handle("start-rtmp", async (_, cfg) => {
  if (ffmpegProcess)
    return { ok: false, message: "An RTMP stream is already running" };
  if (!cfg.file || !/^rtmps?:\/\//i.test(String(cfg.url || "")))
    return {
      ok: false,
      message:
        "Select media and paste one complete RTMP/RTMPS URL including the stream key",
    };
  const resolved = await resolveNetworkInput(cfg.file);
  if (!resolved.ok) return { ok: false, message: resolved.error };
  cfg.file = resolved.url;
  let requested = cfg.encoder || "libx264";
  const hevc = /hevc|265/.test(requested);
  requested =
    requested === "copy" || hevc
      ? requested.includes("nvenc")
        ? "h264_nvenc"
        : "libx264"
      : requested;
  const enc = await selectEncoder(requested),
    decoder = await selectDecoder(cfg.file, cfg.decodeEngine || "auto-lowcpu"),
    aud = cfg.audioCodec === "copy" ? "aac" : cfg.audioCodec || "aac";
  const pre = ["libx264", "libx265"].includes(enc)
    ? [
        "-preset",
        "ultrafast",
        "-threads",
        "2",
      ]
    : [];
  const comp = composition(cfg);
  const available = await capture("ffmpeg", ["-hide_banner", "-encoders"]);
  if (!available.ok)
    return {
      ok: false,
      message:
        "Full FFmpeg is not available. Open Settings and install Full FFmpeg.",
    };
  const seek = Number(cfg.startAt) > 0 ? ["-ss", String(cfg.startAt)] : [],
    limit =
      Number(cfg.endAt) > Number(cfg.startAt || 0)
        ? ["-t", String(Number(cfg.endAt) - Number(cfg.startAt || 0))]
        : [];
  const br = cfg.bitrate || "4500k",
    buf = cfg.bufferSize || "9000k",
    bind = cfg.localAddress ? ["-local_addr", cfg.localAddress] : [];
  ffmpegProcess = startChild(
    "ffmpeg",
    [
      "-re",
      "-fflags",
      "+genpts",
      ...seek,
      ...decoder.args,
      "-stream_loop",
      "-1",
      "-i",
      cfg.file,
      ...comp.inputs,
      ...comp.filters,
      ...comp.maps,
      ...audioFilters(cfg),
      ...limit,
      "-c:v",
      enc,
      ...pre,
      "-pix_fmt",
      "yuv420p",
      "-b:v",
      br,
      "-maxrate",
      br,
      "-bufsize",
      buf,
      "-g",
      String(cfg.gop || 50),
      "-c:a",
      aud,
      "-b:a",
      cfg.audioBitrate || "160k",
      "-ar",
      "48000",
      ...bind,
      "-f",
      "flv",
      cfg.url,
    ],
    { requested: cfg.ffmpeg },
  );
  let err = "";
  ffmpegProcess.stderr.on("data", (d) => (err = String(d).slice(-500)));
  ffmpegProcess.on("close", () => {
    ffmpegProcess = null;
  });
  await new Promise((r) => setTimeout(r, 900));
  if (!ffmpegProcess)
    return {
      ok: false,
      message:
        err ||
        `FFmpeg could not connect to ${String(cfg.url).replace(/\/[^/]*$/, "/••••")}`,
    };
  return {
    ok: true,
    message: `RTMP LIVE • ${enc}${enc !== requested ? " (automatic fallback from " + requested + ")" : ""} • ${br}`,
  };
});
ipcMain.handle("stop-rtmp", async () => {
  if (ffmpegProcess) {
    ffmpegProcess.kill();
    ffmpegProcess = null;
  }
  return { ok: true };
});
ipcMain.handle("start-udp", async (_, cfg) => {
  if (udpProcess)
    return { ok: false, message: "UDP output ஏற்கனவே இயங்குகிறது" };
  if (
    !cfg.file ||
    (cfg.protocol !== "srt" && (!cfg.ip || !cfg.port)) ||
    (cfg.protocol === "srt" && !/^srt:\/\//i.test(String(cfg.srtUrl || "")))
  )
    return {
      ok: false,
      message:
        "Media and a valid UDP/RTP destination or complete SRT URL are required",
    };
  const resolved = await resolveNetworkInput(cfg.file);
  if (!resolved.ok) return { ok: false, message: resolved.error };
  cfg.file = resolved.url;
  const protocol = ["rtp", "srt"].includes(cfg.protocol) ? cfg.protocol : "udp";
  const target =
    protocol === "srt"
      ? cfg.srtUrl
      : `${protocol}://${cfg.ip}:${cfg.port}?pkt_size=1316&ttl=${cfg.ttl || 16}&buffer_size=65535${cfg.localAddress ? "&localaddr=" + encodeURIComponent(cfg.localAddress) : ""}`;
  const comp = composition(cfg);
  const requestedEncoder =
    cfg.encoder === "copy" && cfg.cg ? "libx264" : cfg.encoder || "libx264";
  const encoder = await selectEncoder(requestedEncoder);
  const decoder = await selectDecoder(
    cfg.file,
    cfg.decodeEngine || "auto-lowcpu",
  );
  const preset = ["libx264", "libx265"].includes(encoder)
    ? [
        "-preset",
        "ultrafast",
        "-threads",
        "2",
      ]
    : [];
  const fps = cfg.fps && cfg.fps !== "source" ? ["-r", String(cfg.fps)] : [];
  const audio = cfg.audioCodec || "aac";
  const af = audioFilters(cfg);
  const seek = Number(cfg.startAt) > 0 ? ["-ss", String(cfg.startAt)] : [],
    limit =
      Number(cfg.endAt) > Number(cfg.startAt || 0)
        ? ["-t", String(Number(cfg.endAt) - Number(cfg.startAt || 0))]
        : [];
  const br = cfg.bitrate || "8M",
    rate =
      cfg.bitrateMode === "vbr"
        ? ["-b:v", br]
        : ["-b:v", br, "-minrate", br, "-maxrate", br, "-bufsize", br];
  const args = [
    "-re",
    "-fflags",
    "+genpts",
    ...seek,
    ...decoder.args,
    "-stream_loop",
    "-1",
    "-i",
    cfg.file,
    ...comp.inputs,
    ...comp.filters,
    ...comp.maps,
    ...af,
    ...limit,
    ...fps,
    "-c:v",
    encoder,
    ...preset,
    "-pix_fmt",
    "yuv420p",
    ...rate,
    "-g",
    String(cfg.gop || 50),
    "-keyint_min",
    String(cfg.gop || 50),
    "-sc_threshold",
    "0",
    "-c:a",
    audio,
    "-b:a",
    cfg.audioBitrate || "192k",
    "-ar",
    "48000",
    "-ac",
    cfg.sound && cfg.sound.mono ? "1" : "2",
    "-mpegts_transport_stream_id",
    String(cfg.tsId || 1),
    "-mpegts_original_network_id",
    String(cfg.networkId || 1),
    "-mpegts_service_id",
    String(cfg.serviceId || 1),
    "-mpegts_service_type",
    cfg.serviceType || "digital_tv",
    "-mpegts_pmt_start_pid",
    String(cfg.pmtPid || 4096),
    "-streamid",
    `0:${cfg.videoPid || 256}`,
    "-streamid",
    `1:${cfg.audioPid || 257}`,
    "-metadata",
    `service_provider=${cfg.providerName || "SR NETWORK"}`,
    "-metadata",
    `service_name=${cfg.serviceName || "SR MUSIX HD"}`,
    "-mpegts_flags",
    "+resend_headers",
    "-pat_period",
    String(cfg.patPeriod || 0.1),
    "-sdt_period",
    String(cfg.sdtPeriod || 0.5),
    "-pcr_period",
    String(cfg.pcrPeriod || 20),
    ...(cfg.muxRate && cfg.muxRate !== "0"
      ? ["-muxrate", String(cfg.muxRate)]
      : []),
    "-f",
    protocol === "rtp" ? "rtp_mpegts" : "mpegts",
    target,
  ];
  udpProcess = startChild("ffmpeg", args, { requested: cfg.ffmpeg });
  let err = "";
  udpProcess.stderr.on("data", (d) => (err = String(d).slice(-700)));
  udpProcess.on("close", () => {
    udpProcess = null;
  });
  await new Promise((r) => setTimeout(r, 1200));
  if (
    !udpProcess &&
    cfg.autoFallback !== false &&
    !["libx264", "libx265", "mpeg2video", "copy"].includes(encoder)
  ) {
    const fallback = encoder.includes("hevc") ? "libx265" : "libx264";
    const retry = args.map((x) => (x === encoder ? fallback : x));
    udpProcess = startChild("ffmpeg", retry, { requested: cfg.ffmpeg });
    udpProcess.on("close", () => {
      udpProcess = null;
    });
    await new Promise((r) => setTimeout(r, 1200));
    if (udpProcess)
      return {
        ok: true,
        message: `UDP LIVE • ${encoder} கிடைக்கவில்லை; ${fallback} fallback பயன்படுத்தப்படுகிறது`,
      };
  }
  if (!udpProcess)
    return { ok: false, message: err || "FFmpeg UDP தொடங்கவில்லை" };
  const fallbackNote =
    encoder !== requestedEncoder
      ? ` • ${requestedEncoder} → ${encoder} AUTO FALLBACK`
      : "";
  return {
    ok: true,
    message:
      (protocol === "srt"
        ? `SRT LIVE → ${target}`
        : `DVB/${protocol.toUpperCase()} LIVE → ${cfg.ip}:${cfg.port}`) +
      fallbackNote,
  };
});
ipcMain.handle("stop-udp", async () => {
  if (udpProcess) {
    udpProcess.kill();
    udpProcess = null;
  }
  return { ok: true };
});
ipcMain.handle("ffplay-preview", async (_, input) => {
  const cfg = typeof input === "string" ? { file: input } : input;
  if (previewProcess) previewProcess.kill();
  let visual = { ...cfg, cg: { ...(cfg.cg || {}), logoFile: null } };
  previewProcess = startChild(
    "ffplay",
    [
      "-autoexit",
      "-window_title",
      "SR MUSIX HD - FFmpeg Preview",
      ...videoFilters(visual),
      ...audioFilters(cfg),
      cfg.file,
    ],
    { requested: cfg.ffplay },
  );
  previewProcess.on("error", () => {
    previewProcess = null;
  });
  previewProcess.on("close", () => {
    previewProcess = null;
  });
  return { ok: true };
});
ipcMain.handle("output-info", async () => {
  const { screen } = require("electron");
  return {
    displays: screen
      .getAllDisplays()
      .map((d, i) => ({
        id: String(d.id),
        name: `Display ${i + 1}`,
        size: `${d.size.width}x${d.size.height}`,
      })),
  };
});
ipcMain.handle("network-interfaces", async () => {
  let rows = [];
  for (const [name, items] of Object.entries(os.networkInterfaces()))
    for (const x of items || [])
      if (x.family === "IPv4" && !x.internal)
        rows.push({
          name,
          address: x.address,
          netmask: x.netmask,
          mac: x.mac,
          label: `${name} — ${x.address}`,
        });
  return rows;
});
ipcMain.handle("read-clipboard-text", async () => clipboard.readText());
ipcMain.handle("system-stats", async () => {
  const now = os.cpus(),
    per = now.map((c, i) => {
      const old = cpuSnapshot[i] || c.times,
        total = Object.values(c.times).reduce((a, b) => a + b, 0),
        prior = Object.values(old).reduce((a, b) => a + b, 0),
        delta = Math.max(1, total - prior),
        idle = c.times.idle - old.idle;
      return Math.max(0, Math.min(100, (100 * (delta - idle)) / delta));
    });
  cpuSnapshot = now.map((c) => ({ ...c.times }));
  return {
    cpu: per.length ? per.reduce((a, b) => a + b, 0) / per.length : 0,
    cores: per.length,
    memory: 100 * (1 - os.freemem() / os.totalmem()),
  };
});
ipcMain.handle(
  "probe-decklink",
  async (_, ffmpeg = "") =>
    new Promise((resolve) => {
      const p = startChild(
        "ffmpeg",
        ["-hide_banner", "-f", "decklink", "-list_devices", "1", "-i", "dummy"],
        { requested: ffmpeg },
      );
      let out = "";
      p.stderr.on("data", (d) => (out += String(d)));
      p.on("error", (e) => resolve({ ok: false, message: e.message }));
      p.on("close", () =>
        resolve({
          ok: /decklink/i.test(out) && !/Unknown input format/.test(out),
          message: out.slice(-3000),
        }),
      );
    }),
);
ipcMain.handle("start-decklink", async (_, cfg) => {
  if (!cfg.file || !cfg.device)
    return { ok: false, message: "Media மற்றும் DeckLink device name தேவை" };
  if (ffmpegProcess)
    return { ok: false, message: "மற்றொரு hardware/RTMP output இயங்குகிறது" };
  if (cfg.width && cfg.height)
    cfg.resolution = `scale=${cfg.width}:${cfg.height}:force_original_aspect_ratio=decrease,pad=${cfg.width}:${cfg.height}:(ow-iw)/2:(oh-ih)/2`;
  const comp = composition(cfg);
  ffmpegProcess = startChild(
    "ffmpeg",
    [
      "-re",
      "-fflags",
      "+genpts",
      "-stream_loop",
      "-1",
      "-i",
      cfg.file,
      ...comp.inputs,
      ...comp.filters,
      ...comp.maps,
      ...audioFilters(cfg),
      "-r",
      cfg.fps || "25",
      "-pix_fmt",
      "uyvy422",
      "-ar",
      "48000",
      "-ac",
      "2",
      "-f",
      "decklink",
      cfg.device,
    ],
    { requested: cfg.ffmpeg },
  );
  let err = "";
  ffmpegProcess.stderr.on("data", (d) => (err = String(d).slice(-800)));
  ffmpegProcess.on("close", () => {
    ffmpegProcess = null;
  });
  await new Promise((r) => setTimeout(r, 1200));
  return ffmpegProcess
    ? { ok: true, message: `DeckLink LIVE + CG → ${cfg.device}` }
    : { ok: false, message: err || "DeckLink தொடங்கவில்லை" };
});
