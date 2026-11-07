// The maintenance screen of the interface without a browser: texts in both languages, the mock scenarios, and the markup contract in app.js.
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
let fails = 0;
const check = (ok, what) => { console.log((ok ? '  ok   ' : '  FAIL ') + what); if (!ok) fails++; };

// i18n: every maint key exists in ru and en, with the same placeholders
const win = {};
vm.runInNewContext(readFileSync('ui/i18n.js', 'utf8'), { window: win });
const T = win.RF_TEXTS;
const keys = Object.keys(T.ru).filter((k) => k.startsWith('maint'));
check(keys.length >= 12, 'maintenance texts exist (' + keys.length + ')');
check(keys.every((k) => typeof T.en[k] === 'string' && T.en[k].length > 0), 'every maintenance text has an English version');
const ph = (s) => (s.match(/\{\d\}/g) || []).sort().join('');
check(keys.every((k) => ph(T.ru[k]) === ph(T.en[k])), 'placeholders match in ru and en');
check(!keys.some((k) => /[\u{1F300}-\u{1FAFF}☀-➿]/u.test(T.ru[k] + T.en[k])), 'no emoji in the texts');

// the mock scenarios emit the maint event with a live phase
function run(s) {
  const out = [];
  const w = { location: { origin: 'http://x', search: '?s=' + s }, sessionStorage: { getItem() { return null; }, setItem() {} }, setTimeout: (f) => { f(); return 0; }, console };
  w.window = w; w.URLSearchParams = URLSearchParams;
  w.document = { querySelector: () => null };
  vm.runInNewContext(readFileSync('ui/mock.js', 'utf8'), w);
  w.RFMock.on((m) => out.push(m));
  w.RFMock.send({ cmd: 'init' });
  return out.filter((m) => m.ev === 'maint');
}
const soon = run('maint-soon')[0], stopped = run('maint-stopped')[0], on = run('maint-on')[0];
check(soon && soon.phase === 'soon' && new Date(soon.startsAt) > Date.now() && new Date(soon.stopAt) < new Date(soon.startsAt), 'mock maint-soon: starts later, recording stops before the start');
check(stopped && stopped.phase === 'stopped' && new Date(stopped.stopAt) < Date.now() && new Date(stopped.startsAt) > Date.now(), 'mock maint-stopped: reading stopped, works not begun');
check(on && on.phase === 'on' && new Date(on.endsAt) > Date.now() && /^https:\/\/t\.me\//.test(on.link), 'mock maint-on: the works with an end time and a Telegram link');
const done = run('maint-done');
check(done.length >= 1 && done[done.length - 1].phase === 'off' && done[done.length - 1].done === true, 'mock maint-done: the works end with the «done» event');
check(run('idle').length === 0, 'no maint event in the other scenarios');

// app.js contract: the event, the action, the screen, the rate limit, escaped text
const app = readFileSync('ui/app.js', 'utf8');
check(/case 'maint':/.test(app) && /maintCheck/.test(app) && /cmd: 'maint\.check'/.test(app), 'app.js handles the maint event and sends maint.check');
check(/60000/.test(app.slice(app.indexOf('maintCooldown')), 0) && /Math\.ceil\(\(S\.maintCheckAt \+ 60000/.test(app), '«Check again» is limited to once in 60 s');
check(/esc\(custom\)/.test(app) && /data-act="open" data-url="\$\{esc\(lk\)\}"/.test(app), 'the site text and link are escaped');
check(/id="maint"/.test(readFileSync('ui/index.html', 'utf8')), 'index.html has the screen container');
if (fails) { console.log(fails + ' failed'); process.exit(1); }
console.log('maintenance ui: all ok');
