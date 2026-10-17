// «Найти в игре» texts of the desktop interface (ui/locate.js) against ui/i18n.js.
import assert from 'node:assert/strict';
globalThis.window = globalThis;   // i18n.js is a plain browser script
await import('../ui/locate.js');
await import('../ui/i18n.js');
const L = globalThis.RFLocate;
const T = globalThis.RF_TEXTS;
let n = 0; const ok = (c, m) => { assert.ok(c, m); n++; };
const tr = (lang) => (k, ...a) => { let s = (T[lang] && T[lang][k]) || T.ru[k] || k; a.forEach((v, i) => { s = s.split('{' + i + '}').join(v); }); return s; };

const item = { uid: 7001, slot: 4, slotName: 'Кольцо', setName: 'Буря', mainStat: 'Крит. УРН 80%', main: [{ name: 'Крит. УРН', value: '80%' }],
  subs: [{ text: 'Скорость +11', name: 'Скорость', value: '11' }, { text: 'АТК +5%', name: 'АТК', value: '5%' }] };
const req = { id: 'l1', heroName: 'Байек', item };
const st = (o) => ({ step: 'inList', pos: 14, row: 2, col: 6, count: 60, missing: [], onHero: false, needClick: false, subs: null, changed: false, ...o });

for (const lang of ['ru', 'en']) {
  const t = tr(lang);
  ok(L.describe(null, st(), t) === null, 'no request -> nothing');
  const gone = L.describe({ id: 'x', item: null }, null, t);
  ok(gone.tone === 'warn' && gone.head === T[lang].locNoItem, lang + ': item not in the snapshot');
  // every step kind has a head and a short plate, and no key is left unresolved
  for (const k of L.KINDS) {
    const d = L.describe(req, st({ step: k, missing: ['slot', 'suit', 'main', 'subs'] }), t);
    ok(d && d.head && !/^loc[A-Z]/.test(d.head) && d.lines.every((x) => !/^loc[A-Z]/.test(x)), lang + ': step ' + k + ' has texts');
    ok(d.tip.length >= 1 && d.tip.length <= 3 && d.tip.every((x) => x.length <= 90), lang + ': step ' + k + ' plate is short');
  }
  ok(L.describe(req, null, t).head === T[lang].locOpenInventory, lang + ': no state yet -> open the inventory');
  const il = L.describe(req, st(), t);
  ok(il.head.includes('2') && il.head.includes('6'), lang + ': row and place in the head');
  const nc = L.describe(req, st({ needClick: true }), t);
  ok(nc.head === T[lang].locNeedClick, lang + ': no anchor -> click any item');
  const hero = L.describe(req, st({ step: 'openInventory', onHero: true }), t);
  ok(hero.head.includes('Кольцо') && hero.head.includes('Байек'), lang + ': the hero is open -> its slot');
  const rf = L.describe(req, st({ step: 'recastFilter', missing: ['slot', 'main', 'subs'], pos: 31, count: 40 }), t);
  ok(rf.lines.length === 4 && rf.lines[0].includes('Кольцо') && rf.lines[1].includes('Крит. УРН') && rf.lines[2].includes('Скорость, АТК') && rf.lines[3].includes('31'), lang + ': filter lines: slot, main, subs, place');
  const rf2 = L.describe(req, st({ step: 'recastFilter', missing: ['suit'], pos: 0 }), t);
  ok(rf2.lines.length === 1 && rf2.lines[0].includes('Буря'), lang + ': only the missing part is told');
  ok(L.describe(req, st({ step: 'recastPick', pos: 2, count: 2 }), t).head.includes('2'), lang + ': pick by place');
  ok(L.describe(req, st({ step: 'gemPick', pos: 0 }), t).head === T[lang].locGemOpen, lang + ': gem pop-up closed');
  ok(L.describe(req, st({ step: 'done' }), t).tone === 'done', lang + ': done');
  // Ascension: the way to the Foundry from the screen the player is on, then the Foundry's own list; recast texts stay as they were
  const areq = { ...req, purpose: 'ascend' };
  ok(L.describe(areq, st({ step: 'openFoundry' }), t).head === T[lang].locFoundryInv && L.describe(areq, st({ step: 'openFoundry', onGear: true }), t).head === T[lang].locFoundryHero, lang + ': Foundry button of the inventory / of the hero screen');
  ok(L.describe(req, st({ step: 'selected' }), t).lines[0] === T[lang].locSelectedP, lang + ': recast selected text unchanged');
  const af = L.describe(areq, st({ step: 'ascendFilter', missing: ['slot', 'main'], pos: 0 }), t);
  ok(af.head === T[lang].locFilterA && af.lines.length === 2 && af.lines[0].includes('Кольцо') && af.lines[1].includes('Крит. УРН'), lang + ': ascend filter lines: slot, main');
  ok(L.describe(areq, st({ step: 'ascendPick', pos: 40, count: 90, scroll: 'below', scrollRows: 3 }), t).lines[0].includes('3'), lang + ': ascend pick: rows below');
  ok(L.describe(areq, st({ step: 'ascendPick', pos: 4, count: 90, scroll: 'above', scrollRows: 2 }), t).lines[0].includes('2'), lang + ': ascend pick: rows above');
  ok(L.describe(areq, st({ step: 'ascendPick', pos: 4, count: 9, scroll: 'visible' }), t).lines[0] === T[lang].locVisible, lang + ': ascend pick: on the screen');
  ok(L.describe(areq, st({ step: 'ascendPick', pos: 4, count: 9, scroll: '' }), t).lines.length === 0, lang + ': ascend pick: scroll unknown -> no line');
  ok(L.describe(areq, st({ step: 'ascendPick', pos: 3, count: 9 }), t).head.includes('3'), lang + ': ascend pick by place');
  ok(L.describe(areq, st({ step: 'ascendReady' }), t).tone === 'done' && L.describe(areq, st({ step: 'ascendNotInList' }), t).tone === 'warn', lang + ': ascend ready / not in list');
}

