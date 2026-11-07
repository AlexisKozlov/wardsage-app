// Wardsage desktop - the «Настройки» page: the markup as a pure function of the state (tests: tests/settings.test.mjs).
// html(S, t, esc, I, H): S = the app state, t = text lookup, esc = HTML escape, I = icons,
// H = { dpsOff, codeBtns, recLabel, recNote, DISCORD, shownVer } from app.js
(function (root) {
  function html(S, t, esc, I, H) {
    const { dpsOff, codeBtns, recLabel, recNote, DISCORD, shownVer } = H;
    // a row: title + one short line on the left, the control on the right; the long text hides under «Подробнее»
    const row = (title, sub, ctl, o = {}) => {
      const open = !!(o.more && S.more[o.key]);
      return `<div class="srow${o.dep ? ' dep' : ''}${o.off ? ' off' : ''}${o.wide ? ' wide' : ''}">
        <div class="stx"><b>${esc(title)}</b>${sub ? `<small>${esc(sub)}</small>` : ''}${o.more ? `<button class="btn-ghost more" data-act="more" data-k="${o.key}" aria-expanded="${open}">${esc(t(open ? 'setLess' : 'setMore'))}</button>` : ''}</div>
        <div class="sct">${ctl}</div>${open ? `<p class="sdet">${esc(t(o.more))}</p>` : ''}</div>`;
    };
    const sw = (act, on, dis) => `<button class="switch" role="switch" data-act="${act}" aria-checked="${on}" ${dis ? 'disabled' : ''}></button>`;
    const sec = (title, body) => `<section class="card sec"><h2>${esc(title)}</h2>${body}</section>`;
    const adv = !!S.more.adv;
    // the language sits in the page head; the account section holds the sync code alone, on a full line
    const account = `<section class="card sec acct"><h2>${esc(t('secAccount'))}</h2>${
      row(t('setCode'), t('setCodeP'), S.hasCode
        ? `<span class="num code">${esc(S.codePrefix)}…</span><button class="btn-line sm" data-act="editCode">${esc(t('change'))}</button><button class="btn-ghost" data-act="unlink">${esc(t('unlink'))}</button>`
        : codeBtns())}</section>`;
    // TEST build only: the screen recorder for the developers (host: rec.start / rec.stop, event 'rec')
    const rec = S.channel !== 'test' ? '' : sec(t('secRec'), row(t('setRec'), t('setRecP'), `<button class="btn-line" id="recBtn" data-act="rec">${esc(recLabel())}</button>`, { wide: true })
      + row(t('setRecBtn'), t('setRecBtnP'), sw('recButton', S.recButton))
      + (S.rec.sent || S.rec.total || S.rec.why ? `<p class="sdet" id="recInfo">${esc(recNote())}</p>` : ''));
    const left = [
      sec(t('secSync'),
        row(t('setAutoSync'), t('setAutoSyncS'), sw('autoSync', S.autoSync), { more: 'setAutoSyncP', key: 'autoSync' })
        + row(t('setCopy'), t('setCopyP'), sw('copy', S.saveCopy))),
      rec].join('');
    const right = [
      sec(t('secApp'),
        row(t('setAutoUpdate'), t('setAutoUpdateS'), sw('autoUpdate', S.autoUpdate), { more: 'setAutoUpdateP', key: 'autoUpdate' })
        + row(t('setSendCodes'), t('setSendCodesS'), sw('sendCodes', S.sendCodes), { more: 'setSendCodesP', key: 'sendCodes' })
        + row(t('setBossHints'), t('setBossHintsS'), sw('bossHints', S.bossHints), { more: 'setBossHintsP', key: 'bossHints' })
        + row(t('setDps'), dpsOff() ? t('setDpsOff') : t('setDpsS'), sw('dps', S.dps && !dpsOff(), dpsOff()), { more: 'setDpsP', key: 'dps' })
        + row(t('setShowInCapture'), t(S.showInCapture ? 'setShowInCaptureOn' : 'setShowInCaptureS'), sw('showInCapture', S.showInCapture))
        + row(t('setTray'), t('setTrayS'), sw('tray', S.tray), { more: 'setTrayP', key: 'tray' })
        + row(t('setAutostart'), t('setAutostartS'), sw('autostart', S.autostart), { more: 'setAutostartP', key: 'autostart' })),
      sec(t('secHelp'),
        row(t('setHelp'), t('setHelpS'), `<button class="btn-line" data-act="open" data-url="${DISCORD}">${I.ext}${esc(t('openDiscord'))}</button>`, { more: 'setHelpP', key: 'help' })
        + row(t('setReports'), t('setReportsS'), sw('errorReports', S.errorReports), { more: 'setReportsP', key: 'reports' })
        + row(t('setLog'), t('setLogP'), `<button class="btn-line" data-act="openLog">${esc(t('openLog'))}</button>`)),
      `<section class="card sec adv"><h2><button class="advbtn" data-act="more" data-k="adv" aria-expanded="${adv}">${esc(t('secAdv'))}</button></h2>${adv ?
        row(t('setSite'), t('setSiteP'), `<div class="input"><input id="site" value="${esc(S.site)}" spellcheck="false"></div>
          <button class="btn-line" data-act="saveSite">${esc(t('save'))}</button>${S.site !== S.defaultSite ? `<button class="btn-ghost" data-act="resetSite">${esc(t('reset'))}</button>` : ''}`, { wide: true })
        + row(t('setDiag'), t('setDiagP'), `<button class="btn-line" data-act="diag">${esc(t('diagStart'))}</button>`) : ''}</section>`].join('');
    return `
      <div class="head"><div><h1>${esc(t('setTitle'))}</h1></div><div class="setlang"><span>${esc(t('setLang'))}</span><div class="langs"><button data-act="lang" data-lang="ru" aria-pressed="${S.lang === 'ru'}">РУССКИЙ</button><button data-act="lang" data-lang="en" aria-pressed="${S.lang === 'en'}">ENGLISH</button></div></div></div>
      ${account}
      <div class="setcols"><div class="setcol">${left}</div><div class="setcol">${right}</div></div>
      <p class="setfoot">${esc(t('aboutP', shownVer(S.version)))} · <button class="btn-ghost" data-act="open" data-url="https://github.com/AlexisKozlov/wardsage-app">${esc(t('source'))}</button></p>`;
  }

  const api = { html };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.RFSettings = api;
})(typeof window !== 'undefined' ? window : globalThis);
