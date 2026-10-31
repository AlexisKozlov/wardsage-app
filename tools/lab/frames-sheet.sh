#!/usr/bin/env bash
# Contact sheet of the hint frames of a lab run: each "frame check:" line of the test app's log (src/SpriteArt.cs CheckLine: the frame, the
# visible art, the client and its monitor) cropped from the run's screenshots (the stop / lead-up shots carry our overlay), one row per
# frame, one column per shot (the newest 4), the logged frame in thin cyan over the crop, so the frames can be eyeballed in one picture.
#   frames-sheet.sh <run id> [out.png]                       (staging: the run's app_test_logs and lab_files; default out: $LAB_DIR/frames-<run>.png)
#   frames-sheet.sh --log <log file> --shots <dir> [out.png] (offline: a log and a folder of .jpg/.png screenshots)
# Uses sharp from ~/wardsage/wardsage-web/node_modules (no new dependency). Prints the sheet's path, or "no frame checks".
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
SHARP_FROM=${SHARP_FROM:-$HOME/wardsage/wardsage-web/package.json}

if [ "${1:-}" = --log ]; then
  log=${2:-}; [ "${3:-}" = --shots ] || { echo "usage: frames-sheet.sh --log <file> --shots <dir> [out.png]" >&2; exit 2; }
  shots=${4:-}; out=${5:-./frames-sheet.png}
  [ -f "$log" ] && [ -d "$shots" ] || { echo "no log file or shots folder" >&2; exit 2; }
else
  . "$HERE/lab-common.sh"
  run=${1:-}
  [[ "$run" =~ ^[0-9a-f-]{36}$ ]] || { echo "usage: frames-sheet.sh <run id> [out.png]" >&2; exit 2; }
  out=${2:-$LAB_DIR/frames-${run:0:8}.png}
  log=$(mktemp); trap 'rm -f "$log"' EXIT
  q -v r="$run" <<< "
    select l.lines from public.app_test_logs l
    join public.lab_devices dv on dv.account_id = l.account_id
    join public.lab_runs r on r.device_id = dv.id
    where r.id = :'r'::uuid and l.at >= r.started_at - interval '2 minutes' and l.at <= coalesce(r.finished_at, now()) + interval '2 minutes' order by l.at, l.id" > "$log"
  shots="$LAB_DIR/shots-${run:0:8}"
  bash "$HERE/lab-shots.sh" "$run" "$shots" > /dev/null
fi
grep -q 'frame check: ' "$log" || { echo "no frame checks"; exit 0; }

node --input-type=module - "$log" "$shots" "$out" "$SHARP_FROM" <<'EOF'
import fs from 'fs';
import path from 'path';
import { createRequire } from 'module';
const [logF, dir, out, from] = process.argv.slice(2);
const sharp = createRequire(from)('sharp');
const nums = (s) => (s || '').split(',').map(Number);
const rx = /frame check: (\S+) sprite=(.*?) shape=(\w+) frame=\[([-\d,]+)\](?: visible=\[([-\d,]+)\])? client=\[([-\d,]+)\] screen=\[([-\d,]+)\]/;
// one row per target and source (src=live|layout: src/FrameTargets.cs, app/Overlay.cs FrameFor), so every scenario's targets are on the sheet
const seen = new Set(), frames = [];
const lines = fs.readFileSync(logF, 'utf8').split('\n');
lines.forEach((line, at) => {
  const m = rx.exec(line); if (!m) return;
  const src = (/ src=(\w+)/.exec(line) || [])[1] || 'live';
  const key = m[1] + '|' + src; if (seen.has(key)) return; seen.add(key);
  frames.push({ who: m[1] + ' (' + src + ')', sprite: m[2].split(' ')[0], shape: m[3], f: nums(m[4]), v: m[5] ? nums(m[5]) : null, c: nums(m[6]), s: nums(m[7]), p: at / Math.max(1, lines.length - 1) });
});
// the run's shots in their order (lab-shots.sh writes them as the lab made them); each frame is cropped from the 3 shots nearest its own place in
// the log (a scenario's frames from that scenario's shots)
const all = fs.readdirSync(dir).filter((n) => /\.(jpe?g|png)$/i.test(n)).map((n) => path.join(dir, n))
  .sort((a, b) => fs.statSync(a).mtimeMs - fs.statSync(b).mtimeMs || a.localeCompare(b));
