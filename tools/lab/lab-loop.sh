#!/usr/bin/env bash
# Drives the test-lab runs of the devices (staging). Two modes:
#   lab-loop.sh --until-done <device> [--timeout MIN]   FOREGROUND, by hand: drives the one queued run of a device (default 60 min).
#                                                       Exits 0 when the run finished (Telegram message + report file done), 1 on timeout / failure.
#   lab-loop.sh --auto                                  ONE pass, for cron every minute (flock; exits at once when nothing is to do). For every enabled
#                                                       device that is online (heartbeat < 3 min): game not running -> queue start_game (once per 15 min);
#                                                       the newest published test build has no finished run yet, or a lab-request waits -> start a run;
#                                                       a run in progress advances (the same state machine, state in lab_runs). Always exits 0.
#                                                       Env: TEST_FEED (feed dir, default /srv/wardsage/test-feed), LAB_NO_TG=1 (print instead of Telegram), LAB_ONLY=<device> (tests).
#
# Flow (state kept in lab_runs.phase, so a restart of this script resumes; each phase is idempotent):
#   wait_online -> update_app -> restart_app -> wait_ready -> [seed -> wait done]* -> wait logs
#   -> upload_logs -> screenshot -> finish -> report
# The runner on the laptop only executes the whitelisted jobs queued here; it never clicks (start_game starts the game exe, no launcher, no clicks).
set -uo pipefail
. "$(dirname "$0")/lab-common.sh"
HERE=$(dirname "$(readlink -f "$0")")
USAGE="usage: lab-loop.sh --until-done <device> [--timeout MIN]  |  lab-loop.sh --auto"

dev=""; timeout_min=60; tick=10; auto=0
while [ $# -gt 0 ]; do
  case "$1" in
    --until-done) dev=${2:-}; shift 2 ;;
    --auto) auto=1; shift ;;
    --timeout) timeout_min=${2:-60}; shift 2 ;;
    --tick) tick=${2:-10}; shift 2 ;;
    *) echo "$USAGE" >&2; exit 2 ;;
  esac
done
{ [ -n "$dev" ] || [ "$auto" = 1 ]; } && { [ -z "$dev" ] || [ "$auto" = 0 ]; } || { echo "$USAGE" >&2; exit 2; }
[[ "$timeout_min" =~ ^[0-9]{1,3}$ ]] && [[ "$tick" =~ ^[0-9]{1,3}$ ]] || { echo "bad number" >&2; exit 2; }
FEED=${TEST_FEED:-/srv/wardsage/test-feed}

say() { echo "$(date -u +%H:%M:%S) $*"; }
now() { date +%s; }

# ---- run row helpers (RUN = run id, DEV = device id)
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


# ---- the state machine
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
NOPROGRESS=${LAB_NOPROGRESS:-330}   # the app sends its log every ~2 min, in batches
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

# The runner (v6+) reads the app's own log every 5 s and reports it in the heartbeat status (every 15 s while a run is active):
# log_at (epoch of the newest log line), ready, stop + stop_at. That is ~1 min faster than the app_test_logs the app uploads every ~2 min,
# which stay as the fallback for an older runner (no log_at in the status).
# Runner v7+ also reports pilot_at (epoch of the newest PILOT line: a step / guide change, a click allowed or taking effect, the locate / sell
# pilots' own lines). The app keeps logging its syncs while a pilot waits on a screen it does not know, so a scenario's progress is pilot_at;
# without it (older runner) the newest log line stands in, as before.
NOPROG_DEV=${LAB_NOPROGRESS_DEV:-75}   # 60 s without a pilot line (old runner: a log line) while a scenario waits + one heartbeat
READY_DEV=${LAB_READY_DEV:-180}        # the laptop read takes ~50 s
SG_LOGAT=""; SG_SEEN=0; SG_READY=""; SG_STOPAT=0; SG_APPOK=""; SG_APPVER=""; SG_STOP=""; SG_PILOTAT=""
devsig() {
  local row
  row=$(dstat "coalesce(status->>'log_at', '') || '|' || coalesce(extract(epoch from last_seen)::bigint, 0) || '|' || coalesce(status->>'ready', '') || '|' || coalesce(status->>'stop_at', '0') || '|' || coalesce(status->>'app_running', '') || '|' || coalesce(status->>'app_version', '') || '|' || coalesce(status->>'pilot_at', '') || '|' || coalesce(replace(status->>'stop', chr(10), ' '), '')")
  IFS='|' read -r SG_LOGAT SG_SEEN SG_READY SG_STOPAT SG_APPOK SG_APPVER SG_PILOTAT SG_STOP <<< "$row"
  [[ "$SG_LOGAT" =~ ^[0-9]+$ ]] || SG_LOGAT=""
  [[ "$SG_PILOTAT" =~ ^[0-9]+$ ]] || SG_PILOTAT=""
  [[ "$SG_SEEN" =~ ^[0-9]+$ ]] || SG_SEEN=0
  [[ "$SG_STOPAT" =~ ^[0-9]+$ ]] || SG_STOPAT=0
}
# dev_watch <since epoch> <no-progress reference epoch> <no-progress seconds> [pilot]: 0 = all well; 1 = a stop line (WHY); 2 = no progress for
# that long (WHY); 9 = the runner is old (no log_at in its status): the caller uses watch_logs. A stop counts only when the status is newer than
# <since>. With "pilot" (a scenario waits) the progress is the newest pilot line (pilot_at, runner v7+), else the newest log line.
dev_watch() {
  local ref at what
  devsig
  [ -n "$SG_LOGAT" ] || return 9
  if [ -n "$SG_STOP" ] && [ "$SG_SEEN" -gt "$1" ] && [ "$SG_STOPAT" -ge "$1" ]; then WHY="${SG_STOP:0:200}"; return 1; fi
  at=$SG_LOGAT; what="no new app log line"
  if [ "${4:-}" = pilot ] && [ -n "$SG_PILOTAT" ]; then at=$SG_PILOTAT; what="no pilot step"; fi
  ref=$2; [ "$at" -gt "$ref" ] && ref=$at
  if [ $(( $(now) - ref )) -gt "$3" ]; then WHY="no progress: $what for $3 s"; return 2; fi
  return 0
}

