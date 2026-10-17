// Draws a replay (tests/RecReplay.cs) over the recorded pictures: the rectangles the app drew then in red, the ones the pure code gives now in green,
// and on top left the time, the screen, the step and the mismatches.
//
//   node tests/rec-draw.mjs <recording dir with part_*.zip> <replay out dir> <png out dir> [--every N]
//
// Needs sharp (taken from the site's node_modules by an absolute path). The recorder saves frames downscaled (the "scale" of the line), the
// rectangles are in the game window's client px: they are mapped by the picture's size. The zip parts are read here (stored / deflated entries).
import { createRequire } from 'node:module';
import { readFileSync, readdirSync, mkdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { inflateRawSync } from 'node:zlib';

const require = createRequire(import.meta.url);
const sharp = require('/home/dev/wardsage/wardsage-web/node_modules/sharp');

const args = process.argv.slice(2);
let every = 1;
const ei = args.indexOf('--every');
if (ei >= 0) { every = Math.max(1, parseInt(args[ei + 1], 10) || 1); args.splice(ei, 2); }
if (args.length < 3) { console.error('usage: node tests/rec-draw.mjs <recording dir> <replay out dir> <png out dir> [--every N]'); process.exit(2); }
const [recDir, replayDir, pngDir] = args;

// ---- a small zip reader: the central directory, then each entry's data (method 0 stored, 8 deflate)
function zipEntries(buf) {
  let e = buf.length - 22;
  while (e >= 0 && buf.readUInt32LE(e) !== 0x06054b50) e--;
  if (e < 0) throw new Error('not a zip');
  const n = buf.readUInt16LE(e + 10);
  let p = buf.readUInt32LE(e + 16);
  const out = new Map();
  for (let i = 0; i < n; i++) {
    if (buf.readUInt32LE(p) !== 0x02014b50) throw new Error('bad central directory');
    const method = buf.readUInt16LE(p + 10), csize = buf.readUInt32LE(p + 20);
    const nameLen = buf.readUInt16LE(p + 28), extraLen = buf.readUInt16LE(p + 30), commentLen = buf.readUInt16LE(p + 32);
    const local = buf.readUInt32LE(p + 42);
    const name = buf.toString('utf8', p + 46, p + 46 + nameLen);
    out.set(name, () => {
      const lnl = buf.readUInt16LE(local + 26), lel = buf.readUInt16LE(local + 28);
      const data = buf.subarray(local + 30 + lnl + lel, local + 30 + lnl + lel + csize);
      return method === 0 ? Buffer.from(data) : inflateRawSync(data);
    });
    p += 46 + nameLen + extraLen + commentLen;
  }
  return out;
}

const pictures = new Map();   // frame name -> () => jpeg bytes
for (const f of readdirSync(recDir).filter(f => /^part_\d{3}\.zip$/.test(f)).sort()) {
  for (const [name, get] of zipEntries(readFileSync(join(recDir, f)))) {
    const m = /^(frame_\d+)\.jpg$/.exec(name);
    if (m) pictures.set(m[1], get);
  }
}

const esc = s => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

function rects(list, s, color, dash) {
  let svg = '';
  for (const r of list || []) {
    const x = r.x * s, y = r.y * s, w = Math.max(1, r.w * s), h = Math.max(1, r.h * s);
    svg += `<rect x="${x.toFixed(1)}" y="${y.toFixed(1)}" width="${w.toFixed(1)}" height="${h.toFixed(1)}" fill="none" stroke="${color}" stroke-width="2"${dash ? ' stroke-dasharray="6 3"' : ''}/>`;
  }
  return svg;
}

mkdirSync(pngDir, { recursive: true });
const frames = readdirSync(replayDir).filter(f => /^frame_\d+\.json$/.test(f)).sort();
let done = 0;
for (let i = 0; i < frames.length; i += every) {
  const j = JSON.parse(readFileSync(join(replayDir, frames[i]), 'utf8'));
  const get = pictures.get(j.frame);
  if (!get) continue;
  const jpg = get();
  const meta = await sharp(jpg).metadata();
  const W = meta.width, H = meta.height;
  const s = j.w > 0 ? W / j.w : (j.scale || 1);
  const fs = Math.max(9, Math.round(H / 45));
  const lines = [
    `${j.frame}  t=${j.t}  ${j.mode}`,
    `screen: ${j.screenRecorded}${j.screenReplayed !== null && j.screenReplayed !== j.screenRecorded ? '  ->  ' + j.screenReplayed : ''}`,
    `step: ${j.stepRecorded}${j.stepReplayed !== null && j.stepReplayed !== j.stepRecorded ? '  ->  ' + j.stepReplayed : ''}`,
    `red = recorded, green = replayed`,
    ...(j.mismatch || []).map(m => 'MISMATCH ' + m),
  ];
  const bw = Math.min(W, Math.round(fs * 0.62 * Math.max(...lines.map(l => l.length))) + 10), bh = lines.length * (fs + 3) + 6;
  let svg = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${H}">`;
  svg += rects(j.rectsRecorded, s, '#ff3030', false) + rects(j.rectsReplayed, s, '#30e060', true);
  svg += `<rect x="0" y="0" width="${bw}" height="${bh}" fill="#000000" fill-opacity="0.65"/>`;
  lines.forEach((l, k) => {
    const color = l.startsWith('MISMATCH') ? '#ffd040' : '#ffffff';
    svg += `<text x="4" y="${(k + 1) * (fs + 3)}" font-family="sans-serif" font-size="${fs}" fill="${color}">${esc(l)}</text>`;
  });
  svg += '</svg>';
  await sharp(jpg).composite([{ input: Buffer.from(svg) }]).png().toFile(join(pngDir, j.frame + '.png'));
  done++;
}
console.log(`drew ${done} of ${frames.length} frames into ${pngDir}`);
if (frames.length > 0 && done === 0 && !existsSync(recDir)) process.exit(1);
