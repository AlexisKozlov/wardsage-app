// Equip helper logic of the desktop interface (ui/guide.js).
import assert from 'node:assert/strict';
await import('../ui/guide.js');   // ui scripts are plain browser scripts: they set globals
const G = globalThis.RFGuide;

const plan = { id: 'p1', heroUid: 214700000, heroName: 'Сунь Укун', items: [
  { slot: 2, uid: 6423, level: 16, fromHeroUid: 200100000, fromHeroName: 'Байек' },
  { slot: 4, uid: 33, level: 0, fromHeroUid: 0, fromHeroName: null } ] };
const live = (o) => ({ gameRunning: true, heroUid: 214700000, panelOk: true, part: 2, hideEquipped: false, hideEnhanced: false, filterActive: false, rows: {}, owner: {}, ...o });
let n = 0; const ok = (c, m) => { assert.ok(c, m); n++; };

ok(G.next(plan, null).kind === 'closed', 'no live -> closed');
ok(G.next(plan, live({ gameRunning: false })).kind === 'closed', 'game exited');
ok(G.next(plan, live({ heroUid: 1 })).kind === 'hero', 'other hero');
ok(G.next(plan, live({ part: -1 })).kind === 'slot', 'no slot');
ok(G.next(plan, live({ panelOk: false })).kind === 'slot', 'panel gone');
let g = G.next(plan, live({ rows: { 6423: [3, 2] } }));
ok(g.kind === 'pick' && g.row === 3 && g.col === 2 && g.item.uid === 6423, 'pick in the first rows');
g = G.next(plan, live({ rows: { 6423: [103, 1] } }));
ok(g.kind === 'filter' && g.row === 103, 'far down a long list -> narrow it with the filter');
ok(G.next(plan, live({ rows: { 6423: [7, 2] }, filterActive: true })).kind === 'pick', 'filtered list: pick (scroll hint)');
ok(G.statOf('ОЗ 750') === 'ОЗ' && G.statOf('Крит. УРН 80%') === 'Крит. УРН' && G.statOf('Бонус к АТК 12,5%') === 'Бонус к АТК', 'stat name');
ok(g.states[6423] === 'now' && g.states[33] === 'wait', 'states');
g = G.next(plan, live({ hideEquipped: true }));
ok(g.kind === 'hiddenEq' && g.other === 'Байек', 'hidden: worn by another hero');
ok(G.next(plan, live({ hideEquipped: true, owner: { 6423: 0 } })).kind === 'notIn', 'in the bag now: not hidden by that filter');
ok(G.next(plan, live({ hideEnhanced: true })).kind === 'hiddenEnh', 'enhanced hidden');
ok(G.next(plan, live({ filterActive: true })).kind === 'hiddenFilter', 'filter');
ok(G.next(plan, live()).kind === 'notIn', 'not in list');
g = G.next(plan, live({ owner: { 6423: 214700000 } }));
ok(g.kind === 'slot' && g.item.uid === 33 && g.done === 1, 'first on -> next slot');
ok(G.next(plan, live({ owner: { 6423: 214700000, 33: 214700000 } })).kind === 'done', 'done');
ok(G.pickPlan([{ heroUid: 1 }, plan], live(), 0) === 1, 'follows the open hero');
ok(G.pickPlan([{ heroUid: 1 }, plan], live({ heroUid: 5 }), 0) === 0, 'keeps the choice');
ok(G.pickPlan([], live(), 0) === -1, 'no plans');
ok(G.bustUrl('https://x', 214700000) === 'https://x/art/heroes/HeroBust_2147.webp', 'bust url');
ok(G.headUrl('https://x', 214700001) === 'https://x/art/heads/HeroHead_2147.webp', 'portrait url');
ok(G.itemUrl('https://x', 'Item_101403') === 'https://x/art/items/Item_101403.webp' && G.itemUrl('https://x', '../a') === '' && G.itemUrl('https://x', '') === '', 'item icon url');
ok(G.rankOf(6) === 6 && G.rankOf(9) === 6 && G.rankOf(0) === 0 && G.rankOf(1) === 1, 'rarity by stars');
// selection in the game list
g = G.next(plan, live({ rows: { 6423: [3, 2] }, selUid: 6423, selRow: 3 }));
ok(g.kind === 'selected', 'the right item is selected');
g = G.next(plan, live({ rows: { 6423: [5, 3] }, selUid: 77, selRow: 2 }));
ok(g.kind === 'rel' && g.dr === 3 && g.col === 3, 'relative to the selected item');
ok(G.next(plan, live({ rows: { 6423: [5, 3] }, selUid: 77, selRow: 9 })).dr === -4, 'above the selected one');
ok(G.next(plan, live({ rows: { 6423: [103, 1] }, selUid: 77, selRow: 2 })).kind === 'filter', 'still far: filter first');
ok(G.next(plan, live({ rows: { 6423: [103, 1] }, selUid: 77, selRow: 101 })).kind === 'rel', 'far list but the selection is near');
ok(G.highlightUid(G.next(plan, live({ rows: { 6423: [3, 2] } }))) === 6423, 'frame the item while it is in the list');
ok(G.highlightUid(G.next(plan, live({ part: -1 }))) === 0 && G.highlightUid(G.next(plan, null)) === 0, 'no frame otherwise');
ok(G.plural(1, 'ряд', 'ряда', 'рядов') === 'ряд' && G.plural(3, 'ряд', 'ряда', 'рядов') === 'ряда' && G.plural(12, 'ряд', 'ряда', 'рядов') === 'рядов' && G.plural(22, 'ряд', 'ряда', 'рядов') === 'ряда', 'plural');
console.log(`guide: ${n} passed`);
