#!/usr/bin/env bash
# The frame audit of a lab run (src/FrameAudit.cs, app/RecCapture.cs FrameAuditSink): the test app shoots every hint frame once it has stood
# still (once per scenario, step and target), with a sidecar JSON (step, target, frame, live and layout rects, sprite, source, flags), into a
# lab recording of its own named after the run (lab-<date>-<time>-<the run's first 6 hex>). This downloads those shots (staging recordings
# folder, read as deploy) to $LAB_DIR/frame-audit-<run8>/ and builds a contact sheet: each frame cropped with its rects drawn (frame cyan, live
# green, layout magenta, hint plates yellow) beside the whole client, its key and flags, the flagged ones first.
#   frame-audit.sh <run id|latest> [out.png]       the sheet (default $LAB_DIR/frame-audit-<run8>.png); last line: «рамки: N проверено, K под подозрением»
#   frame-audit.sh <run id> --summary              "N K" (shots, flagged) only
#   frame-audit.sh <run id> --count <scenario>     "N K S" for one scenario (shots, flagged, distinct steps) and the first flag (lab-loop.sh)
# Uses sharp from ~/wardsage/wardsage-web/node_modules (no new dependency) and jq.
set -uo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
. "$HERE/lab-common.sh"
SHARP_FROM=${SHARP_FROM:-$HOME/wardsage/wardsage-web/package.json}

run=${1:-}; mode=sheet; scn=""; out=""
case "${2:-}" in --summary) mode=summary ;; --count) mode=count; scn=${3:-} ;; "") ;; *) out=$2 ;; esac
if [ "$run" = latest ]; then run=$(q <<< "select id from public.lab_runs where started_at is not null order by started_at desc limit 1"); fi
[[ "$run" =~ ^[0-9a-f-]{36}$ ]] || { echo "usage: frame-audit.sh <run id|latest> [out.png | --summary | --count <scenario>]" >&2; exit 2; }
[ "$mode" != count ] || [[ "$scn" =~ ^[a-z0-9_]{1,40}$ ]] || { echo "bad scenario" >&2; exit 2; }
dir=${FA_DIR:-$LAB_DIR/frame-audit-${run:0:8}}
[ -n "$out" ] || out=$LAB_DIR/frame-audit-${run:0:8}.png
mkdir -p "$dir/shots"; chmod 700 "$dir" 2>/dev/null || true

# the run's audit recordings; only the parts not fetched yet are copied (as deploy, written as the current user)
if [ -z "${FA_LOCAL:-}" ]; then
  while read -r src; do
    [ -n "$src" ] || continue
    id=$(basename "$src"); [[ "$id" =~ $LAB_REC_RX ]] || continue
    mkdir -p "$dir/$id"
    for z in $(sudo -n -u deploy ls "$src" 2>/dev/null | grep -E '^[0-9]{3}\.zip$'); do
      [ -s "$dir/$id/$z" ] && continue
      sudo -n -u deploy cat "$src/$z" > "$dir/$id/$z.tmp" 2>/dev/null && mv "$dir/$id/$z.tmp" "$dir/$id/$z" || rm -f "$dir/$id/$z.tmp"
      # a part of this run only (audit.json names it): its shot and sidecar
      if [ "$(unzip -p "$dir/$id/$z" audit.json 2>/dev/null | jq -r '.run // ""' 2>/dev/null)" = "$run" ]; then
        unzip -o -q -j "$dir/$id/$z" 'audit_*' -d "$dir/shots" 2>/dev/null || true
      fi
    done
  done < <(sudo -n -u deploy find "$LAB_REC_ROOT" -mindepth 3 -maxdepth 3 -type d -name "lab-*-${run:0:6}" 2>/dev/null)
fi

all=$(cat "$dir"/shots/audit_*.json 2>/dev/null | jq -cs --arg r "$run" '[.[] | select(.run == $r)]' 2>/dev/null); [ -n "$all" ] || all='[]'
case "$mode" in
  summary) jq -r '"\(length) \([.[] | select(.flags | length > 0)] | length)"' <<< "$all"; exit 0 ;;
  count) jq -r --arg s "$scn" '[.[] | select(.scenario == $s)] | "\(length) \([.[] | select(.flags | length > 0)] | length) \([.[].step] | unique | length) \(([.[] | select(.flags | length > 0)][0] // {}) | if .flags then "\(.step) / \(.target): \(.flags[0])" else "" end)"' <<< "$all"; exit 0 ;;
esac

n=$(jq length <<< "$all"); k=$(jq '[.[] | select(.flags | length > 0)] | length' <<< "$all")
if [ "$n" = 0 ]; then echo "рамки: 0 проверено (no audit shots of run ${run:0:8})"; exit 0; fi
jq -r '.[] | select(.flags | length > 0) | "FLAG \(.scenario): \(.step) / \(.target): \(.flags | join("; "))"' <<< "$all" | head -40

