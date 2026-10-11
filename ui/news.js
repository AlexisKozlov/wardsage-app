// Wardsage desktop - the unified «What's new» list. Pure logic (tests: tests/news.test.mjs).
//
// feed: the site's /api/changelog reply [{v, date, items:[{text, kind:'app'|'site', appVersion?}]}] (via the host's news.fetch)
// builtin: ui/changelog.js [{v:'1.6.55', date, ru:[], en:[]}] - the app's own changes, used when the site cannot be reached
(function (root) {
  // Entries to show: [{kind:'site'|'app', v, date, items:[{text, kind, appVersion}]}]; the feed (if valid) else the built-in list.
  const cap = (s) => (s ? s.charAt(0).toUpperCase() + s.slice(1) : s);

  function entries(feed, builtin, lang) {
    if (Array.isArray(feed) && feed.length) {
      return feed.filter((e) => e && typeof e.v === 'number' && Array.isArray(e.items)).map((e) => ({
        src: 'site', v: e.v, date: e.date || '',
        items: e.items.filter((x) => x && typeof x.text === 'string').map((x) => ({ text: cap(x.text), kind: x.kind === 'app' ? 'app' : 'site', appVersion: x.appVersion || '' })),
      }));
    }
    return (builtin || []).map((e) => {
      const own = lang === 'en' ? e.en : e.ru, other = lang === 'en' ? e.ru : e.en;
      return { src: 'app', v: e.v, date: e.date || '', items: (own && own.length ? own : other || []).map((text) => ({ text, kind: 'app', appVersion: String(e.v) })) };
    });
  }

  // Only the bullets of one kind ('all' keeps everything); entries left without bullets are dropped.
  function filter(list, kind) {
    if (kind !== 'app' && kind !== 'site') return list;
    return list.map((e) => ({ ...e, items: e.items.filter((x) => x.kind === kind) })).filter((e) => e.items.length);
  }

  // The newest site version of a feed (0 when there is none).
  function newest(feed) {
    let n = 0;
    if (Array.isArray(feed)) feed.forEach((e) => { if (e && typeof e.v === 'number' && e.v > n) n = e.v; });
    return n;
  }

  // Whether the site has something newer than the last seen version (seen: stored text or null).
  function siteUnseen(feed, seen) {
    const n = newest(feed);
    return n > 0 && n > (parseInt(seen, 10) || 0);
  }

  const api = { entries, filter, newest, siteUnseen };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else root.RFNews = api;
})(typeof window !== 'undefined' ? window : globalThis);
