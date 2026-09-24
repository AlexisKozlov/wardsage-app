// PowerShell syntax check without PowerShell: parses .ps1 files with the tree-sitter PowerShell
// grammar (WebAssembly build, from npm) and reports ERROR / MISSING nodes.
// It is a grammar approximation, not the real PowerShell parser.
//   npm install && node tests/ps-syntax.mjs RealmForge-Extractor.ps1
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import * as TS from 'web-tree-sitter';

const require = createRequire(import.meta.url);
const wasm = require.resolve('tree-sitter-powershell/tree-sitter-powershell.wasm');
await TS.Parser.init();
const lang = await TS.Language.load(wasm);

let bad = 0;
for (const f of process.argv.slice(2)) {
  const src = readFileSync(f, 'utf8').replace(/^﻿/, '');
  const parser = new TS.Parser();
  parser.setLanguage(lang);
  const errs = [];
  (function walk(n) { if (n.type === 'ERROR' || n.isMissing) errs.push(n); for (const c of n.children) walk(c); })(parser.parse(src).rootNode);
  console.log(`${f}: ${errs.length ? errs.length + ' syntax error(s)' : 'no syntax errors'}`);
  for (const e of errs.slice(0, 5)) console.log(`  line ${e.startPosition.row + 1}: ${JSON.stringify(src.slice(e.startIndex, e.startIndex + 80))}`);
  bad += errs.length;
}
process.exit(bad ? 1 : 0);
