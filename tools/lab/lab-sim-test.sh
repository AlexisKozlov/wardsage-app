#!/usr/bin/env bash
# Speed test of lab-loop.sh --auto against a SIMULATED device (fake instant app) in a throw-away copy of the scripts, in its own LAB_DIR,
# its own device row 'sim-test' (no account) and its own schema simtest (the request table). Touches no real device, run or request.
#   lab-sim-test.sh [max seconds, default 120]     exit 0 when the 3-scenario run finished 'done' in time and wrote its build-<N>.status (OVERALL PASS)
#   lab-sim-test.sh [max seconds] latemark         the fake runner answers each mark job 6 s late: exit 0 when no request was seeded before its mark was done
#   lab-sim-test.sh [max seconds] prep             the lab setup flow: 4 extra scenarios (equip_hero_filter_open, locate_from_bulk_sale, locate_recast_item_filter,
#                                                  equip_toggle_auto_late); the fake app acks "city" / "prep <name>" (prepok; item_filter answers prepfail). Exit 0 when a city job
#                                                  came before every scenario, the preps ran in order (hero_filter, start_bulk_sale, item_filter), the failed prep is a WARN step with no
#                                                  request seeded for it, the plan with auto click off waited and the "settings" command followed, and the run finished 'done'
#   lab-sim-test.sh [max seconds] noack            like prep, but the fake app never answers "city" (an old app): the setup is switched off for the run, the prep scenarios are skipped
#                                                  with a WARN, the others run
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
case "${2:-}" in equip_*) t=equip_plans ;; sell_*) t=sell_plans ;; *) t=item_locates ;; esac
q <<< "insert into simtest.$t (kind) values ('${2:-}') returning id"
EOS
q <<< "
  drop schema if exists simtest cascade; create schema simtest;
  create table simtest.item_locates (id uuid primary key default gen_random_uuid(), status text not null default 'pending', created_at timestamptz not null default now(), done_at timestamptz, kind text);
  create table simtest.equip_plans (like simtest.item_locates including defaults);
  create table simtest.sell_plans (like simtest.item_locates including defaults);
  delete from public.lab_devices where name = 'sim-test';
  insert into public.lab_devices (name, enabled) values ('sim-test', true);" >/dev/null
