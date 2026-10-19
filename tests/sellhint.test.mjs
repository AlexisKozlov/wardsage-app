// Storage cleanup hint texts of the desktop interface (ui/sellhint.js) against ui/i18n.js.
import assert from 'node:assert/strict';
globalThis.window = globalThis;   // i18n.js is a plain browser script
await import('../ui/sellhint.js');
await import('../ui/i18n.js');
const SH = globalThis.RFSellHint;
const T = globalThis.RF_TEXTS;
let n = 0; const ok = (c, m) => { assert.ok(c, m); n++; };
const tr = (lang) => (k, ...a) => { let s = (T[lang] && T[lang][k]) || k; a.forEach((v, i) => { s = s.split('{' + i + '}').join(v); }); return s; };
const st = (o) => ({ step: 'select', selected: 3, total: 12, missing: 0, extra: 0, below: 0, above: 0, ...o });

for (const lang of ['ru', 'en']) {
  const t = tr(lang);
  ok(SH.describe(null, t).head === T[lang].shOpenInv, lang + ': no state yet -> open the inventory');
  ok(SH.describe({ step: 'bogus' }, t).tip[0] === T[lang].shOpenInv, lang + ': unknown step -> open the inventory');
  // every step has a head and a short plate, and no key is left unresolved
  for (const k of SH.KINDS) {
    const d = SH.describe(st({ step: k }), t);
    ok(d.head && !/^sh[A-Z]/.test(d.head) && d.lines.every((x) => !/^sh[A-Z]/.test(x)), lang + ': step ' + k + ' has texts');
    ok(d.tip.length >= 1 && d.tip.length <= 2 && d.tip.every((x) => x.length <= 70), lang + ': step ' + k + ' plate is short');
  }
  const sel = SH.describe(st(), t);
  ok(sel.lines[0].includes('3') && sel.lines[0].includes('12'), lang + ': the counter says 3 of 12');
  ok(sel.head === T[lang].shSelect && sel.tone === 'info', lang + ': select step');
  ok(SH.describe(st({ step: 'allSelected', selected: 12 }), t).tone === 'done', lang + ': all selected is done');
  ok(SH.describe(st({ step: 'allSelected', selected: 12 }), t).head === T[lang].shAll, lang + ': all selected -> press Sell');
  const ex = SH.describe(st({ extra: 1 }), t);
  ok(ex.tone === 'warn' && ex.head === T[lang].shExtra, lang + ': a stray item -> warning');
  // the scroll pills: only while items are left to select
  const below = SH.describe(st({ below: 5 }), t), above = SH.describe(st({ above: 2 }), t);
  ok(below.pillDown.includes('5') && below.pillUp === '' && above.pillUp.includes('2') && above.pillDown === '', lang + ': pills point the right way');
  ok(SH.describe(st({ step: 'allSelected', below: 5 }), t).pillDown === '', lang + ': no pill when all are selected');
  ok(SH.describe(st(), t).pillDown === '' && SH.describe(st(), t).pillUp === '', lang + ': no pill when everything is in view');
  // the page's lines
  ok(T[lang].sellHintLead && !/^sell/.test(T[lang].sellHintLead), lang + ': sellHintLead');
}
// artifact lists: their own words for the tab, the items and the stray one; the same steps otherwise
for (const lang of ['ru', 'en']) {
  const t = tr(lang);
  ok(SH.KINDS.includes('openArtTab'), lang + ': the artifact tab step exists');
  const tab = SH.describe(st({ step: 'openArtTab' }), t, 'artifact');
  ok(tab.head === T[lang].shArtTab && tab.tone === 'info' && tab.tip.length === 1, lang + ': open the artifact tab');
  ok(SH.describe(st({ step: 'pressBulk' }), t, 'artifact').head === T[lang].shBulk, lang + ': press bulk sell (same words)');
  ok(SH.describe(st(), t, 'artifact').head === T[lang].shSelectArt, lang + ': tick the framed artifacts');
  ok(SH.describe(st({ extra: 1 }), t, 'artifact').head === T[lang].shExtraArt && SH.describe(st({ extra: 1 }), t, 'artifact').tone === 'warn', lang + ': a stray artifact -> warning');
  ok(SH.describe(st({ step: 'noneHere' }), t, 'artifact').head === T[lang].shNoneArt, lang + ': none here');
  ok(SH.describe(st({ step: 'allSelected', selected: 3 }), t, 'artifact').head === T[lang].shAll, lang + ': all ticked -> press Sell (same words)');
  ok(SH.describe(st({ below: 4 }), t, 'artifact').pillDown.includes('4'), lang + ': the scroll pill for artifacts');
  ok(SH.describe(st(), t).head === T[lang].shSelect && SH.describe(st({ step: 'noneHere' }), t, 'equip').head === T[lang].shNone, lang + ': gear words unchanged');
  for (const k of SH.KINDS) {
    const d = SH.describe(st({ step: k, extra: 0 }), t, 'artifact');
    ok(d.tip.every((x) => x.length <= 70) && !/^sh[A-Z]/.test(d.head), lang + ': artifact step ' + k + ' text is short and resolved');
  }
  ok(T[lang].sellTitleArt && T[lang].sellHintLeadArt && T[lang].sellShowArt && !/вы сами.*автонажат/i.test(T[lang].sellHintLeadArt), lang + ': artifact card texts');
}
// the texts asked for
ok(T.ru.sellHintLead === 'Программа покажет рамками, какие вещи отметить; отмечаете вы сами.', 'ru: lead text');
ok(T.ru.shAll === 'Все отмечены — нажмите «Продать»' && T.ru.shExtra === 'Эта вещь не из списка — снимите с неё отметку' && T.en.shExtra === 'This item is not on the list — untick it', 'ru/en: step texts');
// both languages have the same keys
for (const k of Object.keys(T.ru).filter((x) => /^(sellHint|sh[A-Z])/.test(x))) ok(k in T.en, 'en has ' + k);
for (const k of Object.keys(T.en).filter((x) => /^(sellHint|sh[A-Z])/.test(x))) ok(k in T.ru, 'ru has ' + k);
console.log(n + ' sell hint checks ok');
