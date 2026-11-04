// Compiles tools/recipes/*.json into src/NavRecipesData.cs and validates them (docs/specs/navigator.md 6b.2). Run in tests/run-tests.sh and by tools/game-update.mjs after ui-graph.
//
//   node tools/recipes.mjs                         write src/NavRecipesData.cs (after validating)
//   node tools/recipes.mjs --check [--registry F]   validate; fail when the generated file is out of date. F = the code registries dumped by the tests (RECIPE_REGISTRY=1)
//
// A game update that breaks a recipe fails the build, never the player.
import { readFileSync, writeFileSync, readdirSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const dir = join(root, 'tools', 'recipes');
const outFile = join(root, 'src', 'NavRecipesData.cs');
const args = process.argv.slice(2);
const check = args.includes('--check');
const regFile = args.includes('--registry') ? args[args.indexOf('--registry') + 1] : null;

const errors = [];
const err = (m) => errors.push(m);

// the UI graph: forms, widgets (path, flags), the generated pilot chains
const graph = readFileSync(join(root, 'src', 'UiGraphData.cs'), 'utf8');
const forms = new Map(); let cur = null; const chains = new Map();
for (const m of graph.matchAll(/"((?:F|W|P)\|[^"]*)"/g)) {
  const p = m[1].split('|');
  if (p[0] === 'F') { cur = p[1]; forms.set(cur, new Map()); }
  else if (p[0] === 'W' && cur) forms.get(cur).set(p[1], p[3] || '');
  else if (p[0] === 'P') chains.set(p[1], p[2]);
}
const neverClick = new Set();   // the screen map's never-click buttons are widget fields; a recipe step presses geo points or graph widgets that are Safe (no d / k)

let reg = null;
if (regFile) {
  reg = { target: new Set(), facet: new Set(), const: new Set(), action: new Set(), geo: new Set(), pic: new Set() };
  for (const line of readFileSync(regFile, 'utf8').split(/\r?\n/)) {
    const i = line.indexOf(':'); if (i < 0) continue;
    const k = line.slice(0, i), v = line.slice(i + 1);
    if (reg[k]) reg[k].add(v);
  }
}

const recipes = [];
for (const f of readdirSync(dir).filter((x) => x.endsWith('.json') && !x.endsWith('.draft.json')).sort()) {
  let r; try { r = JSON.parse(readFileSync(join(dir, f), 'utf8')); } catch (e) { err(f + ': not JSON: ' + e.message); continue; }
  const where = (s) => f + ' step ' + s.id;
  if (!r.id || !Array.isArray(r.steps) || !r.steps.length) { err(f + ': id and steps are required'); continue; }
  if (recipes.some((x) => x.id === r.id)) err(f + ': duplicate id ' + r.id);
  const ids = new Set(); let terminal = false;
  for (const s of r.steps) {
    if (!s.id || ids.has(s.id)) err(where(s) + ': missing or duplicate step id');
    ids.add(s.id);
    if (!forms.has(s.from)) err(where(s) + ': from ' + s.from + ' is not a form of the UI graph');
    if (s.expect && s.expect.form && !forms.has(s.expect.form)) err(where(s) + ': expect.form ' + s.expect.form + ' is not a form of the UI graph');
    if (!s.expect || (!s.expect.form && !s.expect.facet && s.expect.action !== 'done')) err(where(s) + ': an expect is required');
    if (s.widget && !s.widget.startsWith('geo:') && !s.widget.startsWith('std:')) {
      const w = forms.get(s.from) && forms.get(s.from).get(s.widget);
      if (w === undefined) err(where(s) + ': widget ' + s.widget + ' is not on ' + s.from);
      else if (/[dk]/.test(w)) err(where(s) + ': widget ' + s.widget + ' is dangerous or a confirm');
    }
    if (s.action) {
      if (terminal && !s.expect.action) err(where(s) + ': a facet action after a terminal action');
      if (s.expect && s.expect.action === 'done') terminal = true;
    } else if (terminal) err(where(s) + ': a click step after a terminal action');
    if (!s.action && !s.target && s.widget) err(where(s) + ': a click step needs a target');
    if (reg) {
      if (s.target && !reg.target.has(s.target)) err(where(s) + ': target ' + s.target + ' is not a ClickTarget');
      if (s.action && !reg.action.has(s.action)) err(where(s) + ': action ' + s.action + ' is not in NavActions');
      for (const k of [s.fallback, s.widget]) if (k && (k.startsWith('geo:') || k.startsWith('std:')) && !reg.geo.has(k)) err(where(s) + ': geo key ' + k + ' is not in the code');
      if (s.need && !reg.pic.has(s.need)) err(where(s) + ': need ' + s.need + ' is not in the code');
      for (const c of [s.when, s.expect && s.expect.facet]) if (c) for (const t of c.split('&&')) {
        const m = t.trim().match(/^(\$?\w+)\s*(==|!=|>=|<=|<|>)\s*(\$?\w+|-?\d+)$/);
        if (!m) { err(where(s) + ': cannot read the condition ' + t.trim()); continue; }
        for (const n of [m[1], m[3]]) if (!n.startsWith('$') && !/^-?\d+$/.test(n) && !reg.facet.has(n) && !reg.const.has(n)) err(where(s) + ': ' + n + ' is neither a facet nor a constant');
        if (m[1].startsWith('$') && !(r.params || []).includes(m[1].slice(1))) err(where(s) + ': parameter ' + m[1] + ' is not declared');
      }
    }
  }
  // the forms of the navigation steps follow the generated chain of the graph (the stage 1 pin): the first click step of a recipe that opens a screen has the chain's widget
  if (r.id === 'equip') {
    const ch = chains.get('hero') || '';
    const first = r.steps[0];
    if (!ch.startsWith(first.from + '>' + first.widget + '>' + first.expect.form)) err(f + ': the first step does not follow the graph chain P|hero (' + ch + ')');
  }
  recipes.push(r);
}

if (errors.length) { for (const e of errors) console.error('recipes: ' + e); process.exit(1); }

const q = (s) => '@"' + s.replace(/"/g, '""') + '"';
const body = '// GENERATED by tools/recipes.mjs from tools/recipes/*.json - do not edit. The navigator\'s tasks as data (docs/specs/navigator.md 6b.2).\n'
  + 'namespace RealmForge {\n  public static class NavRecipesData {\n    public const string Game = ' + JSON.stringify(recipes[0].game) + ';\n    public static readonly string[] Data = {\n'
  + recipes.map((r) => '      ' + q(JSON.stringify(r))).join(',\n') + '\n    };\n  }\n}\n';
if (check) {
  const have = existsSync(outFile) ? readFileSync(outFile, 'utf8') : '';
  if (have !== body) { console.error('recipes: src/NavRecipesData.cs is out of date: run `node tools/recipes.mjs`'); process.exit(1); }
  console.log('recipes: ' + recipes.length + ' valid, ' + (reg ? 'registries checked' : 'registries not checked') + ', generated file up to date');
} else { writeFileSync(outFile, body); console.log('recipes: wrote ' + outFile + ' (' + recipes.length + ' recipes)'); }
