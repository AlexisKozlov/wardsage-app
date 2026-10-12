// The community page logic (ui/forum.js).
import assert from 'node:assert/strict';
await import('../ui/forum.js');
const F = globalThis.RFForum;
let n = 0; const ok = (c, m) => { assert.ok(c, m); n++; };

ok(F.badge(0) === '' && F.badge(-1) === '' && F.badge(undefined) === '' && F.badge(1) === '1' && F.badge(9) === '9' && F.badge(10) === '9+' && F.badge(120) === '9+', 'badge cap');
const ru = [1, 2, 4, 5, 11, 12, 14, 21, 22, 25, 101, 111].map((x) => F.countKey(x, 'ru'));
ok(ru.join() === 'forumNew1,forumNew2,forumNew2,forumNew5,forumNew5,forumNew5,forumNew5,forumNew1,forumNew2,forumNew5,forumNew1,forumNew5', 'russian plural keys');
ok(F.countKey(1, 'en') === 'forumNew1' && F.countKey(2, 'en') === 'forumNew5' && F.countKey(21, 'en') === 'forumNew5', 'english plural keys');
const reply = { ok: true, total: 2, topics: [], newTopicUrl: 'https://wardsage.com/community/new?kind=bug&area=app' };
ok(F.reportUrl(reply, 'https://wardsage.com/', '1.6.57') === 'https://wardsage.com/community/new?kind=bug&area=app&v=1.6.57', 'site url + version');
ok(F.reportUrl({ ok: false, error: 'invalid_token' }, 'https://wardsage.com/', '1.6.57') === 'https://wardsage.com/community/new?kind=bug&area=app&v=1.6.57', 'fallback url');
ok(F.reportUrl(null, 'https://wardsage.com', '') === 'https://wardsage.com/community/new?kind=bug&area=app', 'no version');
ok(F.reportUrl({ ok: true, newTopicUrl: 'javascript:1' }, 'https://s.test', '1.0.0').startsWith('https://s.test/'), 'foreign scheme ignored');
ok(F.listUrl('https://wardsage.com/') === 'https://wardsage.com/community', 'list url');
ok(F.topics(null).length === 0 && F.topics({ ok: false }).length === 0 && F.topics({ ok: true, topics: [{ title: 'a', url: 'https://x/1' }, { title: 'b' }, null] }).length === 1, 'topics filter');
console.log('forum.test: ' + n + ' checks ok');
