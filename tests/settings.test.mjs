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
    remoteSwitchNote: (r) => (r.readEnabled ? '' : '<div class="remote-note" role="status">off</div>'),
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
const sw = (h, act) => (h.match(new RegExp(`<button class="switch"[^>]*data-act="${act}"[^>]*>`)) || [''])[0];
check(/aria-checked="false"/.test(sw(off, 'dps')) && /\sdisabled/.test(sw(off, 'dps')) && /class="lockb"/.test(off) && /Отключено командой Wardsage/.test(off), 'damage meter off by the site: aria-checked=false, disabled, a lock badge');
check(/aria-checked="true"/.test(sw(mk('ru'), 'dps')) && !/\sdisabled/.test(sw(mk('ru'), 'dps')) && !/class="lockb"/.test(mk('ru')), 'damage meter on: aria-checked=true, not disabled, no badge');
check(/role="switch"/.test(sw(mk('ru'), 'tray')) && /aria-labelledby="l-tray"/.test(sw(mk('ru'), 'tray')) && /id="l-tray"/.test(mk('ru')), 'a switch is named by its row title');
const rd = mk('ru', { remote: { readEnabled: false, off: [] } });
check((rd.match(/class="remote-note/g) || []).length === 3 && (mk('ru').match(/class="remote-note/g) || []).length === 0, 'game reading off: the note on top and a strip in «Синхронизация» and «Поверх игры»; none otherwise');
check(!/switch"[^>]*disabled/.test(rd), 'game reading off: the switches stay usable');
check(has(mk('ru', { hasCode: false }), 'editCode') && !has(mk('ru', { hasCode: false }), 'unlink'), 'no code: «Ввести код», no «Отвязать»');

// the site address is escaped; the field is only in «Дополнительно»
check(mk('ru', { more: { adv: true }, site: '"><x>' }).indexOf('"><x>') < 0 && /id="site"/.test(mk('ru', { more: { adv: true } })), '#site value is escaped');
check(!/id="site"/.test(mk('ru', { more: { adv: false } })), 'closed «Дополнительно»: no #site');

// ---- texts: the new keys exist in both languages with the same placeholders, no emoji, short descriptions
const NEW = ['secSync', 'secOver', 'secDev', 'secAdv', 'setCodeNone', 'setCtrl', 'setDpsOff', 'setDpsOffP', 'setMore', 'setUnlinkSure', 'setLangRow', 'setOn', 'setOff', 'setWaitRead', 'setNav', 'setTestBadge', 'setSiteHint',
  'setCodeP', 'setAutoSyncS', 'setSendCodesS', 'setCopyP', 'setBossHintsS', 'setDpsS', 'setShowInCaptureS', 'setShowInCaptureOn', 'setAutoUpdateS', 'setTrayS', 'setAutostartS', 'setHelpS', 'setReportsS', 'setLogP', 'setSiteP'];
const ph = (s) => (s.match(/\{\d\}/g) || []).sort().join('');
check(NEW.every((k) => typeof T.ru[k] === 'string' && T.ru[k] && typeof T.en[k] === 'string' && T.en[k]), 'every new settings text exists in ru and en');
check(NEW.every((k) => ph(T.ru[k]) === ph(T.en[k])), 'placeholders match in ru and en');
check(!NEW.some((k) => /[\u{1F300}-\u{1FAFF}☀-➿]/u.test(T.ru[k] + T.en[k])), 'no emoji in the new texts');
const SHORT = NEW.filter((k) => /S$|^setCodeP$|^setCopyP$|^setLogP$|^setSiteP$|^setCodeNone$|^setDpsOffP$|^setCtrl$|^setWaitRead$/.test(k) && k !== 'setShowInCaptureOn');
check(SHORT.every((k) => T.ru[k].length <= 90 && T.en[k].length <= 90), 'short descriptions are at most 90 characters (' + SHORT.filter((k) => T.ru[k].length > 90 || T.en[k].length > 90).join(',') + ')');
check(T.ru.setShowInCaptureOn.length <= 90 && T.en.setShowInCaptureOn.length <= 90, 'the capture line is short too');
for (const lang of ['ru', 'en']) check(['secAccount', 'secSync', 'secOver', 'secApp', 'secHelp', 'secAdv', 'secDev'].every((k) => T[lang][k]), lang + ': all section titles');

// ---- anchors: one per section, unique ids, the active one is never in the markup
const { SECTIONS, visible } = R;
const ids = SECTIONS.map((s) => s.id);
check(new Set(ids).size === ids.length, 'section ids are unique');
const m1 = mk('ru'), m2 = mk('ru', { channel: 'test' });
const secIds = (h) => [...h.matchAll(/<section[^>]* id="sec-(\w+)"/g)].map((m) => m[1]);
const navIds = (h) => [...h.matchAll(/data-act="setGo" data-sec="(\w+)"/g)].map((m) => m[1]);
check(secIds(m1).join() === navIds(m1).join() && secIds(m1).length === 6, 'main: every section has an anchor (6)');
check(secIds(m2).join() === navIds(m2).join() && secIds(m2).includes('dev') && !secIds(m1).includes('dev') && !navIds(m1).includes('dev'), 'test: the developers section and its anchor; main: neither');
check(/id="sec-dev"/.test(m2) && visible({ channel: 'main' }).length === 6 && visible({ channel: 'test' }).length === 7, 'visible() follows the channel');
check(!/class="[^"]*\bon\b/.test(m1.slice(m1.indexOf('setnav'), m1.indexOf('setsecs'))) && !/aria-current/.test(m1), 'the active anchor is not in the markup');
check(/data-act="setGo"/.test(m1) && /a === 'setGo'/.test(app) && /classList\.toggle\('on'/.test(app), 'app.js handles setGo and marks the anchor with classList');

// ---- the long text under «Как это работает»
const more = mk('ru', { more: { autoSync: true } });
check(/aria-expanded="true" aria-controls="more-autoSync"/.test(more) && /id="more-autoSync"/.test(more) && /aria-expanded="false" aria-controls="more-tray"/.test(more), '«Как это работает»: aria-expanded and aria-controls');

// ---- unlink asks first
const asks = mk('ru', { unlinkAsk: true });
check(/Точно отвязать\?/.test(asks) && !/Точно отвязать\?/.test(mk('ru')) && /data-act="unlink"/.test(asks), '«Отвязать» turns into «Точно отвязать?» while asking');
check(/a === 'unlink'[\s\S]{0,400}clearCode/.test(app) && /4000/.test(app.slice(app.indexOf("a === 'unlink'"), app.indexOf("a === 'unlink'") + 300)), 'app.js: the second press sends clearCode, the question lasts 4 s');

// ---- the language row, English too
check(has(mk('en'), 'lang') && /Language · Русский|Language · Язык/.test(mk('en')) && /Язык · Language/.test(mk('ru')), 'the language row is in both languages');
check(!/<div class="head">[\s\S]{0,300}data-act="lang"/.test(mk('ru')), 'the language is not in the page head');
// the English page renders as well, with its own texts
check(/Sync and data/.test(mk('en')) && /Auto sync/.test(mk('en')), 'the English page renders');
// the site hint is only a hint
const bad = mk('ru', { more: { adv: true }, site: 'http://127.0.0.1:3000' });
check(/class="hint warnhint"/.test(bad) && !/class="hint warnhint"/.test(mk('ru', { more: { adv: true } })), 'a site address without https:// gets a hint, a normal one does not');
// the markup stays the same when only the scroll position changes: render has no scroll state
check(!/setSec/.test(readFileSync('ui/settings.js', 'utf8')), 'settings.js knows nothing about the scroll state');

console.log(fails ? fails + ' FAILED' : 'settings ui: all ok');
process.exit(fails ? 1 : 0);
