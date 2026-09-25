// One time: the key that signs app updates. The private key stays on the release PC (never in git, never online);
// the public key goes into app/Updater.cs. Losing the private key means players must install one new version by hand.
//
//   node tools/update-keygen.mjs <folder for the private key>
import { generateKeyPairSync } from 'node:crypto';
import { existsSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const dir = process.argv[2];
if (!dir) { console.error('usage: node tools/update-keygen.mjs <folder>'); process.exit(2); }
const file = join(dir, 'realmforge-update-key.pem');
if (existsSync(file)) { console.error(`${file} exists: not overwriting the signing key`); process.exit(1); }
const { privateKey, publicKey } = generateKeyPairSync('rsa', { modulusLength: 3072 });
writeFileSync(file, privateKey.export({ type: 'pkcs8', format: 'pem' }), { mode: 0o600 });
const jwk = publicKey.export({ format: 'jwk' });
writeFileSync(join(dir, 'realmforge-update-key.pub.json'), JSON.stringify({ n: jwk.n, e: jwk.e }, null, 1));
console.log(`private key: ${file}\npublic key for app/Updater.cs:\n  n = ${jwk.n}\n  e = ${jwk.e}`);
