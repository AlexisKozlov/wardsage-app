#!/usr/bin/env bash
# Drives the test-lab runs of the devices (staging). Two modes:
#   lab-loop.sh --until-done <device> [--timeout MIN]   FOREGROUND, by hand: drives the one queued run of a device (default 90 min).
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

dev=""; timeout_min=90; tick=3; auto=0
while [ $# -gt 0 ]; do
  case "$1" in
    --until-done) dev=${2:-}; shift 2 ;;
    --auto) auto=1; shift ;;
    --timeout) timeout_min=${2:-60}; shift 2 ;;
    --tick) tick=${2:-3}; shift 2 ;;
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
warn()   { q -v r="$RUN" -v n="$1" -v s="$2" -v note="${3:-}" <<< "update public.lab_runs set steps = steps || jsonb_build_array(jsonb_build_object('name', :'n', 'ok', true, 'warn', true, 'seconds', :'s'::numeric, 'note', :'note')) where id = :'r'::uuid"; say "step $1 WARN ${2}s ${3:-}"; }
info()   { q -v r="$RUN" -v n="$1" -v s="$2" -v note="${3:-}" <<< "update public.lab_runs set steps = steps || jsonb_build_array(jsonb_build_object('name', :'n', 'ok', true, 'info', true, 'seconds', :'s'::numeric, 'note', :'note')) where id = :'r'::uuid"; say "step $1 INFO ${2}s ${3:-}"; }
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
  local rid; rid=$(dget rec_id)
  if [ -n "$rid" ]; then rec_tag "$rid"; dset rec_id ""; fi   # a run that ended inside a scenario (timeout): its recording is kept
  # shellcheck disable=SC2046
  rec_delete $(dget rec_del)
  q -v d="$DEV" -v r="$RUN" <<< "update public.lab_jobs set state = 'cancelled', finished_at = now() where run_id = :'r'::uuid and state = 'queued'" >/dev/null
  q -v r="$RUN" -v s="$1" <<< "update public.lab_runs set state = :'s', finished_at = now() where id = :'r'::uuid" >/dev/null
  "$HERE/lab-report.sh" "$RUN" || say "report failed"
}

# the app is not usable: only ask the runner to finish (so it exits), then report
bail_finish() { local f; f=$(mkjob finish) && dset job "$f" && dset job_timeout "" && phase finish; }

# What the app logs when a pilot or the click gate stops (app/Overlay.cs AutoSay + «auto click stopped», src/ClickGate.cs Note «click gate <owner>: STOP (...)»,
# HALT, the pilot states of HostBridge.OnAutoState, the art equip/promote «auto: stopped»)
STOP_RE='auto click stopped|auto click is off|click gate .*: STOP|HALT|auto: (failed|slot_failed|unconfirmed|hero_failed|stopped)'
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
NOPROG_DEV=${LAB_NOPROGRESS_DEV:-40}   # 40 s without a pilot line (old runner: a log line) while a scenario waits (runner v17 beats every 5 s)
REPEAT_N=${LAB_REPEAT_N:-10}           # the same newest pilot line this many times in a row (each a new pilot_at) = a pilot that spins
READY_DEV=${LAB_READY_DEV:-180}        # the laptop read takes ~50 s
RP_AT=""; RP_LINE=""; RP_N=0; SG_PLINE=""
SG_LOGAT=""; SG_SEEN=0; SG_READY=""; SG_STOPAT=0; SG_APPOK=""; SG_APPVER=""; SG_STOP=""; SG_PILOTAT=""
devsig() {
  local row
  row=$(dstat "coalesce(status->>'log_at', '') || '|' || coalesce(extract(epoch from last_seen)::bigint, 0) || '|' || coalesce(status->>'ready', '') || '|' || coalesce(status->>'stop_at', '0') || '|' || coalesce(status->>'app_running', '') || '|' || coalesce(status->>'app_version', '') || '|' || coalesce(status->>'pilot_at', '') || '|' || coalesce(replace(replace(status->>'pilot_line', chr(10), ' '), '|', '/'), '') || '|' || coalesce(replace(status->>'stop', chr(10), ' '), '')")
  IFS='|' read -r SG_LOGAT SG_SEEN SG_READY SG_STOPAT SG_APPOK SG_APPVER SG_PILOTAT SG_PLINE SG_STOP <<< "$row"
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
  if [ "${4:-}" = pilot ] && [ -n "$SG_PILOTAT" ]; then
    at=$SG_PILOTAT; what="no pilot step"
    if [ "$SG_PILOTAT" != "$RP_AT" ]; then
      if [ -n "$SG_PLINE" ] && [ "$SG_PLINE" = "$RP_LINE" ]; then RP_N=$(( RP_N + 1 )); else RP_N=1; fi
      RP_AT=$SG_PILOTAT; RP_LINE=$SG_PLINE
    fi
    if [ "$RP_N" -ge "$REPEAT_N" ] && [ "${5:-}" != norepeat ]; then WHY="pilot repeats the same line $RP_N times: ${SG_PLINE:0:120}"; return 2; fi
  fi
  ref=$2; [ "$at" -gt "$ref" ] && ref=$at
  if [ $(( $(now) - ref )) -gt "$3" ]; then WHY="no progress: $what for $3 s"; return 2; fi
  return 0
}

