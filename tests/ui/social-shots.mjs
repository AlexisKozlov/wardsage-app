// Screenshots + geometry checks of the social icons (rail foot of the main window, the works screen) in headless Chromium. Test server only:
//   PLAYWRIGHT_BROWSERS_PATH=/srv/work/browsers nice -n 19 node --max-old-space-size=512 tests/ui/social-shots.mjs [--out DIR] [--sizes 880x600,1100x720,1920x1080] [--dsf 1,1.5] [--langs ru,en]
import { createRequire } from 'node:module';
import { createServer } from 'node:http';
import { readFileSync, mkdirSync } from 'node:fs';
import { join, extname, resolve } from 'node:path';
const require = createRequire('/srv/work/qa/package.json');
const { chromium } = require('playwright-core');
const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i < 0 ? d : process.argv[i + 1]; };
const out = resolve(arg('out', 'social-png'));
const sizes = String(arg('sizes', '880x600,1100x720,1920x1080')).split(',').map((x) => x.split('x').map(Number));
const dsfs = String(arg('dsf', '1,1.5')).split(',').map(Number);
const langs = String(arg('langs', 'ru')).split(',');
mkdirSync(out, { recursive: true });
const ui = resolve('ui');
const mime = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.webp': 'image/webp', '.png': 'image/png', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' };
const srv = createServer((req, res) => {
  const p = decodeURIComponent(req.url.split('?')[0]);
  try { const f = join(ui, p === '/' ? 'index.html' : p); if (!f.startsWith(ui)) throw 0; const b = readFileSync(f); res.writeHead(200, { 'content-type': mime[extname(f)] || 'application/octet-stream' }); res.end(b); }
  catch (e) { res.writeHead(404); res.end(); }
});
await new Promise((r) => srv.listen(0, '127.0.0.1', r));
const base = 'http://127.0.0.1:' + srv.address().port + '/index.html';
const browser = await chromium.launch({ args: ['--no-sandbox'] });
let fails = 0;
const check = (ok, what) => { console.log((ok ? '  ok   ' : '  FAIL ') + what); if (!ok) fails++; };
try {
  for (const dsf of dsfs) for (const lang of langs) for (const [w, h] of sizes) {
    const ctx = await browser.newContext({ viewport: { width: w, height: h }, deviceScaleFactor: dsf });
    const page = await ctx.newPage();
    const errs = []; page.on('pageerror', (e) => errs.push(String(e)));
    const tag = `${w}x${h}@${dsf}-${lang}`;
    // main window: the rail foot
    await page.goto(`${base}?s=idle&lang=${lang}`);
    await page.waitForSelector('.rail-foot .soc', { timeout: 8000 });
    await page.waitForTimeout(500);
    const g = await page.evaluate(() => {
      const r = (e) => { const b = e.getBoundingClientRect(); return { t: b.top, b: b.bottom, l: b.left, r: b.right, w: b.width, h: b.height }; };
      const rail = document.querySelector('.rail'), navs = [...document.querySelectorAll('.rail .nav')], gs = document.querySelector('.game-state'), row = document.querySelector('.rail-foot .soc-row');
      return { rail: r(rail), lastNav: r(navs[navs.length - 1]), gs: r(gs), row: r(row), soc: [...document.querySelectorAll('.rail-foot .soc')].map(r), titles: [...document.querySelectorAll('.rail-foot .soc')].map((e) => e.title),
        scroll: rail.scrollHeight - rail.clientHeight, gsText: gs.textContent.trim().replace(/\s+/g, ' ') };
    });
    check(g.soc.length === 2 && g.titles.join() === 'Discord,Telegram', `${tag}: two icons, titles ${g.titles}`);
    check(g.row.t >= g.gs.b - 0.5, `${tag}: icons start below the game status (${g.row.t.toFixed(0)} >= ${g.gs.b.toFixed(0)})`);
    check(g.gs.t >= g.lastNav.b, `${tag}: the status does not touch the menu (${g.gs.t.toFixed(0)} >= ${g.lastNav.b.toFixed(0)})`);
    check(g.row.b <= g.rail.b + 0.5 && g.scroll <= 0, `${tag}: inside the rail, no overflow (bottom ${g.row.b.toFixed(0)} of ${g.rail.b.toFixed(0)}, overflow ${g.scroll})`);
    check(g.soc.every((s) => s.w >= 32 && s.h >= 32), `${tag}: click area >= 32 px (${g.soc.map((s) => s.w + 'x' + s.h)})`);
    const v = await page.evaluate(() => [...document.querySelectorAll('.rail-foot .soc')].map((e) => { const s = e.querySelector('svg'), b = s.querySelector('path').getBBox(), k = s.getBoundingClientRect().width / 24, cs = getComputedStyle(e);
      return { vh: b.height * k, vw: b.width * k, ring: cs.boxShadow !== 'none' || parseFloat(cs.borderTopWidth) > 0 || cs.backgroundImage !== 'none' || cs.backgroundColor !== 'rgba(0, 0, 0, 0)' }; }));
    check(Math.abs(v[0].vh - v[1].vh) <= 1, `${tag}: same visible height (${v.map((x) => x.vh.toFixed(1))})`);
    check(v.every((x) => x.vh >= 20 && !x.ring), `${tag}: visible height >= 20 px, no ring/plate`);
    const gap = g.soc[1].l - g.soc[0].r; check(gap >= 0 && gap <= 8, `${tag}: spacing between icons (hit areas gap ${gap.toFixed(1)})`);
    await page.screenshot({ path: join(out, `main-${tag}.png`) });
    await page.locator('.rail').screenshot({ path: join(out, `rail-${tag}.png`) });
    await page.locator('.rail-foot .soc').first().hover(); await page.waitForTimeout(300);
    if (w === 1100) await page.locator('.rail').screenshot({ path: join(out, `rail-hover-${tag}.png`) });
    // the works screen
    await page.goto(`${base}?s=maint-on&lang=${lang}`);
    await page.waitForSelector('.maint-veil .soc', { timeout: 8000 });
    await page.waitForTimeout(500);
    const m = await page.evaluate(() => ({ n: document.querySelectorAll('.maint-veil .soc').length, discordBtn: !!document.querySelector('.maint-btns [data-url*="discord.gg"]'), urls: [...document.querySelectorAll('.maint-veil .soc')].map((e) => e.dataset.url),
      card: document.querySelector('.maint-card').getBoundingClientRect().bottom, vh: innerHeight }));
    check(m.n === 2 && !m.discordBtn && m.urls.join() === 'https://discord.gg/xJrfv3zYx5,https://t.me/wardsage', `${tag}: works screen shows two icons instead of the Discord button`);
    const mg = await page.evaluate(() => { const r = (e) => { const b = e.getBoundingClientRect(); return { t: b.top, b: b.bottom, l: b.left, r: b.right, w: b.width, h: b.height }; };
      const st = [...document.querySelectorAll('.maint-card > *')].filter((e) => !e.contains(document.querySelector('.maint-veil .soc-row')) && !e.querySelector('.soc-row')).map(r);
      const soc = [...document.querySelectorAll('.maint-veil .soc')]; const row = r(document.querySelector('.maint-veil .soc-row'));
      return { soc: soc.map(r), vh: soc.map((e) => e.querySelector('path').getBBox().height * e.querySelector('svg').getBoundingClientRect().width / 24), row, st, ring: soc.some((e) => getComputedStyle(e).boxShadow !== 'none' || e.getBoundingClientRect().width < 32) }; });
    check(mg.soc.every((s) => s.w >= 32 && s.h >= 32) && !mg.ring && Math.abs(mg.vh[0] - mg.vh[1]) <= 1, `${tag}: works icons same height (${mg.vh.map((x) => x.toFixed(1))}), area >= 32, no ring`);
    check(!mg.st.some((e) => e.h > 0 && e.t < mg.row.b - 0.5 && e.b > mg.row.t + 0.5 && e.l < mg.row.r && e.r > mg.row.l), `${tag}: works icons do not overlap other card blocks`);
    await page.locator('.maint-veil .soc').first().hover(); await page.waitForTimeout(300);
    await page.screenshot({ path: join(out, `maint-${tag}.png`) });
    check(errs.length === 0, `${tag}: no page errors ${errs.join(';')}`);
    await ctx.close();
  }
} finally { await browser.close(); srv.close(); }
process.exit(fails ? 1 : 0);
