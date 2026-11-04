// A recipe draft from a recording (docs/specs/navigator.md 6b.3). A dev / lab tool: it is not in the app and is not shipped.
//
//   node tools/rec-to-recipe.mjs <recording dir | frames.jsonl> <id> [--params hero,slot] [--out tools/recipes/<id>.draft.json] [--resolver "dotnet exec ... CoreTests.dll"]
//
// Reads the recording's frames (the parts' zips as tools/rec-to-test.mjs, or a plain frames.jsonl): the screen brief of each frame ("City", "HeroScreen hero H tab T slot S flow(F)") and
// the cursor. A stable stretch of one screen state is a state; a change between two states is a step: the form it starts from, what it expects (the new form, or the facet that
// changed: Hero / HeroTab / HeroSlot), and the click point (the cursor in the first frame of the new state). The click point goes to the C# side (CoreTests, RECIPE_FROM=<tsv>)
// that finds the UI graph's widget under it (UiGraph.WidgetAt) or a geo key; a run of hero changes in one grid becomes one action step, "TODO:action", for a human to bind to an
// existing action id. Older recordings have no click slots: the cursor and the screen brief are all there is (the draft says so).
// The draft is written to tools/recipes/<id>.draft.json; run `node tools/recipes.mjs --only <draft>` to validate it, review it, rename it and commit.
import { readFileSync, readdirSync, writeFileSync, existsSync, statSync, mkdtempSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { inflateRawSync } from 'node:zlib';
import { execSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const argv = process.argv.slice(2);
const opt = (k, d = null) => { const i = argv.indexOf(k); if (i < 0) return d; const v = argv[i + 1]; argv.splice(i, 2); return v; };
const params = (opt('--params', '') || '').split(',').filter(Boolean);
const outArg = opt('--out');
const resolver = opt('--resolver');
if (argv.length < 2) { console.error('usage: node tools/rec-to-recipe.mjs <recording dir | frames.jsonl> <id> [--params hero,slot] [--out file] [--resolver "cmd"]'); process.exit(2); }
const [src, id] = argv;

function zipEntries(buf) {
  let e = buf.length - 22; while (e >= 0 && buf.readUInt32LE(e) !== 0x06054b50) e--;
  if (e < 0) throw new Error('not a zip');
  const n = buf.readUInt16LE(e + 10); let p = buf.readUInt32LE(e + 16); const out = new Map();
  for (let i = 0; i < n; i++) {
    const method = buf.readUInt16LE(p + 10), csize = buf.readUInt32LE(p + 20), nl = buf.readUInt16LE(p + 28), xl = buf.readUInt16LE(p + 30), cl = buf.readUInt16LE(p + 32), local = buf.readUInt32LE(p + 42);
    const name = buf.toString('utf8', p + 46, p + 46 + nl);
    out.set(name, () => { const lnl = buf.readUInt16LE(local + 26), lel = buf.readUInt16LE(local + 28); const d = buf.subarray(local + 30 + lnl + lel, local + 30 + lnl + lel + csize); return method === 0 ? Buffer.from(d) : inflateRawSync(d); });
    p += 46 + nl + xl + cl;
  }
  return out;
}
function readFrames(path) {
  if (statSync(path).isFile()) return readFileSync(path, 'utf8').split(/\r?\n/).filter(Boolean).map((l) => JSON.parse(l));
  const rows = [];
  for (const f of readdirSync(path).filter((x) => /^\d+\.zip$/.test(x)).sort()) {
    const z = zipEntries(readFileSync(join(path, f)));
    if (z.has('frames.jsonl')) for (const l of z.get('frames.jsonl')().toString('utf8').split(/\r?\n/)) if (l) { try { rows.push(JSON.parse(l)); } catch { /* a re-written part start */ } }
  }
  return rows;
}

const FORM = { City: 'Form_MainCity', HeroScreen: 'Form_CharactorMain', Inventory: 'Form_BackpackIntegration', Foundry: 'Form_EquipSubliming', Gem: 'Form_EquipGemMain', ArtWorkshop: 'Form_EquipArtifactIntensify' };
function parse(b) {
  if (!b || b === '-') return null;
  const m = b.match(/^(\w+)/); if (!m || !FORM[m[1]]) return null;
  const s = { kind: m[1], hero: 0, tab: -1, slot: -1, flow: '' };
  const h = b.match(/hero (\d+) tab (-?\d+) slot (-?\d+)/); if (h) { s.hero = +h[1]; s.tab = +h[2]; s.slot = +h[3]; }
  const f = b.match(/flow\(([^)]*)\)/); if (f) s.flow = f[1];
  return s;
}
const key = (s) => s.kind + '|' + s.hero + '|' + s.tab + '|' + s.slot;

const frames = readFrames(src);
// states: runs of the same state key (a flow form is a sub-state of its screen, not a new state; its first appearance marks the terminal action)
const states = [];
for (const f of frames) {
  const s = parse(f.screen); if (!s) continue;
  const last = states[states.length - 1];
  if (last && key(last.s) === key(s)) { last.n++; if (s.flow && !last.flow) last.flow = s.flow; continue; }
  states.push({ s, n: 1, w: f.w, h: f.h, cursor: f.cursor || null, flow: s.flow, t: f.t });
}
const stable = states.filter((x) => x.n >= 2);
const start = stable.findIndex((x) => x.s.kind === 'City');
if (start < 0) { console.error('rec-to-recipe: no stable city state in the recording: nothing to start a recipe from'); process.exit(1); }
const seq = stable.slice(start);

// events
const ev = [];
for (let i = 1; i < seq.length; i++) {
  const a = seq[i - 1], b = seq[i];
  if (a.s.kind !== b.s.kind) { ev.push({ type: 'screen', from: a.s, to: b.s, at: b }); continue; }
  if (a.s.hero !== b.s.hero) ev.push({ type: 'hero', from: a.s, to: b.s, at: b });
  if (a.s.tab !== b.s.tab) ev.push({ type: 'tab', from: a.s, to: b.s, at: b });
  if (a.s.slot !== b.s.slot) ev.push({ type: 'slot', from: a.s, to: b.s, at: b });
}
const finalHero = seq[seq.length - 1].s.hero;
const steps = []; const clicks = [];
const lit = (v, name) => (params.includes(name) ? '$' + name : String(v));
for (let i = 0; i < ev.length; i++) {
  const e = ev[i];
  if (e.type === 'hero') {
    if (steps.length && steps[steps.length - 1].action === 'TODO:action' && steps[steps.length - 1].expect.facet) continue;   // a run of grid picks: one action
    steps.push({ id: 'hero', from: FORM.HeroScreen, action: 'TODO:action', expect: { facet: 'Hero==' + lit(finalHero, 'hero') } });
  } else if (e.type === 'screen') {
    const st = { id: 'to' + e.to.kind, from: FORM[e.from.kind], expect: { form: FORM[e.to.kind] } };
    st.__click = { form: st.from, at: e.at };
    steps.push(st);
  } else if (e.type === 'tab') {
    const st = { id: 'tab' + e.to.tab, from: FORM.HeroScreen, expect: { facet: 'HeroTab==' + (e.to.tab === 3 ? 'gear' : e.to.tab) } };
    st.__click = { form: st.from, at: e.at };
    steps.push(st);
  } else if (e.type === 'slot') {
    if (e.to.slot < 0) continue;   // the gear list closing is the listClosed step (a recording that starts mid-way has it; the draft keeps the opening only)
    const st = { id: 'slot', from: FORM.HeroScreen, expect: { facet: 'HeroSlot==' + lit(e.to.slot, 'slot') }, __click: { form: FORM.HeroScreen, at: e.at } };
    steps.push(st);
  }
}
if (seq.some((x) => x.flow)) steps.push({ id: 'pick', from: FORM.HeroScreen, action: 'TODO:action', expect: { action: 'done' } });

// the click points -> widgets (the C# side)
const pts = steps.filter((s) => s.__click && s.__click.at.cursor);
let resolved = [];
if (pts.length) {
  const dir = mkdtempSync(join(tmpdir(), 'rec2recipe-'));
  const tsv = join(dir, 'clicks.tsv');
  writeFileSync(tsv, pts.map((s) => [s.__click.form, s.__click.at.cursor[0], s.__click.at.cursor[1], s.__click.at.w, s.__click.at.h, s.expect.form || ''].join('\t')).join('\n') + '\n');
  const cmd = resolver || ('dotnet exec --runtimeconfig ' + join(root, 'build', 'CoreTests.runtimeconfig.json') + ' ' + join(root, 'build', 'CoreTests.dll') + ' --sim 1');
  try { resolved = execSync(cmd, { env: { ...process.env, RECIPE_FROM: tsv }, cwd: root, encoding: 'utf8' }).split(/\r?\n/).filter(Boolean); }
  catch (e) { console.error('rec-to-recipe: the widget resolver did not run (' + e.message.split('\n')[0] + '): the clicks stay as pick:x,y'); }
}
let pi = 0;
for (const s of steps) {
  if (!s.__click) continue;
  const c = s.__click.at.cursor;
  const r = c ? (resolved[pi++] || '-') : '-';
  if (r.startsWith('widget:')) s.widget = r.slice(7);
  else if (r.startsWith('geo:') || r.startsWith('std:')) s.fallback = r.slice(0, r.indexOf('\t') > 0 ? r.indexOf('\t') : r.length);
  else if (c) s.widget = 'pick:' + c[0] + ',' + c[1] + ' (TODO: not a known widget)';
  s.target = 'TODO:target';
  delete s.__click;
}

const recipe = { id, v: 1, game: 'TODO', owner: 'TODO', params, note: 'DRAFT from ' + src + '. The recording has the screen briefs and the cursor only (no click slots): the target of each click and the actions are for a human.', steps };
const out = outArg || join(root, 'tools', 'recipes', id + '.draft.json');
writeFileSync(out, JSON.stringify(recipe, null, 2) + '\n');
console.log('rec-to-recipe: ' + steps.length + ' steps -> ' + out);
