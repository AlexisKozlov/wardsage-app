// RealmForge desktop — equip helper: what to tell the player next. Pure logic (tests: tests/guide.test.mjs).
//
// plan: {id, heroUid, heroName, items:[{slot, uid, slotName, name, setName, level, mainStat, fromHeroUid, fromHeroName}]}
// live (from the host, read-only memory reads): {gameRunning, heroUid, panelOk, part, hideEquipped, hideEnhanced,
//   filterActive, rows:{uid:[row, col]}, owner:{uid: heroUid}}
(function (root) {
  function next(plan, live) {
    const g = { kind: null, item: null, row: 0, col: 0, other: null, states: {}, done: 0 };
    const owner = (live && live.owner) || {};
    let cur = null;
    for (const it of plan.items) {
      const on = owner[it.uid] === plan.heroUid;
      if (on) g.done++;
      g.states[it.uid] = on ? 'done' : 'wait';
      if (!on && !cur) cur = it;
    }
    if (cur) g.states[cur.uid] = 'now';
    if (!live || live.gameRunning === false) { g.kind = 'closed'; return g; }
    if (!cur) { g.kind = 'done'; return g; }
    g.item = cur;
    if (live.heroUid !== plan.heroUid) { g.kind = 'hero'; return g; }
    if (!live.panelOk || live.part !== cur.slot) { g.kind = 'slot'; return g; }
    const pos = live.rows && live.rows[cur.uid];
    if (pos) { g.kind = 'pick'; g.row = pos[0]; g.col = pos[1] || 0; return g; }
    const who = owner[cur.uid] !== undefined ? owner[cur.uid] : cur.fromHeroUid;
    if (who > 0 && who !== plan.heroUid && live.hideEquipped) {
      g.kind = 'hiddenEq';
      g.other = who === cur.fromHeroUid ? cur.fromHeroName : null;
      return g;
    }
    if (live.hideEnhanced && cur.level > 0) { g.kind = 'hiddenEnh'; return g; }
    if (live.filterActive) { g.kind = 'hiddenFilter'; return g; }
    g.kind = 'notIn';
    return g;
  }

  // Plan of the hero open in the game, else the current choice, else the first.
  function pickPlan(plans, live, current) {
    if (!plans || !plans.length) return -1;
    if (live && live.heroUid > 0) { const i = plans.findIndex((p) => p.heroUid === live.heroUid); if (i >= 0) return i; }
    return current >= 0 && current < plans.length ? current : 0;
  }

  // Hero bust on the site: hero uid = base id × 100000 (+ copy index).
  function bustUrl(site, heroUid) {
    const id = Math.floor(heroUid / 100000);
    return id > 0 ? `${site}/art/heroes/HeroBust_${id}.webp` : '';
  }

  const api = { next, pickPlan, bustUrl };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.RFGuide = api;
})(typeof window !== 'undefined' ? window : globalThis);
