// Builds app/res/dps_desc.bin for the DPS meter's skill tooltips: the descriptions of every hero skill (and talent) of the site's hero pages.
// Format: gzip of UTF-8 text, one line per distinct text: "id,id,...<TAB>ru<TAB>en"; a line break in a text is the two characters \n, a backslash is \\.
// Ids that share the same text (a hero's three talents ...) share a line. Read by app/DpsDesc.cs.
//
//   node tools/dps-desc.mjs [../wardsage-web] [--dir data-root-folder] [--out app/res/dps_desc.bin]
// Reads the web repo's origin/main through git, or a plain folder (--dir) with data/hero-pages/*.json.
import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { execFileSync } from 'node:child_process';

const args = process.argv.slice(2);
const valOf = (flag) => { const i = args.indexOf(flag); return i >= 0 ? args[i + 1] : null; };
const dir = valOf('--dir'), out = valOf('--out') || 'app/res/dps_desc.bin';
const web = args.find((a, i) => !a.startsWith('--') && args[i - 1] !== '--dir' && args[i - 1] !== '--out') || path.join(import.meta.dirname, '..', '..', 'wardsage-web');
const git = (...a) => execFileSync('git', ['-C', web, ...a], { encoding: 'utf8', maxBuffer: 1 << 29 });
const readJson = (p) => JSON.parse(dir ? fs.readFileSync(path.join(dir, p), 'utf8') : git('show', 'origin/main:' + p));
const ids = (dir ? fs.readdirSync(path.join(dir, 'data/hero-pages')) : git('ls-tree', '--name-only', 'origin/main', 'data/hero-pages/').split('\n').map((f) => path.basename(f)))
  .filter((f) => /^\d+\.json$/.test(f)).map((f) => +f.replace('.json', '')).sort((a, b) => a - b);

const esc = (t) => (t || '').replace(/\\/g, '\\\\').replace(/\r?\n/g, '\\n').replace(/\t/g, ' ').trim();
const seen = new Set(), byText = new Map();
let skills = 0;
for (const hid of ids) {
  const h = readJson(`data/hero-pages/${hid}.json`);
  for (const s of h.skills || []) {
    if (!s.id || seen.has(s.id)) continue;
    const ru = esc(s.d && s.d.ru), en = esc(s.d && s.d.en);
    if (!ru && !en) continue;
    seen.add(s.id); skills++;
    const key = (ru || en) + '\t' + (en || ru);
    if (!byText.has(key)) byText.set(key, []);
    byText.get(key).push(s.id);
  }
}
const lines = [...byText].map(([k, v]) => v.join(',') + '\t' + k);
const raw = Buffer.from(lines.join('\n'), 'utf8');
const gz = zlib.gzipSync(raw, { level: 9 });
fs.writeFileSync(out, gz);
console.log(`${skills} skills with a description, ${lines.length} distinct texts, ${raw.length} bytes of text, ${gz.length} bytes gzipped -> ${out}`);
