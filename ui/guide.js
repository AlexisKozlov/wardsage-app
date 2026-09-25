// RealmForge desktop — equip helper: what to tell the player next. Pure logic (tests: tests/guide.test.mjs).
//
// plan: {id, heroUid, heroName, items:[{slot, uid, slotName, name, setName, level, mainStat, fromHeroUid, fromHeroName}]}
// live (from the host, read-only memory reads): {gameRunning, heroUid, panelOk, part, hideEquipped, hideEnhanced,
//   filterActive, rows:{uid:[row, col]}, owner:{uid: heroUid}}
(function (root) {
  // rows of the gear list visible without scrolling in the game (1080p: about 4.5)
  const VISIBLE_ROWS = 4;

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
    if (pos) {
      g.row = pos[0]; g.col = pos[1] || 0;
      // the player already selected it in the game list
      if (live.selUid > 0 && live.selUid === cur.uid) { g.kind = 'selected'; return g; }
      // far down a long list: narrow it with the game's filter first (set + main stat) instead of scrolling
      const far = pos[0] > VISIBLE_ROWS && !live.filterActive;
      // another item is selected: say where the right one is from there (unless that is still a long way)
      if (live.selRow > 0) {
        g.dr = pos[0] - live.selRow;
        if (!far || Math.abs(g.dr) <= VISIBLE_ROWS) { g.kind = 'rel'; return g; }
      }
      g.kind = far ? 'filter' : 'pick';
      return g;
    }
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

  // Stat name of a main-stat text such as «ОЗ 750» / «Крит. УРН 80%» (for the filter hint).
  function statOf(mainStat) {
    const first = String(mainStat || '').split(',')[0].trim();
    return first.replace(/\s*[+\-]?[\d\s.,]+%?$/, '').trim();
  }

  // Hero bust on the site: hero uid = base id × 100000 (+ copy index).
  function bustUrl(site, heroUid) {
    const id = Math.floor(heroUid / 100000);
    return id > 0 ? `${site}/art/heroes/HeroBust_${id}.webp` : '';
  }

  // Hero portrait from the game (HeroHead_<base id>, 122×185) on the site.
  function headUrl(site, heroUid) {
    const id = Math.floor(heroUid / 100000);
    return id > 0 ? `${site}/art/heads/HeroHead_${id}.webp` : '';
  }

  // Item icon from the game on the site; only plain sprite names (they come from the server).
  function itemUrl(site, icon) {
    return /^[A-Za-z0-9_]{1,64}$/.test(icon || '') ? `${site}/art/items/${icon}.webp` : '';
  }

  // Set icon from the game on the site (icon_suit_…).
  function setUrl(site, icon) {
    return /^[A-Za-z0-9_]{1,64}$/.test(icon || '') ? `${site}/art/sets/${icon}.webp` : '';
  }

  // Background texture by item stars (game rarity colours 1..6), 0 = none.
  const rankOf = (stars) => (stars > 0 ? Math.min(6, Math.max(1, stars | 0)) : 0);

  // Item to frame in the game (0 = none): only while the list with it is on the screen.
  const HIGHLIGHT = { pick: 1, filter: 1, rel: 1, selected: 1 };
  function highlightUid(g) { return g && g.item && HIGHLIGHT[g.kind] ? g.item.uid : 0; }

  // Russian plural: 1 ряд, 2 ряда, 5 рядов.
  function plural(n, one, few, many) {
    const a = Math.abs(n) % 100, b = a % 10;
    return a > 10 && a < 20 ? many : b === 1 ? one : b >= 2 && b <= 4 ? few : many;
  }

  const api = { next, pickPlan, highlightUid, plural, bustUrl, headUrl, itemUrl, setUrl, rankOf, statOf, VISIBLE_ROWS };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.RFGuide = api;
})(typeof window !== 'undefined' ? window : globalThis);
