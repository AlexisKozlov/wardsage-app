// RealmForge desktop — equip helper: what to tell the player next. Pure logic (tests: tests/guide.test.mjs).
//
// plan: {id, heroUid, heroName, items:[{slot, uid, slotName, name, setName, level, mainStat, fromHeroUid, fromHeroName}]}
//   from the site, or {…, bridge: true} from the local bridge (bridgePlan)
// live (from the host, read-only memory reads): {gameRunning, heroUid, panelOk, part, hideEquipped, hideEnhanced,
//   filterActive, rows:{uid:[row, col]}, owner:{uid: heroUid}}
(function (root) {
  // rows of the gear list visible without scrolling in the game (1080p: about 4.5)
  const VISIBLE_ROWS = 4;

  function next(plan, live) {
    const g = { kind: null, item: null, row: 0, col: 0, other: null, states: {}, done: 0, taken: 0 };
    const owner = (live && live.owner) || {};
    let cur = null;
    for (const it of plan.items) {
      const own = owner[it.uid];
      const on = own === plan.heroUid;
      // on a hero the plan did not count on (put on someone after the plan was made): never taken away from them.
      // fromHeroUid null: not known yet (a bridge plan before the first reading, see adoptOwners)
      const taken = !on && own > 0 && it.fromHeroUid != null && own !== it.fromHeroUid;
      if (on) g.done++;
      if (taken) g.taken++;
      g.states[it.uid] = on ? 'done' : taken ? 'taken' : 'wait';
      if (!on && !taken && !cur) cur = it;
    }
    if (cur) g.states[cur.uid] = 'now';
    if (!live || live.gameRunning === false) { g.kind = 'closed'; return g; }
    if (!cur) { g.kind = g.taken ? 'taken' : 'done'; return g; }
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

  // A plan from the local bridge ({id: "bridge:…", heroUid, heroName, items: [{slot, uid}]}). The bridge says only what
  // goes where; the fields a site plan fills (names, level, icons, the card) get neutral values.
  function bridgePlan(raw, itemName) {
    return {
      id: raw.id, heroUid: raw.heroUid, heroName: raw.heroName, createdAt: null, bridge: true,
      items: (raw.items || []).map((it) => ({
        slot: it.slot, uid: it.uid, name: itemName(it.uid), slotName: '', setName: '', level: 0, stars: 0, mainStat: '',
        fromHeroUid: null, fromHeroName: null, icon: '', setIcon: '', subs: [], setBonus: [], main: [],
      })),
    };
  }

  // A plan made on the site within the last 3 minutes that this app has not listed before: the player pressed «Надеть в
  // игре» just now, so it starts by itself. The first listing after the app starts only marks what is there.
  const FRESH_MS = 3 * 60 * 1000;
  function freshPlans(plans, seen, now) {
    if (!seen) return [];
    return (plans || []).filter((p) => !p.bridge && !seen[p.id] && p.createdAt && now - Date.parse(p.createdAt) < FRESH_MS
      && now - Date.parse(p.createdAt) > -FRESH_MS);
  }
  function freshPlan(plans, seen, now) { return freshPlans(plans, seen, now)[0] || null; }

  // The started plans queue up («Надеть» on the site for several heroes, «Надеть все» here): the next one still to do,
  // or null. A plan that is done, whose items are all on other heroes, or gone from the list is skipped.
  function nextQueued(queue, plans, live) {
    for (const id of queue || []) {
      const p = (plans || []).find((x) => x.id === id);
      if (!p) continue;
      const k = next(p, live).kind;
      if (k !== 'done' && k !== 'taken') return p;
    }
    return null;
  }

  // A bridge command names only the items: whoever wears an item when the command arrives is the hero it is taken from
  // on purpose (the dashboard checked it against the same snapshot). Later changes of owner count as «taken».
  function adoptOwners(plans, owner) {
    if (!owner) return;
    for (const p of plans || []) {
      if (!p.bridge) continue;
      for (const it of p.items) if (it.fromHeroUid == null && owner[it.uid] !== undefined) it.fromHeroUid = owner[it.uid] || 0;
    }
  }

  // Plans after a reload from the site: the bridge's plans still in progress stay (first), the site's list follows.
  function mergePlans(current, site, reported) {
    return (current || []).filter((p) => p.bridge && !(reported && reported[p.id])).concat(site || []);
  }

  // Stat name of a main-stat text such as «ОЗ 750» / «Крит. УРН 80%» (for the filter hint).
  function statOf(mainStat) {
    const first = String(mainStat || '').split(',')[0].trim();
    return first.replace(/\s*[+\-]?[\d\s.,]+%?$/, '').trim();
  }

  // Stat id of a main-stat text («АТК 1 056», «Крит. УРН 80%», «HP 12%»): flat ATK/DEF/HP with % are the bonus stats.
  const STAT_IDS = {
    'атк': 1, 'atk': 1, 'защ': 2, 'def': 2, 'сопр. магии': 5, 'm. res.': 5, 'оз': 7, 'hp': 7,
    'бонус к атк': 13, 'atk bonus': 13, 'бонус к защ': 14, 'def bonus': 14, 'бонус к оз': 19, 'hp bonus': 19,
    'восстановление ярости': 23, 'rage regen': 23, 'шанс крита': 24, 'шанс крит.': 24, 'crit. rate': 24,
    'крит. урн': 25, 'crit. dmg': 25, 'эффект исцеления': 27, 'healing effect': 27, 'са': 29, 'atk spd.': 29,
  };
  function statId(mainStat) {
    const first = String(mainStat || '').split(',')[0].trim();
    const id = STAT_IDS[statOf(first).toLowerCase()] || 0;
    if (/%/.test(first) && (id === 1 || id === 2 || id === 7)) return id === 1 ? 13 : id === 2 ? 14 : 19;
    return id;
  }
  function statUrl(site, id) { return id > 0 ? `${site}/art/ui/stat_${id}.webp` : ''; }

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
  // (stars = the game's iStarLvl 1…13: EquipStarQuality → ItemQuality background rank; 7 = ancient legendary = rank 5)
  const rankOf = (stars) => (stars > 0 ? (stars === 7 ? 5 : Math.min(6, Math.max(1, stars | 0))) : 0);

  // Item to frame in the game (0 = none): only while the list with it is on the screen.
  const HIGHLIGHT = { pick: 1, filter: 1, rel: 1, selected: 1 };
  function highlightUid(g) { return g && g.item && HIGHLIGHT[g.kind] ? g.item.uid : 0; }

  // Russian plural: 1 ряд, 2 ряда, 5 рядов.
  function plural(n, one, few, many) {
    const a = Math.abs(n) % 100, b = a % 10;
    return a > 10 && a < 20 ? many : b === 1 ? one : b >= 2 && b <= 4 ? few : many;
  }

  const api = { next, pickPlan, bridgePlan, mergePlans, freshPlan, freshPlans, nextQueued, adoptOwners, highlightUid, plural, bustUrl, headUrl, itemUrl, setUrl, rankOf, statOf, statId, statUrl, VISIBLE_ROWS };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.RFGuide = api;
})(typeof window !== 'undefined' ? window : globalThis);
