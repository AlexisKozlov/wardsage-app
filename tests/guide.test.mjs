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
let g = G.next(plan, live({ rows: { 6423: [7, 2] } }));
ok(g.kind === 'pick' && g.row === 7 && g.col === 2 && g.item.uid === 6423, 'pick');
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
console.log(`guide: ${n} passed`);
