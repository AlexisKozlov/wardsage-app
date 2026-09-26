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
// plans from the local bridge
const bp = G.bridgePlan({ id: 'bridge:c1', heroUid: 214700000, heroName: 'Сунь Укун', items: [{ slot: 0, uid: 42 }, { slot: 2, uid: 6423 }] }, (u) => 'Item #' + u);
ok(bp.bridge && bp.id === 'bridge:c1' && bp.items.length === 2 && bp.items[0].name === 'Item #42' && bp.items[1].slot === 2, 'bridge plan');
ok(G.next(bp, live({ part: 0, rows: { 42: [2, 1] } })).kind === 'pick', 'bridge plan: the guide works without the site fields');
ok(G.next(bp, live({ owner: { 42: 214700000, 6423: 214700000 } })).kind === 'done', 'bridge plan: done when the hero wears both');
const merged = G.mergePlans([bp, plan, { ...bp, id: 'bridge:c2' }], [{ id: 's1', items: [] }], { 'bridge:c2': true });
ok(merged.map((p) => p.id).join() === 'bridge:c1,s1', 'reload keeps unfinished bridge plans first, drops old site plans and finished bridge plans');
ok(G.mergePlans(null, null, null).length === 0, 'merge of nothing');
// an item put on another hero after the plan was made is never taken away from them
g = G.next(plan, live({ owner: { 6423: 999900000 } }));
ok(g.states[6423] === 'taken' && g.item.uid === 33 && g.taken === 1, 'item on an unexpected hero: skipped, the next one is current');
ok(G.next(plan, live({ owner: { 6423: 200100000 } })).item.uid === 6423, 'the hero the plan takes it from: still wanted');
ok(G.next(plan, live({ owner: { 6423: 999900000, 33: 214700000 } })).kind === 'taken', 'only taken items left -> taken, not done');
ok(G.next(plan, live({ owner: { 6423: 0 } })).item.uid === 6423, 'in the bag: wanted');
// «Надеть в игре» on the site a moment ago: the app starts the plan by itself (older plans wait for «Надеть»)
{
  const now = Date.parse('2026-09-26T10:00:00Z');
  const mk = (id, min) => ({ id, heroUid: 1, items: [], createdAt: new Date(now - min * 60000).toISOString() });
  ok(G.freshPlan([mk('a', 1)], null, now) === null, 'first listing after the app starts: nothing started');
  ok(G.freshPlan([mk('a', 1)], {}, now).id === 'a', 'a plan made a minute ago, not seen before: started');
  ok(G.freshPlan([mk('a', 1)], { a: true }, now) === null, 'seen before: not started again');
  ok(G.freshPlan([mk('b', 10)], {}, now) === null, 'made 10 minutes ago: waits for «Надеть»');
  ok(G.freshPlan([{ ...mk('c', 1), bridge: true }], {}, now) === null, 'bridge plans start on their own');
}
// several builds started at once on the site: all of them, in order; the queue skips what is done or gone
{
  const now = Date.parse('2026-09-26T10:00:00Z');
  const mk = (id, heroUid, items) => ({ id, heroUid, items, createdAt: new Date(now - 30000).toISOString() });
  const a = mk('a', 1, [{ slot: 0, uid: 11, fromHeroUid: 0 }]), b = mk('b', 2, [{ slot: 0, uid: 22, fromHeroUid: 0 }]);
  ok(G.freshPlans([a, b], {}, now).map((p) => p.id).join() === 'a,b', 'both fresh builds are started, in order');
  const lv = live({ owner: { 11: 1, 22: 0 } });
  ok(G.nextQueued(['a', 'b'], [a, b], lv).id === 'b', 'the queue skips a build that is on already');
  ok(G.nextQueued(['x', 'b'], [a, b], lv).id === 'b' && G.nextQueued(['a'], [a, b], lv) === null, 'gone or done: skipped / nothing left');
}
// a bridge command names only the items: the owner when it arrives is the hero it is taken from on purpose
{
  const bp = G.bridgePlan({ id: 'bridge:1', heroUid: 7, heroName: 'X', items: [{ slot: 0, uid: 321 }] }, (u) => '#' + u);
  const lv = (o) => live({ heroUid: 7, part: 0, ...o });
  ok(G.next(bp, lv({ owner: { 321: 5 } })).kind !== 'taken', 'owner not adopted yet: not taken');
  G.adoptOwners([bp], { 321: 5 });
  ok(bp.items[0].fromHeroUid === 5 && G.next(bp, lv({ owner: { 321: 5 } })).kind !== 'taken', 'taken from hero 5 on purpose');
  ok(G.next(bp, lv({ owner: { 321: 9 } })).kind === 'taken', 'on another hero afterwards: taken, not taken off');
}
console.log(`guide: ${n} passed`);
