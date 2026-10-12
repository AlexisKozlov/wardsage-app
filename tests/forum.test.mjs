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
ok(F.onSite('https://wardsage.com/community/t/5?a=1#c', 'https://test.wardsage.com') === 'https://test.wardsage.com/community/t/5?a=1#c', 'rebased to staging');
ok(F.onSite('https://www.wardsage.com/community/t/5', 'https://test.wardsage.com/') === 'https://test.wardsage.com/community/t/5', 'www rebased');
ok(F.onSite('https://wardsage.com/community/t/5', 'https://wardsage.com') === 'https://wardsage.com/community/t/5', 'unchanged on prod');
ok(F.onSite('https://example.com/x', 'https://test.wardsage.com') === 'https://example.com/x', 'foreign host unchanged');
ok(F.onSite('https://wardsage.com/x', 'ftp://bad') === 'https://wardsage.com/x' && F.onSite('https://wardsage.com/x', '') === 'https://wardsage.com/x', 'bad site unchanged');
ok(F.reportUrl(reply, 'https://test.wardsage.com', '1.6.57') === 'https://test.wardsage.com/community/new?kind=bug&area=app&v=1.6.57', 'reportUrl on staging');
ok(F.topics({ ok: true, topics: [{ title: 'a', url: 'https://x/1', newCount: 0 }, { title: 'b', url: 'https://x/2', newCount: 2 }, { title: 'c', url: 'https://x/3', newCount: 0 }] }).map((x) => x.title).join() === 'b,a,c', 'unread first, rest keep order');
ok([1, 2, 5, 11, 21].map((x) => F.repliesKey(x, 'ru')).join() === 'forumRep1,forumRep2,forumRep5,forumRep5,forumRep1' && F.repliesKey(1, 'en') === 'forumRep1' && F.repliesKey(2, 'en') === 'forumRep5', 'replies keys');
const T0 = Date.parse('2026-10-12T12:00:00Z');
ok(JSON.stringify(F.ago('2026-10-12T11:59:40Z', T0)) === '{"key":"forumAgoNow","n":0}' && F.ago('2026-10-12T11:15:00Z', T0).key === 'forumAgoMin' && F.ago('2026-10-12T11:15:00Z', T0).n === 45
  && F.ago('2026-10-12T09:00:00Z', T0).n === 3 && F.ago('2026-10-12T09:00:00Z', T0).key === 'forumAgoH' && F.ago('2026-10-09T12:00:00Z', T0).key === 'forumAgoD' && F.ago('2026-10-09T12:00:00Z', T0).n === 3 && F.ago('x', T0) === null, 'ago');
console.log('forum.test: ' + n + ' checks ok');
