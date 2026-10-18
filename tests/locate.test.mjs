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
  // recast: item selected -> card button «Улучшение самоцветами» -> gem screen function -> «Улучшить»; never the Foundry / the card's other buttons
  const sel = L.describe(req, st({ step: 'selected' }), t);
  ok(sel.lines[0] === T[lang].locSelectedP && !/Литейн|Foundry|Преобразовать|Recast/.test(sel.lines[0]), lang + ': recast selected -> the gem button of the card, no Foundry');
  ok(sel.lines[0].includes(lang === 'ru' ? 'Улучшение самоцветами' : 'Gem Enhancement'), lang + ': the game\'s name of the card button');
  ok(L.describe(req, st({ step: 'gemFunction' }), t).head.includes(lang === 'ru' ? 'Преобразование' : 'Recast'), lang + ': gem function head');
  // the gem screen names the one function the item needs, not both
  const gRec = L.describe(req, st({ step: 'gemFunction', func: 7 }), t), gAdv = L.describe(req, st({ step: 'gemFunction', func: 8 }), t);
  ok(gRec.head.includes(lang === 'ru' ? '«Преобразование»' : '«Recast»') && !gRec.head.includes(lang === 'ru' ? 'Продв.' : 'Advanced'), lang + ': mythic -> the plain recast only');
  ok(gAdv.head.includes(lang === 'ru' ? 'Продв. преобразование' : 'Advanced Recast') && gAdv.lines.length === 1 && !/^loc[A-Z]/.test(gAdv.lines[0]), lang + ': ancient mythic -> the advanced recast only');
  const gWrong = L.describe(req, st({ step: 'gemFunction', func: 8, wrongFunc: true }), t);
  ok(gWrong.head !== gAdv.head && gWrong.head.includes(lang === 'ru' ? 'Продв. преобразование' : 'Advanced Recast') && !/^loc[A-Z]/.test(gWrong.head), lang + ': the other recast chosen -> says which one is needed');
  const gDone = L.describe(req, st({ step: 'done', onGem: true, func: 7 }), t);
  ok(gDone.tone === 'done' && gDone.head === T[lang].locDone && gDone.lines[0] === T[lang].locDoneGemP, lang + ': gem screen done -> press «Улучшить» yourself');
  const pt = L.pillTexts(t, lang);
  ok(pt.lang === lang && pt.pillDown.includes('{0}') && pt.pillUp.includes('{0}') && pt.rows1 && pt.rows2 && pt.rows5, lang + ': the pills over the game have texts and row forms');
  ok(L.describe(req, st({ step: 'done', onGem: true }), t).lines[0] === T[lang].locDoneGemP && L.describe(req, st({ step: 'done' }), t).lines[0] === T[lang].locDoneP, lang + ': done on the gem screen -> «Улучшить»');
  // ascend: no step talks about the item card's buttons ("Gem Enhancement" / "Remove"); the way is Foundry -> «Ascend» tab -> list
  for (const k of ['openInventory', 'openEquipTab', 'openFoundry', 'openAscendTab', 'ascendFilter', 'ascendPick', 'ascendNotInList', 'ascendReady', 'selected']) {
    const d = L.describe({ ...req, purpose: 'ascend' }, st({ step: k, missing: ['slot'] }), t), all = [d.head].concat(d.lines).join(' ');
    ok(!/самоцвет|Gem Enh|Снять|Unequip|Remove/.test(all), lang + ': ascend ' + k + ' does not mention the card buttons');
  }
  // the locate plate has its own header and counter
  const c1 = L.caps({ ...req, purpose: 'recast', item: { ...item, name: 'Амулет Бури' } }, st({ step: 'selected' }), t);
  ok(c1.includes('Амулет Бури') && c1.includes('4') && c1.includes('6') && c1.toLowerCase().startsWith(lang === 'ru' ? 'показать в игре' : 'show in game') && !/Шаг 4 из 5|АМУЛЕТ/.test(c1), lang + ': locate caps: own header, item name, step 4 of 6');
  ok(!L.caps({ ...req, item: { ...item, name: 'X' } }, st({ step: 'recastPick' }), t).includes(' шаг ') || lang === 'en', lang + ': off-path step has no counter');
  ok(L.stepNo({ ...req, purpose: 'ascend' }, st({ step: 'ascendReady' })).n === 6 && L.stepNo(req, st({ step: 'noItem' })) === null, 'step numbers');
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
  // recast: the plate says what the program presses / chooses (the last button, «Улучшить», is never announced as a program action)
  const aGem = L.describe({ ...req, purpose: 'recast' }, st({ step: 'selected', auto: 'work' }), t);
  ok(aGem.head === T[lang].locAutoGem && aGem.head.includes(lang === 'ru' ? 'Улучшение самоцветами' : 'Gem Enhancement') && aGem.lines[0] === T[lang].locSelected, lang + ': auto at the card: names the gem button');
  ok(L.describe({ ...req, purpose: 'ascend' }, st({ step: 'selected', auto: 'work' }), t).head === T[lang].locSelected, lang + ': ascend: no gem button announcement');
  const aRec = L.describe(req, st({ step: 'gemFunction', func: 7, auto: 'work' }), t), aAdv = L.describe(req, st({ step: 'gemFunction', func: 8, auto: 'work' }), t);
  ok(aRec.head === T[lang].locAutoFuncRec && aRec.head.includes(lang === 'ru' ? '«Преобразование»' : '«Recast»') && aAdv.head === T[lang].locAutoFuncAdv && aAdv.head.includes(lang === 'ru' ? 'Продв.' : 'Advanced'), lang + ': auto at the gem list: names the function');
  ok(L.describe(req, st({ step: 'gemFunction', func: 0, auto: 'work' }), t).head !== T[lang].locAutoFuncRec, lang + ': unknown function: nothing is announced');
  ok(L.describe(req, st({ step: 'done', onGem: true, auto: 'work' }), t).head === T[lang].locDone, lang + ': the final step is never "auto work"');
  ok([aGem, aRec, aAdv].every((d) => d.tip.length <= 3 && d.tip.every((x) => x.length <= 90)), lang + ': auto plates are short');
  ok(L.describe(req, st({ step: 'recastPick', auto: 'work' }), t).head !== T[lang].locAutoWork, lang + ': the Foundry steps are never "auto work"');
  ok(L.describe(req, st({ step: 'inList', auto: 'paused' }), t).head === plain.head, lang + ': paused: the normal hint');
  // the click gate's stops (src/ClickGate.cs): «Автонажатие остановлено: <причина>», the step's own hint after it
  for (const why of ['unknown', 'screen', 'modal', 'bulk', 'cell', 'target', 'nochange']) {
    const d = L.describe(req, st({ step: 'inList', auto: 'stopped', autoWhy: why }), t);
    ok(d.head === t('gateStop', t('gateWhy_' + why)) && !/\{0\}|^gate/.test(d.head) && d.lines[0] === t('locAutoRest') && d.lines[1] === plain.head && d.tone === 'warn', lang + ': gate stop (' + why + ') says why and keeps the hint');
    ok(d.tip.length <= 3 && d.tip.every((x) => x.length <= 90), lang + ': gate stop (' + why + ') plate is short');
  }
  // «Массовая продажа»: the step is to leave it, and the program clicks nothing
  const eb = L.describe(req, st({ step: 'exitBulk', auto: 'hint', autoWhy: 'bulk' }), t);
  ok(eb.head === T[lang].locExitBulk && eb.tone === 'warn' && eb.lines[0] === T[lang].locExitBulkP, lang + ': bulk sale: leave it');
  ok(L.describe(req, st({ step: 'exitBulk', auto: 'work' }), t).head === T[lang].locExitBulk, lang + ': bulk sale is never "auto work"');
  for (const why of ['lost', 'stuck', 'cancelled']) {
    const d = L.describe(req, st({ step: 'inList', auto: 'stopped', autoWhy: why }), t);
    ok(d.head === T[lang].locAutoStop && d.lines.length >= 2 && !/^loc[A-Z]/.test(d.lines[0]) && d.lines[1] === plain.head, lang + ': stopped (' + why + ') says why and keeps the hint');
    ok(d.tip.length <= 3 && d.tip.every((x) => x.length <= 90), lang + ': stopped (' + why + ') plate is short');
  }
  ok(L.describe(req, st({ step: 'inList', auto: 'stopped', autoWhy: 'lost' }), t).tone === 'warn' && L.describe(req, st({ step: 'inList', auto: 'stopped', autoWhy: 'cancelled' }), t).tone === 'info', lang + ': stop tones');
  ok(T[lang].locLeadAuto && T[lang].locLeadAuto !== T[lang].locLead, lang + ': the lead changes with auto click');
}
// «Продвинуть» артефакт: every step has texts and a short plate, in both languages; the artifact's name rides on the plate's header
const areq = { id: 'a1', purpose: 'artPromote', item: null, art: { uid: 5001, id: 200088, name: 'Клинок', lv: 10, cap: 10, st: 0, need: 2, via: 'copy' } };
for (const lang of ['ru', 'en']) {
  const t = tr(lang);
  for (const k of L.ART_KINDS) {
    const d = L.describe(areq, { step: k, row: 2, col: 3, count: 20, artCap: 10, artLevel: 7, matKind: 'copy', matNeed: 2, matChosen: 1 }, t);
    ok(d && d.head && !/^loc[A-Z]/.test(d.head) && d.lines.every((x) => !/^loc[A-Z]/.test(x)), lang + ': art step ' + k + ' has texts');
    ok(d.tip.length >= 1 && d.tip.length <= 3 && d.tip.every((x) => x.length <= 90), lang + ': art step ' + k + ' plate is short');
  }
  ok(L.describe({ id: 'a2', purpose: 'artPromote', item: null, art: null }, null, t).head === T[lang].locNoItem, lang + ': art not in the snapshot');
  ok(L.describe(areq, null, t).head === T[lang].locArtInventory, lang + ': art, no state yet -> open the inventory');
  ok(L.describe(areq, { step: 'artMatPick', matKind: 'essence' }, t).head === T[lang].locArtMatEssence, lang + ': an essence is ticked as an essence');
  ok(L.describe(areq, { step: 'artMatPick', matWrong: true }, t).tone === 'warn', lang + ': a wrong copy is a warning');
  ok(L.describe(areq, { step: 'artMatPick', matKind: 'copy', matNeed: 2, matChosen: 1 }, t).lines.some((x) => /1/.test(x) && /2/.test(x)), lang + ': several copies: how many are chosen');
  ok(L.describe(areq, { step: 'artMatGuess' }, t).head.includes('Клинок') && L.stepNo(areq, { step: 'artMatGuess' }).n === 6, lang + ': the geometry hint names the artifact, step 6');
  ok(L.describe(areq, { step: 'artLevelFirst', artCap: 13, artLevel: 10 }, t).head.includes('13'), lang + ': level first names the cap');
  ok(L.caps(areq, { step: 'artPromote' }, t).includes('Клинок') && L.stepNo(areq, { step: 'artPromote' }).n === 7, lang + ': plate header names the artifact, the button is step 7');
  ok(L.pillTexts(t, lang).doneArtPromote === T[lang].locJobArtPromote, lang + ': the done plate text');
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