# a scenario step failed: stop its request, record the reason, go on with the next scenario (each scenario is independent: the app's
# pilots start from any screen, back to the city by themselves)
fail_scn() {   # $1 = step label, $2 = reason, $3 = table, $4 = row id
  [ -n "$4" ] && q -v r="$4" <<< "update public.$3 set status = 'cancelled', done_at = now() where id = :'r'::uuid and status = 'pending'" >/dev/null
  step "$1" false "$(phase_age)" "$2"; FAILED=1
  # logs + a screenshot of the failed screen are queued together (the runner takes them back to back) and the next scenario starts at once
  mkjob upload_logs '{"lines":1000}' >/dev/null || true; mkjob screenshot '{"target":"app"}' >/dev/null || true; dset fail_shot 1
  next_scn
}
# next_scn: the scenario ended (pass, fail or skip): its request is closed at once, so the app stops guiding before the next one. The site's
# long poll returns only pending rows, so the app drops a guide whose row is anything else (app/Overlay.cs locateReq = null); 'cancelled'
# (not 'done': the app did not finish it, the last press is the player's). An equip plan that passed is already 'done' (the app closed it).
next_scn() {
  local r t; r=$(dget row); t=$(scn_tbl "${scenarios[$(dget scn_idx)]:-}")
  rec_end
  [ -n "$r" ] && [ -n "$t" ] && q -v r="$r" <<< "update public.$t set status = 'cancelled', done_at = now() where id = :'r'::uuid and status = 'pending'" >/dev/null
  dset row ""; dset last_scn_end "$(now)"; dset scn_idx "$(( $(dget scn_idx) + 1 ))"; dset sar_ready ""; phase scenario
}
# the scenario's screen recording (runner v16+, src/LabRec.cs): started before the request is seeded; when the scenario ends it is stopped.
# Passed (the last step ok, not a warning): the recording is deleted on the server (at the run's end, when its last part is in; the --auto
# pass deletes them again for a day, in case a part came later). Else the last step keeps its id (rec) for the report and lab-rec.sh.
rec_end() {
  local id; id=$(dget rec_id); dset rec_for ""
  [ -n "$id" ] || return 0
  mkjob rec '{"cmd":"stop"}' >/dev/null || true
  local pass; pass=$(q -v r="$RUN" <<< "select coalesce((s->>'ok')::boolean and coalesce(s->>'warn', '') = '', false) from public.lab_runs r, jsonb_array_elements(r.steps) with ordinality e(s, n) where r.id = :'r'::uuid order by n desc limit 1")
  if [ "$pass" = t ]; then dset rec_del "$(dget rec_del) $id"; say "recording $id: passed, deleted at the end"
  else rec_tag "$id"; say "recording $id: kept"; fi
  dset rec_id ""
}
# rec_tag <id>: the last step names the recording
rec_tag() { q -v r="$RUN" -v id="$1" <<< "update public.lab_runs set steps = jsonb_set(steps, array[(jsonb_array_length(steps) - 1)::text, 'rec'], to_jsonb(:'id'::text)) where id = :'r'::uuid and jsonb_array_length(steps) > 0" >/dev/null; }
# scenario -> table of its request
scn_tbl() { case "$1" in equip_*) echo equip_plans ;; locate*) echo item_locates ;; sell_mark|sell_after_restart) echo sell_plans ;; esac; }
# the storage cleanup may click only these targets (src/ClickGate.cs, owner Sell); the runner (v11+) reports any other one as "never"
SELL_OK_RE='click gate Sell: allow (HeroBack|CityInventory|InvEquipTab|InvBulkEnter|InvBulkCell|InvListDrag|OfferClose|UnknownBack|CityBack|EscapeBack|MapClose|ArtTab|ArtBulkEnter|ArtBulkCell|ArtListDrag)\b'
# hero_grades keeps an HMAC of the game account (a server secret, never read here), not the account: the newest updated_at of the table
# stands for the device's account (on staging only the lab's app sends grades as a rule)
LOGS_WAIT=${LAB_LOGS_WAIT:-60}   # max wait at the end for the app's next log batch (the report's figures come from it)
GRADES_WAIT=${LAB_GRADES_WAIT:-360}
grades_at() { q <<< "select coalesce(extract(epoch from max(updated_at))::bigint, 0) from public.hero_grades"; }
grades_mark() { dset ready_at "$(now)"; dset grades0 "$(grades_at)"; }
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
          devsig
          if [ "$(jres "$j" updated)" = false ] && [ "$SG_READY" = true ] && [ "$SG_APPOK" = true ] && [ "$SG_APPVER" = "$(jres "$j" version)" ]; then
            step app_ready true 0 "already ready, no restart"; grades_mark; dset scn_idx 0; phase scenario; return
          fi
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
          grades_mark; dset scn_idx 0; phase scenario
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
        grades_mark; dset scn_idx 0; phase scenario
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
      # the screen recording starts before anything of the scenario (phase scn_rec waits for the app's answer); sell_after_restart: after
      # the app's restart (a restart ends any recording)
      if [ "$(dget rec_for)" != "$i" ] && [[ "$sc" =~ ^[a-z0-9_]{1,40}$ ]] && { [ "$sc" != sell_after_restart ] || [ -n "$(dget sar_ready)" ]; }; then
        dset rec_for "$i"
        if r=$(mkjob rec "{\"cmd\":\"start\",\"scenario\":\"$sc\",\"run\":\"$RUN\"}"); then dset job "$r"; phase scn_rec; return; fi
        say "recording: the job could not be queued"
      fi
      mkjob game_front >/dev/null || true   # the game window to the front before each scenario
      dset mark_at "$(now)"; mkjob mark "{\"scn\":\"$sc\"}" >/dev/null || true   # the runner forgets the old stop / ready markers
      if [ "$sc" = equip_city ]; then
        # never seed the build the hero already wears (then the app has nothing to change): the other one, whatever the last run did
        pb=$("$HERE/lab-seed.sh" "$dev" --pick "$build" 2>/dev/null) || pb=""
        if [[ "$pb" =~ ^[ab]$ ]] && [ "$pb" != "$build" ]; then
          say "equip build $build -> $pb (the other one is worn now)"; build=$pb
          q -v r="$RUN" -v b="$build" <<< "update public.lab_runs set build = :'b' where id = :'r'::uuid" >/dev/null
        fi
      fi
      if [ "$sc" = sell_after_restart ] && [ -z "$(dget sar_ready)" ]; then
        r=$(mkjob restart_app) && dset job "$r" && dset job_timeout "" && phase scn_restart; return
      fi
      seederr=$(mktemp)
      row=$("$HERE/lab-seed.sh" "$dev" "$sc" "$build" 2>"$seederr") || row=""
      sw=$(grep -m1 WARNING "$seederr" | sed -E 's/^lab-seed: WARNING //'); cat "$seederr" >&2; rm -f "$seederr"
      if [ -z "$row" ] && [ -n "$sw" ]; then warn "$sc" 0 "$sw"; next_scn; return; fi
      if [ -z "$row" ]; then step "$sc" false 0 "seed failed (fixture missing?)"; FAILED=1; next_scn; return; fi
      dset row "$row"; dset seeded_at "$(now)"; RP_AT=""; RP_LINE=""; RP_N=0; phase wait_scn ;;
    scn_rec)
      # the recording is a help, never a verdict: no answer in 40 s (old runner, old app) and the scenario goes on without one
      j=$(dget job); st=$(jstate "$j")
      case "$st" in
        done) id=$(jres "$j" rec); [[ "$id" =~ $LAB_REC_RX ]] && dset rec_id "$id"; say "recording ${id:-?} started"; phase scenario ;;
        failed|expired|cancelled) say "recording not started: $(jres "$j" error)$(jres "$j" state)"; phase scenario ;;
        *) if [ "$(phase_age)" -gt 40 ]; then
             q -v j="$j" <<< "update public.lab_jobs set state = 'cancelled', finished_at = now() where id = :'j'::uuid and state = 'queued'" >/dev/null
             say "recording: no answer in 40 s"; phase scenario
           fi ;;
      esac ;;
    scn_restart)
      # sell_after_restart: the app restarted, then ready again (it read the game account), then the list is seeded
      if [ -z "$(dget sar_at)" ]; then
        if wait_job restart_app x 120; then
          j=$(dget job)
          if [ "$JOBOK" = 1 ]; then dset sar_at "$(q -v j="$j" <<< "select extract(epoch from finished_at)::bigint from public.lab_jobs where id = :'j'::uuid")"
          else [ -n "$(dget job_timeout)" ] || step sell_after_restart false "$(jsecs "$j")" "restart_app: $(jres "$j" error)"; FAILED=1; next_scn; fi
        fi
        return
      fi
      ra=$(dget sar_at); devsig
      if [ "$SG_READY" = true ] && [ "$SG_APPOK" = true ] && [ "$SG_SEEN" -gt "${ra:-0}" ]; then
        dset sar_ready "$(( $(now) - ra ))"; dset sar_at ""; phase scenario
      elif [ $(( $(now) - ra )) -gt "$READY_DEV" ]; then
        dset sar_at ""; step sell_after_restart false "$(phase_age)" "the app did not report ready after the restart"; FAILED=1; next_scn
      fi ;;
    wait_scn)
      i=$(dget scn_idx); sc=${scenarios[$i]}; row=$(dget row)
      tbl=$(scn_tbl "$sc")
      case "$sc" in equip_city) maxs=600; label="$sc (build ${build^^})" ;; equip_*) maxs=600; label="$sc" ;; sell_*) maxs=480; label="$sc" ;; *) maxs=300; label="$sc" ;; esac
      st=$(q -v r="$row" <<< "select status || '|' || coalesce(round(extract(epoch from done_at - created_at))::int, 0) from public.$tbl where id = :'r'::uuid")
      if [ "$tbl" = sell_plans ]; then
        sa=$(dget seeded_at); devsig
        nev=$(dstat "coalesce(status->>'never', '')")
        # the app closes a list as done only when its items are gone from the game = sold: never allowed here
        if [ "${st%%|*}" = done ]; then step "$label" false "$(phase_age)" "ОПАСНО: приложение закрыло список как проданный"; FAILED=1; next_scn; return; fi
        if [ -n "$nev" ]; then fail_scn "$label" "ОПАСНО: запрещённый клик продажи: ${nev:0:160}" "$tbl" "$row"; return; fi
        if [ "${st%%|*}" = cancelled ]; then step "$label" false "$(phase_age)" "the list was cancelled"; FAILED=1; next_scn; return; fi
        fin=$(dstat "coalesce(status->>'done', '')"); fl=$(dstat "coalesce(status->>'fin_line', '')")
        # runner <= 13 reported only "sell: done" (its $Matches was overwritten): the full line from the uploaded app log
        if [ "$fin" = sell ] && ! [[ "$fl" =~ selected\ [0-9]+/[0-9]+ ]]; then
          fl=$(q -v d="$DEV" -v s="${sa:-0}" <<< "select l.lines from public.app_test_logs l join public.lab_devices dv on dv.account_id = l.account_id where dv.id = :'d'::uuid and l.at >= to_timestamp(:'s'::bigint) order by l.id" | grep -oE '(^|[^t] )sell: done selected [0-9]+/[0-9]+ missing [0-9]+ extra [0-9]+' | tail -1)
        fi
        if [ "$fin" = sell ] && [[ "$fl" =~ selected\ ([0-9]+)/([0-9]+)\ missing\ ([0-9]+)\ extra\ ([0-9]+) ]]; then
          a=${BASH_REMATCH[1]}; b=${BASH_REMATCH[2]}; m=${BASH_REMATCH[3]}; e=${BASH_REMATCH[4]}
          # the list ends here: cancelled, so the app stops (it never presses «Продать»; the marks stay in the game's bulk sale)
          q -v r="$row" <<< "update public.sell_plans set status = 'cancelled', done_at = now() where id = :'r'::uuid and status = 'pending'" >/dev/null
          note="выделено $a/$b, не найдено $m, лишних $e; «Продать» не нажата"
          [ "$sc" = sell_after_restart ] && note="$note; готово через $(dget sar_ready) с после перезапуска"
          if [ "$a" = "$b" ] && [ "$m" = 0 ] && [ "$e" = 0 ]; then step "$label" true "$(( $(now) - ${sa:-0} ))" "$note"; next_scn
          else step "$label" false "$(( $(now) - ${sa:-0} ))" "$note"; FAILED=1; next_scn; fi
          return
        fi
        dev_watch "$(dget mark_at)" "${sa:-0}" "$NOPROG_DEV" pilot norepeat; w=$?
        if [ "$w" = 9 ]; then fail_scn "$label" "runner too old (v11+ reports the sell marker)" "$tbl" "$row"
        elif [ "$w" != 0 ]; then fail_scn "$label" "$WHY" "$tbl" "$row"
        elif [ "$(phase_age)" -gt "$maxs" ]; then fail_scn "$label" "not done in ${maxs} s" "$tbl" "$row"; fi
        return
      fi
      case "$st" in
        done\|*) step "$label" true "${st#*|}" ""; next_scn ;;
        cancelled\|*) step "$label" false "$(phase_age)" "the request was cancelled"; FAILED=1; next_scn ;;
        *) sa=$(dget seeded_at); ma=$(dget mark_at); [ -n "$ma" ] || ma=$sa
           # runner v9+: the app reached its final step (the item is selected, the last press is the player's) = Locate passed
           if [ "$tbl" = item_locates ]; then
             fin=$(dstat "coalesce(status->>'done', '')")
             # the final step of each purpose (runner v13+ reports art): ascend = AscendReady, recast = Done, artPromote = ArtPromote
             case "$sc" in locate) fok='^(ascend|ready)$' ;; locate_recast) fok='^ready$' ;; locate_art) fok='^art$' ;; *) fok='^$' ;; esac
             if [ -n "$fin" ] && [[ "$fin" =~ $fok ]]; then
               case "$fin" in ascend) fnote="вещь выбрана в «Вознесении», осталось нажать «Вознести»" ;; art) fnote="копии выбраны в «Мастерской», осталось нажать «Продвинуть»" ;;
                 *) [ "$sc" = locate_recast ] && fnote="вещь выбрана в «Преобразовании», осталось нажать «Улучшить»" || fnote="вещь найдена, последний шаг за игроком" ;; esac
               step "$label" true "$(( $(now) - ${sa:-0} ))" "$fnote"; next_scn; return
             fi
           fi
           dev_watch "${ma:-0}" "${sa:-0}" "$NOPROG_DEV" pilot; w=$?
           if [ "$w" = 9 ]; then watch_logs "${sa:-0}" "${sa:-0}"; w=$?; fi   # old runner
           if [ "$w" = 0 ]; then
             if [ "$(phase_age)" -gt "$maxs" ]; then fail_scn "$label" "not done in ${maxs} s" "$tbl" "$row"; fi
           else
             fail_scn "$label" "$WHY" "$tbl" "$row"
           fi ;;
      esac ;;
    logs)
      # passive check: GRADES_WAIT s after app_ready hero_grades.updated_at has moved (hero_grades_put sets it on every send, changed or not), else
      # only an info line: the app reads grades only while a hero's rating screen is open (app/GradeWatch.cs), which a lab run never opens
      ra=$(dget ready_at)
      if [ -n "$ra" ] && [ -z "$(dget grades_done)" ]; then
        g=$(grades_at)
        if [ $(( $(now) - ra )) -lt "$GRADES_WAIT" ]; then : # the run ended early: never wait for this passive check
        elif [ "${g:-0}" -gt "$(dget grades0)" ]; then step grades true "$(( g - ra ))" "оценки обновились через $(( g - ra )) с после запуска"
        else info grades "$(( $(now) - ra ))" "оценки не отправлялись: приложение шлёт их, только когда открыт экран оценок героя"; fi
        dset grades_done 1; dset phase_at "$(now)"
      fi
      # the test app sends its log every 2 min: wait for a batch newer than the last scenario (max $LOGS_WAIT s), then go on; no wait when a scenario failed
      # (its logs and screenshot were queued at the failure)
      le=$(dget last_scn_end); [ -n "$le" ] || le=$(now)
      n=$(q -v d="$DEV" -v le="$le" <<< "select count(*) from public.app_test_logs l join public.lab_devices dv on dv.account_id = l.account_id where dv.id = :'d'::uuid and l.at > to_timestamp(:'le'::bigint)")
      if [ "$n" -gt 0 ] || [ "$FAILED" = 1 ] || [ "$(phase_age)" -gt "$LOGS_WAIT" ]; then
        # logs, screenshot and finish are queued together: the runner takes them back to back
        dset job_ul "$(mkjob upload_logs '{"lines":1000}')"
        [ -n "$(dget fail_shot)" ] || dset job_ss "$(mkjob screenshot '{"target":"app"}')"
        f=$(mkjob finish) && dset job "$f" && dset job_timeout "" && phase finish
      fi ;;
    upload_logs|screenshot) phase logs ;;   # a run started by an older loop
    finish)
      if wait_job finish report 300; then
        for kv in upload_logs:job_ul screenshot:job_ss; do
          j=$(dget "${kv#*:}"); [ -n "$j" ] || continue
          [ "$(jstate "$j")" = done ] && ok=true || ok=false
          step "${kv%%:*}" "$ok" "$(jsecs "$j")" ""
        done
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

