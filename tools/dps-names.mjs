// Builds src/DpsNames.cs for the DPS meter: hero names, skill names and icons, and the "child skill -> parent skill" groups.
// The game books damage under the ids of NESTED skills (bullets, buffs of talents); the groups fold them into the named skill.
// Reads the web repo's origin/main through git (the working copy may be behind), or a plain folder (a checkout of the same
// paths: data/hero-pages, data/engine/heroes, data/aspects.json, data/engine/sets.json) when --dir is given.
//
//   node tools/dps-names.mjs [../wardsage-web] [--dir data-root-folder] [--report fixture.json]
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';

const args = process.argv.slice(2);
const valOf = (flag) => { const i = args.indexOf(flag); return i >= 0 ? args[i + 1] : null; };
const dir = valOf('--dir'), reportAt = valOf('--report');
const web = args.find((a, i) => !a.startsWith('--') && args[i - 1] !== '--dir' && args[i - 1] !== '--report') || path.join(import.meta.dirname, '..', '..', 'wardsage-web');

const git = (...a) => execFileSync('git', ['-C', web, ...a], { encoding: 'utf8', maxBuffer: 1 << 29 });
const readJson = (p) => JSON.parse(dir ? fs.readFileSync(path.join(dir, p), 'utf8') : git('show', 'origin/main:' + p));
const listIds = (d) => (dir ? fs.readdirSync(path.join(dir, d)) : git('ls-tree', '--name-only', 'origin/main', d + '/').split('\n').map((f) => path.basename(f)))
  .filter((f) => /^\d+\.json$/.test(f)).map((f) => +f.replace('.json', ''));

const heroes = new Map();             // id -> [ru, en]
const skills = new Map();             // id -> [ru, en, icon]  (first hero wins)
const roots = new Map();              // hero -> Map(skill id -> kind), the skills that carry a name for that hero
const talentWord = ['Талант', 'Talent'];
const PRIO = { ultimate: 0, passive: 1, basic: 2, auto: 3, link: 3.5, talent: 4 };
const heroIds = listIds('data/hero-pages');
for (const id of heroIds) {
  const h = readJson(`data/hero-pages/${id}.json`);
  if (!h.id || !h.n) continue;
  heroes.set(h.id, [h.n.ru || h.n.en || '', h.n.en || h.n.ru || '']);
  const mine = new Map();
  for (const s of h.skills || []) {
    if (!s.id) continue;
    let ru, en;
    if (s.k === 'talent') {
      const q = ((s.d && s.d.ru) || '').match(/«([^»]+)»/);
      const star = s.star || 0;
      if (q && q[1].length <= 22) { ru = `${talentWord[0]} «${q[1]}»`; en = star > 0 ? `${talentWord[1]} ★${star}` : talentWord[1]; }
      else if (star > 0) { ru = `${talentWord[0]} ★${star}`; en = `${talentWord[1]} ★${star}`; }
      else { ru = talentWord[0]; en = talentWord[1]; }
    } else if (s.n && (s.n.ru || s.n.en)) { ru = s.n.ru || s.n.en; en = s.n.en || s.n.ru; }
    else continue;
    mine.set(s.id, s.k || 'passive');
    if (!skills.has(s.id)) skills.set(s.id, [ru, en, s.icon || '']);
  }
  roots.set(h.id, mine);
}

// the nested-skill links of one hero: node references of the graphs (not removals, cooldown starts or layer counts; a cooldown change links its target) and m_RelationParam
const SKIP_NODES = new Set(['KnotRemoveBuff', 'KnotStartCoolDown', 'KnotBuffLayerCount', 'KnotChangeSkillBack']);
const edgesOf = (eng) => {
  const e = new Map(), add = (a, b) => { if (b > 0 && a !== b) { if (!e.has(a)) e.set(a, new Set()); e.get(a).add(b); } };
  for (const [g, nodes] of Object.entries(eng.graphs || {})) {
    const from = +g.replace('skill_', '');
    for (const n of nodes) {
      if (SKIP_NODES.has(n[1])) continue;
      for (const [k, v] of Object.entries(n[3] || {})) if (/^m_(new|target)?skillid$/i.test(k) && typeof v === 'number') add(from, v);
    }
  }
  for (const [sid, lvs] of Object.entries(eng.skills || {})) for (const d of Object.values(lvs)) for (const r of d.m_RelationParam || []) add(+sid, r[0]);
  return e;
};

