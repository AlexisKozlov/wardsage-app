// RealmForge desktop — interface. Talks to the host (RealmForge.exe) through WebView2 messages:
//   UI → host: {cmd, ...}      host → UI: {ev, ...}      (protocol: see HostBridge.cs)
(function () {
  'use strict';
  const T = window.RF_TEXTS;
  const G = window.RFGuide;
  const host = window.chrome && window.chrome.webview
    ? { send: (m) => window.chrome.webview.postMessage(m), on: (f) => window.chrome.webview.addEventListener('message', (e) => f(e.data)) }
    : window.RFMock;

  const CODE_RE = /^rf_[0-9A-Za-z]{32}$/;
  const S = {
    ready: false, lang: 'ru', version: '', site: '', defaultSite: '', hasCode: false, codePrefix: '', saveCopy: false,
    game: { running: false, version: null }, last: null, page: 'sync', editCode: false,
    sync: { phase: 'idle', stage: null, seconds: 0, result: null, error: null },
    plans: { status: 'idle', list: [], err: null }, sel: 0,
    scan: { status: 'idle', panel: false }, live: null, reported: {}, compact: false, overlay: 'off', hl: '',
  };

  // ---------------------------------------------------------------- helpers
  const t = (k, ...a) => {
    let s = (T[S.lang] && T[S.lang][k]) || T.ru[k] || k;
    a.forEach((v, i) => { s = s.split('{' + i + '}').join(v); });
    return s;
  };
  const esc = (v) => String(v == null ? '' : v).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  const fmt = (n) => (n == null ? '—' : Number(n).toLocaleString(S.lang === 'en' ? 'en-US' : 'ru-RU'));
  const when = (iso) => {
    if (!iso) return '';
    const d = new Date(iso); if (isNaN(d)) return '';
    return d.toLocaleString(S.lang === 'en' ? 'en-GB' : 'ru-RU', { day: '2-digit', month: '2-digit', hour: '2-digit', minute: '2-digit' });
  };
  const $ = (sel) => document.querySelector(sel);

  const I = {
    sync: '<svg viewBox="0 0 24 24"><path d="M20 12a8 8 0 0 1-14.3 4.9M4 12a8 8 0 0 1 14.3-4.9"/><path d="M18.5 3v4.2h-4.2M5.5 21v-4.2h4.2"/></svg>',
    equip: '<svg viewBox="0 0 24 24"><path d="M12 3 5 6v5c0 4.4 3 8.3 7 10 4-1.7 7-5.6 7-10V6z"/><path d="m9 12 2 2 4-4"/></svg>',
    gear: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="3"/><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1z"/></svg>',
    arrow: '<svg viewBox="0 0 24 24"><path d="M4 12h15m-5-5 5 5-5 5"/></svg>',
    check: '<svg viewBox="0 0 24 24"><path d="m5 12.5 4.5 4.5L19 7.5"/></svg>',
    x: '<svg viewBox="0 0 24 24"><path d="M6 6l12 12M18 6 6 18"/></svg>',
    game: '<svg viewBox="0 0 24 24"><rect x="3" y="7" width="18" height="11" rx="3"/><path d="M8 11v3M6.5 12.5h3M15 12h.01M17.5 13.5h.01"/></svg>',
    key: '<svg viewBox="0 0 24 24"><circle cx="8" cy="15" r="4"/><path d="m11 12 9-9M17 6l2 2M15 8l2 2"/></svg>',
    clock: '<svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/></svg>',
    ext: '<svg viewBox="0 0 24 24"><path d="M14 4h6v6M20 4l-9 9M18 14v5a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V7a1 1 0 0 1 1-1h5"/></svg>',
    warn: '<svg viewBox="0 0 24 24"><path d="M12 3 2 20h20z"/><path d="M12 10v4M12 17h.01"/></svg>',
    lock: '<svg viewBox="0 0 24 24"><rect x="5" y="11" width="14" height="9" rx="1.5"/><path d="M8 11V8a4 4 0 0 1 8 0v3"/></svg>',
    hand: '<svg viewBox="0 0 24 24"><path d="M9 11V5a1.5 1.5 0 0 1 3 0v5M12 10V4a1.5 1.5 0 0 1 3 0v6M15 10V6a1.5 1.5 0 0 1 3 0v8a6 6 0 0 1-6 6h-1a6 6 0 0 1-4.7-2.3L3.6 14a1.5 1.5 0 0 1 2.3-1.9L9 15"/></svg>',
    pin: '<svg viewBox="0 0 24 24"><path d="M9 3h6l-1 6 4 4H6l4-4zM12 13v8"/></svg>',
    expand: '<svg viewBox="0 0 24 24"><path d="M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5"/></svg>',
    reload: '<svg viewBox="0 0 24 24"><path d="M20 11a8 8 0 1 0-2.3 5.7M20 5v6h-6"/></svg>',
    chest: '<svg viewBox="0 0 64 64"><path d="M10 26h44v26H10zM10 26l6-12h32l6 12M28 34h8v8h-8zM10 36h18M36 36h18"/></svg>',
    filter: '<svg viewBox="0 0 24 24"><path d="M4 5h16l-6 7.5V19l-4 1v-7.5z"/></svg>',
    folder: '<svg viewBox="0 0 24 24"><path d="M3 7a1 1 0 0 1 1-1h5l2 2h9a1 1 0 0 1 1 1v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1z"/></svg>',
  };

  let toastTimer;
  function toast(msg) {
    const el = $('#toast'); el.textContent = msg; el.classList.add('show');
    clearTimeout(toastTimer); toastTimer = setTimeout(() => el.classList.remove('show'), 1800);
  }

  // ---------------------------------------------------------------- shell
  let lastRail = '', lastPage = '', lastView = '';
  // Re-renders only what changed (the game is polled several times a second): no flicker, hover and focus survive.
  function render() {
    syncHighlight();
    document.documentElement.lang = S.lang;
    document.body.classList.toggle('compact', S.compact);
    const r = rail();
    if (r !== lastRail) { $('#rail').innerHTML = r; lastRail = r; }
    const view = S.compact ? 'compact' : S.page + (S.editCode || !S.hasCode ? ':code' : '');
    const html = S.compact ? compactView() : S.page === 'equip' ? equipPage() : S.page === 'settings' ? settingsPage() : syncPage();
    if (html === lastPage && view === lastView) return;
    const pg = $('#page');
    const keep = document.activeElement && document.activeElement.id === 'code' ? pg.querySelector('#code').value : null;
    pg.innerHTML = html;
    if (keep != null && pg.querySelector('#code')) { pg.querySelector('#code').value = keep; pg.querySelector('#code').focus(); codeTyped(); }
    if (view !== lastView) { pg.className = ''; void pg.offsetWidth; pg.className = 'page'; }
    lastPage = html; lastView = view;
  }

  function rail() {
    const pending = S.plans.list.length;
    const nav = (id, icon, label, extra = '') =>
      `<button class="nav" data-act="nav" data-page="${id}" ${S.page === id ? 'aria-current="page"' : ''}>${icon}<span>${esc(label)}</span>${extra}</button>`;
    return `
      <div class="brand"><div class="crest">R</div><div><b>REALMFORGE</b><small>${esc(t('tagline'))}</small></div></div>
      ${nav('sync', I.sync, t('navSync'))}
      ${nav('equip', I.equip, t('navEquip'), pending ? `<span class="badge">${pending}</span>` : '')}
      ${nav('settings', I.gear, t('navSettings'))}
      <div class="rail-foot">
        <div class="game-state"><span class="dot ${S.game.running ? 'on' : ''}"></span><div>${esc(S.game.running ? t('gameOn') : t('gameOff'))}
          ${S.game.running && S.game.version ? `<small>${esc(t('gameVer', S.game.version))}</small>` : ''}</div></div>
        <div class="langs">
          <button data-act="lang" data-lang="ru" aria-pressed="${S.lang === 'ru'}">RU</button>
          <button data-act="lang" data-lang="en" aria-pressed="${S.lang === 'en'}">EN</button>
        </div>
      </div>`;
  }

  // ---------------------------------------------------------------- sync
  function syncPage() {
    if (!S.hasCode || S.editCode) return onboarding();
    const sy = S.sync;
    const running = sy.phase === 'run';
    const last = S.last;
    const tops = (last && last.top && last.top.length ? last.top : []).slice(0, 3);
    const tile = (ok, icon, title, sub, extra = '') =>
      `<div class="card tile ${ok === true ? 'ok' : ok === false ? 'bad' : ''}"><span class="ico">${icon}</span><div><b>${esc(title)}</b><span>${esc(sub)}</span>${extra}</div></div>`;
    return `
      <div class="head"><div><h1>${esc(t('syncTitle'))}</h1><p class="lead">${esc(t('syncLead'))}</p></div></div>
      <div class="card framed banner ${tops.length ? 'has-collage' : ''}">
        ${tops.length ? `<div class="collage">${tops.map((id) => `<img src="${esc(S.site)}/art/heroes/HeroBust_${Number(id)}.webp" alt="" onerror="this.remove()">`).join('')}</div>` : ''}
        <div class="inner">
          <button class="btn-gold big" data-act="sync" ${running ? 'disabled' : ''}>${I.sync}<span>${esc(running ? t('btnBusy') : t('btnSync'))}</span></button>
          ${steps()}
          ${sy.phase === 'run' ? `<div class="bar"><i style="width:${progress()}%"></i></div><div class="status">${esc(sy.stage === 'read' ? t('readHint', sy.seconds) : sy.stage === 'send' ? t('sendHint') : '')}</div>` : ''}
          ${sy.phase === 'done' ? result() : ''}
          ${sy.phase === 'error' ? errorBox() : ''}
          <div class="readonly" style="margin:0">${I.lock}<span>${esc(t('readonly'))}</span></div>
        </div>
      </div>
      <div class="tiles">
        ${tile(S.game.running, S.game.running ? I.check : I.game, t('chkGame'), S.game.running ? (S.game.version ? t('chkGameOn', S.game.version) : t('chkGameOnNoVer')) : t('chkGameOff'))}
        ${tile(true, I.key, t('chkCode'), t('chkCodeOn', S.codePrefix + '…'), `<button class="btn-ghost" data-act="editCode">${esc(t('change'))}</button>`)}
        ${tile(last ? true : null, I.clock, t('chkLast'), last ? t('chkLastVal', when(last.at), fmt(last.heroes), fmt(last.items)) : t('chkLastNever'))}
      </div>`;
  }

  function progress() {
    const sy = S.sync;
    if (sy.stage === 'find') return 6;
    if (sy.stage === 'read') return Math.min(84, 8 + (sy.seconds / 42) * 76);
    if (sy.stage === 'send' || sy.stage === 'save') return 92;
    return 100;
  }

  function steps() {
    const order = ['find', 'read', 'send', 'done'];
    const sy = S.sync;
    const cur = sy.phase === 'done' ? 4 : sy.phase === 'idle' ? -1 : order.indexOf(sy.stage === 'save' ? 'send' : sy.stage);
    const labels = [t('stFind'), t('stRead'), t('stSend'), t('stDone')];
    return `<ol class="steps">${labels.map((l, i) => {
      let c = '';
      if (sy.phase === 'error' && i === cur) c = 'fail';
      else if (i < cur || (sy.phase === 'done')) c = 'done';
      else if (i === cur) c = 'now';
      return `<li class="${c}">${esc(l)}</li>`;
    }).join('')}</ol>`;
  }

  function result() {
    const r = S.sync.result || {};
    return `<div class="result">
      <div class="title">${I.check}<span>${esc(r.saved ? t('resSaved', r.saved) : t('resTitle', r.seconds || 0))}</span></div>
      <div class="stats">
        <div class="stat"><b class="num">${fmt(r.heroes)}</b><span>${esc(t('heroes'))}</span></div>
        <div class="stat"><b class="num">${fmt(r.items)}</b><span>${esc(t('items'))}</span></div>
        <div class="stat"><b class="num">${fmt(r.artifacts)}</b><span>${esc(t('artifacts'))}</span></div>
      </div>
      <div class="row">
        ${r.viewUrl ? `<button class="btn-line" data-act="open" data-url="${esc(r.viewUrl)}">${I.ext}${esc(t('openSite'))}</button>` : ''}
        ${r.saved ? `<button class="btn-line" data-act="openFolder">${I.folder}${esc(t('openFolder'))}</button>` : ''}
      </div></div>`;
  }

  function errorBox() {
    const e = S.sync.error || {};
    const map = {
      not_running: ['errNotRunning', 'errNotRunningP'], access: ['errAccess', 'errAccessP'], read: ['errRead', 'errReadP'],
      token: ['errToken', 'errTokenP'], rate: ['errRate', 'errRateP'], net: ['errNet', 'errNetP'], server: ['errServer', 'errServerP'],
      payload: ['errPayload', 'errPayloadP'],
    };
    const [h, p] = map[e.kind] || ['errRead', 'errReadP'];
    const arg = e.kind === 'rate' ? (e.retryAfter || 30) : e.detail || '';
    return `<div class="alert">${I.warn}<div><b>${esc(t(h))}</b><p>${esc(t(p, arg))}</p><div class="row">
      ${e.kind === 'token' ? `<button class="btn-line" data-act="open" data-url="${esc(S.site + '/app/settings')}">${I.ext}${esc(t('newCode'))}</button>` : ''}
      <button class="btn-line" data-act="openLog">${esc(t('showLog'))}</button></div></div></div>`;
  }

  function onboarding() {
    return `
      <div class="head"><div><h1>${esc(t('obTitle'))}</h1><p class="lead">${esc(t('obLead'))}</p></div></div>
      <div class="card framed">
        <ol class="onboard">
          <li><b>${esc(t('ob1'))}</b>${esc(t('ob1p'))}</li><li><b>${esc(t('ob2'))}</b>${esc(t('ob2p'))}</li><li><b>${esc(t('ob3'))}</b>${esc(t('ob3p'))}</li>
        </ol>
        <div class="row" style="margin-bottom:20px"><button class="btn-line" data-act="open" data-url="${esc(S.site + '/app/settings')}">${I.ext}${esc(t('openSettings'))}</button></div>
        <div class="field">
          <label for="code">${esc(t('codeLabel'))}</label>
          <div class="input"><input id="code" spellcheck="false" autocomplete="off" placeholder="rf_…" maxlength="200"><button data-act="paste">${esc(t('paste'))}</button></div>
          <div class="hint" id="codeHint">&nbsp;</div>
        </div>
        <div class="row" style="margin-top:14px">
          <button class="btn-gold" data-act="saveCode" id="saveCode" disabled>${esc(t('save'))}</button>
          ${S.hasCode ? `<button class="btn-ghost" data-act="cancelCode">${S.lang === 'en' ? 'Cancel' : 'Отмена'}</button>` : `<button class="btn-ghost" data-act="saveOnly">${esc(t('orSave'))}</button>`}
        </div>
        <div class="readonly">${I.lock}<span>${esc(t('readonly'))}</span></div>
      </div>`;
  }

  function codeTyped() {
    const inp = $('#code'); if (!inp) return;
    const m = inp.value.match(/rf_[0-9A-Za-z]{32}/);
    if (m && m[0] !== inp.value) inp.value = m[0];
    const v = inp.value.trim(), ok = CODE_RE.test(v);
    const h = $('#codeHint');
    h.className = 'hint ' + (v ? (ok ? 'ok' : 'err') : '');
    h.textContent = v ? (ok ? '✓ ' + t('codeOk') : t('codeBad', v.length)) : ' ';
    $('#saveCode').disabled = !ok;
  }

  // ---------------------------------------------------------------- equip
  function equipPage() {
    const P = S.plans;
    let body;
    if (!S.hasCode) body = emptyBox(t('needCode'), '', `<button class="btn-line" data-act="nav" data-page="sync">${esc(t('navSync'))}</button>`);
    else if (P.status === 'loading' && !P.list.length) body = `<div class="card"><div class="scan"><span class="spinner"></span>${esc(t('plansLoading'))}</div></div>`;
    else if (P.status === 'error') body = emptyBox(t('plansErr'), P.err === 'token' ? t('plansErrToken') : P.err || '', `<button class="btn-line" data-act="reload">${I.reload}${esc(t('reload'))}</button>`);
    else if (!P.list.length) body = emptyBox(t('plansNone'), t('plansNoneP'), `<button class="btn-line" data-act="open" data-url="${esc(S.site + '/app/optimizer')}">${I.ext}${esc(t('openOptimizer'))}</button>`);
    else body = `<div class="stack"><div class="plans">${P.list.map(planCard).join('')}</div>${guideCard()}</div>`;
    return `
      <div class="head"><div><h1>${esc(t('equipTitle'))}</h1><p class="lead">${esc(t('equipLead'))}</p></div>
        <div class="row">
          ${P.list.length ? `<button class="btn-line" data-act="compact">${I.pin}${esc(t('overlay'))}</button>` : ''}
          ${S.hasCode ? `<button class="btn-line" data-act="reload" ${P.status === 'loading' ? 'disabled' : ''}>${I.reload}${esc(t('reload'))}</button>` : ''}
        </div></div>
      ${body}`;
  }

  function emptyBox(title, text, actions) {
    return `<div class="card empty">${I.chest}<b>${esc(title)}</b><p>${esc(text)}</p><div class="row" style="justify-content:center">${actions}</div></div>`;
  }

  // hero portrait: the game's HeroHead card art, the site bust if it is missing
  function bust(p, cls = '') {
    const head = G.headUrl(S.site, p.heroUid), b = G.bustUrl(S.site, p.heroUid);
    return `<div class="bust ${cls}">${head ? `<img src="${esc(head)}" data-alt="${esc(b)}" alt="" onerror="heroImgFail(this)">` : ''}</div>`;
  }
  window.heroImgFail = (img) => { const a = img.getAttribute('data-alt'); if (a) { img.removeAttribute('data-alt'); img.className = 'bust-img'; img.src = a; } else img.remove(); };

  // item cell like in the game: rarity background by stars, icon, +level, stars
  function cell(icon, stars, level, slot, cls = '', setIcon = '', card = '') {
    const url = G.itemUrl(S.site, icon), r = G.rankOf(stars), su = G.setUrl(S.site, setIcon);
    return `<span class="cell ${r ? 'r' + r : 'r0'} ${cls}"${card ? ` data-card="${esc(card)}"` : ''}>${url ? `<img src="${esc(url)}" alt="" onerror="this.src='img/slot${slot | 0}.webp';this.className='ph'">` : `<img class="ph" src="img/slot${slot | 0}.webp" alt="">`}`
      + `${su ? `<img class="set" src="${esc(su)}" alt="" onerror="this.remove()">` : ''}`
      + `${level ? `<i class="lv num">+${level}</i>` : ''}</span>`;
  }

  function planCard(p, i) {
    const g = G.next(p, S.live);
    const pct = Math.round((g.done / p.items.length) * 100);
    return `<button class="plan" data-act="pick" data-i="${i}" aria-pressed="${i === S.sel}">${bust(p)}
      <div><b>${esc(p.heroName)}</b><span>${esc(t('itemsOn', g.done, p.items.length))}</span><div class="mini-bar"><i style="width:${pct}%"></i></div></div></button>`;
  }

  function current() { return S.plans.list[S.sel] || null; }

  // one row per slot: what the hero wears now → what to put on
  function slotList(p, g) {
    return `<ul class="slots">${p.items.map((it) => {
      const st = g.states[it.uid] || 'wait';
      const from = st !== 'done' && it.fromHeroUid > 0 && it.fromHeroUid !== p.heroUid && it.fromHeroName ? t('fromHero', it.fromHeroName) : '';
      const known = it.cur !== undefined, cur = it.cur && it.cur.uid !== it.uid ? it.cur : null;
      const was = st === 'done' ? '' : cur ? cell(cur.icon, cur.stars, cur.level, it.slot, 'old', '', it.uid + ':cur') : cell('', 0, 0, it.slot, 'old none' + (known ? '' : ' unknown'));
      const curText = st === 'done' ? t('eqWorn') : cur ? t('eqNow', cur.name + (cur.level ? ' +' + cur.level : '')) : known ? t('eqEmpty') : '';
      return `<li class="${st}"><span class="st">${I.check}</span>
        <span class="swap">${was}${st === 'done' ? '' : `<span class="arrow">${I.arrow}</span>`}${cell(it.icon, it.stars, it.level, it.slot, 'new', it.setIcon, it.uid + ':new')}</span>
        <span class="info"><span class="slot">${esc(it.slotName || t('slot' + it.slot))}</span>
          <span class="name">${esc(it.name)}${it.setName ? `<em> · ${esc(it.setName)}</em>` : ''}</span>
          <span class="sub">${esc([it.mainStat, from || curText].filter(Boolean).join(' · '))}</span></span></li>`;
    }).join('')}</ul>`;
  }

  // «what to look for»: the cell as it looks in the game, its set and main stat with the game's icons, where it is
  function findCard(it, g, compact) {
    const su = G.setUrl(S.site, it.setIcon), sid = G.statId(it.mainStat), stu = G.statUrl(S.site, sid);
    const pos = g.row > 0 ? `<span class="fpos"><span class="mm">${[1, 2, 3].map((c) => `<i class="${c === (g.col || 0) ? 'on' : ''}"></i>`).join('')}</span>
        <span>${S.lang === 'en' ? `row <b class="num">${g.row}</b>${g.col ? `, #<b class="num">${g.col}</b> from the left` : ''}` : `ряд <b class="num">${g.row}</b>${g.col ? `, <b class="num">${g.col}</b>-й слева` : ''}`}</span></span>` : '';
    return `<div class="find${compact ? ' sm' : ''}">
      <span class="find-cell">${cell(it.icon, it.stars, it.level, it.slot, 'big', it.setIcon, it.uid + ':new')}</span>
      <div class="find-info">
        <b class="find-name">${esc(it.name)}</b>
        <div class="find-tags">
          ${it.setName ? `<span class="ftag">${su ? `<img src="${esc(su)}" alt="" onerror="this.remove()">` : ''}${esc(it.setName)}</span>` : ''}
          ${it.mainStat ? `<span class="ftag stat">${stu ? `<img src="${esc(stu)}" alt="" onerror="this.remove()">` : ''}${esc(it.mainStat)}</span>` : ''}
          ${it.level ? `<span class="ftag num">+${it.level}</span>` : ''}
        </div>
        ${pos}
      </div></div>`;
  }

  // the game's filter as three steps with the same icons as in the game
  function filterSteps(it) {
    const su = G.setUrl(S.site, it.setIcon), stat = G.statOf(it.mainStat), stu = G.statUrl(S.site, G.statId(it.mainStat));
    const steps = [`<span class="fk">${I.filter}${esc(t('fStep1'))}</span>`];
    if (it.setName) steps.push(`${esc(t('fStep2'))} <b>${su ? `<img src="${esc(su)}" alt="">` : ''}${esc(it.setName)}</b>`);
    if (stat) steps.push(`${esc(t('fStep3'))} <b>${stu ? `<img src="${esc(stu)}" alt="">` : ''}${esc(stat)}</b>`);
    return `<ol class="fsteps">${steps.map((x) => `<li>${x}</li>`).join('')}</ol>`;
  }

  function say(p, g) {
    if (S.scan.status === 'scanning') return sayBox('', `<span class="spinner"></span>`, t('scanning'), t('scanningP'));
    if (S.scan.status === 'fail') return sayBox('warn', I.warn, t('scanFail'), t('scanFailP'), rescanBtn());
    if (S.scan.status === 'ok' && !S.scan.panel && g.kind !== 'done' && g.kind !== 'hero') return sayBox('', I.hand, t('noPanel'), '', rescanBtn());
    const it = g.item;
    const itemText = it ? `${it.name}${it.level ? ' +' + it.level : ''}${it.mainStat ? ' · ' + it.mainStat : ''}` : '';
    switch (g.kind) {
      case 'closed': return sayBox('warn', I.game, t('gClosed'), t('gClosedP'));
      case 'done': return sayBox('done', I.check, t('gDone'), t('gDoneP'));
      case 'hero': return sayBox('', I.hand, t('gOpenHero', p.heroName), '') + findCard(it, g, S.compact);
      case 'slot': return sayBox('', I.hand, t('gOpenSlot', it.slotName || t('slot' + it.slot)), '') + findCard(it, g, S.compact);
      case 'pick': {
        // what to do right now, by what the frame over the game is doing
        const head = { on: t('fOn'), below: t('fBelow'), above: t('fAbove'), need_click: t('fNeedClick') }[S.overlay] || t('fLook');
        return sayBox('', I.hand, head, '') + findCard(it, g, S.compact);
      }
      case 'filter':
        return `<div class="say"><span class="ring">${I.filter}</span><div><p>${esc(t('fFilterHead'))}</p>${filterSteps(it)}<small>${esc(t('fFilterP', g.row))}</small></div></div>` + findCard(it, g, S.compact);
      case 'selected': return sayBox('done', I.check, t('gSelected'), '') + findCard(it, g, true);
      case 'rel': {
        const n = Math.abs(g.dr), col = g.col || 1;
        const rows = S.lang === 'en' ? `${n} row${n === 1 ? '' : 's'}` : `${n} ${G.plural(n, 'ряд', 'ряда', 'рядов')}`;
        const head = g.dr === 0 ? t('gRelSame', col) : t(g.dr > 0 ? 'gRelDown' : 'gRelUp', rows, col);
        return sayBox('', I.hand, head, ovText()) + findCard(it, g, S.compact);
      }
      case 'hiddenEq': return sayBox('warn', I.warn, t('gHiddenEq', g.other || t('otherHero')), t('gHiddenEqP'));
      case 'hiddenEnh': return sayBox('warn', I.warn, t('gHiddenEnh'), t('gHiddenEnhP'));
      case 'hiddenFilter': return sayBox('warn', I.warn, t('gHiddenFilter'), t('gHiddenFilterP'));
      default: return sayBox('warn', I.warn, t('gNotIn'), t('gNotInP'), rescanBtn());
    }
  }
  // what the frame over the game is doing (host «overlay» events)
  function ovText() {
    return { need_click: t('ovNeedClick'), on: t('ovOn'), above: t('ovAbove'), below: t('ovBelow') }[S.overlay] || '';
  }
  const rescanBtn = () => `<div class="row" style="margin-top:10px"><button class="btn-line" data-act="rescan">${I.reload}${esc(t('rescan'))}</button></div>`;
  const sayBox = (cls, icon, head, sub, extra = '') =>
    `<div class="say ${cls}"><span class="ring">${icon}</span><div><p>${esc(head)}</p>${sub ? `<small>${esc(sub)}</small>` : ''}${extra}</div></div>`;

  function guideCard() {
    const p = current(); if (!p) return '';
    const g = G.next(p, S.live);
    return `<div class="card framed"><div class="guide">${bust(p, 'lg')}
      <div><h3>${esc(p.heroName)}</h3><div class="meta">${esc(t('itemsOn', g.done, p.items.length))}</div>
        <div class="bar"><i style="width:${Math.round((g.done / p.items.length) * 100)}%"></i></div></div></div>
      ${say(p, g)}${slotList(p, g)}
      <div class="row" style="margin-top:16px;justify-content:space-between">
        <div class="readonly" style="margin:0">${I.lock}<span>${esc(t('readonly'))}</span></div>
        <button class="btn-ghost" data-act="removePlan">${esc(t('remove'))}</button></div></div>`;
  }

  function compactView() {
    const p = current();
    if (!p) return `<div class="compact-view"><div class="row" style="justify-content:space-between"><b>${esc(t('plansNone'))}</b>
      <button class="icon-btn" data-act="compact" title="${esc(t('expand'))}">${I.expand}</button></div></div>`;
    const g = G.next(p, S.live);
    return `<div class="compact-view">
      <div class="compact-top">${bust(p)}<div><b>${esc(p.heroName)}</b><span>${esc(t('itemsOn', g.done, p.items.length))}</span>
        <div class="mini-bar"><i style="width:${Math.round((g.done / p.items.length) * 100)}%"></i></div></div>
        <button class="icon-btn" data-act="compact" title="${esc(t('expand'))}">${I.expand}</button></div>
      ${say(p, g)}${slotList(p, g)}</div>`;
  }

  // ---------------------------------------------------------------- settings
  function settingsPage() {
    const row = (title, sub, body) => `<div class="set"><dt>${esc(title)}${sub ? `<small>${esc(sub)}</small>` : ''}</dt><dd>${body}</dd></div>`;
    return `
      <div class="head"><div><h1>${esc(t('setTitle'))}</h1></div></div>
      <div class="card"><dl style="margin:0">
        ${row(t('setCode'), t('setCodeP'), S.hasCode
          ? `<div class="row"><span class="num" style="font-family:Consolas,monospace;color:var(--gold)">${esc(S.codePrefix)}…</span>
             <button class="btn-line" data-act="editCode">${esc(t('change'))}</button><button class="btn-ghost" data-act="unlink">${esc(t('unlink'))}</button></div>`
          : `<button class="btn-line" data-act="editCode">${esc(t('save'))}</button>`)}
        ${row(t('setLang'), '', `<div class="langs"><button data-act="lang" data-lang="ru" aria-pressed="${S.lang === 'ru'}">РУССКИЙ</button><button data-act="lang" data-lang="en" aria-pressed="${S.lang === 'en'}">ENGLISH</button></div>`)}
        ${row(t('setCopy'), t('setCopyP'), `<button class="switch" role="switch" data-act="copy" aria-checked="${S.saveCopy}"></button>`)}
        ${row(t('setSite'), t('setSiteP'), `<div class="row"><div class="input" style="flex:1;min-width:240px"><input id="site" value="${esc(S.site)}" spellcheck="false"></div>
          <button class="btn-line" data-act="saveSite">${esc(t('save'))}</button>${S.site !== S.defaultSite ? `<button class="btn-ghost" data-act="resetSite">${esc(t('reset'))}</button>` : ''}</div>`)}
        ${row(t('setLog'), t('setLogP'), `<button class="btn-line" data-act="openLog">${esc(t('openLog'))}</button>`)}
        ${row(t('setDiag'), t('setDiagP'), `<button class="btn-line" data-act="diag">${esc(t('diagStart'))}</button>`)}
        ${row(t('setFiles'), t('setFilesP'), `<button class="btn-line" data-act="gameFiles">${esc(t('filesStart'))}</button> <span class="hint" id="filesState">${esc(S.filesState || '')}</span>`)}
        ${row(t('setAbout'), '', `<p style="margin:0 0 10px;color:var(--muted)">${esc(t('aboutP', S.version))}</p>
          <button class="btn-line" data-act="open" data-url="https://github.com/AlexisKozlov/realmforge-extractor">${I.ext}${esc(t('source'))}</button>`)}
      </dl></div>`;
  }

  // ---------------------------------------------------------------- actions
  function allUids() { const s = new Set(); S.plans.list.forEach((p) => p.items.forEach((i) => s.add(i.uid))); return [...s]; }
  function loadPlans() { if (!S.hasCode) return; S.plans.status = 'loading'; host.send({ cmd: 'plans.load', lang: S.lang }); render(); }
  function scan() { if (!S.plans.list.length) return; S.scan = { status: 'scanning', panel: false }; host.send({ cmd: 'equip.scan', uids: allUids() }); render(); }

  // ---------------------------------------------------------------- item card on hover (like the game's item window)
  function itemCard(key) {
    const [uidS, which] = key.split(':'); const p = current(); if (!p) return '';
    const it = p.items.find((x) => String(x.uid) === uidS); if (!it) return '';
    const o = which === 'cur' ? it.cur : it; if (!o) return '';
    const r = G.rankOf(o.stars) || 1, url = G.itemUrl(S.site, o.icon);
    const subs = (o.subs || []).map((x) => `<li><span>${esc(x.text)}</span>${x.rolls > 0 ? `<i class="pips">${'◆'.repeat(Math.min(x.rolls, 6))}</i>` : ''}</li>`).join('');
    const setB = which === 'cur' ? '' : (it.setBonus || []).map((b) => `<li><b class="num">${b.pieces}</b><span>${esc(b.text)}</span></li>`).join('');
    const head = which === 'cur' ? t('cardNow') : t('cardNew');
    return `<div class="ic-head" style="background-image:url(img/tip${r}.webp)">
        <span class="cell r${r}">${url ? `<img src="${esc(url)}" alt="">` : `<img class="ph" src="img/slot${it.slot | 0}.webp" alt="">`}${o.level ? `<i class="lv num">+${o.level}</i>` : ''}</span>
        <div><small>${esc(head)} · ${esc(it.slotName || t('slot' + it.slot))}</small><b>${esc(o.name)}</b>
          <span class="stars">${'★'.repeat(Math.min(o.stars || 0, 8))}</span></div></div>
      ${o.mainStat ? `<div class="ic-main">${esc(o.mainStat)}</div>` : ''}
      ${subs ? `<ul class="ic-subs">${subs}</ul>` : o.subs ? '' : `<p class="ic-none">${esc(t('cardNoStats'))}</p>`}
      ${which !== 'cur' && it.setName ? `<div class="ic-set">${G.setUrl(S.site, it.setIcon) ? `<img src="${esc(G.setUrl(S.site, it.setIcon))}" alt="">` : ''}<b>${esc(it.setName)}</b></div>${setB ? `<ul class="ic-bonus">${setB}</ul>` : ''}` : ''}`;
  }
  let cardKey = '';
  document.addEventListener('mouseover', (e) => {
    const c = e.target.closest && e.target.closest('[data-card]');
    const box = $('#icard');
    if (!c) { if (cardKey) { box.hidden = true; cardKey = ''; } return; }
    const k = c.getAttribute('data-card');
    if (k !== cardKey) { const h = itemCard(k); if (!h) return; box.innerHTML = h; cardKey = k; }
    box.hidden = false;
    const rc = c.getBoundingClientRect(), bw = box.offsetWidth, bh = box.offsetHeight;
    let x = rc.right + 10, y = rc.top - 8;
    if (x + bw > innerWidth - 8) x = Math.max(8, rc.left - bw - 10);
    if (y + bh > innerHeight - 8) y = Math.max(8, innerHeight - bh - 8);
    box.style.left = x + 'px'; box.style.top = y + 'px';
  });

  document.addEventListener('click', (e) => {
    const el = e.target.closest('[data-act]'); if (!el) return;
    const a = el.dataset.act;
    if (a === 'nav') {
      S.page = el.dataset.page; S.editCode = false;
      if (S.page === 'equip' && S.plans.status === 'idle') loadPlans();
      render();
    } else if (a === 'lang') { S.lang = el.dataset.lang; host.send({ cmd: 'setLang', lang: S.lang }); render(); }
    else if (a === 'sync') { S.sync = { phase: 'run', stage: 'find', seconds: 0 }; host.send({ cmd: 'sync' }); render(); }
    else if (a === 'saveOnly') { S.sync = { phase: 'run', stage: 'find', seconds: 0 }; host.send({ cmd: 'sync', saveOnly: true }); S.page = 'sync'; render(); }
    else if (a === 'open') host.send({ cmd: 'open', url: el.dataset.url });
    else if (a === 'openLog') host.send({ cmd: 'openLog' });
    else if (a === 'gameFiles') { host.send({ cmd: 'gameFiles' }); S.filesState = t('filesWork', 0); render(); }
    else if (a === 'diag') { host.send({ cmd: 'diag' }); el.textContent = t('diagOn'); el.disabled = true; }
    else if (a === 'openFolder') host.send({ cmd: 'openFolder' });
    else if (a === 'editCode') { S.editCode = true; S.page = 'sync'; render(); setTimeout(() => $('#code') && $('#code').focus(), 30); }
    else if (a === 'cancelCode') { S.editCode = false; render(); }
    else if (a === 'paste') host.send({ cmd: 'paste' });
    else if (a === 'saveCode') { const v = $('#code').value.trim(); if (CODE_RE.test(v)) host.send({ cmd: 'setCode', code: v }); }
    else if (a === 'unlink') host.send({ cmd: 'clearCode' });
    else if (a === 'copy') { S.saveCopy = !S.saveCopy; host.send({ cmd: 'setSaveCopy', on: S.saveCopy }); render(); }
    else if (a === 'saveSite') host.send({ cmd: 'setSite', site: $('#site').value });
    else if (a === 'resetSite') host.send({ cmd: 'setSite', site: S.defaultSite });
    else if (a === 'reload') loadPlans();
    else if (a === 'rescan') scan();
    else if (a === 'pick') { S.sel = Number(el.dataset.i); render(); }
    else if (a === 'compact') { S.compact = !S.compact; host.send({ cmd: 'compact', on: S.compact }); render(); }
    else if (a === 'removePlan') {
      const p = current(); if (!p) return;
      host.send({ cmd: 'equip.finish', id: p.id, done: false });
      S.plans.list.splice(S.sel, 1); S.sel = Math.max(0, Math.min(S.sel, S.plans.list.length - 1)); render();
    }
  });
  document.addEventListener('input', (e) => { if (e.target.id === 'code') codeTyped(); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Enter' && e.target.id === 'code' && !$('#saveCode').disabled) $('#saveCode').click(); });

  // ---------------------------------------------------------------- host events
  host.on((m) => {
    if (!m || !m.ev) return;
    switch (m.ev) {
      case 'state':
        Object.assign(S, { lang: m.lang, version: m.version, site: m.site, defaultSite: m.defaultSite, hasCode: m.hasCode, codePrefix: m.codePrefix, saveCopy: m.saveCopy, last: m.last || S.last });
        if (m.game) S.game = m.game;
        if (m.codeSaved) { S.editCode = false; toast(t('saved')); if (S.page === 'equip' || S.plans.status !== 'idle') loadPlans(); }
        if (m.siteSaved) toast(t('saved'));
        S.ready = true; render(); break;
      case 'game': {
        const was = S.game.running; S.game = { running: m.running, version: m.version };
        render();
        break;
      }
      case 'paste': { const i = $('#code'); if (i) { i.value = m.text || ''; codeTyped(); } break; }
      case 'sync':
        if (m.stage === 'done') { S.sync = { phase: 'done', result: m.result }; if (m.last) S.last = m.last; }
        else if (m.stage === 'error') S.sync = { phase: 'error', stage: S.sync.stage, error: m.error };
        else S.sync = { phase: 'run', stage: m.stage, seconds: m.seconds || 0 };
        if (S.page === 'sync' && !S.compact) render();
        break;
      case 'plans':
        if (m.status === 'ok') {
          const id = current() && current().id;
          S.plans = { status: 'ok', list: m.plans, err: null };
          const i = S.plans.list.findIndex((p) => p.id === id); S.sel = i >= 0 ? i : 0;
          if (m.plans.length && S.scan.status !== 'ok') scan();
          else if (m.plans.length) host.send({ cmd: 'equip.watch', uids: allUids() });
        } else S.plans = { status: 'error', list: S.plans.list, err: m.status === 'invalid_token' ? 'token' : m.detail || '' };
        render(); break;
      case 'equip.scan': S.scan = { status: m.status, panel: !!m.panel }; render(); break;
      case 'equip.live': {
        S.live = m.live;
        const auto = G.pickPlan(S.plans.list, S.live, S.sel); if (auto >= 0) S.sel = auto;
        const p = current();
        if (p && !S.reported[p.id] && G.next(p, S.live).kind === 'done') { S.reported[p.id] = true; host.send({ cmd: 'equip.finish', id: p.id, done: true }); }
        if (S.page === 'equip' || S.compact) render(); else syncHighlight();
        break;
      }
      case 'gameFiles': S.filesState = m.state === 'progress' ? t('filesWork', m.n) : m.state === 'done' ? t('filesDone', m.n) : m.state === 'no_game' ? t('filesNoGame') : t('filesErr'); if (S.page === 'settings') render(); break;
      case 'overlay': S.overlay = m.state; if (S.page === 'equip' || S.compact) render(); break;
      case 'focusEquip': S.page = 'equip'; render(); break;
    }
  });

  // tell the host which item to frame in the game
  function syncHighlight() {
    const p = current();
    const g = p && S.hasCode ? G.next(p, S.live) : null;
    const uid = g ? G.highlightUid(g) : 0;
    // hints on the game's own buttons: the filter to set, «Заменить», the next slot
    let hint = '', slot = -1, line1 = '', line2 = '';
    if (g && g.item) {
      if (g.kind === 'filter') {
        hint = 'filter';
        line1 = g.item.setName ? t('hSet', g.item.setName) : '';
        const st = G.statOf(g.item.mainStat); line2 = st ? t('hStat', st) : '';
      } else if (g.kind === 'selected') hint = 'replace';
      else if (g.kind === 'slot' && S.live && S.live.panelOk) { hint = 'slot'; slot = g.item.slot; line1 = t('hSlot', g.item.slotName || t('slot' + g.item.slot)); }
    }
    const key = [uid, hint, slot, line1, line2].join('|');
    if (key !== S.hl) { S.hl = key; host.send({ cmd: 'highlight', uid, hint, slot, line1, line2 }); }
  }

  render();
  host.send({ cmd: 'init' });
})();