# a scenario step failed: stop its request, record the reason, skip the remaining scenarios (upload_logs + screenshot + finish still run)
fail_scn() {   # $1 = step label, $2 = reason, $3 = table, $4 = row id
  q -v r="$4" <<< "update public.$3 set status = 'cancelled', done_at = now() where id = :'r'::uuid and status = 'pending'" >/dev/null
  step "$1" false "$(phase_age)" "$2"; FAILED=1; dset last_scn_end "$(now)"; skip_to_logs
}
skip_to_logs() { dset scn_idx "${#scenarios[@]}"; phase logs; }


notify() { if [ -n "${LAB_NO_TG:-}" ]; then say "telegram (skipped): $1"; else tg "$1" || true; fi; }

# the run RUN of device DEV: mark it running, load its scenarios, say hello on the first start. Sets deadline, first, scenarios, build.
init_run() {
  q -v r="$RUN" <<< "update public.lab_runs set state = 'running', started_at = coalesce(started_at, now()) where id = :'r'::uuid" >/dev/null
  first=0; [ -n "$(dget phase_at)" ] || { first=1; phase wait_online; mkjob update_runner >/dev/null || true; }
  if [ "$auto" = 1 ]; then
    local sa; sa=$(rget "coalesce(extract(epoch from started_at)::bigint, 0)"); deadline=$(( ${sa:-0} + timeout_min * 60 ))
    [ "${sa:-0}" -gt 0 ] || deadline=$(( $(now) + timeout_min * 60 ))
  else
    deadline=$(( $(now) + timeout_min * 60 ))
  fi
  { [ "$auto" = 0 ] || [ "$first" = 1 ]; } && say "run ${RUN:0:8} for '$dev', timeout ${timeout_min} min"
  if [ "$first" = 1 ]; then
    local ver; ver=$(dstat "coalesce(status->>'app_version', '?')"); notify "▶️ Проверка на ноутбуке началась: сборка ${ver:-?}"
  fi
  scenarios=($(q -v r="$RUN" <<< "select unnest(scenarios) from public.lab_runs where id = :'r'::uuid"))
  build=$(rget build)
  FAILED=0; FINAL=""
}

