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
ok(G.next(plan, live({ screen: 'inventory', bulk: true })).kind === 'bulk' && G.next(plan, live({ screen: 'inventory', bulk: true, heroUid: 1 })).kind === 'bulk', 'the Inventory in bulk sale: leave it first (before any hero or slot step)');
ok(G.next(plan, live({ screen: 'inventory', bulk: false, heroUid: 1 })).kind === 'hero', 'the Inventory in the normal mode: the usual steps');
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
ok(G.next(plan, live({ rows: { 6423: [5, 3] }, selUid: 77, selRow: 4, selOwner: 214700000 })).wrong === true, 'another item put on: the build item is still to find');
ok(G.next(plan, live({ rows: { 6423: [5, 3] }, selUid: 77, selRow: 4, selOwner: 0 })).wrong === false, 'another item only selected: not a wrong one');
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
  const boot = now - 90000;
  ok(G.freshPlans([mk('a', 1)], null, now, boot).map((p) => p.id).join() === 'a', 'first listing, plan made after the app started: started');
  ok(G.freshPlans([mk('a', 2)], null, now, now - 30000).length === 0, 'first listing, plan made before the app started: waits for «Надеть»');
  ok(G.freshPlans([mk('a', 1)], null, now).length === 0, 'first listing without the start time: nothing started');
}
// storage cleanup list: started only when made after the app started, within 10 minutes
{
  const now = Date.parse('2026-09-26T10:00:00Z'), boot = now - 60000;
  const mk = (min) => ({ id: 's', createdAt: new Date(now - min * 60000).toISOString() });
  ok(G.freshSell(mk(25), {}, now, boot) === false, 'sell list made 25 minutes before the start: not fresh');
  ok(G.freshSell(mk(6), {}, now, boot) === false, 'sell list made 5 minutes before the start: not fresh');
  ok(G.freshSell(mk(0.5), {}, now, boot) === true, 'sell list made after the start: fresh');
  ok(G.freshSell(mk(0.5), { s: true }, now, boot) === false, 'sell list seen before: not fresh');
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
// «Снять с других героев»: the items put on someone after the plan are taken from them - only for the plans asked for
{
  const p = { id: 't', heroUid: 1, items: [{ slot: 0, uid: 11, fromHeroUid: 0 }, { slot: 1, uid: 12, fromHeroUid: 0 }] };
  const lv = live({ owner: { 11: 5, 12: 1 } });
  ok(G.next(p, lv).kind === 'taken', 'an item put on another hero later: taken');
  G.takeOver([p], lv.owner, {});
  ok(G.next(p, lv).kind === 'taken', 'not asked: still taken');
  G.takeOver([p], lv.owner, { t: true });
  ok(p.items[0].fromHeroUid === 5 && G.next(p, lv).states[11] === 'now', 'asked: taken from the hero wearing it now');
}
// the plan's artifact comes after the gear: the host's step (live.art) decides; done when it is on the hero
{
  const p = { id: 'art', heroUid: 7, artifact: { uid: 9004, id: 3012 }, items: [{ slot: 0, uid: 11, fromHeroUid: 0 }] };
  const lv = (o) => live({ heroUid: 7, owner: { 11: 7 }, ...o });
  ok(G.next(p, lv({ owner: { 11: 0 } })).kind !== 'art', 'the artifact waits for the gear: the gear step comes first');
  let g = G.next(p, lv());
  ok(g.kind === 'art' && G.artStep(g) === 'wait' && g.done === 1 && !g.artDone, 'gear on, no word from the host yet: the artifact step, waiting');
  g = G.next(p, lv({ art: { uid: 9004, step: 'pick', done: false, owner: 5 } }));
  ok(g.kind === 'art' && G.artStep(g) === 'pick', 'the host says pick');
  for (const st of ['hero', 'tab', 'open', 'openAdd', 'missing', 'filter', 'unfit', 'unfitEx', 'replace', 'equip', 'confirm']) ok(G.artStep(G.next(p, lv({ art: { uid: 9004, step: st } }))) === st, 'step ' + st + ' passes through');
  ok(G.artStep(G.next(p, lv({ art: { uid: 9004, step: 'bogus' } }))) === 'wait', 'an unknown step is waiting');
  ok(G.next(p, lv({ art: { uid: 1234, step: 'done', done: true } })).kind === 'art', "another artifact's report does not count");
  g = G.next({ ...p, id: 'art1' }, lv({ art: { uid: 9004, step: 'done', done: true, owner: 7 } }));
  ok(g.kind === 'done' && g.artDone, 'on the hero: done');
  const p2 = { ...p, id: 'art2' };
  G.next(p2, lv({ art: { uid: 9004, step: 'done', done: true, owner: 7 } }));
  g = G.next(p2, lv());
  ok(g.kind === 'done' && g.artDone, 'a later live without art stays done (the page remembers it)');
  g = G.next(p2, lv({ art: { uid: 9004, step: 'wait', done: false } }));
  ok(g.kind === 'done' && g.artDone, 'a later live with art not done stays done too');
  ok(G.next({ ...p, artifact: undefined }, lv()).kind === 'done', 'a plan without an artifact is done with the gear');
  ok(G.next(p, lv({ gameRunning: false })).kind === 'closed', 'the game closed: closed');
  ok(G.next(p, lv({ owner: { 11: 5 } })).kind === 'taken', 'the gear taken by another hero: taken, no artifact step');
  ok(G.highlightUid(G.next(p, lv())) === 0, 'no gear item is framed during the artifact step');
  ok(G.nextQueued(['art'], [p], lv()) === p && G.nextQueued(['art'], [p], lv({ art: { uid: 9004, step: 'done', done: true } })) === null, 'the queue keeps the build until the artifact is on');
}
// the artifact step's texts exist in both languages, one per step, short enough for the plates
{
  globalThis.window = globalThis;
  await import('../ui/i18n.js');
  const tt = globalThis.RF_TEXTS;
  ok(tt && tt.ru && tt.en, 'the texts are loaded');
  const ks = (l) => Object.keys(tt[l]).filter((k) => /^art(S_|Sub_|Slot|FromSite|DoneRow|Head|Caps)/.test(k)).sort();
  ok(ks('ru').join() === ks('en').join() && ks('ru').length >= 15, 'artifact texts: the same keys in Russian and English');
  for (const st of ['hero', 'wait', 'tab', 'open', 'openAdd', 'pick', 'pickAny', 'missing', 'filter', 'unfit', 'unfitEx', 'replace', 'equip', 'confirm', 'done'])
    for (const l of ['ru', 'en']) ok(typeof tt[l]['artS_' + st] === 'string' && tt[l]['artS_' + st].length > 3 && tt[l]['artS_' + st].replace('{0}', 'Хххххххххххх').length <= 80, 'artS_' + st + ' (' + l + ') fits a plate');
}
console.log(`guide: ${n} passed`);
