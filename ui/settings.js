// Wardsage desktop - the «Настройки» page: one page of game-style section cards with an anchor column on the left.
// html(S, t, esc, I, H) is a pure function of the state (tests: tests/settings.test.mjs).
//   S = the app state, t = text lookup, esc = HTML escape, I = icons,
//   H = { dpsOff, remoteSwitchNote, recLabel, recNote, DISCORD, shownVer } from app.js
// Nothing that changes while scrolling (the active anchor) or by a timer is put into the markup: app.js sets it with classList.
(function (root) {
  // the sections in page order: id (anchor #sec-<id>), icon from the set I, title text key; test: shown in the TEST build only
  const SECTIONS = [
    { id: 'acct', ico: 'key', title: 'secAccount' },
    { id: 'sync', ico: 'sync', title: 'secSync' },
    { id: 'over', ico: 'game', title: 'secOver' },
    { id: 'app', ico: 'gear', title: 'secApp' },
    { id: 'help', ico: 'chat', title: 'secHelp' },
    { id: 'adv', ico: 'filter', title: 'secAdv' },
    { id: 'dev', ico: 'pin', title: 'secDev', test: true },
  ];
  const visible = (S) => SECTIONS.filter((s) => !s.test || S.channel === 'test');

  function html(S, t, esc, I, H) {
    const { dpsOff, remoteSwitchNote, recLabel, recNote, DISCORD, shownVer } = H;
    // a row: title + one short line on the left, the control on the right; the long text hides under «Как это работает»
    const row = (title, sub, ctl, o = {}) => {
      const open = !!(o.more && S.more[o.key]);
      return `<div class="srow${o.off ? ' off' : ''}">
        <div class="stx"><b${o.id ? ` id="l-${o.id}"` : ''}>${esc(title)}</b>${o.badge ? `<span class="lockb">${I.lock}${esc(o.badge)}</span>` : ''}${sub ? `<small>${esc(sub)}</small>` : ''}${o.more ? `<button class="more" data-act="more" data-k="${o.key}" aria-expanded="${open}" aria-controls="more-${o.key}">${esc(t('setMore'))}</button>` : ''}</div>
        <div class="sct">${ctl}</div>${open ? `<p class="sdet" id="more-${o.key}">${esc(t(o.more))}</p>` : ''}</div>`;
    };
    const sw = (act, on, dis) => `<button class="switch" role="switch" data-act="${act}" aria-checked="${on}"${dis ? ' disabled' : ''} aria-labelledby="l-${act}"></button><span class="swl" aria-hidden="true">${esc(t(on ? 'setOn' : 'setOff'))}</span>`;
    const sec = (def, body, o = {}) => `<section class="card framed sec${o.cls ? ' ' + o.cls : ''}" id="sec-${def.id}">${o.head || head(def)}${body}</section>`;
    const head = (def) => `<h2 class="sh"><span class="medal">${I[def.ico]}</span><span class="shx">${esc(t(def.title))}</span>${def.test ? `<span class="tbadge">${esc(t('setTestBadge'))}</span>` : ''}</h2>`;
    const def = (id) => SECTIONS.find((s) => s.id === id);
    const wait = () => (S.remote && S.remote.readEnabled === false ? `<div class="remote-note wait" role="status">${I.clock}<div><b>${esc(t('setWaitRead'))}</b></div></div>` : '');

    // 1. account: the sync code on a full line
    const account = sec(def('acct'), S.hasCode
      ? `<div class="acct-code"><span class="num code big">${esc(S.codePrefix)}…</span><div class="sct"><button class="btn-line sm" data-act="editCode">${esc(t('change'))}</button>
          <button class="quiet" data-act="unlink"${S.unlinkAsk ? ' data-ask="1"' : ''}>${esc(t(S.unlinkAsk ? 'setUnlinkSure' : 'unlink'))}</button></div></div><p class="acct-sub">${esc(t('setCodeP'))}</p>`
      : `<p class="acct-sub none">${esc(t('setCodeNone'))}</p><div class="acct-btns"><button class="btn-gold" data-act="editCode">${esc(t('enterCode'))}</button>
          <button class="btn-line sm" data-act="open" data-url="${esc(S.site + '/app/settings')}">${I.ext}${esc(t('getCode'))}</button></div>`, { cls: 'acct' });

    const sync = sec(def('sync'), wait()
      + row(t('setAutoSync'), t('setAutoSyncS'), sw('autoSync', S.autoSync), { id: 'autoSync', more: 'setAutoSyncP', key: 'autoSync' })
      + row(t('setSendCodes'), t('setSendCodesS'), sw('sendCodes', S.sendCodes), { id: 'sendCodes', more: 'setSendCodesP', key: 'sendCodes' })
      + row(t('setCopy'), t('setCopyP'), sw('copy', S.saveCopy), { id: 'copy' }));

    const off = dpsOff();
    const over = sec(def('over'), wait()
      + row(t('setBossHints'), t('setBossHintsS'), sw('bossHints', S.bossHints), { id: 'bossHints', more: 'setBossHintsP', key: 'bossHints' })
      + row(t('setDps'), off ? t('setDpsOffP') : t('setDpsS'), sw('dps', S.dps && !off, off), { id: 'dps', more: off ? '' : 'setDpsP', key: 'dps', off, badge: off ? t('setDpsOff') : '' })
      + row(t('setShowInCapture'), t(S.showInCapture ? 'setShowInCaptureOn' : 'setShowInCaptureS'), sw('showInCapture', S.showInCapture), { id: 'showInCapture' })
      + `<p class="sechint">${esc(t('setCtrl'))}</p>`);

    const app = sec(def('app'),
      row(t('setLangRow'), '', `<div class="langs"><button data-act="lang" data-lang="ru" aria-pressed="${S.lang === 'ru'}">РУССКИЙ</button><button data-act="lang" data-lang="en" aria-pressed="${S.lang === 'en'}">ENGLISH</button></div>`)
      + row(t('setAutoUpdate'), t('setAutoUpdateS'), sw('autoUpdate', S.autoUpdate), { id: 'autoUpdate', more: 'setAutoUpdateP', key: 'autoUpdate' })
      + row(t('setTray'), t('setTrayS'), sw('tray', S.tray), { id: 'tray', more: 'setTrayP', key: 'tray' })
      + row(t('setAutostart'), t('setAutostartS'), sw('autostart', S.autostart), { id: 'autostart', more: 'setAutostartP', key: 'autostart' }));

    const help = sec(def('help'),
      row(t('setHelp'), t('setHelpS'), `<button class="btn-line" data-act="open" data-url="${DISCORD}">${I.ext}${esc(t('openDiscord'))}</button>`)
      + row(t('setReports'), t('setReportsS'), sw('errorReports', S.errorReports), { id: 'errorReports', more: 'setReportsP', key: 'reports' })
      + row(t('setLog'), t('setLogP'), `<button class="btn-line" data-act="openLog">${esc(t('openLog'))}</button>`));

    // «Дополнительно» is folded: the header is the button
    const adv = !!S.more.adv;
    const advHead = `<h2 class="sh"><button class="advbtn" data-act="more" data-k="adv" aria-expanded="${adv}" aria-controls="adv-body"><span class="medal">${I[def('adv').ico]}</span><span class="shx">${esc(t('secAdv'))}</span></button></h2>`;
    const siteBad = !/^https:\/\//i.test(S.site || '');
    const advBody = adv ? `<div id="adv-body">${
      row(t('setSite'), t('setSiteP'), `<div class="input"><input id="site" value="${esc(S.site)}" spellcheck="false" aria-labelledby="l-site"></div>
          <button class="btn-line" data-act="saveSite">${esc(t('save'))}</button>${S.site !== S.defaultSite ? `<button class="btn-line sm" data-act="resetSite">${esc(t('reset'))}</button>` : ''}`, { id: 'site' }).replace('<div class="srow', '<div class="srow wide')
      + (siteBad ? `<p class="hint warnhint">${esc(t('setSiteHint'))}</p>` : '')
      + row(t('setDiag'), t('setDiagP'), `<button class="btn-line" data-act="diag">${esc(t('diagStart'))}</button>`)}</div>` : '';
    const advanced = sec(def('adv'), advBody, { head: advHead, cls: 'adv' + (adv ? '' : ' shut') });

    // TEST build only: the screen recorder for the developers (host: rec.start / rec.stop, event 'rec')
    const dev = S.channel !== 'test' ? '' : sec(def('dev'),
      row(t('setRec'), t('setRecP'), `<button class="btn-line" id="recBtn" data-act="rec">${esc(recLabel())}</button>`)
      + row(t('setRecBtn'), t('setRecBtnP'), sw('recButton', S.recButton), { id: 'recButton' })
      + (S.rec.sent || S.rec.total || S.rec.why ? `<p class="sdet" id="recInfo">${esc(recNote())}</p>` : ''));

    const nav = `<nav class="setnav" id="setnav" aria-label="${esc(t('setNav'))}">${visible(S).map((d) =>
      `<button data-act="setGo" data-sec="${d.id}">${esc(t(d.title))}</button>`).join('')}</nav>`;
    return `
      <div class="head"><div><h1>${esc(t('setTitle'))}</h1></div></div>
      ${remoteSwitchNote(S.remote)}
      <div class="setroot"><div class="setgrid">${nav}<div class="setsecs">${account}${sync}${over}${app}${help}${advanced}${dev}</div></div></div>
      <p class="setfoot">${esc(t('aboutP', shownVer(S.version)))} · <button class="btn-ghost" data-act="open" data-url="https://github.com/AlexisKozlov/wardsage-app">${esc(t('source'))}</button></p>`;
  }

  const api = { html, SECTIONS, visible };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.RFSettings = api;
})(typeof window !== 'undefined' ? window : globalThis);