tick() {
  if [ "$(now)" -gt "$deadline" ]; then
    say "TIMEOUT"; step "timeout" false "$((timeout_min * 60))" "the run did not finish in ${timeout_min} min"
    end_run timeout; FINAL=1; return
  fi
  ph=$(rget phase)
  case "$ph" in
    start|wait_online)
      ok=$(dstat "(last_seen > now() - interval '150 seconds' and status->>'game_running' = 'true' and coalesce(status->>'busy', 'false') = 'false')")
      if [ "$ok" = t ]; then
        j=$(mkjob update_app) && dset job "$j" && phase update
      elif [ "$(phase_age)" -gt 600 ]; then
        step online false "$(phase_age)" "the laptop is not online with the game running"
        end_run failed; FINAL=1; return
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
      dev_watch "${rs:-0}" 0 999999; w=$?
      if [ "$w" != 9 ]; then
        # device status first (runner v6+)
        if [ "$w" = 1 ]; then
          step app_ready false "$(phase_age)" "$WHY"; FAILED=1; skip_to_logs
        elif [ "$SG_READY" = true ] && [ "$SG_APPOK" = true ] && [ "$SG_SEEN" -gt "${rs:-0}" ] && { [ -z "$want" ] || [ "$SG_APPVER" = "$want" ]; }; then
          step app_ready true "$(( $(now) - ${rs:-0} ))" "version ${want:-?}"
          dset scn_idx 0; phase scenario
        elif [ "$(phase_age)" -gt "$READY_DEV" ]; then
          step app_ready false "$(phase_age)" "the app did not report ready in $(( READY_DEV / 60 )) min"
          FAILED=1; skip_to_logs
        fi
        return
      fi
      # fallback, old runner: the app asked the site for its account after the restart
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
      if [ "$i" -ge "${#scenarios[@]}" ]; then phase logs; return; fi
      sc=${scenarios[$i]}
      mkjob game_front >/dev/null || true   # the game window to the front before each scenario
      dset mark_at "$(now)"; mkjob mark >/dev/null || true   # the runner forgets the old stop / ready markers
      row=$("$HERE/lab-seed.sh" "$dev" "$sc" "$build") || row=""
      if [ -z "$row" ]; then step "$sc" false 0 "seed failed (fixture missing?)"; FAILED=1; skip_to_logs; return; fi
      dset row "$row"; dset seeded_at "$(now)"; phase wait_scn ;;
    wait_scn)
      i=$(dget scn_idx); sc=${scenarios[$i]}; row=$(dget row)
      case "$sc" in equip_city) tbl=equip_plans; maxs=600; label="$sc (build ${build^^})" ;; *) tbl=item_locates; maxs=300; label="$sc" ;; esac
      st=$(q -v r="$row" <<< "select status || '|' || coalesce(round(extract(epoch from done_at - created_at))::int, 0) from public.$tbl where id = :'r'::uuid")
      case "$st" in
        done\|*) step "$label" true "${st#*|}" ""; dset last_scn_end "$(now)"; dset scn_idx "$((i + 1))"; phase scenario ;;
        cancelled\|*) step "$label" false "$(phase_age)" "the request was cancelled"; FAILED=1; dset last_scn_end "$(now)"; skip_to_logs ;;
        *) sa=$(dget seeded_at); ma=$(dget mark_at); [ -n "$ma" ] || ma=$sa
           dev_watch "${ma:-0}" "${sa:-0}" "$NOPROG_DEV" pilot; w=$?
           if [ "$w" = 9 ]; then watch_logs "${sa:-0}" "${sa:-0}"; w=$?; fi   # old runner
           if [ "$w" = 0 ]; then
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
      [ "$final" = done ] && FINAL=0 || FINAL=1 ;;
    *) say "unknown phase '$ph'"; FINAL=1 ;;
  esac
}

# ================================================================ --until-done
if [ "$auto" = 0 ]; then
  DEV=$(dev_id "$dev") || exit 2
  exec 9>"$LAB_DIR/loop-$dev.lock"
  flock -n 9 || { echo "lab-loop for '$dev' is already running" >&2; exit 1; }
  RUN=$(q -v d="$DEV" <<< "select id from public.lab_runs where device_id = :'d'::uuid and state in ('queued', 'running') order by created_at limit 1")
  [ -n "$RUN" ] || { echo "no queued run for '$dev' (lab-request.sh first)"; exit 0; }
  init_run
  while :; do
    tick
    [ -z "$FINAL" ] || exit "$FINAL"
    sleep "$tick"
  done
fi

# ================================================================ --auto (one pass)
exec 8>"$LAB_DIR/auto.lock"
flock -n 8 || exit 0

# ensure_game: the game is not running -> queue ONE start_game job per 15 min
ensure_game() {
  local n; n=$(q -v d="$DEV" <<< "select count(*) from public.lab_jobs where device_id = :'d'::uuid and action = 'start_game' and (state in ('queued', 'claimed') or created_at > now() - interval '15 minutes')")
  if [ "${n:-1}" = 0 ]; then
    q -v d="$DEV" <<< "insert into public.lab_jobs (device_id, action, args, expires_at) values (:'d'::uuid, 'start_game', '{}'::jsonb, now() + interval '20 minutes')" >/dev/null
    say "'$dev': game not running, start_game queued"
  fi
}

