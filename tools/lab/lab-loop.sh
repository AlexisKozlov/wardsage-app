#!/usr/bin/env bash
# Drives one queued test-lab run of a device (staging), in the FOREGROUND. Started by hand, never by cron or a service.
#   lab-loop.sh --until-done <device> [--timeout MIN]      (default 60 min)
# Exits 0 when the run finished (then the Telegram message and the report file are done), 1 on timeout / failure.
#
# Flow (state kept in lab_runs.phase, so a restart of this script resumes; each phase is idempotent):
#   wait_online -> update_app -> restart_app -> wait_ready -> [seed -> wait done]* -> wait logs
#   -> upload_logs -> screenshot -> finish -> report
# The runner on the laptop only executes the whitelisted jobs queued here; it never clicks and never starts the game.
set -uo pipefail
. "$(dirname "$0")/lab-common.sh"
HERE=$(dirname "$(readlink -f "$0")")

dev=""; timeout_min=60; tick=10
while [ $# -gt 0 ]; do
  case "$1" in
    --until-done) dev=${2:-}; shift 2 ;;
    --timeout) timeout_min=${2:-60}; shift 2 ;;
    --tick) tick=${2:-10}; shift 2 ;;
    *) echo "usage: lab-loop.sh --until-done <device> [--timeout MIN]" >&2; exit 2 ;;
  esac
done
[ -n "$dev" ] || { echo "usage: lab-loop.sh --until-done <device> [--timeout MIN]" >&2; exit 2; }
[[ "$timeout_min" =~ ^[0-9]{1,3}$ ]] && [[ "$tick" =~ ^[0-9]{1,3}$ ]] || { echo "bad number" >&2; exit 2; }
DEV=$(dev_id "$dev") || exit 2

exec 9>"$LAB_DIR/loop-$dev.lock"
flock -n 9 || { echo "lab-loop for '$dev' is already running" >&2; exit 1; }

say() { echo "$(date -u +%H:%M:%S) $*"; }
now() { date +%s; }

# ---- run row helpers (RUN = run id)
RUN=$(q -v d="$DEV" <<< "select id from public.lab_runs where device_id = :'d'::uuid and state in ('queued', 'running') order by created_at limit 1")
[ -n "$RUN" ] || { echo "no queued run for '$dev' (lab-request.sh first)"; exit 0; }
rget()   { q -v r="$RUN" <<< "select $1 from public.lab_runs where id = :'r'::uuid"; }
dget()   { q -v r="$RUN" -v k="$1" <<< "select coalesce(data->>:'k', '') from public.lab_runs where id = :'r'::uuid"; }
dset()   { q -v r="$RUN" -v k="$1" -v v="$2" <<< "update public.lab_runs set data = data || jsonb_build_object(:'k', :'v'::text) where id = :'r'::uuid"; }
phase()  { q -v r="$RUN" -v p="$1" <<< "update public.lab_runs set phase = :'p', data = data || jsonb_build_object('phase_at', extract(epoch from now())::bigint::text) where id = :'r'::uuid"; say "phase: $1"; }
step()   { q -v r="$RUN" -v n="$1" -v ok="$2" -v s="$3" -v note="${4:-}" <<< "update public.lab_runs set steps = steps || jsonb_build_array(jsonb_build_object('name', :'n', 'ok', :'ok'::boolean, 'seconds', :'s'::numeric, 'note', :'note')) where id = :'r'::uuid"; say "step $1 ok=$2 ${3}s ${4:-}"; }
phase_at_epoch() { dget phase_at; }
phase_age() { echo $(( $(now) - $(dget phase_at) )); }
mkjob()  { q -v d="$DEV" -v r="$RUN" -v a="$1" -v args="${2:-{\}}" <<< "insert into public.lab_jobs (device_id, run_id, action, args, expires_at) values (:'d'::uuid, :'r'::uuid, :'a', :'args'::jsonb, now() + interval '90 minutes') returning id"; }
jstate() { q -v j="$1" <<< "select state from public.lab_jobs where id = :'j'::uuid"; }
jsecs()  { q -v j="$1" <<< "select coalesce(round(extract(epoch from finished_at - coalesce(claimed_at, created_at)))::int, 0) from public.lab_jobs where id = :'j'::uuid"; }
jres()   { q -v j="$1" -v k="$2" <<< "select coalesce(result->>:'k', '') from public.lab_jobs where id = :'j'::uuid"; }
dstat()  { q -v d="$DEV" <<< "select $1 from public.lab_devices where id = :'d'::uuid"; }

