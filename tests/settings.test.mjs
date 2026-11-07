// The «Настройки» page without a browser: markup contract of ui/settings.js, texts in both languages, handlers in app.js.
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
let fails = 0;
const check = (ok, what) => { console.log((ok ? '  ok   ' : '  FAIL ') + what); if (!ok) fails++; };

const win = {};
vm.runInNewContext(readFileSync('ui/i18n.js', 'utf8'), { window: win });
const T = win.RF_TEXTS;
await import('../ui/settings.js');
const R = globalThis.RFSettings;
const app = readFileSync('ui/app.js', 'utf8');

const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
const I = new Proxy({}, { get: (_, k) => `<svg data-i="${String(k)}"></svg>` });
const mk = (lang, o = {}) => {
  const S = { lang, version: '1.6.69', channel: 'main', site: 'https://wardsage.com', defaultSite: 'https://wardsage.com', hasCode: true, codePrefix: 'rf_VKSjw', saveCopy: true,
    autoSync: true, errorReports: true, autoUpdate: true, tray: true, bossHints: true, dps: true, showInCapture: true, sendCodes: true, recButton: true, autostart: true,
    more: {}, rec: { on: false, sec: 0, sent: 0, total: 0, why: '' }, remote: { readEnabled: true, off: [] }, ...o };
  const t = (k, ...a) => { const v = (T[lang] || {})[k]; if (v === undefined) throw new Error('no text ' + k); return v.replace(/\{(\d)\}/g, (_, n) => a[n]); };
  const H = {
    dpsOff: () => !!(S.remote && S.remote.readEnabled !== false && Array.isArray(S.remote.off) && S.remote.off.includes('dps')),
    codeBtns: () => `<button data-act="editCode">x</button><button data-act="open" data-url="u">x</button>`,
    recLabel: () => 'Rec', recNote: () => 'note', DISCORD: 'https://discord.gg/x', shownVer: (v) => String(v),
  };
  return R.html(S, t, esc, I, H);
};
const acts = (h) => [...h.matchAll(/data-act="(\w+)"/g)].map((m) => m[1]);
const has = (h, act) => acts(h).includes(act);

const full = mk('ru', { channel: 'test', more: { adv: true }, rec: { on: false, sec: 0, sent: 3, total: 3, why: '' } });
// every old switch and button is still on the page
const must = ['autoSync', 'copy', 'sendCodes', 'bossHints', 'dps', 'showInCapture', 'tray', 'autostart', 'autoUpdate', 'errorReports', 'recButton',
  'rec', 'editCode', 'unlink', 'open', 'openLog', 'saveSite', 'diag', 'lang'];
for (const a of must) check(has(full, a), 'present: data-act ' + a);
check(has(mk('ru', { channel: 'test', more: { adv: true }, site: 'https://x.example' }), 'resetSite'), 'present: resetSite (own site address)');
check(/id="recBtn"/.test(full) && /id="recInfo"/.test(full), 'recBtn and recInfo ids kept (the timer updates them in place)');

// every action is handled in app.js
const all = new Set(acts(full).concat(acts(mk('ru', { hasCode: false }))));
for (const a of all) check(new RegExp(`a === '${a}'`).test(app), 'app.js handles data-act ' + a);

// the channel
const main = mk('ru', { channel: 'main', more: { adv: true } });
check(!/id="recBtn"/.test(main) && !has(main, 'recButton') && !has(main, 'rec'), 'channel main: no recorder');
check(/id="recBtn"/.test(full) && has(full, 'recButton'), 'channel test: recorder present');

// remote switches
const off = mk('ru', { remote: { readEnabled: true, off: ['dps'] } });
check(/data-act="dps" aria-checked="false" disabled/.test(off), 'damage meter off by the site: aria-checked=false and disabled');
check(/data-act="dps" aria-checked="true" >/.test(mk('ru')), 'damage meter on: aria-checked=true, not disabled');
check(has(mk('ru', { hasCode: false }), 'editCode') && !has(mk('ru', { hasCode: false }), 'unlink'), 'no code: «Ввести код», no «Отвязать»');

// the site address is escaped; the field is only in «Дополнительно»
check(mk('ru', { more: { adv: true }, site: '"><x>' }).indexOf('"><x>') < 0 && /id="site"/.test(mk('ru', { more: { adv: true } })), '#site value is escaped');
check(!/id="site"/.test(mk('ru', { more: { adv: false } })), 'closed «Дополнительно»: no #site');

console.log(fails ? fails + ' FAILED' : 'settings ui: all ok');
process.exit(fails ? 1 : 0);
