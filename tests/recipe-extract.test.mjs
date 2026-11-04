// The demonstration extractor (tools/rec-to-recipe.mjs) on a recorded lab run of equip_city: its draft equals tools/recipes/equip.json in the navigation steps
// (same form, same expectation, same widget or geo key), the action steps being "TODO:action" placeholders. Needs build/CoreTests.dll (tests/run-tests.sh builds it).
import { execFileSync } from 'node:child_process';
import { readFileSync, mkdtempSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const out = join(mkdtempSync(join(tmpdir(), 'recipe-extract-')), 'draft.json');
execFileSync('node', [join(root, 'tools', 'rec-to-recipe.mjs'), join(root, 'tests', 'fixtures', 'recipe-from', 'equip_city', 'frames.jsonl'), 'equip_city', '--params', 'hero,slot', '--out', out], { cwd: root, stdio: 'inherit' });
const draft = JSON.parse(readFileSync(out, 'utf8'));
const equip = JSON.parse(readFileSync(join(root, 'tools', 'recipes', 'equip.json'), 'utf8'));
let bad = 0;
const fail = (m) => { console.error('FAIL recipe-extract: ' + m); bad++; };
const ex = (s) => JSON.stringify(s.expect);
let at = 0;   // the draft's steps are a subsequence of the recipe's, in order
for (const d of draft.steps) {
  let found = -1;
  for (let i = at; i < equip.steps.length; i++) {
    const r = equip.steps[i];
    if (r.from !== d.from) continue;
    if (d.action) {   // a placeholder stands for an action step of the same expectation kind (a facet, or the terminal "done")
      if (d.action !== 'TODO:action') { fail('step ' + d.id + ': the action is not a placeholder'); break; }
      if (r.action && ((d.expect.facet && r.expect.facet === d.expect.facet) || (d.expect.action && r.expect.action === d.expect.action))) { found = i; break; }
      continue;
    }
    if (ex(d) !== ex(r)) continue;
    const sameWidget = d.widget && d.widget === r.widget, sameGeo = d.fallback && d.fallback === r.fallback;
    if (sameWidget || sameGeo) { found = i; break; }
  }
  if (found < 0) fail('step ' + d.id + ' (' + d.from + ' ' + ex(d) + ' ' + (d.widget || d.fallback || '') + ') is not in equip.json in order');
  else at = found + 1;
}
if (draft.steps.length < 5) fail('the draft has only ' + draft.steps.length + ' steps');
if (bad) process.exit(1);
console.log('recipe-extract: the draft of ' + draft.steps.length + ' steps from the equip_city recording matches equip.json (navigation steps; actions are placeholders)');
