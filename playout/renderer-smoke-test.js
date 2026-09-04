const { JSDOM } = require("jsdom");
const fs = require("fs");
const dom = new JSDOM(fs.readFileSync("index.html", "utf8"), {
  url: "http://localhost/app/index.html",
  runScripts: "outside-only",
  pretendToBeVisual: true,
});
const w = dom.window;
w.HTMLDialogElement.prototype.showModal = function () {
  this.open = true;
};
w.HTMLDialogElement.prototype.close = function () {
  this.open = false;
  this.dispatchEvent(new w.Event("close"));
};
w.HTMLMediaElement.prototype.load = function () {};
w.HTMLMediaElement.prototype.play = async function () {};
w.HTMLMediaElement.prototype.pause = function () {};
w.Element.prototype.requestFullscreen = async function () {};
w.alert = () => {};
w.prompt = () => null;
w.AudioContext = class {
  createMediaElementSource() {
    return {
      connect() {
        return this;
      },
    };
  }
  createcreate() {}
  createBiquadFilter() {
    return {
      connect() {
        return this;
      },
      frequency: {},
      Q: {},
      gain: {},
    };
  }
  createDynamicsCompressor() {
    return {
      connect() {
        return this;
      },
      threshold: {},
      knee: {},
      ratio: {},
    };
  }
  createGain() {
    return {
      connect() {
        return this;
      },
      gain: {},
    };
  }
  get destination() {
    return {};
  }
};
const ok = (x = {}) => Promise.resolve({ ok: true, ...x });
w.playoutAPI = {
  pickMedia: () => ok([]),
  savePlaylistFile: () => ok(),
  loadPlaylistFile: () => ok({ canceled: true }),
  saveProjectFile: () => ok(),
  loadProjectFile: () => ok({ canceled: true }),
  setAutoStart: () => ok(),
  pickLogo: () => ok(null),
  pickFont: () => ok(null),
  probeMedia: () => ok({ format: { duration: 10 }, embeddedPreviewSupported: true }),
  openMediaFolder: () => ok(),
  startRTMP: () => ok({ message: "ok" }),
  stopRTMP: () => ok(),
  startUDP: () => ok({ message: "ok" }),
  stopUDP: () => ok(),
  outputInfo: () => ok({ displays: [] }),
  networkInterfaces: () => ok([]),
  readClipboardText: () => ok("https://example.com/video.mp4"),
  systemStats: () => ok({ cpu: 25, memory: 40 }),
  droppedFilePath: (f) => f.name,
  probeDeckLink: () => ok(),
  startDeckLink: () => ok({ message: "ok" }),
  ffplayPreview: () => ok(),
  startCompatPreview: () => ok({ url: "http://127.0.0.1/test" }),
  stopPreview: () => ok(),
  setEngineConfig: () => ok(),
  detectCodecEngine: () => ok({ message: "WORKING DECODER" }),
  checkFFmpeg: () => ok({ message: "ok" }),
  installFFmpeg: () => ok({ message: "ok" }),
  installYtDlp: () => ok({ message: "ok" }),
  resolveNetworkMedia: (u) => ok({ url: u }),
};
try {
  w.eval(fs.readFileSync("app.js", "utf8"));
  const click = (id) => {
    const el = w.document.getElementById(id);
    if (!el) throw new Error(`Missing control: ${id}`);
    el.click();
    return el;
  };
  click("streamingPageBtn");
  if (!w.document.getElementById("pageStreaming").open)
    throw new Error("Streaming page did not open");
  const rtmp = w.document.getElementById("rtmp"),
    rtmpCodec = w.document.getElementById("rtmpCodec");
  if (
    !rtmp ||
    !rtmp.placeholder.includes("stream-key") ||
    !rtmpCodec ||
    rtmpCodec.options.length < 6 ||
    !["libx264", "h264_nvenc", "h264_mf"].includes(rtmpCodec.value)
  )
    throw new Error("Single-line RTMP URL or automatic codec selector missing");
  if (!w.document.querySelector(".retired-rtmp-key"))
    throw new Error("Legacy split RTMP key field was not retired");
  w.document.getElementById("pageStreaming").close();
  click("audioPageBtn");
  if (!w.document.getElementById("pageAudio").open)
    throw new Error("Audio page did not open");
  w.document.getElementById("pageAudio").close();
  click("generatePids");
  if (
    w.document.getElementById("videoPid").value !== "256" ||
    w.document.getElementById("pcrPid").value !== "256"
  )
    throw new Error("Automatic PID generation failed");
  if (!w.document.getElementById("bitrateMode"))
    throw new Error("Manual bitrate mode missing");
  if (
    !w.document.getElementById("udpLiveLamp") ||
    !w.document.getElementById("rtmpLiveLamp")
  )
    throw new Error("Main streaming lamps missing");
  const url = w.document.getElementById("networkUrlInput");
  url.focus();
  url.value = "https://example.com/test.mp4";
  if (w.document.activeElement !== url || url.value.indexOf("example.com") < 0)
    throw new Error("Network URL field is not editable");
  if (
    !w.document.getElementById("networkPreviewDock") ||
    !w.document
      .getElementById("networkPreviewDock")
      .classList.contains("network-preview-dock")
  )
    throw new Error("Right-bottom network preview dock missing");
  click("newSchedule");
  const date = w.document.getElementById("schDate"),
    clock = w.document.getElementById("schClock");
  date.value = "2026-09-04";
  clock.value = "12:30:00";
  if (
    !w.document.getElementById("scheduleDialog").open ||
    date.disabled ||
    clock.disabled ||
    !date.value ||
    !clock.value
  )
    throw new Error("Schedule date/time controls are not selectable");
  w.document.getElementById("scheduleDialog").close();
  if (w.document.querySelectorAll(".eq-band-grid input").length !== 10)
    throw new Error("10-band equalizer missing");
  if (!w.document.getElementById("cpuGraph"))
    throw new Error("CPU usage graph missing");
  if (
    !w.document.getElementById("playlistDropZone") ||
    !w.document.getElementById("activeProgramDisplay") ||
    !w.document.getElementById("activeSongDisplay")
  )
    throw new Error("Drop zone or active program display missing");
  if (
    !w.document.getElementById("playoutStoryboard") ||
    !w.document.getElementById("storyNowTiming")
  )
    throw new Error("Now/Next storyboard and timing missing");
  if (!w.document.getElementById("playlistPreviewPlayer"))
    throw new Error("Independent playlist preview missing");
  if (
    !/d\.ondblclick\s*=\s*\(\)\s*=>\s*previewPlaylistItem\(i\)/.test(
      fs.readFileSync("app.js", "utf8"),
    )
  )
    throw new Error(
      "Playlist double-click is not isolated from On-Air playback",
    );
  const seek = w.document.getElementById("seek");
  seek.value = "50";
  seek.dispatchEvent(new w.Event("input", { bubbles: true }));
  if (!w.document.getElementById("storyNowTiming").textContent.includes("SEEK"))
    throw new Error("Manual seek cursor is not responsive");
  if (
    !w.document.getElementById("videoSeekConsole") ||
    seek.closest("#seekTrackMount") === null ||
    !w.document.getElementById("seekBack1") ||
    !w.document.getElementById("seekForward1")
  )
    throw new Error("Dedicated full-width 3D seek console missing");
  if (
    !w.document.getElementById("frontProgramName") ||
    !w.document.getElementById("frontProgramFileCount")
  )
    throw new Error("Front program/file list summary missing");
  if (
    !w.document.getElementById("udpNetworkCard") ||
    !w.document.getElementById("streamProtocol")
  )
    throw new Error("UDP/RTP Ethernet card or protocol selector missing");
  if (
    !w.document.getElementById("autoStartRtmp") ||
    !w.document.getElementById("autoStartUdp")
  )
    throw new Error("Automatic stream controls missing");
  if (!w.document.getElementById("previewDecoder"))
    throw new Error("Network preview decoder selector missing");
  if (
    !w.document.getElementById("refreshSchedules") ||
    !w.document.getElementById("refreshFillers")
  )
    throw new Error("Manager refresh controls missing");
  if (
    !w.document.querySelector("main").classList.contains("main-two-column") ||
    !w.document
      .querySelector("main>aside")
      .classList.contains("retired-main-sidebar")
  )
    throw new Error("Unused main sidebar was not retired");
  if (
    !w.document.querySelector(
      ".program-library-layout>.program-project-pane",
    ) ||
    !w.document.querySelector(".program-library-layout>.program-editor")
  )
    throw new Error("Project/song side-by-side layout missing");
  const source = fs.readFileSync("app.js", "utf8");
  for (const ext of ["vob", "vop", "dat", "m2p", "m2b", "avi", "mpeg"])
    if (!new RegExp(`[\"']${ext}[\"']`).test(source))
      throw new Error("Legacy compatibility routing missing: " + ext);
  w.localStorage.setItem(
    "scheduleItems",
    JSON.stringify([
      {
        id: 99,
        title: "Test",
        type: "Program",
        when: "2026-09-04T12:30:00.000Z",
      },
    ]),
  );
  w.eval("refreshPlaylistSchedules()");
  if (!w.document.querySelector('[data-edit-schedule="program:99"]'))
    throw new Error("Schedule EDIT action missing");
  const remove = w.document.querySelector(
    '[data-remove-schedule="program:99"]',
  );
  if (!remove) throw new Error("Schedule REMOVE action missing");
  remove.click();
  if (JSON.parse(w.localStorage.getItem("scheduleItems")).length)
    throw new Error("Schedule REMOVE action failed");
  if (
    !/localStorage\.setItem\([\"']cgSettings[\"']/.test(source) ||
    !source.includes("playoutResumeState") ||
    !source.includes("animateAudioMeters")
  )
    throw new Error("CG persistence, resume state, or live L/R meters missing");
  const css = fs.readFileSync("v32.css", "utf8");
  if (
    !/\.screen\.media-active \.mcr-bars\{display:none!important\}/.test(css) ||
    !/#programPreviewPlayer,#networkPreviewPlayer\{background-color:#000!important/.test(
      css,
    )
  )
    throw new Error("Preview transparency / colour-bar guard missing");
  const actionClasses = [
    "action-green",
    "action-red",
    "action-purple",
    "action-orange",
    "action-cyan",
  ];
  const unthemed = [
    ...w.document.querySelectorAll("button:not([data-window-action])"),
  ].filter((b) => !actionClasses.some((c) => b.classList.contains(c)));
  if (unthemed.length)
    throw new Error(
      "Unthemed action buttons: " +
        unthemed.map((b) => b.id || b.textContent.trim()).join(", "),
    );
  const requiredPages = [
    "pagePrograms",
    "pageSchedule",
    "pageCG",
    "pageCorrection",
    "pageSettings",
    "pageStreaming",
    "pageAudio",
  ];
  requiredPages.forEach((id) => {
    const page = w.document.getElementById(id);
    if (
      !page ||
      !page.querySelector('[data-window-action="min"]') ||
      !page.querySelector('[data-window-action="max"]') ||
      !page.querySelector('[data-window-action="close"]')
    )
      throw new Error("Window controls missing: " + id);
  });
  console.log("RENDERER_STARTUP_PASS");
  process.exit(0);
} catch (e) {
  console.error("RENDERER_STARTUP_FAIL");
  console.error(e.stack);
  process.exit(1);
}
