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
