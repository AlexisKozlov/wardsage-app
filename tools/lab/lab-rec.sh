#!/usr/bin/env bash
# Downloads the screen recordings of a lab run's failed scenarios (their step's "rec", lab-loop.sh rec_end) from the staging recordings
# folder (owned by deploy, read as deploy) to ~/wardsage/work/recordings-ro/lab/<id>/, replays each with tests/RecReplay and prints the
# start of its summary: the steps timeline's end, the first mismatches, the tags (an automatic halt during the scenario) and lab.json.
#   lab-rec.sh <run id|latest> [--no-replay]
# RecReplay.dll: build/RecReplay.dll of this checkout (tests/run-tests.sh builds it); RECREPLAY_DIR=<dir> for another one.
set -uo pipefail
. "$(dirname "$0")/lab-common.sh"
REPO=$(cd "$(dirname "$0")/../.." && pwd)
OUT=$HOME/wardsage/work/recordings-ro/lab
RB=${RECREPLAY_DIR:-$REPO/build}
run=${1:-}; replay=1; [ "${2:-}" = --no-replay ] && replay=0
[ -n "$run" ] || { echo "usage: lab-rec.sh <run id|latest> [--no-replay]" >&2; exit 2; }
if [ "$run" = latest ]; then run=$(q <<< "select id from public.lab_runs where started_at is not null order by started_at desc limit 1"); fi
[[ "$run" =~ ^[0-9a-f-]{36}$ ]] || { echo "bad run id" >&2; exit 2; }
mkdir -p "$OUT"; chmod 700 "$OUT" 2>/dev/null || true
if [ "$replay" = 1 ] && [ ! -f "$RB/RecReplay.dll" ]; then echo "no $RB/RecReplay.dll (run tests/run-tests.sh first): only downloading"; replay=0; fi
n=0
while IFS='|' read -r name id note; do
  [[ "$id" =~ $LAB_REC_RX ]] || continue
  echo "=== $name: $id${note:+ ($note)}"
  src=$(rec_dir "$id")
  if [ -z "$src" ]; then echo "  not on the server (deleted, or no part arrived)"; continue; fi
  dst=$OUT/$id; mkdir -p "$dst"
  # the part files only (NNN.zip), copied out as deploy, written as the current user
  sudo -n -u deploy sh -c 'cd "$1" && tar -cf - [0-9][0-9][0-9].zip' sh "$src" 2>/dev/null | tar -C "$dst" -xf - || { echo "  copy failed"; continue; }
  echo "  $(ls "$dst" | grep -c '\.zip$') part(s) in $dst"
  n=$((n+1))
  unzip -p "$dst/000.zip" lab.json 2>/dev/null | head -c 400 | sed 's/^/  lab.json: /'; echo
  for z in "$dst"/*.zip; do unzip -Z1 "$z" 2>/dev/null | grep -E '^tag_[0-9]{3}\.json$' | while read -r t; do unzip -p "$z" "$t" | head -c 300 | sed 's/^/  tag: /'; echo; done; done
  [ "$replay" = 1 ] || continue
  ro=$dst/replay; rm -rf "$ro"
  if dotnet exec --runtimeconfig "$RB/RecReplay.runtimeconfig.json" "$RB/RecReplay.dll" "$dst" "$ro" >/dev/null 2>&1 && [ -s "$ro/summary.txt" ]; then
    s=$ro/summary.txt
    grep -E '^(recording|note|layout\.json|frames without)' "$s" | sed 's/^/  /'
    echo "  steps (last 8):"; sed -n '/^steps timeline/,/^$/p' "$s" | sed '1d;/^$/d' | tail -8
    echo "  first mismatches:"; sed -n '/^mismatches recorded vs replayed:/,/^$/p' "$s" | sed '1d;/^$/d' | head -10
    grep -E '^mismatches: ' "$s" | sed 's/^/  /'
    echo "  full: $s"
  else
    echo "  RecReplay could not read it"
  fi
done < <(q -F '|' -v r="$run" <<< "select s->>'name', s->>'rec', coalesce(s->>'note', '') from public.lab_runs r, jsonb_array_elements(r.steps) s where r.id = :'r'::uuid and s ? 'rec'")
echo "$n recording(s) of run ${run:0:8} in $OUT"
