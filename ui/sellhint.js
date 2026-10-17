// Wardsage desktop — storage cleanup without auto click: the text for each step the host reports (src/SellHint.cs).
// Pure logic (tests: tests/sellhint.test.mjs).
//
// st: {step, selected, total, missing, extra, below, above}   (total = list items in the inventory's grid now)
// t(key, …args) is the interface's text lookup.
(function (root) {
  const KINDS = ['openInventory', 'openEquipTab', 'pressBulk', 'needPosition', 'select', 'allSelected', 'noneHere'];

  // -> {tone: 'info'|'warn'|'done', head, lines:[…], tip:[…] (the plate over the game, ≤ 2 short lines), pillUp, pillDown}
  function describe(st, t) {
    if (!st || KINDS.indexOf(st.step) < 0) return { tone: 'info', head: t('shOpenInv'), lines: [], tip: [t('shOpenInv')], pillUp: '', pillDown: '' };
    const count = t('shCount', st.selected || 0, st.total || 0);
    let r;
    switch (st.step) {
      case 'openInventory': r = { tone: 'info', head: t('shOpenInv'), lines: [] }; break;
      case 'openEquipTab': r = { tone: 'info', head: t('shEquipTab'), lines: [] }; break;
      case 'pressBulk': r = { tone: 'info', head: t('shBulk'), lines: [] }; break;
      case 'needPosition': r = { tone: 'info', head: t('shScrollTop'), lines: [count] }; break;
      case 'allSelected': r = { tone: 'done', head: t('shAll'), lines: [count] }; break;
      case 'noneHere': r = { tone: 'warn', head: t('shNone'), lines: [] }; break;
      default: r = st.extra > 0 ? { tone: 'warn', head: t('shExtra'), lines: [count] } : { tone: 'info', head: t('shSelect'), lines: [count] };
    }
    r.tip = [r.head].concat(r.lines.slice(0, 1));
    const sel = st.step === 'select';
    r.pillDown = sel && st.below > 0 ? t('shBelow', st.below) : '';
    r.pillUp = sel && st.above > 0 ? t('shAbove', st.above) : '';
    return r;
  }

  const api = { KINDS, describe };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.RFSellHint = api;
})(typeof window !== 'undefined' ? window : globalThis);
