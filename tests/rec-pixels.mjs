// Writes the recorded pictures at the game window's client size as raw B G R A bytes, for `RecReplay.dll ... --pixels <dir>` (the artifact list's rows from the picture).
//
//   node tests/rec-pixels.mjs <recording dir with part_*.zip> <out dir> [--every N]
//
// The recorder saves the frames downscaled (the "scale" of the line); they are scaled back to the client size (the line's w x h). Needs sharp (the site's node_modules).
import { createRequire } from 'node:module';
import { readFileSync, readdirSync, mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { inflateRawSync } from 'node:zlib';

const require = createRequire(import.meta.url);
const sharp = require('/home/dev/wardsage/wardsage-web/node_modules/sharp');

const args = process.argv.slice(2);
let every = 1;
const ei = args.indexOf('--every');
if (ei >= 0) { every = Math.max(1, parseInt(args[ei + 1], 10) || 1); args.splice(ei, 2); }
if (args.length < 2) { console.error('usage: node tests/rec-pixels.mjs <recording dir> <out dir> [--every N]'); process.exit(2); }
const [recDir, outDir] = args;

function zipEntries(buf) {
  let e = buf.length - 22;
  while (e >= 0 && buf.readUInt32LE(e) !== 0x06054b50) e--;
  if (e < 0) throw new Error('not a zip');
  const n = buf.readUInt16LE(e + 10);
  let p = buf.readUInt32LE(e + 16);
  const out = new Map();
  for (let i = 0; i < n; i++) {
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

mkdirSync(outDir, { recursive: true });
let done = 0, k = 0;
for (const f of readdirSync(recDir).filter(f => /^(part_)?\d{3}\.zip$/.test(f)).sort()) {
  const ents = zipEntries(readFileSync(join(recDir, f)));
  const lines = ents.has('frames.jsonl') ? ents.get('frames.jsonl')().toString('utf8').split('\n').filter(Boolean) : [];
  const names = [...ents.keys()].filter(n => /^frame_\d+\.jpg$/.test(n)).sort();
  for (let i = 0; i < names.length; i++, k++) {
    if (k % every) continue;
    const j = lines[i] ? JSON.parse(lines[i]) : null;
    const w = j && j.w > 0 ? j.w : null, h = j && j.h > 0 ? j.h : null;
    let img = sharp(ents.get(names[i])());
    if (w && h) img = img.resize(w, h, { fit: 'fill' });
    const raw = await img.ensureAlpha().raw().toBuffer();   // R G B A
    for (let p = 0; p < raw.length; p += 4) { const r = raw[p]; raw[p] = raw[p + 2]; raw[p + 2] = r; }   // -> B G R A
    writeFileSync(join(outDir, names[i].replace(/\.jpg$/, '.bgra')), raw);
    done++;
  }
}
console.log(`wrote ${done} frames into ${outDir}`);
