// Screenshots of the «Настройки» page (and a scroll check) in headless Chromium. Runs on the test server only:
//   PLAYWRIGHT_BROWSERS_PATH=/srv/work/browsers nice -n 19 node --max-old-space-size=512 tests/ui/settings-shots.mjs [options]
//   --out DIR  --sizes 1100x720,880x600  --scenarios settings,settings-on  --langs ru,en  --dsf 1  --sections (each section too)  --check (scroll test only)
// The ui/ folder is served from a small local http server (not file://).
import { createRequire } from 'node:module';
import { createServer } from 'node:http';
import { readFileSync, mkdirSync } from 'node:fs';
import { join, extname, resolve } from 'node:path';
const require = createRequire('/srv/work/qa/package.json');
const { chromium } = require('playwright-core');

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i < 0 ? d : (process.argv[i + 1] && !process.argv[i + 1].startsWith('--') ? process.argv[i + 1] : true); };
const out = resolve(arg('out', 'settings-png'));
const sizes = String(arg('sizes', '1100x720')).split(',').map((x) => x.split('x').map(Number));
const scenarios = String(arg('scenarios', 'settings')).split(',');
const langs = String(arg('langs', 'ru')).split(',');
const dsf = Number(arg('dsf', 1));
const sections = !!arg('sections', false), checkOnly = !!arg('check', false);
const query = String(arg('query', ''));
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

async function open(sc, lang, w, h) {
  const ctx = await browser.newContext({ viewport: { width: w, height: h }, deviceScaleFactor: dsf });
  const page = await ctx.newPage();
  const errs = []; page.on('pageerror', (e) => errs.push(String(e)));
  await page.goto(`${base}?s=${sc}&lang=${lang}${query}`);
  await page.waitForSelector('#page .head h1, #page section', { timeout: 8000 });
  await page.waitForTimeout(500);
  return { ctx, page, errs };
}

try {
  if (!checkOnly) {
    for (const sc of scenarios) for (const lang of langs) for (const [w, h] of sizes) {
      const { ctx, page, errs } = await open(sc, lang, w, h);
      const tag = `${sc}-${lang}-${w}x${h}${dsf !== 1 ? '@' + dsf : ''}`;
      await page.screenshot({ path: join(out, tag + '.png') });                       // the window as the player sees it
      const full = await page.evaluate(() => document.querySelector('.main').scrollHeight);
      await page.setViewportSize({ width: w, height: Math.max(h, full) });
      await page.waitForTimeout(150);
      await page.screenshot({ path: join(out, tag + '-full.png') });                   // the whole page
      if (sections) {
        const n = await page.locator('#page section').count();
        for (let i = 0; i < n; i++) {
          const el = page.locator('#page section').nth(i);
          const id = (await el.getAttribute('id')) || 'sec' + i;
          await el.screenshot({ path: join(out, `${tag}-${id}.png`) });
        }
      }
      if (errs.length) { console.log('page errors in ' + tag, errs); fails++; }
      console.log('shot ' + tag);
      await ctx.close();
    }
  } else {
    for (const [w, h] of [[880, 600], [1100, 720]]) {
      const { ctx, page, errs } = await open('settings-test', 'ru', w, h);
      const sw = page.locator('button.switch:not([disabled])').last();
      await sw.scrollIntoViewIfNeeded();
      await page.waitForTimeout(300);
      const before = await page.evaluate(() => document.querySelector('.main').scrollTop);
      const on = await sw.getAttribute('aria-checked');
      await sw.click();
      await page.waitForTimeout(300);
      const after = await page.evaluate(() => document.querySelector('.main').scrollTop);
      check(before > 0, `${w}x${h}: scrolled down before the click (${before})`);
      check(Math.abs(after - before) <= 1, `${w}x${h}: scrollTop kept after a switch click (${before} -> ${after})`);
      check((await sw.getAttribute('aria-checked')) !== on, `${w}x${h}: the switch flipped`);
      check(errs.length === 0, `${w}x${h}: no page errors`);
      await ctx.close();
    }
  }
} finally { await browser.close(); srv.close(); }
process.exit(fails ? 1 : 0);
