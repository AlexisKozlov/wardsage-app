// Wardsage desktop - community page logic (tests: tests/forum.test.mjs). Pure, no DOM.
//   reply of the host's forum.unread: {ok, total, topics:[{id, title, kind, status, newCount, lastAt, url}], newTopicUrl} or {ok:false, error}
(function (root) {
  // Text of the red count in the rail: nothing for 0, "9+" above nine.
  function badge(total) {
    const n = Math.floor(Number(total) || 0);
    return n <= 0 ? '' : n > 9 ? '9+' : String(n);
  }

  // i18n key for «N new replies»: Russian has three forms, English two.
  function countKey(n, lang) {
    const a = Math.abs(n) % 100, b = a % 10;
    if (lang === 'en') return n === 1 ? 'forumNew1' : 'forumNew5';
    return a > 10 && a < 20 ? 'forumNew5' : b === 1 ? 'forumNew1' : b >= 2 && b <= 4 ? 'forumNew2' : 'forumNew5';
  }

  // i18n key for «N replies».
  function repliesKey(n, lang) {
    const a = Math.abs(n) % 100, b = a % 10;
    if (lang === 'en') return n === 1 ? 'forumRep1' : 'forumRep5';
    return a > 10 && a < 20 ? 'forumRep5' : b === 1 ? 'forumRep1' : b >= 2 && b <= 4 ? 'forumRep2' : 'forumRep5';
  }

  // «2 h ago»: {key, n} for the i18n keys forumAgoNow / forumAgoMin / forumAgoH / forumAgoD, or null for a bad date.
  function ago(iso, now) {
    const t = Date.parse(iso);
    if (!isFinite(t)) return null;
    const m = Math.floor(((now === undefined ? Date.now() : now) - t) / 60000);
    if (m < 1) return { key: 'forumAgoNow', n: 0 };
    if (m < 60) return { key: 'forumAgoMin', n: m };
    if (m < 1440) return { key: 'forumAgoH', n: Math.floor(m / 60) };
    return { key: 'forumAgoD', n: Math.floor(m / 1440) };
  }

  // The site always builds links on wardsage.com; a test-channel app opens them on its own site.
  function onSite(url, site) {
    try {
      const u = new URL(String(url)), s = new URL(String(site));
      if (!/^https?:$/.test(u.protocol) || !/^https?:$/.test(s.protocol)) return url;
      if (u.hostname !== 'wardsage.com' && u.hostname !== 'www.wardsage.com') return url;
      if (s.host === u.host || s.hostname === 'wardsage.com' || s.hostname === 'www.wardsage.com') return url;
      return s.origin + u.pathname + u.search + u.hash;
    } catch (e) { return url; }
  }

  // «Report a bug» address: the site's own (with kind=bug&area=app) plus the app version.
  function reportUrl(reply, site, version) {
    let u = reply && reply.ok && typeof reply.newTopicUrl === 'string' && /^https?:\/\//.test(reply.newTopicUrl)
      ? onSite(reply.newTopicUrl, site) : String(site || '').replace(/\/+$/, '') + '/community/new?kind=bug&area=app';
    if (version) u += (u.indexOf('?') < 0 ? '?' : '&') + 'v=' + encodeURIComponent(version);
    return u;
  }

  function listUrl(site) { return String(site || '').replace(/\/+$/, '') + '/community'; }

  // Topics worth showing: those with a title and an address; ones with new replies first, the rest in the order the site sent them.
  function topics(reply) {
    return reply && reply.ok && Array.isArray(reply.topics)
      ? reply.topics.filter((x) => x && typeof x.title === 'string' && typeof x.url === 'string' && /^https?:\/\//.test(x.url))
        .map((x, i) => ({ x, i, u: Number(x.newCount) > 0 ? 1 : 0 })).sort((a, b) => b.u - a.u || a.i - b.i).map((o) => o.x) : [];
  }

  const api = { badge, countKey, repliesKey, ago, reportUrl, listUrl, topics, onSite };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.RFForum = api;
})(typeof window !== 'undefined' ? window : globalThis);
