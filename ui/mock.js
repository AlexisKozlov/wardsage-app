// Development only: a fake host for previewing the interface in a normal browser (no WebView2).
// Inert inside RealmForge.exe (window.chrome.webview exists there). Scenario: index.html?s=<name>
//   onboard | idle | reading | done | error | equip | pick | rel | selected | hidden | done-plan | compact | settings | en
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
        emit({ ev: 'state', lang: s === 'en' ? 'en' : 'ru', version: '1.0', site, defaultSite: 'https://realmforge-wor.vercel.app', hasCode: s !== 'onboard',
          codePrefix: 'rf_VKSjw', saveCopy: false, game: { running: s !== 'error', version: '1.0.24' },
          last: { at: '2026-09-24T20:26:00Z', heroes: 128, items: 1109, artifacts: 380, top: [2085, 2016, 2025] } });
        if (s === 'reading') emit({ ev: 'sync', stage: 'read', seconds: 17 });
        if (s === 'done') emit({ ev: 'sync', stage: 'done', result: { seconds: 41, heroes: 128, items: 1109, artifacts: 380, viewUrl: site + '/app/heroes' } });
        if (s === 'error') emit({ ev: 'sync', stage: 'error', error: { kind: 'not_running' } });
        if (['equip', 'pick', 'rel', 'selected', 'hidden', 'done-plan', 'compact'].includes(s)) {
          setTimeout(() => document.querySelector('[data-page=equip]').click(), 20);
        }
        if (s === 'settings') setTimeout(() => document.querySelector('[data-page=settings]').click(), 20);
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
