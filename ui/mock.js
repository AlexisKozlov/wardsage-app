// Development only: a fake host for previewing the interface in a normal browser (no WebView2).
// Inert inside Wardsage.exe (window.chrome.webview exists there). Scenario: index.html?s=<name>
//   locate | locate-select | locate-filter | locate-gone (a «Найти в игре» request)
//   remote-off | remote-some (the site's switches; &page=equip) | remote-layout (no layout data for the game version) | remote-layout-code (no data: no valid sync code)
//   terms (the first-run Terms of Use card; also &terms=1 with any scenario) | onboard | idle | reading | done | error | equip | pick | rel | selected | hidden | done-plan | compact | settings | news | newsoff | en
//   filter-open (the game's filter pop-up over the list) | filter-guide (the pop-up open for the «faster» tip: what to pick is lit) | far (the item more than three screens down: the filter tip)
(function () {
  if (window.chrome && window.chrome.webview) return;
  const q = new URLSearchParams(location.search);
  const s = q.get('s') || 'idle';
  const site = location.origin + '/site';
  let handler = null;
  const emit = (m) => setTimeout(() => handler && handler(m), 0);
  const plan = {
    id: 'p1', heroUid: 214700000, heroName: 'Сунь Укун', createdAt: '2026-09-25T00:33:00Z',
    items: [
      { slot: 0, uid: 11, slotName: 'Оружие', name: 'Меч ярости', setName: 'Буря', level: 16, stars: 6, mainStat: 'АТК 960', fromHeroUid: 0, fromHeroName: null, icon: 'Item_101501', setIcon: 'icon_suit_Anger', cur: null },
      { slot: 1, uid: 12, slotName: 'Нагрудник', name: 'Нагрудник ярости', setName: 'Буря', level: 16, stars: 6, mainStat: 'ЗДР 12,4%', fromHeroUid: 0, fromHeroName: null, icon: 'Item_101502', setIcon: 'icon_suit_Anger', cur: null },
      { slot: 2, uid: 6423, slotName: 'Браслет', name: 'Шлем бесстрашия', setName: 'Критическая резня', level: 16, stars: 6, mainStat: 'Крит. УРН 80%', fromHeroUid: 200100000, fromHeroName: 'Байек', icon: 'Item_101403', setIcon: 'icon_suit_Crit',
        cur: { uid: 501, name: 'Шлем атаки', level: 12, stars: 5, icon: 'Item_100103', mainStat: 'АТК 18%', subs: [{ text: 'Скорость +6', rolls: 1 }, { text: 'ОЗ +310', rolls: 0 }] },
        subs: [{ text: 'Шанс крит. +9%', rolls: 2, stat: 24, name: 'Шанс крит.', value: '9%', bar: 0.62 }, { text: 'АТК +12,5%', rolls: 1, stat: 13, name: 'Бонус к АТК', value: '12,5%', bar: 0.52 },
          { text: 'Скорость +11', rolls: 3, stat: 29, name: 'СА', value: '11', bar: 1 }, { text: 'ЗАЩ +40', rolls: 0, stat: 2, name: 'ЗАЩ', value: '40', bar: 0.83 }],
        main: [{ text: 'Крит. УРН 80%', rolls: 0, stat: 25, name: 'Крит. УРН', value: '80%' }], quality: 8, qualityName: 'Древний мифический',
        setBonus: [{ pieces: 2, text: 'Крит. УРН +20%' }, { pieces: 4, text: 'Крит. удары снижают защиту цели на 15% на 2 хода' }] },
      { slot: 3, uid: 14, slotName: 'Амулет', name: 'Рукавицы бесстрашия', setName: 'Критическая резня', level: 12, stars: 6, mainStat: 'Шанс крит. 40%', fromHeroUid: 0, fromHeroName: null, icon: 'Item_101404', setIcon: 'icon_suit_Crit',
        cur: { uid: 502, name: 'Рукавицы жизни', level: 8, stars: 4, icon: 'Item_100204' } },
      { slot: 4, uid: 15, slotName: 'Кольцо', name: 'Ботинки ярости', setName: 'Буря', level: 16, stars: 5, mainStat: 'АТК 25%', fromHeroUid: 0, fromHeroName: null, icon: 'Item_101505', setIcon: 'icon_suit_Anger', cur: null },
    ],
  };
  const plans = [plan,
    { id: 'p2', heroUid: 200100000, heroName: 'Байек', items: plan.items.slice(0, 3).map((i, n) => ({ ...i, uid: 900 + n })) },
    { id: 'p3', heroUid: 202400000, heroName: 'Элизия', items: plan.items.slice(0, 5).map((i, n) => ({ ...i, uid: 800 + n })) }];
  const locateReq = { id: 'l1', purpose: 'recast', createdAt: '2026-10-12T10:00:00Z', heroUid: 200100000, heroName: 'Байек', item: {
    ...plan.items[2], uid: 7001, slot: 4, slotName: 'Кольцо', name: 'Кольцо бесстрашия', heroUid: 200100000, heroName: 'Байек' } };
  const live = { gameRunning: true, heroUid: 214700000, panelOk: true, part: 2, hideEquipped: false, hideEnhanced: false, filterActive: false,
    rows: { 6423: [7, 2] }, owner: { 11: 214700000, 12: 214700000, 6423: 200100000, 14: 0, 15: 0 } };
  if (s === 'pick') live.rows[6423] = [2, 2];
  if (s === 'rel' || s === 'filter-open' || s === 'filter-guide' || s === 'far') { live.selUid = 77; live.selRow = 5; }
  if (s === 'selected') { live.rows[6423] = [2, 2]; live.selUid = 6423; live.selRow = 2; }
  if (s === 'hidden') { delete live.rows[6423]; live.hideEquipped = true; }
  if (s === 'done-plan') plan.items.forEach((i) => { live.owner[i.uid] = plan.heroUid; });
  if (s === 'equip') { live.part = -1; }

  window.RFMock = {
    on: (f) => { handler = f; },
    send: (m) => {
      if (m.cmd === 'init') {
        emit({ ev: 'state', lang: (s === 'en' || q.get('lang') === 'en') ? 'en' : 'ru', version: '1.6.53', site, defaultSite: 'https://wardsage.com', hasCode: s !== 'onboard', termsOk: !(s === 'terms' || q.get('terms')) || sessionStorage.getItem('rfTerms') === '1',
          codePrefix: 'rf_VKSjw', saveCopy: false, game: { running: s !== 'error', version: '1.0.24' },
          last: { at: '2026-09-24T20:26:00Z', heroes: 128, items: 1109, artifacts: 380, top: [2085, 2016, 2025] } });
        if (q.get('upd')) emit({ ev: 'update', version: '1.6.47', notesRu: 'Чистка склада: если «Автонажатие» выключено, программа прямо об этом говорит и предлагает включить его одной кнопкой.', notesEn: 'Storage cleanup: with «Auto click» off the app now says so and offers to turn it on with one button.' });
        // the site's switches: s=remote-off (game reading off), s=remote-some (some features off; &page=equip for the equip page)
        if (s === 'remote-off') emit({ ev: 'remote', readEnabled: false, off: [] });
        if (s === 'remote-some') emit({ ev: 'remote', readEnabled: true, off: ['guild', 'autoclick', 'locate'] });
        if (s === 'remote-layout') emit({ ev: 'layout', missing: true, game: '1.6.40.830.1' });
        if (s === 'remote-layout-code') emit({ ev: 'layout', missing: true, noCode: true, game: '1.6.40.830.1' });
        if (s.startsWith('remote') && q.get('page') === 'equip') setTimeout(() => document.querySelector('[data-page=equip]').click(), 20);
        if (s === 'reading') emit({ ev: 'sync', stage: 'read', seconds: 17 });
        if (s === 'done') emit({ ev: 'sync', stage: 'done', result: { seconds: 41, heroes: 128, items: 1109, artifacts: 380, viewUrl: site + '/app/heroes' } });
        if (s === 'error') emit({ ev: 'sync', stage: 'error', error: { kind: 'not_running' } });
        if (s.indexOf('tasks') === 0 || ['equip', 'pick', 'rel', 'selected', 'hidden', 'done-plan', 'compact', 'filter-open', 'filter-guide', 'far'].includes(s)) {
          setTimeout(() => document.querySelector('[data-page=equip]').click(), 20);
        }
        if (s === 'settings') setTimeout(() => document.querySelector('[data-page=settings]').click(), 20);
        if (s === 'community' || s === 'forum' || s === 'forum0' || s === 'forum00' || s === 'onboard') setTimeout(() => document.querySelector('[data-page=community]').click(), 20);
        if (s === 'news' || s === 'newsoff') setTimeout(() => document.querySelector('[data-page=news]').click(), 20);
      } else if (m.cmd === 'acceptTerms') {
        try { sessionStorage.setItem('rfTerms', '1'); } catch (e) { /* preview only */ }
        console.log('[mock host]', JSON.stringify(m));
      } else if (m.cmd === 'news.fetch') {
        // s=newsoff: the site cannot be reached (the built-in list shows)
        if (s === 'newsoff') emit({ ev: 'news', ok: false });
        else emit({ ev: 'news', ok: true, feed: [
          { v: 132, date: '2026-10-11', items: [
            { text: 'Гильдия (бета): кто ударил Матрицу и гильдейского босса на этой неделе, лучшие составы гильдии.', kind: 'site' },
            { text: 'файл называется Wardsage.exe (переименуется сам вместе с ярлыками).', kind: 'app', appVersion: '1.6.56' },
            { text: 'Мобильная шапка и меню стали компактнее.', kind: 'site' } ] },
          { v: 131, date: '2026-10-10', items: [
            { text: 'новое название Wardsage и адрес wardsage.com (обновление ставится само).', kind: 'app', appVersion: '1.6.55' },
            { text: 'крестик прячет программу в трей, а сворачивание оставляет её на панели задач.', kind: 'app', appVersion: '1.6.55' } ] },
          { v: 130, date: '2026-10-09', items: [{ text: 'Арена: подсказки по соперникам обновлены.', kind: 'site' }] } ] });
      } else if (m.cmd === 'forum.unread') {
        // s=onboard: no code, the page shows the note; s=forum0: no unread but open topics; s=forum00: no topics; otherwise some with new replies
        const nt = site + '/community/new?kind=bug&area=app', u = (id) => 'https://wardsage.com/community/' + id, ago = (h) => new Date(Date.now() - h * 3600000).toISOString();
        const calm = [
          { id: 't3', title: 'Не снимается экипировка у Сунь Укуна', kind: 'bug', status: 'in_progress', area: 'app', comments: 4, newCount: 0, lastAt: ago(5), url: u('t3') },
          { id: 't4', title: 'Подсказки по арене на втором экране', kind: 'idea', status: 'accepted', area: 'optimizer', comments: 2, newCount: 0, lastAt: ago(30), url: u('t4') },
          { id: 't5', title: 'Как понять, какой набор брать на героя в рейд?', kind: 'question', status: 'new', area: null, comments: 0, newCount: 0, lastAt: ago(52), url: u('t5') } ];
        const busy = [
          { id: 't1', title: 'Не снимается экипировка у Сунь Укуна', kind: 'bug', status: 'in_progress', area: 'app', comments: 6, newCount: 2, lastAt: ago(2), url: u('t1') },
          { id: 't2', title: 'Подсказки по арене на втором экране', kind: 'idea', status: 'accepted', area: 'optimizer', comments: 3, newCount: 1, lastAt: ago(0.3), url: u('t2') },
          { id: 't6', title: 'Программа зависает на этапе чтения снаряжения, если игра свёрнута в трей и открыто окно настроек', kind: 'bug', status: 'answered', area: 'app', comments: 1, newCount: 0, lastAt: ago(27), url: u('t6') },
          { id: 't7', title: 'Как понять, какой набор брать на героя в рейд?', kind: 'question', status: 'new', area: null, comments: 0, newCount: 0, lastAt: ago(52), url: u('t7') } ];
        emit({ ev: 'forum', r: { ok: true, total: s === 'forum00' ? 0 : s === 'forum0' ? 0 : 3, newTopicUrl: nt, topics: s === 'forum00' ? [] : s === 'forum0' ? calm : busy } });
      } else if (m.cmd === 'plans.load') {
        // s=tasks (a build running, the site's find request, a cleanup list, more builds) | tasks-1 (one build) | tasks-0 (nothing) | tasks-sell (a cleanup list only)
        if (s.indexOf('tasks') === 0) {
          const none = s === 'tasks-0' || s === 'tasks-sell';
          emit({ ev: 'plans', status: 'ok', plans: none ? [] : s === 'tasks-1' ? [plan] : plans });
          if (s === 'tasks' || s === 'tasks-sell') emit({ ev: 'sell', status: 'ok', list: { id: 's1', kind: 'equip', createdAt: '2026-10-12T10:00:00Z', items: plan.items.concat(plan.items, plan.items).map((i, n) => ({ uid: 5000 + n, slot: i.slot, name: i.name, setName: i.setName, level: i.level, stars: i.stars })) } });
          if (s === 'tasks') emit({ ev: 'locate', req: locateReq });
          return;
        }
        emit({ ev: 'plans', status: 'ok', plans });
        if (s.indexOf('locate') === 0) emit({ ev: 'locate', req: s === 'locate-gone' ? { id: 'l1', purpose: 'recast', createdAt: '2026-10-12T10:00:00Z', heroUid: 0, heroName: null, item: null } : locateReq });
      } else if (m.cmd === 'locate.show') {
        // the program's steps for the scenario
        const st = { ev: 'locate.state', step: 'inList', pos: 14, row: 2, col: 6, count: 60, onHero: false, needClick: false, missing: [], subs: null, changed: false };
        if (s === 'locate-select') { st.step = 'selected'; st.subs = [true, true, true, false]; st.changed = true; }
        if (s === 'locate-filter') { st.step = 'recastFilter'; st.missing = ['slot', 'suit', 'main', 'subs']; st.pos = 31; st.count = 40; }
        emit(st);
      } else if (m.cmd === 'locate.tip' || m.cmd === 'locate.finish' || m.cmd === 'locate.stop') {
        console.log('[mock host]', JSON.stringify(m));
      } else if (m.cmd === 'equip.scan') {
        emit({ ev: 'equip.scan', status: 'ok', panel: true });
        emit({ ev: 'equip.live', live });
        if (s === 'compact') setTimeout(() => document.querySelector('[data-act=compact]').click(), 30);
      } else if (m.cmd === 'highlight') {
        emit({ ev: 'overlay', state: !m.uid ? 'off' : s === 'pick' ? 'need_click' : s === 'filter-open' ? 'filter_open' : s === 'filter-guide' ? 'filter_guide' : s === 'far' ? 'below' : 'on', far: s === 'far' });
      } else if (m.cmd === 'sync') {
        emit({ ev: 'sync', stage: 'find' });
        let sec = 0;
        const iv = setInterval(() => { sec += 3; emit({ ev: 'sync', stage: 'read', seconds: sec }); if (sec >= 12) { clearInterval(iv);
          emit({ ev: 'sync', stage: 'done', result: { seconds: 41, heroes: 128, items: 1109, artifacts: 380, viewUrl: site + '/app/heroes' } }); } }, 400);
      }
    },
  };
})();
