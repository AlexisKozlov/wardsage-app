// Publishes a new version of Wardsage.exe to the site: signs it, builds the installer and writes what the site
// serves. Run after tools/build-app.sh, then commit and push the site repo.
//
//   node tools/release-app.mjs --key <private key .pem> --web <realmforge-web checkout> [--ru "что нового" --en "what's new"]
//   node tools/release-app.mjs --test --key <pem> --web <checkout>     the test build (tools/build-app.sh --test) -> <web>/public/downloads/test/
//
// Writes into <web>/public/downloads/:
//   update/Wardsage-<version>.exe     the exe the installed apps update to (older ones, also RealmForge-<v>.exe, are removed)
//   latest.json                       {version, url, size, sha256, sig, notes} — what app/Updater.cs checks
//   Wardsage-Setup.exe                the installer for new players (Inno Setup, installer/Wardsage.iss)
//   RealmForge-Setup.exe              the same installer under the old name (old links)
//   Wardsage.zip, RealmForge.zip      the exe + README.txt, for old links to the archive
// With --test the same goes to downloads/test/ with the names Wardsage-Test-<version>.exe and Wardsage-Test-Setup.exe
// (no old-name copies, no zip).
// The signature is RSA PKCS#1 v1.5 over SHA-256 of the whole exe; app/Updater.cs holds the public key.
import { createHash, createPrivateKey, createPublicKey, sign, verify } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const arg = (n) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : undefined; };
const test = process.argv.includes('--test');
const keyFile = arg('--key'), web = arg('--web');
if (!keyFile || !web) { console.error('usage: node tools/release-app.mjs [--test] --key <pem> --web <realmforge-web> [--ru text] [--en text] [--iscc path]'); process.exit(2); }

const product = test ? 'Wardsage-Test' : 'Wardsage';
const version = /Version = "([\d.]+)"/.exec(readFileSync(join(root, 'app', 'Program.cs'), 'utf8'))[1];
const exe = readFileSync(join(root, 'dist', `${product}.exe`));
const key = createPrivateKey(readFileSync(keyFile));

// the key must be the one the app trusts
const n = /KeyN = "([^"]+)"/.exec(readFileSync(join(root, 'app', 'Updater.cs'), 'utf8'))[1];
if (createPublicKey(key).export({ format: 'jwk' }).n !== n) { console.error('this key is not the one in app/Updater.cs'); process.exit(1); }

// notes: --ru / --en, else what tools/bump-version.mjs took from NEXT-RELEASE.md
const notesFile = join(root, 'tools', '.release-notes.json');
const saved = existsSync(notesFile) ? JSON.parse(readFileSync(notesFile, 'utf8')) : {};

const sig = sign('sha256', exe, key);
if (!verify('sha256', exe, createPublicKey(key), sig)) throw new Error('signature does not verify');
const sha256 = createHash('sha256').update(exe).digest('hex');

const out = test ? resolve(web, 'public', 'downloads', 'test') : resolve(web, 'public', 'downloads');
const upd = join(out, 'update');
mkdirSync(upd, { recursive: true });
// older builds go (the old product name's files too: the normal channel was published as RealmForge-<v>.exe)
const oldName = test ? /^Wardsage-Test-[\d.]+\.exe$/ : /^(RealmForge|Wardsage)-[\d.]+\.exe$/;
for (const f of readdirSync(upd)) if (oldName.test(f)) rmSync(join(upd, f));
const name = `${product}-${version}.exe`;
writeFileSync(join(upd, name), exe);
const manifest = {
  version,
  url: `/downloads/${test ? 'test/' : ''}update/${name}`,
  size: exe.length,
  sha256,
  sig: sig.toString('base64'),
  notes: { ru: arg('--ru') ?? saved.ru ?? '', en: arg('--en') ?? saved.en ?? '' },
};
writeFileSync(join(out, 'latest.json'), JSON.stringify(manifest, null, 1) + '\n');

// installer
const iscc = arg('--iscc') ?? 'D:/RealmForge/work/tools/InnoSetup/ISCC.exe';
if (existsSync(iscc)) {
  const setup = `${product}-Setup.exe`;
  execFileSync(iscc, ['/Q', join(root, 'installer', 'Wardsage.iss'), `/DAppVersion=${version}`, ...(test ? ['/DTestBuild'] : [])], { stdio: 'inherit' });
  copyFileSync(join(root, 'dist', setup), join(out, setup));
  if (!test) copyFileSync(join(root, 'dist', setup), join(out, 'RealmForge-Setup.exe'));   // the old link keeps working
} else console.warn(`! ${iscc} not found: the installer was not rebuilt`);

// the archive (the old link keeps working too; Windows' own zip)
if (!test && process.platform === 'win32') {
  const zip = join(out, 'Wardsage.zip');
  rmSync(zip, { force: true });
  execFileSync('powershell', ['-NoProfile', '-Command',
    `Compress-Archive -Path '${join(root, 'dist', 'Wardsage.exe')}','${join(root, 'app', 'README.txt')}' -DestinationPath '${zip}'`], { stdio: 'inherit' });
  copyFileSync(zip, join(out, 'RealmForge.zip'));
}
console.log(`${product} ${version}: ${exe.length} bytes, sha256 ${sha256.slice(0, 16)}…, signed; files in ${out}`);
