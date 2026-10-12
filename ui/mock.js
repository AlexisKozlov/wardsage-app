// Development only: a fake host for previewing the interface in a normal browser (no WebView2).
// Inert inside Wardsage.exe (window.chrome.webview exists there). Scenario: index.html?s=<name>
//   onboard | idle | reading | done | error | equip | pick | rel | selected | hidden | done-plan | compact | settings | news | newsoff | en
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
  const live = { gameRunning: true, heroUid: 214700000, panelOk: true, part: 2, hideEquipped: false, hideEnhanced: false, filterActive: false,
    rows: { 6423: [7, 2] }, owner: { 11: 214700000, 12: 214700000, 6423: 200100000, 14: 0, 15: 0 } };
  if (s === 'pick') live.rows[6423] = [2, 2];
  if (s === 'rel') { live.selUid = 77; live.selRow = 5; }
  if (s === 'selected') { live.rows[6423] = [2, 2]; live.selUid = 6423; live.selRow = 2; }
  if (s === 'hidden') { delete live.rows[6423]; live.hideEquipped = true; }
  if (s === 'done-plan') plan.items.forEach((i) => { live.owner[i.uid] = plan.heroUid; });
  if (s === 'equip') { live.part = -1; }

  window.RFMock = {
    on: (f) => { handler = f; },
    send: (m) => {
      if (m.cmd === 'init') {
        emit({ ev: 'state', lang: (s === 'en' || q.get('lang') === 'en') ? 'en' : 'ru', version: '1.6.53', site, defaultSite: 'https://wardsage.com', hasCode: s !== 'onboard',
          codePrefix: 'rf_VKSjw', saveCopy: false, game: { running: s !== 'error', version: '1.0.24' },
          last: { at: '2026-09-24T20:26:00Z', heroes: 128, items: 1109, artifacts: 380, top: [2085, 2016, 2025] } });
        if (q.get('upd')) emit({ ev: 'update', version: '1.6.47', notesRu: 'Чистка склада: если «Автонажатие» выключено, программа прямо об этом говорит и предлагает включить его одной кнопкой.', notesEn: 'Storage cleanup: with «Auto click» off the app now says so and offers to turn it on with one button.' });
        if (s === 'reading') emit({ ev: 'sync', stage: 'read', seconds: 17 });
        if (s === 'done') emit({ ev: 'sync', stage: 'done', result: { seconds: 41, heroes: 128, items: 1109, artifacts: 380, viewUrl: site + '/app/heroes' } });
        if (s === 'error') emit({ ev: 'sync', stage: 'error', error: { kind: 'not_running' } });
        if (['equip', 'pick', 'rel', 'selected', 'hidden', 'done-plan', 'compact'].includes(s)) {
          setTimeout(() => document.querySelector('[data-page=equip]').click(), 20);
        }
        if (s === 'settings') setTimeout(() => document.querySelector('[data-page=settings]').click(), 20);
        if (s === 'community' || s === 'forum0' || s === 'onboard') setTimeout(() => document.querySelector('[data-page=community]').click(), 20);
        if (s === 'news' || s === 'newsoff') setTimeout(() => document.querySelector('[data-page=news]').click(), 20);
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
        // s=onboard: no code, the page shows the note; s=forum0: nothing new
        emit({ ev: 'forum', r: s === 'forum0' ? { ok: true, total: 0, topics: [], newTopicUrl: site + '/community/new?kind=bug&area=app' } : { ok: true, total: 3, newTopicUrl: site + '/community/new?kind=bug&area=app', topics: [
          { id: 't1', title: 'Не снимается экипировка у Сунь Укуна', kind: 'bug', status: 'in_progress', newCount: 2, lastAt: '2026-10-12T08:10:00Z', url: 'https://wardsage.com/community/t1' },
          { id: 't2', title: 'Подсказки по арене на втором экране', kind: 'idea', status: 'accepted', newCount: 1, lastAt: '2026-10-11T19:00:00Z', url: 'https://wardsage.com/community/t2' } ] } });
      } else if (m.cmd === 'plans.load') {
        emit({ ev: 'plans', status: 'ok', plans });
      } else if (m.cmd === 'equip.scan') {
        emit({ ev: 'equip.scan', status: 'ok', panel: true });
        emit({ ev: 'equip.live', live });
        if (s === 'compact') setTimeout(() => document.querySelector('[data-act=compact]').click(), 30);
      } else if (m.cmd === 'highlight') {
        emit({ ev: 'overlay', state: !m.uid ? 'off' : s === 'pick' ? 'need_click' : 'on' });
      } else if (m.cmd === 'sync') {
        emit({ ev: 'sync', stage: 'find' });
        let sec = 0;
        const iv = setInterval(() => { sec += 3; emit({ ev: 'sync', stage: 'read', seconds: sec }); if (sec >= 12) { clearInterval(iv);
          emit({ ev: 'sync', stage: 'done', result: { seconds: 41, heroes: 128, items: 1109, artifacts: 380, viewUrl: site + '/app/heroes' } }); } }, 400);
      }
    },
  };
})();
