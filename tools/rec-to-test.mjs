// Turns a screen recording (the folder of NNN.zip parts the recorder uploads) into a permanent regression fixture: tests/replays/<name>/.
//
//   node tools/rec-to-test.mjs <recording dir> <name> --kind equip|sell|locate|art [--status pass|pending] [--pending "why"] [--text "what went wrong"]
//        [--check type[:key=value,...]]... [--out tests/replays] [--dry]
//
// Writes (no pictures, only what the replay reads):
//   frames.jsonl   one line per recorded frame: t, w, h, scale, screen, step, frames (the rectangles drawn), cursor and "in" (the raw reads, the guides'
//                  context, the pilots' views "ap" / "hp"); parts are joined, a re-written part start (base reads, unchanged slots) is dropped
//   geometry.json  the game's geometry the guides' rectangles are placed from (only when the recording has a guide or sell context)
//   expect.json    what must happen (see tests/ReplayCheck.cs for the check types); --kind picks the usual checks, --check adds or replaces them
// tests/run-tests.sh (step 5d) replays every fixture through the real code on every run. Needs only node (the zip is read here).
//   status pass     the fix is in: every check must hold, a failing check is a regression
//   status pending  the bug is still open, or the recording lacks the data (the tool says which): the fixture must still fail; when it passes the run
//                   tells you to flip it to pass
// The default status is pending when the data a kind's checks need is not in the recording (the build that made it had no "ap" / "hp" slot), else pass.
import { readFileSync, readdirSync, mkdirSync, writeFileSync, existsSync } from 'node:fs';
import { join, basename, resolve } from 'node:path';
import { inflateRawSync } from 'node:zlib';

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

const args = process.argv.slice(2);
const opt = (k, d = null) => { const i = args.indexOf(k); if (i < 0) return d; const v = args[i + 1]; args.splice(i, 2); return v; };
const flag = (k) => { const i = args.indexOf(k); if (i < 0) return false; args.splice(i, 1); return true; };
const dry = flag('--dry');
const kind = opt('--kind');
let status = opt('--status');
let pendingWhy = opt('--pending');
const text = opt('--text', '');
const outRoot = opt('--out', join(new URL('..', import.meta.url).pathname, 'tests', 'replays'));
const extra = [];
for (let c; (c = opt('--check')) !== null;) extra.push(c);
if (args.length < 2 || !['equip', 'sell', 'locate', 'art'].includes(kind)) {
  console.error('usage: node tools/rec-to-test.mjs <recording dir> <name> --kind equip|sell|locate|art [--status pass|pending] [--pending "why"] [--text "..."] [--check type:key=value,...]... [--out dir] [--dry]');
  process.exit(2);
}
const [recDir, name] = args;
if (!/^[a-z0-9][a-z0-9-]*$/.test(name)) { console.error('name: lower-case letters, digits and - only'); process.exit(2); }

// ---- read the parts
const parts = readdirSync(recDir).filter(f => /^(part_)?\d{3}\.zip$/.test(f)).sort();
if (!parts.length) { console.error('no NNN.zip parts in ' + recDir); process.exit(2); }
const lines = []; let geometry = null;
for (const f of parts) {
  const ents = zipEntries(readFileSync(join(recDir, f)));
  if (ents.has('geometry.json') && !geometry) geometry = ents.get('geometry.json')().toString('utf8');
  if (!ents.has('frames.jsonl')) continue;
  for (const l of ents.get('frames.jsonl')().toString('utf8').split('\n')) if (l.trim()) lines.push(JSON.parse(l));
}
if (!lines.length) { console.error('the recording has no frames.jsonl lines'); process.exit(2); }

// ---- compact: no pictures exist here; a part start (base read, slots written again unchanged) is dropped
const lastSlot = {}; let nBase = 0;
const out = lines.map(j => {
  const inn = j.in;
  if (inn) {
    if (Array.isArray(inn.reads)) inn.reads = inn.reads.filter(r => { if (r.base) { nBase++; return false; } return true; });
    for (const k of ['ctx', 'env', 'sinv', 'sinvList']) if (k in inn) { const s = JSON.stringify(inn[k]); if (lastSlot[k] === s) delete inn[k]; else lastSlot[k] = s; }
  }
  return JSON.stringify(j);
});