# a job phase: waits for the job in data.job; $1 = step name, $2 = next phase, $3 = max seconds. Sets JOBOK=1/0 when finished.
JOBOK=""
wait_job() {
  local j st; j=$(dget job); st=$(jstate "$j")
  case "$st" in
    done)   JOBOK=1; return 0 ;;
    failed) JOBOK=0; return 0 ;;
    expired|cancelled) JOBOK=0; return 0 ;;
  esac
  if [ "$(phase_age)" -gt "$3" ]; then
    q -v j="$j" <<< "update public.lab_jobs set state = 'cancelled', finished_at = now() where id = :'j'::uuid and state = 'queued'" >/dev/null
    JOBOK=0; step "$1" false "$(phase_age)" "no answer from the laptop"; dset job_timeout 1; return 0
  fi
  JOBOK=""; return 1
}

# ---- go
q -v r="$RUN" <<< "update public.lab_runs set state = 'running', started_at = coalesce(started_at, now()) where id = :'r'::uuid" >/dev/null
first=0; [ -n "$(dget phase_at)" ] || { first=1; phase wait_online; }
deadline=$(( $(now) + timeout_min * 60 ))
say "run started for '$dev', timeout ${timeout_min} min"
if [ "$first" = 1 ] && [ -z "${LAB_NO_TG:-}" ]; then
  ver=$(dstat "coalesce(status->>'app_version', '?')"); tg "▶️ Проверка на ноутбуке началась: сборка ${ver:-?}" || true
fi
scenarios=($(q -v r="$RUN" <<< "select unnest(scenarios) from public.lab_runs where id = :'r'::uuid"))
build=$(rget build)
FAILED=0

end_run() {   # $1 = final state
  q -v d="$DEV" -v r="$RUN" <<< "update public.lab_jobs set state = 'cancelled', finished_at = now() where run_id = :'r'::uuid and state = 'queued'" >/dev/null
  q -v r="$RUN" -v s="$1" <<< "update public.lab_runs set state = :'s', finished_at = now() where id = :'r'::uuid" >/dev/null
  "$HERE/lab-report.sh" "$RUN" || say "report failed"
}

# the app is not usable: only ask the runner to finish (so it exits), then report
bail_finish() { local f; f=$(mkjob finish) && dset job "$f" && dset job_timeout "" && phase finish; }

# What the app logs when a pilot or the click gate stops (app/Overlay.cs AutoSay + «auto click stopped», src/ClickGate.cs Note «click gate <owner>: STOP (...)»,
# HALT, the pilot states of HostBridge.OnAutoState, the art equip/promote «auto: stopped»)
STOP_RE='auto click stopped|click gate .*: STOP|HALT|auto: (failed|slot_failed|unconfirmed|hero_failed|stopped)'
NOPROGRESS=${LAB_NOPROGRESS:-180}
# watch_logs <since epoch> <no-progress reference epoch>: 0 = all well; 1 = a stop line (WHY = the line); 2 = no new log line for NOPROGRESS s (WHY)
WHY=""
watch_logs() {
  local txt hit last ref
  txt=$(q -v d="$DEV" -v s="$1" <<< "select l.lines from public.app_test_logs l join public.lab_devices dv on dv.account_id = l.account_id where dv.id = :'d'::uuid and l.at >= to_timestamp(:'s'::bigint) order by l.id")
  hit=$(grep -E "$STOP_RE" <<< "$txt" | head -1 | sed -E 's/^\s+|\s+$//g' | cut -c1-200)
  if [ -n "$hit" ]; then WHY="$hit"; return 1; fi
  last=$(q -v d="$DEV" <<< "select coalesce(extract(epoch from max(l.at))::bigint, 0) from public.app_test_logs l join public.lab_devices dv on dv.account_id = l.account_id where dv.id = :'d'::uuid")
  ref=$2; [ "${last:-0}" -gt "$ref" ] && ref=$last
  if [ $(( $(now) - ref )) -gt "$NOPROGRESS" ]; then WHY="no progress: no new app log line for $(( NOPROGRESS / 60 )) min"; return 2; fi
  return 0
}