# the game needs the launcher (start_game failed with game_needs_launcher): one message per 6 h, and only for a failure newer than the last message
notify_launcher() {
  local mark="$LAB_DIR/needs-launcher-$dev.at" last fin
  last=$(cat "$mark" 2>/dev/null || echo 0); [[ "$last" =~ ^[0-9]+$ ]] || last=0
  fin=$(q -v d="$DEV" <<< "select coalesce(extract(epoch from max(finished_at))::bigint, 0) from public.lab_jobs where device_id = :'d'::uuid and action = 'start_game' and state = 'failed' and result->>'error' = 'game_needs_launcher'")
  if [ "${fin:-0}" -gt "$last" ] && [ $(( $(now) - last )) -gt 21600 ]; then
    notify "🎮 Ноутбук: игре нужно обновление, откройте лаунчер и нажмите «Играть»"
    echo "$(now)" > "$mark"
  fi
}

# advance the run: a few ticks while the phase keeps changing (a chain of quick steps), then leave the rest to the next pass
advance() {
  local i p0 p1
  init_run
  for i in 1 2 3 4 5; do
    p0=$(rget phase); tick
    [ -z "$FINAL" ] || { say "run ${RUN:0:8} ended (exit code $FINAL)"; return; }
    p1=$(rget phase); [ "$p0" != "$p1" ] || return
  done
}

auto_dev() {   # $1 = device name
  dev=$1; DEV=$(dev_id "$dev") || return 0
  exec 9>"$LAB_DIR/loop-$dev.lock"
  flock -n 9 || { exec 9>&-; return 0; }          # a hand-started --until-done owns the device
  local gr st latest fin rv rn
  gr=$(dstat "coalesce(status->>'game_running', 'false')")
  # a newer published runner than the one the laptop reports -> ask it to update (one job at a time)
  rv=$(jq -r '.version // empty' "$FEED/lab/WardsageLab.json" 2>/dev/null); rn=$(dstat "coalesce(status->>'runner', '')")
  if [[ "$rv" =~ ^[0-9]{1,9}$ ]] && [[ "$rn" =~ ^[0-9]{1,9}$ ]] && [ "$rv" -gt "$rn" ]; then
    q -v d="$DEV" <<< "insert into public.lab_jobs (device_id, action, args, expires_at)
      select :'d'::uuid, 'update_runner', '{}'::jsonb, now() + interval '20 minutes'
      where not exists (select 1 from public.lab_jobs where device_id = :'d'::uuid and action = 'update_runner' and (state in ('queued', 'claimed') or created_at > now() - interval '10 minutes'))" >/dev/null
  fi
  RUN=$(q -v d="$DEV" <<< "select id from public.lab_runs where device_id = :'d'::uuid and state in ('queued', 'running') order by created_at limit 1")
  if [ "$gr" != true ]; then ensure_game; notify_launcher; fi
  if [ -n "$RUN" ]; then
    st=$(rget state)
    # a waiting request starts only when the game runs
    if [ "$st" = running ] || [ "$gr" = true ]; then advance; fi
  elif [ "$gr" = true ]; then
    latest=$(jq -r '.version // empty' "$FEED/latest.json" 2>/dev/null)
    if [[ "$latest" =~ ^[0-9]{1,9}(\.[0-9]{1,9}){0,3}$ ]]; then
      fin=$(q -v d="$DEV" -v v="$latest" <<< "select count(*) from public.lab_runs where device_id = :'d'::uuid and state in ('done', 'failed', 'timeout') and (data->>'target_version' = :'v' or data->>'want_version' = :'v')")
      if [ "$fin" = 0 ]; then
        RUN=$(q -v d="$DEV" -v v="$latest" <<< "
          insert into public.lab_runs (device_id, scenarios, build, data)
          values (:'d'::uuid, '{equip_city,locate}', coalesce((select case build when 'a' then 'b' else 'a' end from public.lab_runs
                  where device_id = :'d'::uuid and state <> 'cancelled' order by created_at desc limit 1), 'a'),
                  jsonb_build_object('target_version', :'v'::text))
          returning id")
        say "'$dev': new test build $latest has no finished run, run ${RUN:0:8} queued"
        advance
      fi
    fi
  fi
  exec 9>&-
}

# LAB_ONLY=<device> limits the pass to one device (tests)
for d in $(q -v only="${LAB_ONLY:-}" <<< "select name from public.lab_devices where enabled and last_seen > now() - interval '3 minutes' and (:'only' = '' or name = :'only') order by name"); do
  auto_dev "$d"
done
exit 0
