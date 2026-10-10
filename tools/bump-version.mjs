// Sets the app's version everywhere it is written: app/Program.cs (Version — what the updater compares) and
// app/AssemblyInfo.cs (AssemblyVersion, AssemblyFileVersion, AssemblyInformationalVersion — the file's properties).
// Usage: node tools/bump-version.mjs 1.6.54     (no argument: the patch number + 1)
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.join(path.dirname(fileURLToPath(import.meta.url)), '..');
const prog = path.join(root, 'app', 'Program.cs');
const info = path.join(root, 'app', 'AssemblyInfo.cs');
let p = fs.readFileSync(prog, 'utf8');
const cur = /public const string Version = "(\d+)\.(\d+)\.(\d+)";/.exec(p);
if (!cur) throw new Error('no Version in app/Program.cs');
const next = process.argv[2] || `${cur[1]}.${cur[2]}.${Number(cur[3]) + 1}`;
if (!/^\d+\.\d+\.\d+$/.test(next)) throw new Error('version must be like 1.6.54');
p = p.replace(cur[0], `public const string Version = "${next}";`);
let a = fs.readFileSync(info, 'utf8');
const n = [/AssemblyVersion\("[^"]*"\)/, /AssemblyFileVersion\("[^"]*"\)/, /AssemblyInformationalVersion\("[^"]*"\)/];
if (!n.every((r) => r.test(a))) throw new Error('AssemblyInfo.cs: a version attribute is missing');
a = a
  .replace(n[0], `AssemblyVersion("${next}.0")`)
  .replace(n[1], `AssemblyFileVersion("${next}.0")`)
  .replace(n[2], `AssemblyInformationalVersion("${next}")`);
fs.writeFileSync(prog, p);
fs.writeFileSync(info, a);
console.log(`${cur[1]}.${cur[2]}.${cur[3]} -> ${next}`);

// release notes: NEXT-RELEASE.md (## RU / ## EN bullets) -> ui/changelog.js + tools/.release-notes.json, then the list is emptied
const notesFile = path.join(root, 'NEXT-RELEASE.md');
if (fs.existsSync(notesFile)) {
  const raw = fs.readFileSync(notesFile, 'utf8');
  const crlf = raw.includes('\r\n');
  const lines = raw.replace(/\r\n/g, '\n').split('\n');
  const notes = { ru: [], en: [] };   // strings = bullets, { h } = "### heading"
  const bullets = (a) => a.filter((x) => typeof x === 'string');
  const structured = (a) => a.map((x, i) => typeof x === 'string' ? '• ' + x : (i ? '\n' : '') + x.h).join('\n');
  let sec = null;
  const kept = [];
  for (const line of lines) {
    const h = /^##\s+(RU|EN)\s*$/.exec(line);
    if (h) { sec = h[1].toLowerCase(); kept.push(line); continue; }
    if (sec && /^### /.test(line)) { notes[sec].push({ h: line.slice(4).trim() }); continue; }
    if (sec && /^- /.test(line)) { notes[sec].push(line.slice(2).trim()); continue; }
    if (sec && line.trim() === '') continue;
    kept.push(line);
  }
  if (!bullets(notes.ru).length && !bullets(notes.en).length) {
    console.warn('! NEXT-RELEASE.md has no bullets: ui/changelog.js and tools/.release-notes.json were not touched');
  } else {
    const clFile = path.join(root, 'ui', 'changelog.js');
    const cl = fs.readFileSync(clFile, 'utf8');
    const today = new Date().toISOString().slice(0, 10);
    const entry = `  { v: ${JSON.stringify(next)}, date: ${JSON.stringify(today)}, ru: ${JSON.stringify(bullets(notes.ru))}, en: ${JSON.stringify(bullets(notes.en))} },\n`;
    const marker = /window\.RF_CHANGELOG = \[\r?\n/.exec(cl);
    if (!marker) throw new Error('ui/changelog.js: "window.RF_CHANGELOG = [" not found');
    const at = marker.index + marker[0].length;
    fs.writeFileSync(clFile, cl.slice(0, at) + entry + cl.slice(at));
    const out = { ru: structured(notes.ru), en: structured(notes.en) };
    fs.writeFileSync(path.join(root, 'tools', '.release-notes.json'), JSON.stringify(out, null, 1) + '\n');
    console.log('release notes:', JSON.stringify(out));
    let text = kept.join("\n").replace(/\n*$/, "\n").replace("## RU\n## EN", "## RU\n\n## EN");
    fs.writeFileSync(notesFile, crlf ? text.replace(/\n/g, '\r\n') : text);
  }
}