# a scenario step failed: stop its request, record the reason, skip the remaining scenarios (upload_logs + screenshot + finish still run)
fail_scn() {   # $1 = step label, $2 = reason, $3 = table, $4 = row id
  q -v r="$4" <<< "update public.$3 set status = 'cancelled', done_at = now() where id = :'r'::uuid and status = 'pending'" >/dev/null
  step "$1" false "$(phase_age)" "$2"; FAILED=1; dset last_scn_end "$(now)"; skip_to_logs
}
skip_to_logs() { dset scn_idx "${#scenarios[@]}"; phase logs; }

while :; do
  if [ "$(now)" -gt "$deadline" ]; then
    say "TIMEOUT"; step "timeout" false "$((timeout_min * 60))" "the run did not finish in ${timeout_min} min"
    end_run timeout; exit 1
  fi
  ph=$(rget phase)
  case "$ph" in
    start|wait_online)
      ok=$(dstat "(last_seen > now() - interval '150 seconds' and status->>'game_running' = 'true' and coalesce(status->>'busy', 'false') = 'false')")
      if [ "$ok" = t ]; then
        j=$(mkjob update_app) && dset job "$j" && phase update
      elif [ "$(phase_age)" -gt 600 ]; then
        step online false "$(phase_age)" "the laptop is not online with the game running"
        end_run failed; exit 1
      fi ;;
    update)
      if wait_job update_app restart 300; then
        j=$(dget job)
        if [ "$JOBOK" = 1 ]; then
          step update_app true "$(jsecs "$j")" "$(jres "$j" version)"
          dset want_version "$(jres "$j" version)"
          r=$(mkjob restart_app) && dset job "$r" && dset job_timeout "" && phase restart
        else
          [ -n "$(dget job_timeout)" ] || step update_app false "$(jsecs "$j")" "$(jres "$j" error)"
          FAILED=1; bail_finish
        fi
      fi ;;
    restart)
      if wait_job restart_app wait_ready 120; then
        j=$(dget job)
        if [ "$JOBOK" = 1 ]; then
          step restart_app true "$(jsecs "$j")" ""
          dset restarted_at "$(q -v j="$j" <<< "select extract(epoch from finished_at)::bigint from public.lab_jobs where id = :'j'::uuid")"
          phase wait_ready
        else
          [ -n "$(dget job_timeout)" ] || step restart_app false "$(jsecs "$j")" "$(jres "$j" error)"
          FAILED=1; bail_finish
        fi
      fi ;;
    wait_ready)
      # ready = the test app runs, shows the wanted version and has asked the site for its account after the restart
      want=$(dget want_version); rs=$(dget restarted_at)
      ok=$(q -v d="$DEV" -v w="$want" -v rs="$rs" <<< "
        select (dv.status->>'app_running' = 'true' and (:'w' = '' or dv.status->>'app_version' = :'w')
                and a.app_seen_at > to_timestamp(:'rs'::bigint))
        from public.lab_devices dv join public.game_accounts a on a.id = dv.account_id where dv.id = :'d'::uuid")
      if [ "$ok" = t ]; then
        step app_ready true "$(( $(now) - rs ))" "version ${want:-?}"
        dset scn_idx 0; phase scenario
      elif [ "$(phase_age)" -gt 420 ]; then
        step app_ready false "$(phase_age)" "the app did not report ready in 7 min"
        FAILED=1; skip_to_logs
      else
        pr=$(phase_at_epoch); watch_logs "${rs:-0}" "$pr"; w=$?
        if [ "$w" != 0 ]; then step app_ready false "$(phase_age)" "$WHY"; FAILED=1; skip_to_logs; fi
      fi ;;
    scenario)
      i=$(dget scn_idx)
      if [ "$i" -ge "${#scenarios[@]}" ]; then phase logs; continue; fi
      sc=${scenarios[$i]}
      row=$("$HERE/lab-seed.sh" "$dev" "$sc" "$build") || row=""
      if [ -z "$row" ]; then step "$sc" false 0 "seed failed (fixture missing?)"; FAILED=1; skip_to_logs; continue; fi
      dset row "$row"; dset seeded_at "$(now)"; phase wait_scn ;;
    wait_scn)
      i=$(dget scn_idx); sc=${scenarios[$i]}; row=$(dget row)
      case "$sc" in equip_city) tbl=equip_plans; maxs=600; label="$sc (build ${build^^})" ;; *) tbl=item_locates; maxs=300; label="$sc" ;; esac
      st=$(q -v r="$row" <<< "select status || '|' || coalesce(round(extract(epoch from done_at - created_at))::int, 0) from public.$tbl where id = :'r'::uuid")
      case "$st" in
        done\|*) step "$label" true "${st#*|}" ""; dset last_scn_end "$(now)"; dset scn_idx "$((i + 1))"; phase scenario ;;
        cancelled\|*) step "$label" false "$(phase_age)" "the request was cancelled"; FAILED=1; dset last_scn_end "$(now)"; skip_to_logs ;;
        *) sa=$(dget seeded_at)
           if watch_logs "${sa:-0}" "${sa:-0}"; then
             if [ "$(phase_age)" -gt "$maxs" ]; then fail_scn "$label" "not done in ${maxs} s" "$tbl" "$row"; fi
           else
             fail_scn "$label" "$WHY" "$tbl" "$row"
           fi ;;
      esac ;;
    logs)
      # the test app sends its log every 2 min: wait for a batch newer than the last scenario (max 150 s), then go on
      le=$(dget last_scn_end); [ -n "$le" ] || le=$(now)
      n=$(q -v d="$DEV" -v le="$le" <<< "select count(*) from public.app_test_logs l join public.lab_devices dv on dv.account_id = l.account_id where dv.id = :'d'::uuid and l.at > to_timestamp(:'le'::bigint)")
      if [ "$n" -gt 0 ] || [ "$(phase_age)" -gt 150 ]; then
        j=$(mkjob upload_logs '{"lines":1000}') && dset job "$j" && dset job_timeout "" && phase upload_logs
      fi ;;
    upload_logs)
      if wait_job upload_logs shot 180; then
        j=$(dget job); [ -n "$(dget job_timeout)" ] || step upload_logs "$([ "$JOBOK" = 1 ] && echo true || echo false)" "$(jsecs "$j")" ""
        s=$(mkjob screenshot '{"target":"app"}') && dset job "$s" && dset job_timeout "" && phase screenshot
      fi ;;
    screenshot)
      if wait_job screenshot finish 180; then
        j=$(dget job); [ -n "$(dget job_timeout)" ] || step screenshot "$([ "$JOBOK" = 1 ] && echo true || echo false)" "$(jsecs "$j")" ""
        f=$(mkjob finish) && dset job "$f" && dset job_timeout "" && phase finish
      fi ;;
    finish)
      if wait_job finish report 180; then
        [ "$JOBOK" = 1 ] && dset finish_ok 1
        phase report
      fi ;;
    report)
      [ "$(q -v r="$RUN" <<< "select count(*) from public.lab_runs where id = :'r'::uuid and steps @> '[{\"ok\": false}]'")" = 0 ] && final=done || final=failed
      end_run "$final"
      [ "$final" = done ] && exit 0 || exit 1 ;;
    *) say "unknown phase '$ph'"; exit 1 ;;
  esac
  sleep "$tick"
done
