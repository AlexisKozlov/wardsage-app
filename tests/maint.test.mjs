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
const early = run('maint-early');
check(early.length === 2 && early[0].phase === 'on' && new Date(early[0].endsAt) - Date.now() > 3600000 && early[1].phase === 'off' && early[1].done === true, 'mock maint-early: works screen with a far end, then an early «off» with the «done» event');
check(/case 'maint':[\s\S]{0,400}if \(m\.done\) toast\(t\('maintDone'\)/.test(readFileSync('ui/app.js', 'utf8')) && /m\.phase \|\| 'off'/.test(readFileSync('ui/app.js', 'utf8')), 'app.js: the «off» event with done closes the window and shows the «works over» toast');
check(run('idle').length === 0, 'no maint event in the other scenarios');

// app.js contract: the event, the action, the screen, the rate limit, escaped text
const app = readFileSync('ui/app.js', 'utf8');
check(/case 'maint':/.test(app) && /maintCheck/.test(app) && /cmd: 'maint\.check'/.test(app), 'app.js handles the maint event and sends maint.check');
check(/60000/.test(app.slice(app.indexOf('maintCooldown')), 0) && /Math\.ceil\(\(S\.maintCheckAt \+ 60000/.test(app), '«Check again» is limited to once in 60 s');
const maintFn = app.slice(app.indexOf('  function maintScreen()'), app.indexOf('  // the numbers tick without'));
check(/esc\(custom\)/.test(app), 'the site text is escaped');
check(!/m\.link|maintMore|\blk\b/.test(maintFn) && !/maintTg/.test(maintFn), 'works screen: no separate button for the announcement link (m.link)');
check(/id="maint"/.test(readFileSync('ui/index.html', 'utf8')), 'index.html has the screen container');

// social icons: the mock gives two, the screen and the rail draw only known ids with https, the Discord button stays when the list is empty
check(on && Array.isArray(on.social) && on.social.length === 2 && on.social[0].id === 'discord' && on.social[1].id === 'telegram', 'mock maint-on: two social icons (discord, telegram)');
check(run('maint-early')[0].social === undefined, 'mock maint-early: no social list (the old Discord button)');
check(/discord: '<svg/.test(app) && /telegram: '<svg/.test(app) && /SOCIAL_KEYS = \{ discord: 'maintDiscord', telegram: 'maintTg' \}/.test(app), 'app.js: discord and telegram icons, titles from the texts');
const css = readFileSync(new URL('../ui/app.css', import.meta.url), 'utf8'), socCss = (css.match(/^\.soc[^\n]*$/gm) || []).join('\n');
check(!/border-radius: 50%|radial-gradient|linear-gradient|social-ring/.test(socCss) && !/\.soc \{[^}]*box-shadow/.test(socCss), 'css: social icons have no ring, plate or gradient');
check(/\.soc \{[^}]*width: 36px; height: 36px/.test(socCss) && /\.soc svg \{[^}]*width: 28px/.test(socCss) && /\.soc:hover svg \{[^}]*drop-shadow/.test(socCss) && /\.soc:focus-visible/.test(socCss), 'css: 36 px click area, 28 px icon, hover glow, focus ring');
check(/socialRow\(m\.social, 'maint-soc'\)/.test(app) && /m\.social && m\.social\.length \? ''/.test(app), 'works screen: the icon row, the Discord button only without icons');
check(/\$\{socialRow\(RAIL_SOCIAL\)\}\s*<\/div>`;/.test(app) && /RAIL_SOCIAL = \[\{ id: 'discord', url: 'https:\/\/discord\.gg\/xJrfv3zYx5' \}, \{ id: 'telegram', url: 'https:\/\/t\.me\/wardsage' \}\]/.test(app), 'rail: the two official links are built in, below the game status');
{
  const a2 = app.indexOf('  const SOCIAL_KEYS'), b2 = app.indexOf('  function rail()');
  const ctx = { esc: (x) => String(x), t: (k) => k, I: { discord: '<d/>', telegram: '<t/>' } };
  const f = vm.runInNewContext(app.slice(a2, b2) + '; socialRow', ctx);
  const good = f([{ id: 'discord', url: 'https://discord.gg/x' }, { id: 'telegram', url: 'https://t.me/y' }]);
  check((good.match(/class="soc"/g) || []).length === 2 && /data-act="open"/.test(good) && /title="maintDiscord"/.test(good) && /aria-label="maintTg"/.test(good), 'socialRow: two buttons with title and aria-label');
  check(f([{ id: 'vk', url: 'https://vk.com/x' }, { id: 'discord', url: 'http://discord.gg/x' }, null, 5]) === '' && f(undefined) === '' && f('x') === '', 'socialRow: unknown id, http, odd values give nothing');
  check((f(Array(10).fill({ id: 'discord', url: 'https://discord.gg/x' })).match(/class="soc"/g) || []).length === 6, 'socialRow: at most 6');
}

// one note instead of three: run isMaintNotice() and notices() from app.js against stub state
{
  const a = app.indexOf('  function isMaintNotice'), b = app.indexOf('  // ---- maintenance: the banner');
  const src = app.slice(a, b);
  const mk = (maint, notice, offline) => {
    const S = { maint, notice, offline, lang: 'ru' };
    const ctx = { S, I: { clock: '', x: '' }, esc: (x) => String(x), t: (k) => k, hhmm: () => 'HH', leftText: () => 'L', store: () => null, noticeText: () => (notice ? notice.ru : ''), window: { RFForum: { onSite: (x) => x } } };
    return vm.runInNewContext(src + '; notices()', ctx);
  };
  const count = (h) => (h.match(/remote-note/g) || []).length;
  const soon = { phase: 'soon', startsAt: '2026-11-07T11:30:00Z', endsAt: '2026-11-07T11:40:00Z' };
  const nMaint = { id: 'maintenance:abc:scheduled', ru: 'Технические работы', tone: 'warn' };
  check(count(mk(soon, nMaint, true)) === 1 && /maint-note/.test(mk(soon, nMaint, true)), 'maintenance active + site announcement "maintenance:abc:scheduled" + no connection: one note, ours');
  check(count(mk(soon, { id: 'maintenance', ru: 'x', tone: 'warn' }, false)) === 1, 'announcement id "maintenance" is hidden too');
  check(count(mk({ phase: 'stopped', startsAt: soon.startsAt, endsAt: soon.endsAt }, nMaint, true)) === 1, 'stopped: one note');
  check(mk({ phase: 'on', endsAt: soon.endsAt }, nMaint, true) === '', 'works screen: no notes under it');
  const other = { id: 'other', ru: 'Чужое', tone: 'warn' };
  const ho = mk(soon, other, true);
  check(count(ho) === 2 && /Чужое/.test(ho) && !/siteOffline/.test(ho), 'another announcement stays; "no connection" is hidden');
  check(count(mk({ phase: 'off' }, nMaint, true)) === 2, 'maintenance off: announcement and "no connection" as before');
  check(count(mk({ phase: 'off' }, other, false)) === 1, 'maintenance off: plain announcement');
  const noId = { ru: 'x', tone: 'warn', until: '2026-11-07T11:45:00Z' };
  check(count(mk(soon, noId, false)) === 1, 'no id, warning ending with the window: hidden');
  check(count(mk(soon, { ru: 'x', tone: 'warn', until: '2026-11-09T11:45:00Z' }, false)) === 2, 'no id, another time: stays');
  check(count(mk(soon, { ru: 'x', tone: 'info', until: noId.until }, false)) === 2, 'no id, not a warning: stays');
  check(!/боёв|recording/i.test(T.ru.maintSoon + T.ru.maintStopped + T.en.maintSoon + T.en.maintStopped), 'no fight recording in the maintenance note');
}
if (fails) { console.log(fails + ' failed'); process.exit(1); }
console.log('maintenance ui: all ok');
