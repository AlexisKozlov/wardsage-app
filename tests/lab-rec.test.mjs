// The lab runner's `rec` job arguments (tools/lab/WardsageLab.ps1 $Schemas.rec): the validators are read out of the script and checked here
// (PowerShell -cmatch / -ceq on these plain patterns behaves as a case-sensitive JS RegExp / ===), plus the line the runner writes is one the
// app's parser (src/LabRec.cs) takes. Also: the SQL action list and the runner's dispatch know `rec`; the loop's start job is valid JSON.
//   node tests/lab-rec.test.mjs
import { readFileSync } from 'node:fs';

const ps = readFileSync(new URL('../tools/lab/WardsageLab.ps1', import.meta.url), 'utf8');
const sql = readFileSync(new URL('../tools/lab/sql/lab-staging.sql', import.meta.url), 'utf8');
const cs = readFileSync(new URL('../src/LabRec.cs', import.meta.url), 'utf8');
let bad = 0, n = 0;
const ok = (c, what) => { n++; if (!c) { bad++; console.log('FAIL ' + what); } };

const m = ps.match(/^\s*rec\s*=\s*@\{([\s\S]*?)\}\s*\}\s*$/m);
ok(!!m, 'the rec schema is in the script');
const schema = {};
for (const line of (m ? m[1] : '').split('\n')) {
  const k = line.match(/(\w+)\s*=\s*\{\s*param\(\$v\)(.*)/);
  if (!k) continue;
  const body = k[2];
  const rx = body.match(/-cmatch\s+'([^']+)'/);
  const eqs = [...body.matchAll(/-ceq\s+'([^']+)'/g)].map((x) => x[1]);
  schema[k[1]] = (v) => typeof v === 'string' && (rx ? new RegExp(rx[1]).test(v) : eqs.includes(v));
}
ok(JSON.stringify(Object.keys(schema).sort()) === '["cmd","run","scenario"]', 'rec takes exactly cmd, scenario, run (' + Object.keys(schema) + ')');
const valid = (args) => Object.entries(args).every(([k, v]) => schema[k] && schema[k](v));
const run = '0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0';
ok(valid({ cmd: 'start', scenario: 'equip_city', run }), 'start args pass');
ok(valid({ cmd: 'stop' }), 'stop passes');
for (const [args, what] of [
  [{ cmd: 'keep' }, 'an unknown cmd'], [{ cmd: 'Start' }, 'cmd is case-sensitive'], [{ cmd: 'start', scenario: '../x', run }, 'a path in the scenario'],
  [{ cmd: 'start', scenario: 'a b', run }, 'a space in the scenario'], [{ cmd: 'start', scenario: 'Equip', run }, 'upper case scenario'],
  [{ cmd: 'start', scenario: 'x'.repeat(41), run }, 'a too long scenario'], [{ cmd: 'start', scenario: 'locate', run: run.toUpperCase() }, 'an upper case run id'],
  [{ cmd: 'start', scenario: 'locate', run: run + '\nstop' }, 'a newline in the run id'], [{ cmd: 'start', scenario: 'locate\n1 stop', run }, 'a newline in the scenario'],
  [{ cmd: 'start', scenario: 7, run }, 'a number as the scenario'], [{ cmd: 'stop', reason: 'x' }, 'an unknown key'], [{ cmd: 'start', scenario: '', run }, 'an empty scenario'],
]) ok(!valid(args), 'rejected: ' + what);

// the line the runner builds is one the app takes (the same pattern as src/LabRec.cs LineRx)
const lineRx = cs.match(/LineRx = new Regex\("([^"]+)"\)/);
ok(!!lineRx, 'the app line pattern is found');
if (lineRx) {
  const rx = new RegExp(lineRx[1].replace(/\\\\/g, '\\'));
  ok(rx.test(Date.now() + ' start equip_city ' + run), 'the runner start line parses in the app');
  ok(rx.test(Date.now() + ' stop'), 'the runner stop line parses in the app');
}
ok(/'mark', 'rec'\)\);/.test(sql), 'the SQL action check allows rec');
ok(/'rec'\s*\{\s*\$out = Invoke-Rec \$jobArgs\s*\}/.test(ps), 'the runner dispatches rec');
ok(!/[^\x00-\x7F]/.test(ps), 'the runner stays ASCII');
const loop = readFileSync(new URL('../tools/lab/lab-loop.sh', import.meta.url), 'utf8');
const j = loop.match(/mkjob rec "(\{[^"]*(?:\\"[^"]*)*\})"/);
ok(!!j, 'the loop queues a rec start');
if (j) {
  const txt = j[1].replace(/\\"/g, '"').replace('$sc', 'locate').replace('$RUN', run);
  let o = null; try { o = JSON.parse(txt); } catch { }
  ok(o && valid(o), 'the loop start args are valid for the runner (' + txt + ')');
}
console.log(`lab rec: ${n - bad}/${n} ok`);
process.exit(bad ? 1 : 0);
