// Mock of the RealmForge site's POST /api/sync, for testing the extractor without the real site.
//
//   node tests/mock-server.mjs [port]      (default 3999)
//
// Implements the fixed contract: Bearer token, gzip JSON body, the reply codes below.
// The token decides the scenario, so every reply code can be triggered on purpose:
//   rf_ + 32 x "A"  -> 200 ok            rf_ + 32 x "R" -> 429 (Retry-After: 120)
//   rf_ + 32 x "S"  -> 500               rf_ + 32 x "L" -> 413
//   rf_ + 32 x "M"  -> 415               rf_ + 32 x "P" -> 422 with details
//   rf_ + 32 x "D"  -> 308 redirect      rf_ + 32 x "T" -> answers after 5 s (timeout test)
//   rf_ + 32 x "H"  -> 200 with an HTML page (unexpected reply)
//   anything else   -> 401 invalid_token
// GET /__last returns what the last POST carried (headers, sizes, sha256 of the decoded JSON).

import http from 'node:http';
import zlib from 'node:zlib';
import crypto from 'node:crypto';

const port = Number(process.argv[2] ?? 3999);
const MAX_GZIP = 5 * 1024 * 1024;
const MAX_JSON = 20 * 1024 * 1024;
const tok = (c) => 'rf_' + c.repeat(32);
let last = null;

function send(res, status, body, headers = {}) {
  const text = typeof body === 'string' ? body : JSON.stringify(body);
  res.writeHead(status, { 'Content-Type': typeof body === 'string' ? 'text/html' : 'application/json', 'Cache-Control': 'no-store', ...headers });
  res.end(text);
}

function readBody(req, limit) {
  return new Promise((resolve, reject) => {
    const chunks = []; let size = 0;
    req.on('data', (c) => { size += c.length; if (size > limit) { reject(new Error('too_large')); req.destroy(); } else chunks.push(c); });
    req.on('end', () => resolve(Buffer.concat(chunks)));
    req.on('error', reject);
  });
}

const server = http.createServer(async (req, res) => {
  if (req.method === 'GET' && req.url === '/__last') return send(res, 200, last ?? {});
  if (req.url !== '/api/sync') return send(res, 404, { ok: false, error: 'not_found' });
  if (req.method !== 'POST') return send(res, 405, { ok: false, error: 'method_not_allowed' });

  const auth = req.headers.authorization ?? '';
  const token = auth.startsWith('Bearer ') ? auth.slice(7) : '';
  last = {
    headers: {
      authorization: auth, contentType: req.headers['content-type'], contentEncoding: req.headers['content-encoding'],
      extractor: req.headers['x-rf-extractor'], userAgent: req.headers['user-agent'], contentLength: req.headers['content-length'],
    },
  };

  let raw;
  try { raw = await readBody(req, MAX_GZIP); } catch { return send(res, 413, { ok: false, error: 'too_large' }); }
  last.gzipBytes = raw.length;

  if (token === tok('D')) return send(res, 308, { ok: false }, { Location: 'https://realmforge.example/api/sync' });
  if (token === tok('T')) { await new Promise((r) => setTimeout(r, 5000)); return send(res, 200, { ok: true }); }
  if (!/^rf_[0-9A-Za-z]{32}$/.test(token) || ![tok('A'), tok('R'), tok('S'), tok('L'), tok('M'), tok('P'), tok('H')].includes(token))
    return send(res, 401, { ok: false, error: 'invalid_token' });
  if (token === tok('R')) return send(res, 429, { ok: false, error: 'rate_limited', retryAfter: 120 }, { 'Retry-After': '120' });
  if (token === tok('S')) return send(res, 500, { ok: false, error: 'internal' });
  if (token === tok('L')) return send(res, 413, { ok: false, error: 'too_large' });
  if (token === tok('H')) return send(res, 200, '<!doctype html><title>Not the API</title>');

  if (token === tok('M') || !/^application\/json\b/.test(req.headers['content-type'] ?? '') || req.headers['content-encoding'] !== 'gzip')
    return send(res, 415, { ok: false, error: 'unsupported_media_type' });

  let json, account;
  try { json = zlib.gunzipSync(raw, { maxOutputLength: MAX_JSON }).toString('utf8'); }
  catch { return send(res, 422, { ok: false, error: 'invalid_payload', details: ['body is not valid gzip'] }); }
  last.jsonBytes = Buffer.byteLength(json);
  last.sha256 = crypto.createHash('sha256').update(json, 'utf8').digest('hex');
  try { account = JSON.parse(json); }
  catch (e) { return send(res, 422, { ok: false, error: 'invalid_payload', details: ['invalid JSON: ' + e.message] }); }
  last.meta = account.meta ?? null;

  const details = [];
  if (!Array.isArray(account.heroes)) details.push('heroes: expected an array');
  if (!Array.isArray(account.equipment)) details.push('equipment: expected an array');
  if (typeof account.meta?.extractor !== 'string') details.push('meta.extractor: missing');
  if (token === tok('P')) details.push('heroes[0].iBaseId: unknown hero', 'equipment[3].iItemId: unknown item', 'x', 'y');
  if (details.length) return send(res, 422, { ok: false, error: 'invalid_payload', details });

  const snapshotId = 'snap_' + last.sha256.slice(0, 12);
  const artifacts = account.artifacts && typeof account.artifacts === 'object' ? Object.keys(account.artifacts).length : 0;
  return send(res, 200, {
    ok: true, snapshotId, heroes: account.heroes.length, items: account.equipment.length, artifacts,
    viewUrl: `http://localhost:${port}/app/heroes?snapshot=${snapshotId}`,
  });
});

server.listen(port, '127.0.0.1', () => console.log(`mock /api/sync on http://localhost:${port}`));
