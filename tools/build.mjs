// Builds RealmForge-Extractor.ps1 from src/*.cs and the template, and packs dist/RealmForge-Extractor.zip.
//
//   node tools/build.mjs          write RealmForge-Extractor.ps1
//   node tools/build.mjs --check  fail if RealmForge-Extractor.ps1 is out of date
//   node tools/build.mjs --zip    also pack dist/RealmForge-Extractor.zip (needs the `zip` tool)
//
// Windows PowerShell 5.1 reads .ps1 files without a BOM in the ANSI code page, which would break
// the Russian texts, so the script is written as UTF-8 *with* BOM and CRLF line endings.

import { readFileSync, writeFileSync, mkdirSync, rmSync, existsSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');

// Order matters only for readability of the generated script.
export const SOURCES = [
  'MemoryReader.cs', 'MiniJson.cs', 'GameInfo.cs', 'Extractor.cs', 'SyncClient.cs',
  'CodeProtector.cs', 'Config.cs', 'Strings.cs', 'MainForm.cs', 'App.cs',
];

const USING = /^using [A-Za-z0-9_.]+;\s*$/;

export function combineCSharp() {
  const usings = new Set();
  const bodies = [];
  for (const name of SOURCES) {
    const text = readFileSync(join(root, 'src', name), 'utf8').replace(/\r\n/g, '\n');
    const kept = [];
    for (const line of text.split('\n')) {
      if (USING.test(line)) usings.add(line.trim()); else kept.push(line);
    }
    bodies.push(`// ===== src/${name} =====\n` + kept.join('\n').trim() + '\n');
  }
  const sorted = [...usings].sort((a, b) => a.localeCompare(b));
  return sorted.join('\n') + '\n\n' + bodies.join('\n');
}

export function buildScript() {
  const cs = combineCSharp();
  // A line starting with '@ would end the PowerShell here-string early.
  const bad = cs.split('\n').findIndex((l) => l.startsWith("'@"));
  if (bad >= 0) throw new Error(`C# line ${bad + 1} starts with '@ and would break the here-string`);
  const template = readFileSync(join(root, 'src', 'RealmForge-Extractor.template.ps1'), 'utf8').replace(/\r\n/g, '\n');
  if (!template.includes('/*@@CSHARP@@*/')) throw new Error('template placeholder missing');
  const script = template.replace('/*@@CSHARP@@*/', () => cs.trimEnd());
  return '﻿' + script.replace(/\n/g, '\r\n');
}

function toCrlfBom(text) {
  return '﻿' + text.replace(/\r\n/g, '\n').replace(/\n/g, '\r\n');
}

function main() {
  const args = process.argv.slice(2);
  const out = join(root, 'RealmForge-Extractor.ps1');
  const script = buildScript();
  if (args.includes('--check')) {
    const current = existsSync(out) ? readFileSync(out, 'utf8') : '';
    if (current !== script) {
      console.error('RealmForge-Extractor.ps1 is out of date: run `node tools/build.mjs`');
      process.exit(1);
    }
    console.log('RealmForge-Extractor.ps1 is up to date');
    return;
  }
  writeFileSync(out, script, 'utf8');
  console.log(`wrote ${out} (${Buffer.byteLength(script)} bytes)`);

  if (args.includes('--zip')) {
    const stage = join(root, 'dist', 'RealmForge-Extractor');
    rmSync(join(root, 'dist'), { recursive: true, force: true });
    mkdirSync(stage, { recursive: true });
    writeFileSync(join(stage, 'RealmForge-Extractor.ps1'), script, 'utf8');
    // .bat must be CRLF (cmd.exe misreads LF-only batch files); README.txt gets a BOM for old Notepad.
    const bat = readFileSync(join(root, 'Run-RealmForge.bat'), 'utf8').replace(/\r\n/g, '\n').replace(/\n/g, '\r\n');
    writeFileSync(join(stage, 'Run-RealmForge.bat'), bat, 'utf8');
    writeFileSync(join(stage, 'README.txt'), toCrlfBom(readFileSync(join(root, 'README.txt'), 'utf8').replace(/^﻿/, '')), 'utf8');
    execFileSync('zip', ['-X', '-r', '-q', '../RealmForge-Extractor.zip', '.'], { cwd: stage });
    rmSync(stage, { recursive: true, force: true });
    console.log('wrote dist/RealmForge-Extractor.zip');
  }
}

if (process.argv[1] && fileURLToPath(import.meta.url) === process.argv[1]) main();
