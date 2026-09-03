const express = require('express');
const multer = require('multer');
const { spawn, spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const app = express();
const PORT = Number(process.env.PORT || 3000);
const root = __dirname;
const mediaDir = path.join(root, 'media');
const hlsDir = path.join(root, 'runtime', 'hls');
fs.mkdirSync(mediaDir, { recursive: true });
fs.mkdirSync(hlsDir, { recursive: true });

const ffmpeg = process.env.FFMPEG_PATH || 'ffmpeg';
const ffprobe = process.env.FFPROBE_PATH || 'ffprobe';
let previewProcess = null;
let outputProcess = null;
let currentId = null;
let lastError = '';
const playlist = [];

function cleanName(name) {
  return path.basename(name).replace(/[^\p{L}\p{N}._()\- ]/gu, '_');
}
const storage = multer.diskStorage({
  destination: mediaDir,
  filename: (_req, file, cb) => cb(null, `${Date.now()}-${crypto.randomUUID()}-${cleanName(file.originalname)}`)
});
const upload = multer({ storage, limits: { fileSize: 500 * 1024 * 1024 * 1024 } });

app.use(express.json({ limit: '2mb' }));
app.use(express.static(path.join(root, 'public')));
app.get('/vendor/hls.js', (_req, res) => res.sendFile(path.join(root, 'node_modules', 'hls.js', 'dist', 'hls.min.js')));
app.use('/hls', express.static(hlsDir, { etag: false, maxAge: 0 }));

function stopProcess(proc) {
  if (proc && !proc.killed) proc.kill('SIGKILL');
}
function probe(file) {
  const p = spawnSync(ffprobe, ['-v', 'error', '-show_entries', 'format=duration', '-of', 'json', file], { encoding: 'utf8' });
  try { return Number(JSON.parse(p.stdout).format.duration || 0); } catch { return 0; }
}
function itemView(x) { return { id: x.id, name: x.name, duration: x.duration, schedule: x.schedule || '', status: x.id === currentId ? 'ON AIR' : 'Ready' }; }

app.get('/api/health', (_req, res) => {
  const f = spawnSync(ffmpeg, ['-version'], { encoding: 'utf8' });
  res.json({ ok: f.status === 0, ffmpeg: f.status === 0, port: PORT, error: f.status === 0 ? '' : 'FFmpeg was not found. Add it to PATH or set FFMPEG_PATH.' });
});
app.get('/api/playlist', (_req, res) => res.json(playlist.map(itemView)));
app.post('/api/media', upload.array('files'), (req, res) => {
  const added = (req.files || []).map(file => {
    const item = { id: crypto.randomUUID(), name: file.originalname, file: file.path, duration: probe(file.path), schedule: '' };
    playlist.push(item); return itemView(item);
  });
  res.json({ added, playlist: playlist.map(itemView) });
});
app.patch('/api/items/:id', (req, res) => {
  const item = playlist.find(x => x.id === req.params.id);
  if (!item) return res.status(404).json({ error: 'Item not found' });
  item.schedule = String(req.body.schedule || '');
  res.json(itemView(item));
});
app.delete('/api/items/:id', (req, res) => {
  const i = playlist.findIndex(x => x.id === req.params.id);
  if (i < 0) return res.status(404).json({ error: 'Item not found' });
  if (playlist[i].id === currentId) { stopProcess(previewProcess); currentId = null; }
  const [removed] = playlist.splice(i, 1);
  fs.rm(removed.file, { force: true }, () => {});
  res.json(playlist.map(itemView));
});
app.delete('/api/playlist', (_req, res) => {
  stopProcess(previewProcess); stopProcess(outputProcess); currentId = null;
  for (const x of playlist) fs.rm(x.file, { force: true }, () => {});
  playlist.length = 0; res.json([]);
});

function startPreview(item) {
  stopProcess(previewProcess);
  fs.rmSync(hlsDir, { recursive: true, force: true }); fs.mkdirSync(hlsDir, { recursive: true });
  lastError = ''; currentId = item.id;
  const args = ['-hide_banner', '-loglevel', 'warning', '-re', '-i', item.file,
    '-map', '0:v:0?', '-map', '0:a:0?', '-c:v', 'libx264', '-preset', 'veryfast', '-tune', 'zerolatency',
    '-pix_fmt', 'yuv420p', '-g', '50', '-c:a', 'aac', '-b:a', '192k', '-ar', '48000',
    '-f', 'hls', '-hls_time', '1', '-hls_list_size', '8', '-hls_flags', 'delete_segments+append_list+independent_segments', path.join(hlsDir, 'live.m3u8')];
  previewProcess = spawn(ffmpeg, args, { windowsHide: true });
  previewProcess.stderr.on('data', d => { lastError = String(d).slice(-1000); });
  previewProcess.on('close', () => { previewProcess = null; });
}
app.post('/api/play/:id', (req, res) => {
  const item = playlist.find(x => x.id === req.params.id);
  if (!item) return res.status(404).json({ error: 'Item not found' });
  startPreview(item); res.json({ ok: true, url: `/hls/live.m3u8?t=${Date.now()}`, item: itemView(item) });
});
app.post('/api/stop', (_req, res) => { stopProcess(previewProcess); previewProcess = null; currentId = null; res.json({ ok: true }); });
app.get('/api/status', (_req, res) => res.json({ currentId, preview: !!previewProcess, output: !!outputProcess, error: lastError }));

app.post('/api/output/start', (req, res) => {
  const item = playlist.find(x => x.id === (req.body.id || currentId));
  if (!item) return res.status(400).json({ error: 'Select and play an item first.' });
  const destination = String(req.body.destination || '').trim();
  if (!destination) return res.status(400).json({ error: 'Output destination is required.' });
  stopProcess(outputProcess);
  const codec = req.body.codec === 'h265' ? 'hevc_nvenc' : req.body.codec === 'h264-cpu' ? 'libx264' : 'h264_nvenc';
  const bitrate = Math.max(500, Number(req.body.bitrate || 8000));
  const format = destination.toLowerCase().startsWith('rtmp') ? 'flv' : 'mpegts';
  const args = ['-hide_banner', '-re', '-i', item.file, '-c:v', codec, '-b:v', `${bitrate}k`, '-maxrate', `${bitrate}k`, '-bufsize', `${bitrate * 2}k`, '-c:a', 'aac', '-b:a', '192k', '-ar', '48000', '-f', format, destination];
  outputProcess = spawn(ffmpeg, args, { windowsHide: true });
  outputProcess.stderr.on('data', d => { lastError = String(d).slice(-1000); });
  outputProcess.on('close', () => { outputProcess = null; });
  res.json({ ok: true });
});
app.post('/api/output/stop', (_req, res) => { stopProcess(outputProcess); outputProcess = null; res.json({ ok: true }); });

setInterval(() => {
  const now = Date.now();
  const due = playlist.find(x => x.schedule && !x.triggered && new Date(x.schedule).getTime() <= now);
  if (due) { due.triggered = true; startPreview(due); }
}, 500);

app.listen(PORT, () => console.log(`SR MUSIX Playout: http://localhost:${PORT}`));