D=$(dev_id sim-test)
SCN='{locate,locate_recast,locate_art}'
case "$mode" in prep|noack) SCN='{equip_hero_filter_open,locate_from_bulk_sale,locate_recast_item_filter,equip_toggle_auto_late}' ;; esac
q -v d="$D" -v sc="$SCN" <<< "insert into public.lab_runs (device_id, scenarios, build, data) values (:'d'::uuid, :'sc'::text[], 'a', '{\"target_version\": \"9.9.9.1\", \"want_version\": \"9.9.9.1\"}'::jsonb)" >/dev/null
# the fake device + app: heartbeat and a fresh pilot line every second, every job done at once, every pending request done after ~4 s
( n=0; while :; do
    n=$((n+1)); now=$(date +%s)
    [ "$mode" = offline ] && [ "$n" -gt 12 ] && [ "$n" -lt 28 ] && { sleep 1; continue; }
    q -v d="$D" -v n="$n" -v now="$now" -v late="$([ "$mode" = latemark ] && echo true || echo false)" -v noack="$([ "$mode" = noack ] && echo true || echo false)" <<< "
      update public.lab_devices set last_seen = now(), status = jsonb_build_object('runner','17','game_running',true,'app_running',true,'app_version','9.9.9.1','ready',true,
        'log_at',:'now'::bigint,'pilot_at',:'now'::bigint,'pilot_line','ui: step '||:'n','busy',false) where id = :'d'::uuid;
      update public.lab_jobs set
        state = case when action = 'rec' and args->>'cmd' in ('city', 'prep') and (:'noack'::boolean or args->>'name' = 'item_filter') then 'failed' else 'done' end,
        claimed_at = now(), finished_at = now(),
        result = case when action = 'update_app' then '{\"updated\":false,\"version\":\"9.9.9.1\",\"installed\":\"9.9.9.1\"}'::jsonb
                      when action = 'rec' and args->>'cmd' in ('city', 'prep') and :'noack'::boolean then '{\"error\":\"no_ack\",\"cmd\":\"city\"}'::jsonb
                      when action = 'rec' and args->>'name' = 'item_filter' then '{\"state\":\"prepfail\",\"name\":\"item_filter\",\"why\":\"sim: the gear filter button is not found\"}'::jsonb
                      when action = 'rec' and args->>'cmd' = 'prep' then jsonb_build_object('state', 'prepok', 'name', args->>'name', 'why', 'sim ok')
                      when action = 'rec' and args->>'cmd' = 'city' then '{\"state\":\"city\",\"cmd\":\"city\"}'::jsonb
                      else '{}'::jsonb end
        where device_id = :'d'::uuid and state = 'queued' and not (:'late'::boolean and action = 'mark' and created_at > now() - interval '6 seconds');
      update simtest.item_locates set status = 'done', done_at = now() where status = 'pending' and created_at < now() - interval '4 seconds';
      update simtest.sell_plans set status = 'done', done_at = now() where status = 'pending' and created_at < now() - interval '4 seconds';
      -- an equip plan of the auto-click-off scenario waits for the \"settings\" command (the page picks it up once auto click is on)
      update simtest.equip_plans p set status = 'done', done_at = now() where status = 'pending' and created_at < now() - interval '4 seconds'
        and ((select r.scenarios[(r.data->>'scn_idx')::int + 1] from public.lab_runs r where r.device_id = :'d'::uuid and r.state = 'running' limit 1) is distinct from 'equip_toggle_auto_late'
             or exists (select 1 from public.lab_jobs j where j.device_id = :'d'::uuid and j.action = 'rec' and j.args->>'cmd' = 'settings' and j.created_at > p.created_at));" >/dev/null 2>&1
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
# latemark: the fake runner answers every mark job 6 s late; no request may be seeded before its mark is done (a late mark wiped a fast app's first lines)
lm=$(q -v d="$D" <<< "select count(*) from simtest.item_locates r where exists (select 1 from public.lab_jobs j where j.device_id = :'d'::uuid and j.action = 'mark' and j.created_at < r.created_at
  and j.created_at = (select max(j2.created_at) from public.lab_jobs j2 where j2.device_id = j.device_id and j2.action = 'mark' and j2.created_at < r.created_at) and (j.finished_at is null or j.finished_at > r.created_at))" 2>/dev/null)
mn=$(q -v d="$D" <<< "select count(*) from public.lab_jobs where device_id = :'d'::uuid and action = 'mark'")
sf=$(cat "$W/lab/build-1.status" 2>/dev/null); echo "--- build-1.status"; echo "$sf"
if [ "$mode" = prep ] || [ "$mode" = noack ]; then
  echo "--- jobs"
  JOBS=$(q -v d="$D" <<< "select action || ' ' || coalesce(args->>'cmd', '') || ' ' || coalesce(args->>'name', '') || ' ' || state from public.lab_jobs where device_id = :'d'::uuid and action = 'rec' and args->>'cmd' in ('city', 'prep', 'settings') order by created_at, id")
  echo "$JOBS"
  echo "--- steps"; STEPS=$(q -v d="$D" <<< "select s->>'name' || ' ok=' || (s->>'ok') || ' warn=' || coalesce(s->>'warn', '') || ' ' || coalesce(s->>'note', '') from public.lab_runs r, jsonb_array_elements(r.steps) s where r.device_id = :'d'::uuid"); echo "$STEPS"
  SEEDED=$(q <<< "select string_agg(kind, ',' order by created_at) from (select kind, created_at from simtest.equip_plans union all select kind, created_at from simtest.item_locates union all select kind, created_at from simtest.sell_plans) x"); echo "seeded: $SEEDED"
fi
q <<< "drop schema simtest cascade; delete from public.lab_runs where device_id = '$D'::uuid; delete from public.lab_jobs where device_id = '$D'::uuid; delete from public.lab_devices where id = '$D'::uuid" >/dev/null
echo "run state=$st total=${el}s (limit ${max}s), pending requests left: $pend"
if [ "$mode" = latemark ]; then
  echo "marks: $mn, requests seeded before their mark was done: $lm"
  [ "$st" = done ] && [ "$mn" -ge 3 ] && [ "${lm:-1}" = 0 ] && grep -q '^OVERALL PASS' <<< "$sf"
elif [ "$mode" = prep ]; then
  ncity=$(grep -c '^rec city ' <<< "$JOBS"); preps=$(grep '^rec prep ' <<< "$JOBS" | awk '{print $3}' | paste -sd,)
  nset=$(grep -c '^rec settings ' <<< "$JOBS")
  echo "city jobs: $ncity, preps: $preps, settings jobs: $nset"
  [ "$st" = done ] && [ "$ncity" = 4 ] && [ "$preps" = hero_filter,start_bulk_sale,item_filter ] && [ "$nset" -ge 2 ] \
    && grep -q '^equip_hero_filter_open ok=true' <<< "$STEPS" && grep -q '^locate_from_bulk_sale ok=true' <<< "$STEPS" \
    && grep -q '^locate_recast_item_filter ok=true warn=true .*item_filter' <<< "$STEPS" && grep -q '^equip_toggle_auto_late ok=true' <<< "$STEPS" \
    && [ "$SEEDED" = equip_city,locate,equip_city ]
elif [ "$mode" = noack ]; then
  ncity=$(grep -c '^rec city ' <<< "$JOBS"); npr=$(grep -c '^rec prep ' <<< "$JOBS")
  echo "city jobs: $ncity (the first unanswered switches the setup off), prep jobs: $npr"
  [ "$st" = done ] && [ "$ncity" = 1 ] && [ "$npr" = 0 ] && [ "$(grep -c 'warn=true' <<< "$STEPS")" = 3 ] && grep -q '^equip_toggle_auto_late ok=true' <<< "$STEPS" && [ "$SEEDED" = equip_city ]
elif [ "$mode" = offline ]; then
  [ "$st" = done ] && [ "$resumed" -ge 1 ] && grep -q '^OVERALL PASS' <<< "$sf"
else
  [ "$st" = done ] && [ "$el" -le "$max" ] && grep -q '^OVERALL PASS' <<< "$sf"
fi