const near = (p) => { const c = Math.round(p * (all.length - 1)); const lo = Math.max(0, Math.min(all.length - 3, c - 1)); return all.slice(lo, lo + 3); };
const files = all;
if (!frames.length || !files.length) { console.log(frames.length ? 'no screenshots in ' + dir : 'no frame checks'); process.exit(0); }
const TH = 150, LW = 330, PAD = 6;
const esc = (t) => t.replace(/[&<>]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c]);
const metaOf = new Map((await Promise.all(files.map(async (f) => [f, await sharp(f).metadata()]))));
const rows = [];
for (const fr of frames) {
  const tiles = [];
  const mine = near(fr.p);
  for (let i = 0; i < mine.length; i++) {
    const { width: iw, height: ih } = metaOf.get(mine[i]);
    const k = iw / fr.s[2];                                   // image px per screen px (the shot is the monitor, scaled)
    const m = Math.max(12, 0.6 * Math.max(fr.f[2], fr.f[3]));
    const sx = fr.c[0] - fr.s[0] + fr.f[0] - m, sy = fr.c[1] - fr.s[1] + fr.f[1] - m, sw = fr.f[2] + 2 * m, sh = fr.f[3] + 2 * m;
    let l = Math.round(sx * k), t = Math.round(sy * k), w = Math.round(sw * k), h = Math.round(sh * k);
    l = Math.max(0, Math.min(iw - 2, l)); t = Math.max(0, Math.min(ih - 2, t)); w = Math.max(2, Math.min(iw - l, w)); h = Math.max(2, Math.min(ih - t, h));
    const z = TH / h, tw = Math.max(1, Math.round(w * z));
    const fx = ((fr.c[0] - fr.s[0] + fr.f[0]) * k - l) * z, fy = ((fr.c[1] - fr.s[1] + fr.f[1]) * k - t) * z;
    const box = `<svg width="${tw}" height="${TH}"><rect x="${fx.toFixed(1)}" y="${fy.toFixed(1)}" width="${(fr.f[2] * k * z).toFixed(1)}" height="${(fr.f[3] * k * z).toFixed(1)}" fill="none" stroke="#3ff" stroke-width="1" stroke-dasharray="3,3"/></svg>`;
    const buf = await sharp(mine[i]).extract({ left: l, top: t, width: w, height: h }).resize(tw, TH).composite([{ input: Buffer.from(box), top: 0, left: 0 }]).png().toBuffer();
    tiles.push({ buf, w: tw });
  }
  rows.push({ fr, tiles });
}
const W = LW + Math.max(...rows.map((r) => r.tiles.reduce((a, t) => a + t.w + PAD, 0))) + PAD, H = rows.length * (TH + PAD) + PAD;
const comps = [];
rows.forEach((r, j) => {
  const y = PAD + j * (TH + PAD), fr = r.fr;
  const txt = [fr.who, 'sprite ' + fr.sprite, 'shape ' + fr.shape, 'frame ' + fr.f.slice(0, 4).join(','), fr.v ? 'visible ' + fr.v.join(',') : ''].filter(Boolean);
  const svg = `<svg width="${LW}" height="${TH}"><style>text{font:13px sans-serif;fill:#eee}</style>${txt.map((s, i) => `<text x="6" y="${20 + i * 19}">${esc(s.slice(0, 46))}</text>`).join('')}</svg>`;
  comps.push({ input: Buffer.from(svg), top: y, left: 0 });
  let x = LW;
  for (const t of r.tiles) { comps.push({ input: t.buf, top: y, left: x }); x += t.w + PAD; }
});
await sharp({ create: { width: W, height: H, channels: 3, background: '#1a1a22' } }).composite(comps).png().toFile(out);
console.log(out + ' (' + rows.length + ' targets, ' + files.length + ' shots)');
EOF
