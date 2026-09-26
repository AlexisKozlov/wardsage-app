// Builds app/res/hero_tags.json from the site's game data: hero base id -> [class id, faction ids...].
// The app filters the game's hero grid with them (src/HeroPilot.cs), so the hero is in the first rows without scrolling.
// Heroes added by a later game patch are simply not in it: the grid is scrolled then, as before.
//
//   node tools/hero-tags.mjs [../realmforge-web]
import fs from 'node:fs';
import path from 'node:path';

const web = process.argv[2] || path.join(import.meta.dirname, '..', '..', 'realmforge-web');
const gd = JSON.parse(fs.readFileSync(path.join(web, 'data', 'gamedata.json'), 'utf8'));
const factions = JSON.parse(fs.readFileSync(path.join(web, 'data', 'hero-factions.json'), 'utf8'));
const out = {};
for (const [id, h] of Object.entries(gd.ref.heroes)) {
  const c = Number(h.c) || 0, f = (factions[id] || []).filter((x) => Number(x) > 0 && Number(x) !== 999);
  if (c || f.length) out[id] = [c, ...f];
}
const file = path.join(import.meta.dirname, '..', 'app', 'res', 'hero_tags.json');
fs.writeFileSync(file, JSON.stringify(out) + '\n');
console.log(`wrote ${file}: ${Object.keys(out).length} heroes`);