# advance the run: keep driving it in this process (3 s tick) until it ends or its deadline (timeout_min, from started_at); the flock (auto.lock)
# is held meanwhile, so the cron passes of the next minutes exit at once
advance() {
  init_run
  local t0 st p0 p1; t0=$(now)
  while :; do
    p0=$(rget phase); tick
    [ -z "$FINAL" ] || { say "run ${RUN:0:8} ended (exit code $FINAL), $(( $(now) - t0 )) s in this pass"; return; }
    st=$(rget state); [[ "$st" =~ ^(queued|running)$ ]] || { say "run ${RUN:0:8} is $st: leaving it"; return; }
    p1=$(rget phase); [ "$p0" != "$p1" ] || sleep "$tick"   # a phase change goes straight on
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
        RUN=$(q -v d="$DEV" -v v="$latest" -v all="$LAB_SCENARIOS" <<< "
          insert into public.lab_runs (device_id, scenarios, build, data)
          values (:'d'::uuid, ('{' || replace(:'all', ' ', ',') || '}')::text[], coalesce((select case build when 'a' then 'b' else 'a' end from public.lab_runs
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

# the lab recordings (lab-common.sh): at most every 10 min, those of passed scenarios of the last day once more (a part may have come after the
# run's end), and every lab recording older than 2 days
if [ ! -f "$LAB_DIR/rec-prune.at" ] || [ -n "$(find "$LAB_DIR/rec-prune.at" -mmin +10 2>/dev/null)" ]; then
  touch "$LAB_DIR/rec-prune.at"
  # shellcheck disable=SC2046
  rec_delete $(q <<< "select coalesce(data->>'rec_del', '') from public.lab_runs where finished_at > now() - interval '1 day'")
  rec_prune 2
fi
# LAB_ONLY=<device> limits the pass to one device (tests)
for d in $(q -v only="${LAB_ONLY:-}" <<< "select name from public.lab_devices where enabled and last_seen > now() - interval '3 minutes' and (:'only' = '' or name = :'only') order by name"); do
  auto_dev "$d"
done
exit 0
