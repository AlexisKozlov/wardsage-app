// Publishes a new version of RealmForge.exe to the site: signs it, builds the installer and writes what the site
// serves. Run after tools/build-app.sh, then commit and push the site repo.
//
//   node tools/release-app.mjs --key <private key .pem> --web <realmforge-web checkout> --ru "что нового" --en "what's new"
//
// Writes into <web>/public/downloads/:
//   update/RealmForge-<version>.exe   the exe the installed apps update to (older ones are removed)
//   latest.json                       {version, url, size, sha256, sig, notes} — what app/Updater.cs checks
//   RealmForge-Setup.exe              the installer for new players (Inno Setup, installer/RealmForge.iss)
//   RealmForge.zip                    the exe + README.txt, for old links to the archive
// The signature is RSA PKCS#1 v1.5 over SHA-256 of the whole exe; app/Updater.cs holds the public key.
import { createHash, createPrivateKey, createPublicKey, sign, verify } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const arg = (n) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : undefined; };
const keyFile = arg('--key'), web = arg('--web');
if (!keyFile || !web) { console.error('usage: node tools/release-app.mjs --key <pem> --web <realmforge-web> [--ru text] [--en text] [--iscc path]'); process.exit(2); }

const version = /Version = "([\d.]+)"/.exec(readFileSync(join(root, 'app', 'Program.cs'), 'utf8'))[1];
const exe = readFileSync(join(root, 'dist', 'RealmForge.exe'));
const key = createPrivateKey(readFileSync(keyFile));

// the key must be the one the app trusts
const n = /KeyN = "([^"]+)"/.exec(readFileSync(join(root, 'app', 'Updater.cs'), 'utf8'))[1];
if (createPublicKey(key).export({ format: 'jwk' }).n !== n) { console.error('this key is not the one in app/Updater.cs'); process.exit(1); }

const sig = sign('sha256', exe, key);
if (!verify('sha256', exe, createPublicKey(key), sig)) throw new Error('signature does not verify');
const sha256 = createHash('sha256').update(exe).digest('hex');

const out = resolve(web, 'public', 'downloads');
const upd = join(out, 'update');
mkdirSync(upd, { recursive: true });
for (const f of readdirSync(upd)) if (/^RealmForge-[\d.]+\.exe$/.test(f)) rmSync(join(upd, f));
const name = `RealmForge-${version}.exe`;
writeFileSync(join(upd, name), exe);
const manifest = {
  version,
  url: `/downloads/update/${name}`,
  size: exe.length,
  sha256,
  sig: sig.toString('base64'),
  notes: { ru: arg('--ru') ?? '', en: arg('--en') ?? '' },
};
writeFileSync(join(out, 'latest.json'), JSON.stringify(manifest, null, 1) + '\n');

// installer
const iscc = arg('--iscc') ?? 'D:/RealmForge/work/tools/InnoSetup/ISCC.exe';
if (existsSync(iscc)) {
  execFileSync(iscc, ['/Q', join(root, 'installer', 'RealmForge.iss'), `/DAppVersion=${version}`], { stdio: 'inherit' });
  copyFileSync(join(root, 'dist', 'RealmForge-Setup.exe'), join(out, 'RealmForge-Setup.exe'));
} else console.warn(`! ${iscc} not found: the installer was not rebuilt`);

// the old archive link keeps working (Windows' own zip)
const zip = join(out, 'RealmForge.zip');
if (process.platform === 'win32') {
  rmSync(zip, { force: true });
  execFileSync('powershell', ['-NoProfile', '-Command',
    `Compress-Archive -Path '${join(root, 'dist', 'RealmForge.exe')}','${join(root, 'app', 'README.txt')}' -DestinationPath '${zip}'`], { stdio: 'inherit' });
}
console.log(`RealmForge ${version}: ${exe.length} bytes, sha256 ${sha256.slice(0, 16)}…, signed; files in ${out}`);
