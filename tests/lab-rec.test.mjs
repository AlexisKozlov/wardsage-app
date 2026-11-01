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
const valid0 = (args) => Object.entries(args).every(([k, v]) => schema[k] && schema[k](v));
ok(JSON.stringify(Object.keys(schema).sort()) === '["cmd","name","nav","run","scenario"]', 'rec takes exactly cmd, name, nav, scenario, run (' + Object.keys(schema) + ')');
ok(valid0({ cmd: 'city' }) && valid0({ cmd: 'prep', name: 'hero_filter' }) && valid0({ cmd: 'prep', name: 'start_bulk_sale' }) && valid0({ cmd: 'prep', name: 'random_start:17' }) && valid0({ cmd: 'settings', nav: 'on' }) && valid0({ cmd: 'settings', nav: 'off' }), 'city and the listed preps pass');
for (const [args, what] of [[{ cmd: 'prep', name: 'sell_all' }, 'an unlisted prep'], [{ cmd: 'prep', name: 'hero_filter\n1 stop' }, 'a newline in the prep'], [{ cmd: 'prep', name: 'Hero_filter' }, 'an upper case prep'],
  [{ cmd: 'prep', name: '../x' }, 'a path as the prep'], [{ cmd: 'prep', name: 'hero_filter_x' }, 'a prefix of a listed prep'], [{ cmd: 'city', name: 5 }, 'a number as the name'], [{ cmd: 'prep', name: 'random_start:' }, 'a random start with no seed'], [{ cmd: 'prep', name: 'random_start:1234567890' }, 'a too long seed'],
  [{ cmd: 'prep', name: 'random_start:1\n1 stop' }, 'a newline in the seed'], [{ cmd: 'settings', nav: 'maybe' }, 'a nav value other than on / off']]) ok(!valid0(args), 'rejected: ' + what);
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
  ok(rx.test(Date.now() + ' prep random_start:12') && rx.test(Date.now() + ' settings nav=on') && !rx.test(Date.now() + ' settings nav=maybe') && !rx.test(Date.now() + ' prep random_start:'), 'the runner random start / nav lines parse in the app');
  ok(rx.test(Date.now() + ' city') && rx.test(Date.now() + ' prep hero_filter_closed') && !rx.test(Date.now() + ' prep') && !rx.test(Date.now() + ' prep x y'), 'the runner city / prep lines parse in the app');
  const runnerNames = (ps.match(/\^\(hero_filter\|([^)]+)\)\$/) || [, ''])[1].split('|').filter((x) => !x.startsWith('random_start'));
  const appNames = (cs.match(/Names = \{([^}]+)\}/) || [, ''])[1].match(/"[a-z_]+"/g) || [];
  ok(appNames.length === 7 && ['hero_filter', ...runnerNames].sort().join() === appNames.map((x) => x.slice(1, -1)).sort().join(), 'the runner and the app list the same preps (' + runnerNames + ')');
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
// the lab's forced settings reach the page: the state event carries the forced auto click and the lab flag, the page re-checks plans and the sell list
const hb = readFileSync(new URL('../app/HostBridge.cs', import.meta.url), 'utf8');
const appjs = readFileSync(new URL('../ui/app.js', import.meta.url), 'utf8');
ok(/Append\(B\(\(cfg\.AutoClick \|\| labForce\) && !labHintOnly && !labAutoOff\)\)\.Append\(",\\"lab\\":"\)/.test(hb), 'the state event carries the forced auto click (not in the hint / auto-off scenarios) and lab');
ok(/labForce = true; labAutoOff = false; ApplyAutoClick\(\); SendState\(null\)/.test(hb), 'forcing the settings posts the state to the page (and ends the auto-off scenario)');
ok(/labHintOnly = hint;[^\n]*ApplyAutoClick\(\); SendState\(null\)/.test(hb), 'the hint mode posts the state to the page');
ok(/m\.lab && S\.autoClick && !wasAuto\) labRecheck\(\)/.test(appjs) && /function labRecheck\(\)[\s\S]*enqueue\(fresh\)[\s\S]*sellStart\(\)/.test(appjs), 'the page starts pending plans and the sell list when the lab forces auto click');
ok(/LabRecCmd\.AutoClick\(cfg\.AutoClick, labForce, On\("autoclick"\), labHintOnly \|\| labAutoOff\)/.test(hb), 'the host auto click is the pure rule (never in the hint mode, nor in the auto-off scenario)');
const ov = readFileSync(new URL('../app/Overlay.cs', import.meta.url), 'utf8');
ok(/bool ScreensWanted\(\) \{ return labPrep != null \|\|/.test(ov), 'the screens are read while a lab setup runs (lab 24c266d0: the prep never got a screen read and failed in 100 s)');
ok(/labPrepOn && On\("autoclick"\)/.test(hb) && /if \(!Channel\.IsTest\) \{ LabWrite\("rec\.ack"/.test(hb), 'a lab setup clicks only with the site\'s autoclick switch on, and a release build refuses it');
ok(/if \(hint\) \{ overlay\.SetRun\(0\); overlay\.SetSell\(null, null, null, 0\); \}/.test(hb) && /!labHintOnly && !labAutoOff \? \(long\)h : 0/.test(hb) && /cmd == "sell\.hint" \|\| labHintOnly/.test(hb), 'the hint mode drops a running plan and sell run, and ignores run / sell starts from the page');
ok(/wasAuto && !S\.autoClick && m\.autoSync !== undefined\) \{\s*S\.run = null; S\.queue = \[\];/.test(appjs), 'the page stops its run and queue when the host takes auto click away');
ok(/HintExpired\(labHintAt/.test(hb), 'the hint mode ends by the 10 min cap');
console.log(`lab rec: ${n - bad}/${n} ok`);
process.exit(bad ? 1 : 0);