const groups = [];                    // [hero, child, root]
for (const hid of heroes.keys()) {
  let eng; try { eng = readJson(`data/engine/heroes/${hid}.json`); } catch (x) { continue; }
  const e = edgesOf(eng), mine = roots.get(hid), best = new Map();   // child -> [dist, prio, root]
  for (const [rid, kind] of mine) {
    const dist = new Map([[rid, 0]]); let layer = [rid];
    while (layer.length) {
      const next = [];
      for (const x of layer) for (const y of e.get(x) || []) if (!dist.has(y)) { dist.set(y, dist.get(x) + 1); next.push(y); }
      layer = next;
    }
    for (const [c, d] of dist) {
      if (c === rid || mine.has(c)) continue;
      const cand = [d, PRIO[kind] === undefined ? 5 : PRIO[kind], rid], cur = best.get(c);
      if (!cur || cand[0] < cur[0] || (cand[0] === cur[0] && (cand[1] < cur[1] || (cand[1] === cur[1] && cand[2] < cur[2])))) best.set(c, cand);
    }
  }
  for (const [c, r] of best) groups.push([hid, c, r[2]]);
}

const aspects = readJson('data/aspects.json');
const setsJson = readJson('data/engine/sets.json');
const aspectRows = Object.entries(aspects).map(([k, a]) => [+k, a.n.ru || a.n.en, a.n.en || a.n.ru, a.icon ? k : '']);