printf '%s' "$all" > "$dir/shots.json"
node --input-type=module - "$dir/shots" "$out" "$SHARP_FROM" "$dir/shots.json" <<'EOF'
import fs from 'fs';
import path from 'path';
import { createRequire } from 'module';
const [dir, out, from, jf] = process.argv.slice(2);
const sharp = createRequire(from)('sharp');
const shots = JSON.parse(fs.readFileSync(jf, 'utf8')).filter((s) => s.image && fs.existsSync(path.join(dir, s.image)));
shots.sort((a, b) => (b.flags.length > 0) - (a.flags.length > 0) || a.scenario.localeCompare(b.scenario) || a.at - b.at);
const TH = 220, LW = 420, PAD = 8;
const esc = (t) => String(t).replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
const rect = (r, k, ox, oy, z, color, dash) => r ? `<rect x="${((r[0] * k - ox) * z).toFixed(1)}" y="${((r[1] * k - oy) * z).toFixed(1)}" width="${(r[2] * k * z).toFixed(1)}" height="${(r[3] * k * z).toFixed(1)}" fill="none" stroke="${color}" stroke-width="2"${dash ? ' stroke-dasharray="5,4"' : ''}/>` : '';
const rows = [];
for (const s of shots) {
  const f = path.join(dir, s.image), { width: iw, height: ih } = await sharp(f).metadata();
  const k = s.scale > 0 ? s.scale : iw / s.client[0];
  const marks = (ox, oy, z) => rect(s.layout, k, ox, oy, z, '#f3f', true) + rect(s.live, k, ox, oy, z, '#3f3', true) + (s.plates || []).map((p) => rect(p, k, ox, oy, z, '#fd3', false)).join('') + rect(s.frame, k, ox, oy, z, '#3ff', false);
  // the crop: the frame with a margin of its size (at least 60 client px)
  const fr = s.frame, m = Math.max(60, Math.max(fr[2], fr[3]));
  let l = Math.round((fr[0] - m) * k), t = Math.round((fr[1] - m) * k), w = Math.round((fr[2] + 2 * m) * k), h = Math.round((fr[3] + 2 * m) * k);
  l = Math.max(0, Math.min(iw - 2, l)); t = Math.max(0, Math.min(ih - 2, t)); w = Math.max(2, Math.min(iw - l, w)); h = Math.max(2, Math.min(ih - t, h));
  const z = TH / h, cw = Math.max(1, Math.round(w * z));
  const crop = await sharp(f).extract({ left: l, top: t, width: w, height: h }).resize(cw, TH)
    .composite([{ input: Buffer.from(`<svg width="${cw}" height="${TH}">${marks(l, t, z)}</svg>`), top: 0, left: 0 }]).png().toBuffer();
  const zf = TH / ih, fw = Math.max(1, Math.round(iw * zf));
  const whole = await sharp(f).resize(fw, TH)
    .composite([{ input: Buffer.from(`<svg width="${fw}" height="${TH}">${marks(0, 0, zf)}</svg>`), top: 0, left: 0 }]).png().toBuffer();
  rows.push({ s, tiles: [{ buf: crop, w: cw }, { buf: whole, w: fw }] });
}
if (!rows.length) { console.log('no shot pictures in ' + dir); process.exit(0); }
const W = LW + Math.max(...rows.map((r) => r.tiles.reduce((a, t) => a + t.w + PAD, 0))) + PAD, H = rows.length * (TH + PAD) + PAD;
const comps = [];
rows.forEach((r, j) => {
  const y = PAD + j * (TH + PAD), s = r.s, d = s.disagree;
  const txt = [[s.scenario + ': ' + s.step, '#eee'], ['target ' + s.target + ' (' + s.source + ')', '#eee'], ['widget ' + (s.widget || '-'), '#aaa'], ['sprite ' + (s.sprite || '-') + ' shape ' + (s.shape || '-'), '#aaa'],
    ['frame ' + s.frame.join(','), '#3ff'], ['live ' + (s.live ? s.live.join(',') : '-'), '#3f3'], ['layout ' + (s.layout ? s.layout.join(',') : '-') + (d ? '  d ' + d.join(',') : ''), '#f3f']]
    .concat(s.flags.length ? s.flags.map((x) => ['FLAG ' + x, '#f55']) : [['ok', '#5f5']]);
  const svg = `<svg width="${LW}" height="${TH}"><rect width="${LW}" height="${TH}" fill="${s.flags.length ? '#3a1a1a' : '#1f2a1f'}"/><style>text{font:13px sans-serif}</style>${txt.slice(0, 11).map(([t, c], i) => `<text x="6" y="${18 + i * 18}" fill="${c}">${esc(t.slice(0, 56))}</text>`).join('')}</svg>`;
  comps.push({ input: Buffer.from(svg), top: y, left: 0 });
  let x = LW + PAD;
  for (const t of r.tiles) { comps.push({ input: t.buf, top: y, left: x }); x += t.w + PAD; }
});
await sharp({ create: { width: W, height: H, channels: 3, background: '#15151c' } }).composite(comps).png().toFile(out);
console.log('sheet: ' + out + ' (' + rows.length + ' shots)');
EOF
echo "рамки: $n проверено, $k под подозрением"