// auto click: the app works on the steps it clicks itself, says why it stopped, and leaves the other steps as they are
for (const lang of ['ru', 'en']) {
  const t = tr(lang);
  const plain = L.describe(req, st({ step: 'inList' }), t);
  const work = L.describe(req, st({ step: 'inList', auto: 'work' }), t);
  ok(work.head === T[lang].locAutoWork && work.lines[0] === plain.head, lang + ': auto at work on the list: the program is opening it');
  ok(L.describe(req, st({ step: 'openInventory', auto: 'work' }), t).head === T[lang].locAutoWork, lang + ': auto opens the inventory');
  ok(L.describe(req, st({ step: 'selected', auto: 'hint' }), t).head === T[lang].locSelected, lang + ': the last steps stay the player\'s');
  ok(L.describe(req, st({ step: 'recastPick', auto: 'work' }), t).head !== T[lang].locAutoWork, lang + ': the Foundry steps are never "auto work"');
  ok(L.describe(req, st({ step: 'inList', auto: 'paused' }), t).head === plain.head, lang + ': paused: the normal hint');
  for (const why of ['bulk', 'lost', 'stuck', 'screen', 'cancelled']) {
    const d = L.describe(req, st({ step: 'inList', auto: 'stopped', autoWhy: why }), t);
    ok(d.head === T[lang].locAutoStop && d.lines.length >= 2 && !/^loc[A-Z]/.test(d.lines[0]) && d.lines[1] === plain.head, lang + ': stopped (' + why + ') says why and keeps the hint');
    ok(d.tip.length <= 3 && d.tip.every((x) => x.length <= 90), lang + ': stopped (' + why + ') plate is short');
  }
  ok(L.describe(req, st({ step: 'inList', auto: 'stopped', autoWhy: 'bulk' }), t).tone === 'warn' && L.describe(req, st({ step: 'inList', auto: 'stopped', autoWhy: 'cancelled' }), t).tone === 'info', lang + ': stop tones');
  ok(T[lang].locLeadAuto && T[lang].locLeadAuto !== T[lang].locLead, lang + ': the lead changes with auto click');
}
ok(L.AUTO_STEPS.every((k) => L.KINDS.indexOf(k) >= 0), 'auto steps are known steps');

// the text keys exist in both languages
const locKeys = (l) => Object.keys(T[l]).filter((k) => k.startsWith('loc')).sort();
ok(locKeys('ru').length > 30 && JSON.stringify(locKeys('ru')) === JSON.stringify(locKeys('en')), 'ru and en have the same locate keys');

// sub stat marks
const rows = L.subRows(req, st({ subs: [true, false] }), tr('ru'));
ok(rows.length === 2 && rows[0].ok && !rows[1].ok && rows[0].text === 'Скорость 11', 'sub stat marks');
ok(L.subRows(req, st(), tr('ru')).every((x) => !x.ok), 'no reading -> no marks');
ok(L.subRows({ item: null }, null, tr('ru')).length === 0, 'no item -> no rows');
console.log(`locate: ${n} passed`);