const esc = (t) => String(t).replace(/\\/g, '\\\\').replace(/"/g, '\\"').replace(/[\r\n|]/g, ' ');
const line = (cells) => `    "${cells.map(esc).join('|')}"`;
const heroRows = [...heroes].sort((a, b) => a[0] - b[0]).map(([id, [ru, en]]) => line([id, ru, en])).join(',\n');
const skillRows = [...skills].sort((a, b) => a[0] - b[0]).map(([id, [ru, en, ic]]) => line([id, ru, en, ic])).join(',\n');
groups.sort((a, b) => a[0] - b[0] || a[1] - b[1]);
const groupRows = groups.map((g) => line(g)).join(',\n');
const aspRows = aspectRows.sort((a, b) => a[0] - b[0]).map((r) => line(r)).join(',\n');

const cs = `// GENERATED by tools/dps-names.mjs from the site's data (hero-pages, engine/heroes, aspects; do not edit): names, icons and skill groups for the DPS meter.
// ${heroes.size} heroes, ${skills.size} skills, ${groups.length} nested-skill links, ${aspectRows.length} aspects.
// The game books damage under the ids of nested skills (bullets, talent effects); GroupRows say which named skill of the hero owns them.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace RealmForge {
  public static class DpsNames {
    static readonly string[] HeroRows = {
${heroRows}
    };
    // id|ru|en|icon (a talent has no name of its own: the label is built from its star or the first quoted word of its text)
    static readonly string[] SkillRows = {
${skillRows}
    };
    // hero|child|root: per hero, the nested skill and the named skill it belongs to
    static readonly string[] GroupRows = {
${groupRows}
    };
    // key (hero*10+n)|ru|en|icon (the key when the aspect has an icon)
    static readonly string[] AspectRows = {
${aspRows}
    };

    static readonly Dictionary<int, string[]> Heroes = Load(HeroRows, 3), Skills = Load(SkillRows, 4), Aspects = Load(AspectRows, 4);
    static readonly Dictionary<long, int> Roots = LoadGroups();

    static Dictionary<int, string[]> Load(string[] rows, int cols) {
      var d = new Dictionary<int, string[]>();
      foreach (var r in rows) {
        var p = r.Split('|'); int id;
        if (p.Length == cols && int.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) {
          var v = new string[cols - 1]; Array.Copy(p, 1, v, 0, cols - 1); d[id] = v;
        }
      }
      return d;
    }

    static long Key(int hero, int id) { return (long)hero * 100000000000L + id; }

    static Dictionary<long, int> LoadGroups() {
      var d = new Dictionary<long, int>();
      foreach (var r in GroupRows) {
        var p = r.Split('|'); int h, c, t;
        if (p.Length == 3 && int.TryParse(p[0], out h) && int.TryParse(p[1], out c) && int.TryParse(p[2], out t)) d[Key(h, c)] = t;
      }
      return d;
    }

    static string Pick(string[] n, bool ru) { return ru ? n[0] : n[1]; }

    /// <summary>The hero's name by the first id that is known (unit, base id, then hero id / 100000); otherwise "#unit".</summary>
    public static string Hero(int unit, int baseId, uint hero, bool ru) {
      string[] n;
      if (unit > 0 && Heroes.TryGetValue(unit, out n)) return Pick(n, ru);
      if (baseId > 0 && Heroes.TryGetValue(baseId, out n)) return Pick(n, ru);
      if (hero >= 100000 && Heroes.TryGetValue((int)(hero / 100000), out n)) return Pick(n, ru);
      return "#" + (unit > 0 ? unit : baseId > 0 ? baseId : (int)(hero / 100000));
    }

    public static bool KnownHero(int id) { return Heroes.ContainsKey(id); }
    public static bool KnownSkill(int id) { return Skills.ContainsKey(id); }

    /// <summary>The hero a skill id belongs to by its shape: 226601 is hero 2266, 2290104 is hero 2290; 0 when it has no hero shape.</summary>
    static int HeroOfSkill(int id) {
      if (id >= 200000 && id < 300000) return id / 100;
      if (id >= 2000000 && id < 3000000) return id / 1000;
      return 0;
    }

    /// <summary>The named skill that owns a nested skill of this hero (the pair (unit, id), then the hero the id itself points to); the id itself when there is none.</summary>
    public static int SkillRoot(int unit, int id) {
      int r;
      if (unit > 0 && Roots.TryGetValue(Key(unit, id), out r)) return r;
      int h = HeroOfSkill(id);
      if (h > 0 && h != unit && Roots.TryGetValue(Key(h, id), out r)) return r;
      return id;
    }

    /// <summary>The icon of a skill (the file name without the extension in art/skills, or an aspect key for an aspect), "" when it has none.</summary>
    public static string SkillIcon(int id) {
      string[] n;
      if (Skills.TryGetValue(id, out n)) return n[2];
      if (id >= 1000000 && Aspects.TryGetValue(id / 100, out n) && n[2].Length > 0) return "aspect:" + n[2];
      return "";
    }

    /// <summary>The group of a skill that has no name: an aspect, an equipment set, a field effect, an awakening; null when it is none of them.</summary>
    public static string Category(int id, bool ru) {
      string[] n;
      if (id >= 1000000 && Aspects.TryGetValue(id / 100, out n)) return Pick(n, ru);
      if (id >= 102000000 && id < 102100000) return ru ? "Комплект" : "Set";
      if (id >= 13000000 && id < 13100000) return ru ? "Эффект поля" : "Field effect";
      if (id >= 2000000 && id < 3000000 && id % 1000 >= 100 && id % 1000 < 200 && Heroes.ContainsKey(id / 1000)) return ru ? "Пробуждение" : "Awakening";
      return null;
    }

    public static string Other(bool ru) { return ru ? "Прочее" : "Other"; }

    /// <summary>The skill's name; an unnamed one is its category, otherwise "Прочее" / "Other" (no id: the meter shows no raw numbers).</summary>
    public static string Skill(int id, bool ru) {
      string[] n;
      if (Skills.TryGetValue(id, out n)) return Pick(n, ru);
      return Category(id, ru) ?? Other(ru);
    }
  }
}
`;
fs.writeFileSync(path.join(import.meta.dirname, '..', 'src', 'DpsNames.cs'), cs);
console.log(`wrote src/DpsNames.cs: ${heroes.size} heroes, ${skills.size} skills, ${groups.length} links, ${aspectRows.length} aspects (${(cs.length / 1024) | 0} KB)`);

if (reportAt) {   // the share of "Other" on a fixture of fights (own side), by the same rules as the C# code
  const bySkill = new Map(groups.map((g) => [g[0] + ':' + g[1], g[2]]));
  const hs = (id) => (id >= 200000 && id < 300000 ? (id / 100) | 0 : id >= 2000000 && id < 3000000 ? (id / 1000) | 0 : 0);
  const asp = new Set(Object.keys(aspects).map(Number));
  let tot = 0, oth = 0; const left = new Map();
  for (const b of JSON.parse(fs.readFileSync(reportAt, 'utf8'))) for (const st of b.stats) {
    if (st.c !== 1) continue;
    for (const [k, v] of Object.entries(st.skills || {})) {
      const id = +k; tot += v;
      let r = bySkill.get(st.unit + ':' + id); if (r === undefined) r = bySkill.get(hs(id) + ':' + id); if (r === undefined) r = id;
      const named = skills.has(r) || (r >= 1000000 && asp.has((r / 100) | 0)) || (r >= 102000000 && r < 102100000) || (r >= 13000000 && r < 13100000) ||
        (r >= 2000000 && r < 3000000 && r % 1000 >= 100 && r % 1000 < 200 && heroes.has((r / 1000) | 0));
      if (!named) { oth += v; left.set(id, (left.get(id) || 0) + v); }
    }
  }
  console.log(`Other on the fixture: ${(oth / tot * 100).toFixed(2)}% of ${tot}`);
  for (const [id, v] of [...left].sort((a, b) => b[1] - a[1]).slice(0, 12)) console.log('  ', id, (v / tot * 100).toFixed(2) + '%');
}
