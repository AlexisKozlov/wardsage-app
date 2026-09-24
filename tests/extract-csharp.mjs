// Prints the C# source embedded in RealmForge-Extractor.ps1 (the exact text Add-Type compiles).
//   node tests/extract-csharp.mjs > build/embedded.cs
import { readFileSync } from 'node:fs';

const ps1 = readFileSync(new URL('../RealmForge-Extractor.ps1', import.meta.url), 'utf8');
const m = ps1.match(/\$source = @'\r\n([\s\S]*?)\r\n'@/);
if (!m) { console.error('here-string with the C# source not found'); process.exit(1); }
process.stdout.write(m[1].replace(/\r\n/g, '\n') + '\n');