// ---- what the recording has
const has = { ap: false, hp: false, sell: false, locate: false };
for (const j of lines) {
  const i = j.in; if (!i) continue;
  if (i.ap) has.ap = true; if (i.hp) has.hp = true;
  if (i.ctx && i.ctx.mode === 'sell' && i.ctx.want && i.ctx.want.length) has.sell = true;
  if (i.ctx && i.ctx.mode === 'locate') has.locate = true;
}

// ---- the checks
const parseCheck = (s) => {
  const [type, rest] = s.split(/:(.*)/s); const c = { id: type, type };
  if (rest) for (const kv of rest.split(',')) { const [k, v] = kv.split('='); c[k] = /^-?\d+(\.\d+)?$/.test(v) ? Number(v) : v; }
  return c;
};
let checks, missing = [];
if (kind === 'equip') {
  checks = [
    { id: 'item-clicked-in-3s', text: 'the plan\'s item is clicked within 3 s of its live cell being readable', type: 'click-within', from: 'liveTarget', ms: 3000 },
    { id: 'no-click-on-empty-cell', text: 'no item-pilot click on the live grid without a cell', type: 'no-click-empty' },
    { id: 'item-pilot-never-idle', text: 'the item pilot is never without a click for 8 s while it has work', type: 'max-idle', pilot: 'item', ms: 8000 },
    { id: 'hero-pilot-never-idle', text: 'the hero pilot is never without a click for 6 s while it has work (the pause after the hero pick)', type: 'max-idle', pilot: 'hero', ms: 6000 },
  ];
  if (!has.ap) missing.push('item pilot views (slot "ap")');
  if (!has.hp) missing.push('hero pilot views (slot "hp")');
} else if (kind === 'sell') {
  checks = [{ id: 'final-step-stays', text: 'once the cleanup is done it stays done (mouse moves, clicks, game behind): the frame on «Продать» never goes out', type: 'sell-done-sticky' }];
  if (!has.sell) missing.push('a sell context with the wanted list');
} else if (kind === 'art') {
  checks = [{ id: 'art-frame-on-card', text: 'on every artifact-list step the frame sits on the card the guide names (rows placed from the game\'s scroll / the picture)', type: 'frame-on-card' }];
  missing.push('the artifact list\'s card positions (only the picture shows where the cards really are; fixtures keep no pictures)');
} else {
  checks = [{ id: 'guide-replays', text: 'the guide\'s screens, steps and frames replay as recorded', type: 'replay-agrees', max: 0 }];
  if (!has.locate) missing.push('a locate context');
}
if (extra.length) checks = extra.map(parseCheck);
if (!status) { status = missing.length ? 'pending' : 'pass'; if (missing.length && !pendingWhy) pendingWhy = 'the recording has no ' + missing.join(' and ') + ': re-record with the current test build and rebuild this fixture'; }
if (status === 'pending' && !pendingWhy) pendingWhy = 'known open bug';

const expect = { name, source: basename(resolve(recDir)), kind, status, ...(status === 'pending' ? { pending: pendingWhy } : {}), ...(text ? { text } : {}), checks };
const dir = join(outRoot, name);
const bytes = out.join('\n').length;
console.log(`${name}: ${lines.length} frames, ${(bytes / 1024).toFixed(0)} KB, dropped ${nBase} base reads; has ap=${has.ap} hp=${has.hp} sell=${has.sell} locate=${has.locate}; status ${status}${missing.length ? ' (missing: ' + missing.join(', ') + ')' : ''}`);
if (dry) process.exit(0);
mkdirSync(dir, { recursive: true });
writeFileSync(join(dir, 'frames.jsonl'), out.join('\n') + '\n');
if (geometry && (has.sell || has.locate)) writeFileSync(join(dir, 'geometry.json'), geometry);
else if (existsSync(join(dir, 'geometry.json'))) { /* keep an older one */ }
writeFileSync(join(dir, 'expect.json'), JSON.stringify(expect, null, 2) + '\n');
console.log('written ' + dir);
