// The unified «What's new» list logic (ui/news.js).
import assert from 'node:assert/strict';
await import('../ui/news.js');
const N = globalThis.RFNews;
let n = 0; const ok = (c, m) => { assert.ok(c, m); n++; };

const feed = [
  { v: 132, date: '2026-10-11', items: [{ text: 'Гильдия', kind: 'site' }, { text: 'Файл', kind: 'app', appVersion: '1.6.56' }] },
  { v: 131, date: '2026-10-10', items: [{ text: 'Только сайт', kind: 'site' }] },
];
const builtin = [{ v: '1.6.55', date: '2026-10-10', ru: ['а'], en: ['a'] }, { v: '1.6.54', date: '2026-10-09', ru: ['б'], en: [] }];

let e = N.entries(feed, builtin, 'ru');
ok(e.length === 2 && e[0].src === 'site' && e[0].v === 132 && e[0].items.length === 2, 'feed wins');
ok(N.filter(e, 'app').length === 1 && N.filter(e, 'app')[0].items[0].appVersion === '1.6.56', 'app filter drops site-only entries');
ok(N.filter(e, 'site').length === 2 && N.filter(e, 'site')[0].items.length === 1, 'site filter');
ok(N.filter(e, 'all') === e, 'all keeps everything');
e = N.entries(null, builtin, 'en');
ok(e.length === 2 && e[0].src === 'app' && e[0].items[0].text === 'a' && e[0].items[0].kind === 'app' && e[0].items[0].appVersion === '1.6.55', 'fallback to the built-in list');
ok(e[1].items[0].text === 'б', 'missing language falls back to the other');
ok(N.newest(feed) === 132 && N.newest(null) === 0, 'newest');
ok(N.siteUnseen(feed, null) && N.siteUnseen(feed, '131') && !N.siteUnseen(feed, '132') && !N.siteUnseen(null, null), 'unseen');
console.log('news.test: ' + n + ' checks ok');
