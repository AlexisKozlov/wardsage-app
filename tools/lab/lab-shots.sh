#!/usr/bin/env bash
# Downloads the screenshots of a lab run (staging lab_files of its device, from the run's start to its end + 5 min) to a folder.
#   lab-shots.sh <run id|latest> [outdir]     (default outdir: the scratchpad shots/ folder)
set -uo pipefail
. "$(dirname "$0")/lab-common.sh"
run=${1:-}
OUT=${2:-/tmp/claude-1004/-home-dev/bcaf79a7-1542-4a22-9a8d-50219b308752/scratchpad/shots/}
[ -n "$run" ] || { echo "usage: lab-shots.sh <run id|latest> [outdir]" >&2; exit 2; }
if [ "$run" = latest ]; then run=$(q <<< "select id from public.lab_runs where started_at is not null order by started_at desc limit 1"); fi
[[ "$run" =~ ^[0-9a-f-]{36}$ ]] || { echo "bad run id" >&2; exit 2; }
mkdir -p "$OUT"
n=0
while IFS='|' read -r fid fname; do
  [ -n "$fid" ] || continue
  hex=$(q -v f="$fid" <<< "select encode(data, 'hex') from public.lab_files where id = :'f'::uuid") || continue
  ext=${fname##*.}; base="${run:0:8}-${fname%.*}"
  xxd -r -p <<< "$hex" > "$OUT/$base.$ext" && n=$((n+1))
done < <(q -F '|' -v r="$run" <<< "
  select f.id, f.name from public.lab_files f join public.lab_runs r on r.device_id = f.device_id
  where r.id = :'r'::uuid and f.created_at >= r.started_at - interval '1 minute' and f.created_at <= coalesce(r.finished_at, now()) + interval '5 minutes'
  order by f.created_at")
echo "$n file(s) in $OUT (run ${run:0:8})"
ls "$OUT" | grep "^${run:0:8}-" 2>/dev/null
