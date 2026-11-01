#!/usr/bin/env bash
# Speed test of lab-loop.sh --auto against a SIMULATED device (fake instant app) in a throw-away copy of the scripts, in its own LAB_DIR,
# its own device row 'sim-test' (no account) and its own schema simtest (the request table). Touches no real device, run or request.
#   lab-sim-test.sh [max seconds, default 120]     exit 0 when the 3-scenario run finished 'done' in time and wrote its build-<N>.status (OVERALL PASS)
#   lab-sim-test.sh [max seconds] offline          the fake laptop goes offline for ~14 s in the middle (LAB_OFFLINE_SEC=6): exit 0 when the run
#                                                  waited, restarted its step when the laptop was back, and finished 'done' (data.resumed >= 1)
set -uo pipefail
HERE=$(dirname "$(readlink -f "$0")")
. "$HERE/lab-common.sh"
max=${1:-120}; mode=${2:-normal}
# the real cron pass (lab-loop.sh --auto, real LAB_DIR) would also drive the sim device (it is "online"): hold its lock meanwhile
exec 7>"$LAB_DIR/auto.lock"; flock -w 120 7 || { echo "the real lab-loop is busy, try again"; exit 2; }
W=$(mktemp -d); trap 'kill $SIM 2>/dev/null; rm -rf "$W"' EXIT
cp "$HERE"/*.sh "$W"/; mkdir -p "$W/feed/lab" "$W/rec"
# the loop's request table is simtest.* in the copy
sed -i -E 's/public\.(\$tbl|\$t|\$3)\b/simtest.\1/g' "$W/lab-loop.sh"
sed -i 's/public\.\$tbl/simtest.$tbl/g; s/simtest\.\$\(tbl\|t\|3\)/simtest.$\1/g' "$W/lab-loop.sh"
cat > "$W/lab-seed.sh" <<'EOS'
#!/usr/bin/env bash
. "$(dirname "$0")/lab-common.sh"
[ "${2:-}" = --pick ] && exit 0
q <<< "insert into simtest.item_locates default values returning id"
EOS
q <<< "
  drop schema if exists simtest cascade; create schema simtest;
  create table simtest.item_locates (id uuid primary key default gen_random_uuid(), status text not null default 'pending', created_at timestamptz not null default now(), done_at timestamptz);
  delete from public.lab_devices where name = 'sim-test';
  insert into public.lab_devices (name, enabled) values ('sim-test', true);" >/dev/null
D=$(dev_id sim-test)
q -v d="$D" <<< "insert into public.lab_runs (device_id, scenarios, build, data) values (:'d'::uuid, '{locate,locate_recast,locate_art}', 'a', '{\"target_version\": \"9.9.9.1\", \"want_version\": \"9.9.9.1\"}'::jsonb)" >/dev/null
# the fake device + app: heartbeat and a fresh pilot line every second, every job done at once, every pending request done after ~4 s
( n=0; while :; do
    n=$((n+1)); now=$(date +%s)
    [ "$mode" = offline ] && [ "$n" -gt 12 ] && [ "$n" -lt 28 ] && { sleep 1; continue; }
    q -v d="$D" -v n="$n" -v now="$now" <<< "
      update public.lab_devices set last_seen = now(), status = jsonb_build_object('runner','17','game_running',true,'app_running',true,'app_version','9.9.9.1','ready',true,
        'log_at',:'now'::bigint,'pilot_at',:'now'::bigint,'pilot_line','ui: step '||:'n','busy',false) where id = :'d'::uuid;
      update public.lab_jobs set state = 'done', claimed_at = now(), finished_at = now(),
        result = case action when 'update_app' then '{\"updated\":false,\"version\":\"9.9.9.1\",\"installed\":\"9.9.9.1\"}'::jsonb else '{}'::jsonb end
        where device_id = :'d'::uuid and state = 'queued';
      update simtest.item_locates set status = 'done', done_at = now() where status = 'pending' and created_at < now() - interval '4 seconds';" >/dev/null 2>&1
    sleep 1; done ) & SIM=$!
t0=$(date +%s)
# cron passes: one is enough normally; an offline laptop ends the pass (the run waits), the next one resumes it
while [ $(( $(date +%s) - t0 )) -lt "$max" ]; do
  LAB_DIR="$W/lab" TEST_FEED="$W/feed" LAB_REC_ROOT="$W/rec" LAB_NO_TG=1 LAB_LOGS_WAIT=3 LAB_OFFLINE_SEC=$([ "$mode" = offline ] && echo 6 || echo 180) LAB_ONLY=sim-test bash "$W/lab-loop.sh" --auto 2>&1 | cut -c1-160
  [ "$(q -v d="$D" <<< "select count(*) from public.lab_runs where device_id = :'d'::uuid and state in ('queued', 'running')")" = 0 ] && break
  sleep 3
done
el=$(( $(date +%s) - t0 ))
st=$(q -v d="$D" <<< "select state from public.lab_runs where device_id = :'d'::uuid order by created_at desc limit 1")
q -v d="$D" <<< "select s->>'name' || ' ' || (s->>'ok') || ' ' || (s->>'seconds') || 's' from public.lab_runs r, jsonb_array_elements(r.steps) s where r.device_id = :'d'::uuid"
resumed=$(q -v d="$D" <<< "select coalesce(data->>'resumed', '0') from public.lab_runs where device_id = :'d'::uuid order by created_at desc limit 1")
pend=$(q <<< "select count(*) from simtest.item_locates where status = 'pending'")
sf=$(cat "$W/lab/build-1.status" 2>/dev/null); echo "--- build-1.status"; echo "$sf"
q <<< "drop schema simtest cascade; delete from public.lab_runs where device_id = '$D'::uuid; delete from public.lab_jobs where device_id = '$D'::uuid; delete from public.lab_devices where id = '$D'::uuid" >/dev/null
echo "run state=$st total=${el}s (limit ${max}s), pending requests left: $pend"
if [ "$mode" = offline ]; then
  [ "$st" = done ] && [ "$resumed" -ge 1 ] && grep -q '^OVERALL PASS' <<< "$sf"
else
  [ "$st" = done ] && [ "$el" -le "$max" ] && grep -q '^OVERALL PASS' <<< "$sf"
fi
