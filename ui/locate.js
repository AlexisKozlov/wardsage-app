// Wardsage desktop — «Найти в игре»: the text for each step the host reports. Pure logic (tests: tests/locate.test.mjs).
//
// req:  {id, purpose, createdAt, heroUid, heroName, item|null}  (item: a plan item with slotName, setName, main[], subs[])
// step: {step, pos, row, col, count, missing:[…], onHero, onGear, scroll, scrollRows, needClick, subs:[bool]|null, changed}  (host, src/LocateGuide.cs)
// t(key, …args) is the interface's text lookup.
(function (root) {
  const KINDS = ['noItem', 'otherAccount', 'openInventory', 'openEquipTab', 'inList', 'hiddenFilter', 'selected', 'openRecastTab',
    'recastFilter', 'recastPick', 'recastNotInList', 'choosePending', 'gemPick', 'done',
    'openAscendTab', 'ascendPick', 'ascendNotInList', 'ascendReady', 'openFoundry', 'gemFunction', 'ascendFilter', 'ascendLoading', 'exitBulk'];
  // «Продвинуть» артефакт (purpose artPromote): req.art = {uid, id, name, lv, cap, st, need, via}; the host never clicks for it
  const ART_KINDS = ['openInventory', 'artOpenTab', 'artExitMode', 'artListLoading', 'artNotInList', 'artInList', 'artSelected', 'artWrongArtifact', 'artLevelFirst',
    'artOpenPromote', 'artMatLoading', 'artMatGuess', 'artMatPick', 'artMatNone', 'artPromote'];

  // auto click (host: src/LocatePilot.cs): st.auto = '' (off) | work | paused | background | hint | stopped, st.autoWhy = the reason when stopped.
  // The app clicks by itself only on these steps; the rest (the Foundry, the last button) stay the player's.
  const AUTO_STEPS = ['openInventory', 'openEquipTab', 'inList', 'openFoundry', 'openAscendTab', 'ascendFilter', 'ascendPick'];
  // why the click gate stopped the pilot (host: src/ClickGate.cs)
  const GATE_WHY = { unknown: 'gateWhy_unknown', screen: 'gateWhy_screen', modal: 'gateWhy_modal', bulk: 'gateWhy_bulk', cell: 'gateWhy_cell', scroll: 'gateWhy_scroll', target: 'gateWhy_target', nochange: 'gateWhy_nochange' };
  const AUTO_WHY = { bulk: 'locAutoBulk', lost: 'locAutoLost', stuck: 'locAutoStuck', screen: 'locAutoScreen', cancelled: 'locAutoCancel' };

  function names(list) { return (list || []).map((x) => x.name || x.text).filter(Boolean); }

  // where the game's own reading puts the item in the Foundry list ('' = unknown): one short line
  function scrollLine(st, t) {
    if (st.scroll === 'below') return t('locScrollDown', st.scrollRows);
    if (st.scroll === 'above') return t('locScrollUp', st.scrollRows);
    if (st.scroll === 'visible') return t('locVisible');
    return '';
  }

  // one line per filter part still to set (the foundry's filter keeps only items with all the chosen sub stats)
  function filterLines(req, st, t) {
    const it = req.item, out = [];
    const miss = st.missing || [];
    if (miss.indexOf('slot') >= 0) out.push(t('locFSlot', it.slotName || ''));
    if (miss.indexOf('suit') >= 0 && it.setName) out.push(t('locFSuit', it.setName));
    if (miss.indexOf('main') >= 0) { const m = names(it.main)[0] || it.mainStat; if (m) out.push(t('locFMain', m)); }
    if (miss.indexOf('subs') >= 0) { const s = names(it.subs); if (s.length) out.push(t('locFSubs', s.join(', '))); }
    return out;
  }

  // the artifact promotion's step -> {tone, head, lines}
  function describeArt(req, st, t) {
    const a = req.art, s = st || {};
    switch (s.step) {
      case 'artOpenTab': return { tone: 'info', head: t('locArtOpenTab'), lines: [] };
      case 'artExitMode': return { tone: 'warn', head: t('locArtExit'), lines: [t('locArtExitP')] };
      case 'artListLoading': return { tone: 'info', head: t('locArtLoading'), lines: [] };
      case 'artNotInList': return { tone: 'warn', head: t('locArtNotInList'), lines: [t('locArtNotInListP')] };
      case 'artInList': return { tone: 'info', head: t('locArtInList'), lines: [scrollLine(s, t) || t('locArtInListP', s.row, s.col)] };
      case 'artSelected': return { tone: 'info', head: t('locArtSelected'), lines: [t('locArtSelectedP')] };
      case 'artWrongArtifact': return { tone: 'warn', head: t('locArtWrong'), lines: [t('locArtWrongP', a.name || '')] };
      case 'artLevelFirst': return { tone: 'warn', head: t('locArtLevelFirst', s.artCap || a.cap), lines: [t('locArtLevelFirstP', s.artLevel || a.lv)] };
      case 'artOpenPromote': return { tone: 'info', head: t('locArtOpenPromote'), lines: [] };
      case 'artMatLoading': return { tone: 'info', head: t('locArtMatLoading'), lines: [] };
      case 'artMatGuess': return { tone: 'info', head: t('locArtMatGuess', a.name || ''), lines: [t('locArtMatGuessP', a.need || 1)] };
      case 'artMatPick': {
        if (s.matWrong) return { tone: 'warn', head: t('locArtMatWrong'), lines: [t('locArtMatWrongP')] };
        const lines = [scrollLine(s, t)].filter(Boolean);
        if ((s.matNeed || 1) > 1) lines.push(t('locArtMatCount', s.matChosen || 0, s.matNeed));
        return { tone: 'info', head: t(s.matKind === 'essence' ? 'locArtMatEssence' : 'locArtMatCopy'), lines };
      }
      case 'artMatNone': return { tone: 'warn', head: t('locArtMatNone'), lines: [t('locArtMatNoneP')] };
      case 'artPromote': return { tone: 'done', head: t('locArtPromote'), lines: [t('locArtPromoteP')] };
      default: return { tone: 'info', head: t('locArtInventory'), lines: [t('locArtInventoryP')] };   // openInventory / no state yet
    }
  }

  // -> {tone: 'info'|'warn'|'done', head, lines:[…], tip:[…] (the plate over the game, ≤ 3 short lines)}
  function describe(req, st, t) {
    if (!req) return null;
    if (req.purpose === 'artPromote') {
      const r0 = req.art ? describeArt(req, st, t) : { tone: 'warn', head: t('locNoItem'), lines: [t('locNoItemP')] };
      r0.tip = [r0.head].concat(r0.lines.slice(0, 2)).map((x) => (x.length > 90 ? x.slice(0, 89) + '…' : x));
      return r0;
    }
    if (!req.item) return { tone: 'warn', head: t('locNoItem'), lines: [t('locNoItemP')], tip: [] };
    if (!st || KINDS.indexOf(st.step) < 0) return { tone: 'info', head: t('locOpenInventory'), lines: [], tip: [t('locOpenInventory')] };
    const it = req.item, pos = t('locPos', st.pos, st.count);
    let r;
    // Ascension (gear T1 -> T2): the way to the Foundry (the Inventory's or the hero gear screen's «Foundry» button; nothing is chosen
    // before it), then the Foundry's «Ascend» tab where the item is found in the tab's own list
    switch (st.step) {
      case 'noItem': r = { tone: 'warn', head: t('locNoItem'), lines: [t('locNoItemP')] }; break;
      case 'otherAccount': r = { tone: 'warn', head: t('locOther'), lines: [t('locOtherP')] }; break;
      case 'openInventory':
        r = st.onHero ? { tone: 'info', head: t('locOnHero', it.slotName || '', req.heroName || ''), lines: [t('locOnHeroP')] }
                      : { tone: 'info', head: t('locOpenInventory'), lines: [t('locOpenInventoryP')] };
        break;
      case 'openEquipTab': r = { tone: 'info', head: t('locEquipTab'), lines: [] }; break;
      case 'inList':
        r = st.needClick ? { tone: 'info', head: t('locNeedClick'), lines: [t('locNeedClickP', st.row, st.col)] }
                         : { tone: 'info', head: t('locInList', st.row, st.col), lines: [t('locInListP')] };
        break;
      case 'hiddenFilter': r = { tone: 'warn', head: t('locHidden'), lines: [t('locHiddenP')] }; break;
      // the inventory is in «Массовая продажа»: leave it first (the app clicks nothing there)
      case 'exitBulk': r = { tone: 'warn', head: t('locExitBulk'), lines: [t('locExitBulkP')] }; break;
      // recast goes through the gem screen (the Foundry is for Ascension only): card button, then the function, then «Улучшить»
      case 'selected': r = { tone: 'done', head: t('locSelected'), lines: [t(req.purpose === 'ascend' ? 'locSelectedAscP' : 'locSelectedP')] }; break;
      case 'openFoundry': r = st.onGear ? { tone: 'info', head: t('locFoundryHero'), lines: [t('locFoundryP')] } : { tone: 'info', head: t('locFoundryInv'), lines: [t('locFoundryP')] }; break;
      case 'gemFunction': {
        // the function the item needs (the host reads it from the item's quality): the hint names that one only
        const k = st.func === 8 ? 'Adv' : st.func === 7 ? 'Rec' : '';
        r = k ? { tone: 'info', head: t(st.wrongFunc ? 'locGemWrong' + k : 'locGemFunc' + k), lines: [t('locGemFunc' + k + 'P')] }
              : { tone: 'info', head: t('locGemFunc'), lines: [t('locGemFuncP')] };
        break;
      }
      case 'ascendLoading': r = { tone: 'info', head: t('locAscLoading'), lines: [] }; break;
      case 'ascendFilter': {
        let l = filterLines(req, st, t);
        // the filter pop-up is open: the one thing to press in it
        const go = st.filterGo, pop = {
          reset: () => t('locPopReset'), suitRow: () => t('locPopSuitRow'), mainRow: () => t('locPopMainRow'), subRow: () => t('locPopSubRow'),
          suitTab: () => t('locPopSuitTab', st.suitTab === 1 ? 3 : 2), suitCell: () => t('locPopSuitCell', it.setName || ''),
        };
        if (go && pop[go]) l = [pop[go]()];
        r = { tone: 'info', head: t('locFilterA'), lines: l.concat(st.pos > 0 ? [pos] : []) };
        break;
      }
      case 'openRecastTab': r = { tone: 'info', head: t('locRecastTab'), lines: [] }; break;
      case 'recastFilter': {
        const l = filterLines(req, st, t);
        r = { tone: 'info', head: t('locFilter'), lines: l.concat(st.pos > 0 ? [pos] : []) };
        break;
      }
      case 'recastPick': r = { tone: 'info', head: t('locPick', st.pos, st.count), lines: [scrollLine(st, t)].filter(Boolean) }; break;
      case 'recastNotInList': r = { tone: 'warn', head: t('locNotInList'), lines: [t('locNotInListP')] }; break;
      case 'choosePending': r = { tone: 'warn', head: t('locPending'), lines: [t('locPendingP')] }; break;
      case 'gemPick': r = st.pos > 0 ? { tone: 'info', head: t('locPick', st.pos, st.count), lines: [] } : { tone: 'info', head: t('locGemOpen'), lines: [] }; break;
      case 'openAscendTab': r = { tone: 'info', head: t('locAscendTab'), lines: [] }; break;
      case 'ascendPick': {
        const lines = [scrollLine(st, t)].filter(Boolean);
        // the list is not narrowed to the item's slot and the item is off the screen: the slot icon shortens the way
        if ((st.missing || []).indexOf('slot') >= 0 && st.scroll !== 'visible' && it.slotName) lines.push(t('locFSlot', it.slotName));
        r = { tone: 'info', head: t('locPick', st.pos, st.count), lines };
        break;
      }
      case 'ascendNotInList': r = { tone: 'warn', head: t('locAscNotInList'), lines: [t('locAscNotInListP')] }; break;
      case 'ascendReady': r = { tone: 'done', head: t('locAscendReady'), lines: [t('locAscendReadyP')] }; break;
      default: r = { tone: 'done', head: t('locDone'), lines: [t(st.onGem ? 'locDoneGemP' : 'locDoneP')] };   // the request stays until the player leaves this screen (ui/app.js)
    }
    // auto click: the app is doing this step itself, or it gave up (and says why; the step's own text follows)
    if (st.auto === 'stopped' && GATE_WHY[st.autoWhy]) r = { tone: 'warn', head: t('gateStop', t(GATE_WHY[st.autoWhy])), lines: [t('locAutoRest'), r.head].concat(r.lines) };
    else if (st.auto === 'stopped') r = { tone: st.autoWhy === 'cancelled' ? 'info' : 'warn', head: t('locAutoStop'), lines: [t(AUTO_WHY[st.autoWhy] || 'locAutoLost'), r.head].concat(r.lines) };
    // recast: the app presses the card's gem button / selects the recast function itself, and says which (the last button stays the player's)
    else if (st.auto === 'work' && st.step === 'selected' && req.purpose !== 'ascend') r = { tone: r.tone, head: t('locAutoGem'), lines: [r.head] };
    else if (st.auto === 'work' && st.step === 'gemFunction' && (st.func === 7 || st.func === 8)) r = { tone: r.tone, head: t(st.func === 8 ? 'locAutoFuncAdv' : 'locAutoFuncRec'), lines: [r.head] };
    else if (st.auto === 'work' && AUTO_STEPS.indexOf(st.step) >= 0) r = { tone: r.tone, head: t('locAutoWork'), lines: [r.head] };
    // the plate over the game: the head and the first lines, short
    r.tip = [r.head].concat(r.lines.slice(0, 2)).map((x) => (x.length > 90 ? x.slice(0, 89) + '…' : x));
    return r;
  }

  // the user-facing step number of a request: {n, total} or null (a step off the main path has no number)
  const PATH = {
    recast: { openInventory: 1, openEquipTab: 2, inList: 3, hiddenFilter: 3, selected: 4, gemFunction: 5, gemPick: 5, done: 6, total: 6 },
    artPromote: { openInventory: 1, artOpenTab: 2, artExitMode: 2, artListLoading: 3, artNotInList: 3, artInList: 3, artSelected: 4, artWrongArtifact: 4, artLevelFirst: 5, artOpenPromote: 5, artMatLoading: 6, artMatGuess: 6, artMatPick: 6, artMatNone: 6, artPromote: 7, total: 7 },
    ascend: { openInventory: 1, openEquipTab: 2, openFoundry: 3, openAscendTab: 4, ascendFilter: 5, ascendLoading: 5, ascendPick: 5, ascendNotInList: 5, ascendReady: 6, total: 6 },
  };
  function stepNo(req, st) {
    if (!req || !st) return null;
    const p = PATH[req.purpose === 'ascend' ? 'ascend' : req.purpose === 'artPromote' ? 'artPromote' : 'recast'], n = p[st.step];
    return n ? { n, total: p.total } : null;
  }

  // the locate plate's own header line: «Показать в игре · <вещь> · шаг N из M» (never the equip helper's «Шаг N из M · слот»)
  function caps(req, st, t) {
    if (!req || (!req.item && !req.art)) return '';
    const name = req.art ? req.art.name || '' : req.item.name || req.item.slotName || '', s = stepNo(req, st);
    return s ? t('locCapsN', name, s.n, s.total) : t('locCaps', name);
  }

  // the card's sub stats against the game's: [{text, ok}] (ok only when the game's reading is there)
  function subRows(req, st, t) {
    if (!req || !req.item) return [];
    return (req.item.subs || []).map((x, i) => ({
      text: [x.name || x.text, x.value].filter(Boolean).join(' '),
      ok: !!(st && st.subs && st.subs[i]),
    }));
  }

  // the host's pills over the game for an item off the screen («Вещь ниже: ещё 3 ряда»): the texts and the row forms in the page's language
  function pillTexts(t, lang) {
    return { lang: lang === 'en' ? 'en' : 'ru', pillUp: t('locPillUp'), pillDown: t('locPillDown'), rows1: t('pRows1'), rows2: t('pRows2'), rows5: t('pRows5'), doneAscend: t('locJobAscend'), doneRecast: t('locJobRecast'), doneArtPromote: t('locJobArtPromote') };
  }

  const api = { KINDS, ART_KINDS, pillTexts, describe, caps, stepNo, subRows, filterLines, AUTO_STEPS };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.RFLocate = api;
})(typeof window !== 'undefined' ? window : globalThis);
