#!/usr/bin/env bash
# Drives the test-lab runs of the devices (staging). Two modes:
#   lab-loop.sh --until-done <device> [--timeout MIN]   FOREGROUND, by hand: drives the one queued run of a device (default 90 min).
#                                                       Exits 0 when the run finished (Telegram message + report file done), 1 on timeout / failure.
#   lab-loop.sh --auto                                  ONE pass, for cron every minute (flock; exits at once when nothing is to do). A run starts ONLY from a
#                                                       queued lab-request (lab-request.sh) of an enabled device that is online (heartbeat < 3 min) with the game
#                                                       running; nothing starts by itself (no run for a new test build, no start_game, no explorer: that one only
#                                                       with lab-request.sh <device> --only explore). A run in progress advances (the same state machine, state in
#                                                       lab_runs). Always exits 0.
#                                                       Env: TEST_FEED (feed dir, default /srv/wardsage/test-feed), LAB_NO_TG=1 (print instead of Telegram), LAB_ONLY=<device> (tests).
#
# Per-build result: $LAB_DIR/build-<N>.status (N = 4th part of the version the run tested: PASS / FAIL / SKIP / PENDING per scenario, frame_audit, OVERALL),
# rewritten after every scenario of a run.
# A run interrupted by the laptop going offline (no heartbeat for LAB_OFFLINE_SEC, default 180 s) is not failed: it waits, and when the laptop is back
# its request is restarted from the interrupted scenario (jobs and the pending request cancelled, update/restart/ready again, the run's time limit
# extended by the time gone). The runner (v25) does not look at the user's input at all.
# Before EVERY scenario (explore aside) the test app is told to go to the city (rec job «city», src/LabRec.cs; the app's own road through its click gate), and
# an extra scenario (lab-common.sh LAB_SCENARIOS_EXTRA) then has its prep run (rec job «prep <name>»: the game put into a non-standard start state by the app's
# own click gate) before its request is seeded. A prep that fails is a warning (the scenario is not run: nothing would be proven), not a verdict on the app.
#
# Flow (state kept in lab_runs.phase, so a restart of this script resumes; each phase is idempotent):
#   wait_online -> update_app -> restart_app -> wait_ready -> [city [-> prep] -> seed -> wait done]* -> wait logs
#   -> upload_logs -> screenshot -> finish -> report
# The runner on the laptop only executes the whitelisted jobs queued here; it never clicks (start_game starts the game exe, no launcher, no clicks).
set -uo pipefail
. "$(dirname "$0")/lab-common.sh"
HERE=$(dirname "$(readlink -f "$0")")
USAGE="usage: lab-loop.sh --until-done <device> [--timeout MIN]  |  lab-loop.sh --auto"

dev=""; timeout_min=90; tick=1; auto=0
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
TEST_LOG=${LAB_TEST_LOG:-$HOME/wardsage/work/test-autorelease.log}   # tools/test-autorelease.sh: "<time> published 1.6.66.N (sha8)"

say() { echo "$(date -u +%H:%M:%S) $*"; }
now() { date +%s; }
# the game version of the UI graph on main (src/UiGraphData.cs): the explorer runs again when it changes
graph_ver() { git -C "$HERE/../.." show origin/main:src/UiGraphData.cs 2>/dev/null | sed -nE 's/.*GameVersion = "([0-9.]+)".*/\1/p' | head -1; }

# ---- run row helpers (RUN = run id, DEV = device id)
rget()   { q -v r="$RUN" <<< "select $1 from public.lab_runs where id = :'r'::uuid"; }
dget()   { q -v r="$RUN" -v k="$1" <<< "select coalesce(data->>:'k', '') from public.lab_runs where id = :'r'::uuid"; }
dset()   { q -v r="$RUN" -v k="$1" -v v="$2" <<< "update public.lab_runs set data = data || jsonb_build_object(:'k', :'v'::text) where id = :'r'::uuid"; }
# (every phase change also adds "<old phase>=<seconds in it> " to data.phase_log: the report sums them per phase)
phase()  { q -v r="$RUN" -v p="$1" <<< "update public.lab_runs set phase = :'p', data = data || jsonb_build_object('phase_at', extract(epoch from now())::bigint::text,
  'phase_log', coalesce(data->>'phase_log', '') || coalesce(phase, '') || '=' || greatest(0, extract(epoch from now())::bigint - coalesce(nullif(data->>'phase_at', '')::bigint, extract(epoch from now())::bigint)) || ' ') where id = :'r'::uuid"; say "phase: $1"; }
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


# ---- the build gate: the per-build result file
# run_build: the version this run tests: the one the app was updated to, else the one the run was queued for
run_build() { local v; v=$(dget want_version); [ -n "$v" ] || v=$(dget target_version); echo "$v"; }
# write_build_status: $LAB_DIR/build-<N>.status of the run's build, from the run's steps (atomic write). Never fails the caller.
write_build_status() {
  local v n sha js tmp s nstd=0
  # a run of extra (non-standard) scenarios only: it never writes the build gate's file; mixed with the standard ones their lines are listed apart (no OVERALL effect)
  for s in "${scenarios[@]:-}"; do case " $LAB_SCENARIOS_EXTRA explore " in *" $s "*) ;; *) nstd=1 ;; esac; done
  [ "$nstd" = 1 ] || return 0
  v=$(run_build); [[ "$v" =~ ^[0-9]{1,9}(\.[0-9]{1,9}){1,3}$ ]] || return 0
  n=${v##*.}
  sha=$(sed -nE "s/^[^ ]+ published ${v//./\\.} \(([0-9a-f]+)\)\$/\1/p" "$TEST_LOG" 2>/dev/null | tail -1)
  js=$(q -v r="$RUN" <<< "select jsonb_build_object('state', state, 'scenarios', scenarios, 'steps', steps, 'data', data)::text from public.lab_runs where id = :'r'::uuid") || return 0
  [ -n "$js" ] || return 0
  tmp=$(mktemp "$LAB_DIR/.build-status.XXXXXX") || return 0
  if jq -r --argjson ex "$(printf '%s\n' $LAB_SCENARIOS_EXTRA | jq -R . | jq -cs .)" --arg ver "$v" --arg sha "${sha:-?}" --arg run "${RUN:0:8}" --arg now "$(date -u +%FT%TZ)" '
      . as $r
      | def st($sc): [$r.steps[] | select(.name == $sc or (.name | startswith($sc + " ")))] | last;
      def verdict($sc): st($sc) as $s | if $s == null then "PENDING" elif $s.ok == false then "FAIL" elif ($s.warn // false) then "SKIP" else "PASS" end;
      def line($sc): st($sc) as $s | $sc + " " + verdict($sc) + (if $s == null then "" else " " + (($s.seconds // 0) | floor | tostring) + "s" + (if (($s.note // "") != "") then " - " + ($s.note | gsub("[\n\r]"; " ") | .[0:160]) else "" end) end);
      ($r.scenarios | map(select(. != "explore"))) as $all
      | ($all | map(select(. as $x | ($ex | index($x)) == null))) as $sc
      | ($all | map(select(. as $x | ($ex | index($x)) != null))) as $xs
      | ($sc | map(select(endswith("_hint")))) as $hint
      | ($sc | map(verdict(.))) as $vs
      | ($hint | map(verdict(.))) as $hv
      | ([$r.steps[] | select(.ok == false) | .name as $n | select(([$all[] | . as $x | select($n == $x or ($n | startswith($x + " ")))] | length) == 0)]) as $other
      | "build \($ver) sha \($sha)",
        "run \($run) state \($r.state)\(if ($r.data.superseded_by // "") != "" then " (superseded by " + $r.data.superseded_by + ")" else "" end)\(if ($r.data.abort // "") != "" then " (" + $r.data.abort + ")" else "" end) updated \($now)",
        ($sc[] | line(.)),
        ($xs[] | "extra " + line(.)),
        (if ($hint | length) > 0 then "frame_audit " + (if ($hv | index("FAIL")) then "FAIL" elif ($hv | all(. == "PASS")) then "PASS" else "PENDING" end) + " (" + ($hint | join(", ")) + ")" else empty end),
        ($other[] | "run_step " + .name + " FAIL" + (if ((.note // "") != "") then " - " + (.note | gsub("[\n\r]"; " ") | .[0:160]) else "" end)),
        "OVERALL " + (if (($vs | index("FAIL")) or ($other | length) > 0) then "FAIL" elif ($vs | all(. == "PASS")) then "PASS" else "INCOMPLETE" end)
    ' <<< "$js" > "$tmp" 2>/dev/null; then
    chmod 644 "$tmp"; mv -f "$tmp" "$LAB_DIR/build-$n.status"
  else rm -f "$tmp"; fi
  return 0
}
# resume_run <seconds gone>: the laptop was away (offline, or this loop was not running): restart the interrupted step of the run
OFFLINE=0
resume_run() {
  local gap=$1 ph0 idx r t sc0 rid tot n
  ph0=$(rget phase); idx=$(dget scn_idx); idx=${idx:-0}
  say "run ${RUN:0:8}: the laptop is back after $gap s (phase $ph0, scenario $idx): restarting the interrupted step"
  tot=$(dget offline_total); n=$(dget resumed)
  dset offline_total "$(( ${tot:-0} + gap ))"; dset resumed "$(( ${n:-0} + 1 ))"; dset offline_at ""
  deadline=$(( deadline + gap ))
  [ "$ph0" = report ] && return 0
  sc0=${scenarios[$idx]:-}; r=$(dget row); t=$(scn_tbl "$sc0")
  [ -n "$r" ] && [ -n "$t" ] && q -v r="$r" <<< "update public.$t set status = 'cancelled', done_at = now() where id = :'r'::uuid and status = 'pending'" >/dev/null
  rid=$(dget rec_id); [ -n "$rid" ] && dset rec_del "$(dget rec_del) $rid"
  q -v r="$RUN" <<< "update public.lab_jobs set state = 'cancelled', finished_at = now() where run_id = :'r'::uuid and state in ('queued', 'claimed')" >/dev/null
  dset resume_idx "$idx"; dset rec_id ""; dset row ""; dset rec_for ""; dset setup_for ""; dset kick_for ""; dset setup_stage ""; dset prep_note ""; dset late_sent ""; dset job ""; dset job_timeout ""; dset sar_ready ""; dset sar_at ""
  phase wait_online
}
# scn_start: the scenario index a run continues with after app_ready (0, or the interrupted one after a resume)
scn_start() { local i; i=$(dget resume_idx); echo "${i:-0}"; }

# ---- the state machine
end_run() {   # $1 = final state
  local rid; rid=$(dget rec_id)
  if [ -n "$rid" ]; then rec_tag "$rid"; dset rec_id ""; fi   # a run that ended inside a scenario (timeout): its recording is kept
  # shellcheck disable=SC2046
  rec_delete $(dget rec_del)
  q -v d="$DEV" -v r="$RUN" <<< "update public.lab_jobs set state = 'cancelled', finished_at = now() where run_id = :'r'::uuid and state = 'queued'" >/dev/null
  q -v r="$RUN" -v s="$1" <<< "update public.lab_runs set state = :'s', finished_at = now() where id = :'r'::uuid" >/dev/null
  write_build_status
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
EXPLORE_BUDGET=${LAB_EXPLORE_BUDGET:-60}   # the explorer's forms a run
EXPLORE_POLL=${LAB_EXPLORE_POLL:-30}       # a result job every 30 s
EXPLORE_MAX=${LAB_EXPLORE_MAX:-2400}       # the app's own cap is 30 min
NOPROG_DEV=${LAB_NOPROGRESS_DEV:-40}   # 40 s without a pilot line (old runner: a log line) while a scenario waits (runner v17 beats every 5 s)
REPEAT_N=${LAB_REPEAT_N:-10}           # the same newest pilot line this many times in a row (each a new pilot_at) = a pilot that spins
READY_DEV=${LAB_READY_DEV:-360}        # the laptop read takes ~50 s, 107-190 s on the slow laptop (run d0605ccc: 1.6.66.474 ready 3.5 min after start)
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
# setup_failed <why>: a city / prep job did not answer. The runner / app is "old" (no setup for the rest of the run) when the runner rejects the arguments, or when
# no setup command has worked yet in this run; a later silence is a stall of that one command (the app was busy), the next scenario tries again
setup_failed() { case "$1" in bad_args*) dset setup_off 1 ;; no_ack*|queued*|claimed*|expired*|cancelled*) [ -n "$(dget setup_ok)" ] || dset setup_off 1 ;; esac; }
# scn_log_verdict <scenario>: stdin = the app's newest log lines; prints "ok|<note>" or "fail|<reason>" (Russian). Only the lines after the end of the scenario's prep
# are looked at (the app logs "lab: setup <name> ok: ..." there).
scn_log_verdict() {
  local txt tail hero last fail
  txt=$(cat)
  tail=$(awk '/lab: setup .* ok:/ { buf = "" ; next } { buf = buf $0 "\n" } END { printf "%s", buf }' <<< "$txt")
  [ -n "$tail" ] || tail=$txt
  if [ -z "$txt" ]; then echo "ok|журнал приложения не получен, проверен только итог плана"; return; fi
  case "$1" in
    equip_hero_filter_open|equip_hero_filter_closed)
      # clicks into empty cells: the hero pilot gave up on the grid ("no click near the grid's middle brings up another hero", "the plan's hero at place N is not brought up",
      # the filter pop-up not recognised) - even if a later try finished the plan
      fail=$(grep -E "hero: .* -> failed \((no click near the grid|the plan's hero at place|gear filter pop-up not recognised)" <<< "$tail" | head -1 | sed -E 's/^\s+|\s+$//g' | cut -c1-200)
      if [ -n "$fail" ]; then echo "fail|клики в пустые ячейки сетки героев: $fail"; return; fi
      hero=$(grep -E "^[0-9-]+ [0-9:]+ +hero: plan " <<< "$tail" | tail -1)
      if [[ "$hero" == *"filter=open"* ]]; then echo "fail|план выполнен, но панель фильтра героев осталась открытой"; return; fi
      last=$(sed -nE 's/.*camps=([0-9,]*) class=(True|False).*/фракции в фильтре: \1, класс: \2/p' <<< "$hero")
      echo "ok|${last:-состояние фильтра в журнале не найдено}" ;;
    equip_item_filter_open)
      fail=$(grep -E "filter: item .* -> failed" <<< "$tail" | head -1 | sed -E 's/^\s+|\s+$//g' | cut -c1-200)
      if [ -n "$fail" ]; then echo "fail|фильтр вещей не удалось снять: $fail"; return; fi
      echo "ok|" ;;
    *) echo "ok|" ;;
  esac
}
# lab_settings_start: before the first scenario the app is told to force auto click (locateAuto, artAuto) on for the run (src/LabRec.cs "settings"; a
# saved config with auto click off must not stall the run); phase lab_settings waits for its ack
lab_settings_start() {
  local sj
  if sj=$(mkjob rec '{"cmd":"settings"}'); then dset job "$sj"; phase lab_settings; else say "settings: the job could not be queued"; phase scenario; fi
}
# next_scn: the scenario ended (pass, fail or skip): its request is closed at once, so the app stops guiding before the next one. The site's
# long poll returns only pending rows, so the app drops a guide whose row is anything else (app/Overlay.cs locateReq = null); 'cancelled'
# (not 'done': the app did not finish it, the last press is the player's). An equip plan that passed is already 'done' (the app closed it).
next_scn() {
  local r t sc0; sc0=${scenarios[$(dget scn_idx)]:-}; r=$(dget row); t=$(scn_tbl "$sc0")
  ( write_build_status ) &   # (off the critical path: the next scenario starts at once; end_run writes it again)
  dset last_rec_job ""
  rec_end
  if is_hint "$sc0"; then
    # a hint scenario turned auto click off: the stop command (always sent here) turns it back on - NOT waited for: the next scenario's start command (queued after it,
    # the runner keeps the order) ends the hint mode anyway
    [ -n "$(dget last_rec_job)" ] || { local sj; sj=$(mkjob rec '{"cmd":"stop"}') && dset last_rec_job "$sj" || true; }
    [ -n "$r" ] && [ -n "$t" ] && q -v r="$r" <<< "update public.$t set status = 'cancelled', done_at = now() where id = :'r'::uuid and status = 'pending'" >/dev/null
    dset row ""; dset last_scn_end "$(now)"; dset scn_idx "$(( $(dget scn_idx) + 1 ))"; dset sar_ready ""; phase scenario; return
  fi
  [ -n "$r" ] && [ -n "$t" ] && q -v r="$r" <<< "update public.$t set status = 'cancelled', done_at = now() where id = :'r'::uuid and status = 'pending'" >/dev/null
  dset row ""; dset last_scn_end "$(now)"; dset scn_idx "$(( $(dget scn_idx) + 1 ))"; dset sar_ready ""; phase scenario
}
# the scenario's screen recording (runner v16+, src/LabRec.cs): started before the request is seeded; when the scenario ends it is stopped.
# Passed (the last step ok, not a warning): the recording is deleted on the server (at the run's end, when its last part is in; the --auto
# pass deletes them again for a day, in case a part came later). Else the last step keeps its id (rec) for the report and lab-rec.sh.
rec_end() {
  local id; id=$(dget rec_id); dset rec_for ""
  [ -n "$id" ] || return 0
  local sj; sj=$(mkjob rec '{"cmd":"stop"}') && dset last_rec_job "$sj" || true
  local pass; pass=$(q -v r="$RUN" <<< "select coalesce((s->>'ok')::boolean and coalesce(s->>'warn', '') = '', false) from public.lab_runs r, jsonb_array_elements(r.steps) with ordinality e(s, n) where r.id = :'r'::uuid order by n desc limit 1")
  if [ "$pass" = t ]; then dset rec_del "$(dget rec_del) $id"; say "recording $id: passed, deleted at the end"
  else rec_tag "$id"; say "recording $id: kept"; fi
  dset rec_id ""
}
# rec_tag <id>: the last step names the recording
rec_tag() { q -v r="$RUN" -v id="$1" <<< "update public.lab_runs set steps = jsonb_set(steps, array[(jsonb_array_length(steps) - 1)::text, 'rec'], to_jsonb(:'id'::text)) where id = :'r'::uuid and jsonb_array_length(steps) > 0" >/dev/null; }
# scenario -> table of its request
scn_tbl() { case "$1" in equip_*) echo equip_plans ;; locate*) echo item_locates ;; sell_*) echo sell_plans ;; esac; }
# the storage cleanup may click only these targets (src/ClickGate.cs, owner Sell); the runner (v11+) reports any other one as "never"
SELL_OK_RE='click gate Sell: allow (HeroBack|CityInventory|InvEquipTab|InvMaterialsTab|InvBulkEnter|InvBulkCell|InvListDrag|OfferClose|UnknownBack|CityBack|EscapeBack|MapClose|ArtTab|ArtBulkEnter|ArtBulkCell|ArtListDrag)\b'
# hero_grades keeps an HMAC of the game account (a server secret, never read here), not the account: the newest updated_at of the table
# stands for the device's account (on staging only the lab's app sends grades as a rule)
LOGS_WAIT=${LAB_LOGS_WAIT:-15}
FA_QUIET=${LAB_FA_QUIET:-12}   # a hint scenario: no new frame shot for this long after the first one = the steps shown without a click are done
FA_MAX=${LAB_FA_MAX:-120}      # a hint scenario: at most this long   # max wait at the end for the app's next log batch (the report's figures come from it)
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
  OFFLINE=0
  if [ "$(dstat "(last_seen is null or last_seen < now() - interval '${LAB_OFFLINE_SEC:-180} seconds')")" = t ]; then
    [ -n "$(dget offline_at)" ] || { dset offline_at "$(now)"; say "run ${RUN:0:8}: the laptop is offline, the run waits"; }
    OFFLINE=1; return
  fi
  local oa al gap=0; oa=$(dget offline_at); al=$(dget alive_at)
  if [ -n "$oa" ]; then gap=$(( $(now) - oa )); elif [ -n "$al" ] && [ $(( $(now) - al )) -gt 120 ]; then gap=$(( $(now) - al )); fi
  [ "$gap" -gt 0 ] && resume_run "$gap"
  dset alive_at "$(now)"
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
      elif [ "$(phase_age)" -gt "$([ -n "$(dget resumed)" ] && echo 3600 || echo 600)" ]; then
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
            step app_ready true 0 "already ready, no restart"; grades_mark; dset scn_idx "$(scn_start)"; lab_settings_start; return
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
          grades_mark; dset scn_idx "$(scn_start)"; lab_settings_start
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
        grades_mark; dset scn_idx "$(scn_start)"; lab_settings_start
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
      # ONE kick-off per scenario: the jobs of its start are queued back to back - recording start, game to the front, "city", the prep (extra scenarios), the
      # runner's mark - and the runner takes them in this order (one claim each, no pause between); only the LAST one (the mark) is waited for (phase scn_kick),
      # the answers of the others are read then. Strict order kept: the mark (it forgets the old stop / ready / pilot markers, and with it the setup's own log
      # lines) is DONE before the request is seeded (a mark that ran after a fast app's first lines wiped them: lab cdc2e581). sell_after_restart: its
      # recording / setup only after the app's restart (a restart ends any recording).
      mk="$i/$(dget sar_ready)"
      if [ "$(dget kick_for)" != "$mk" ]; then
        dset kick_for "$mk"; dset mark_for "$mk"; dset prep_note ""; dset rec_job ""; dset city_job ""; dset prep_job ""
        if [ "$sc" != explore ] && { [ "$sc" != sell_after_restart ] || [ -n "$(dget sar_ready)" ]; }; then
          prep=$(scn_prep "$sc")
          if [ -n "$(dget setup_off)" ] && [ -n "$prep" ]; then
            warn "$sc" 0 "подготовка «$prep» не выполнена: приложение или раннер не знает команду (нужны тестовая сборка и раннер v25); сценарий не запускался"; next_scn; return
          fi
          if [[ "$sc" =~ ^[a-z0-9_]{1,40}$ ]]; then
            dset rec_for "$i"
            r=$(mkjob rec "{\"cmd\":\"start\",\"scenario\":\"$sc\",\"run\":\"$RUN\"}") && dset rec_job "$r" || say "recording: the job could not be queued"
          fi
          mkjob game_front >/dev/null || true   # (the setup clicks only with the game in front)
          if [ -z "$(dget setup_off)" ]; then
            r=$(mkjob rec '{"cmd":"city"}') && dset city_job "$r" || say "setup: the city job could not be queued"
            if [ -n "$prep" ]; then r=$(mkjob rec "{\"cmd\":\"prep\",\"name\":\"$prep\"}") && dset prep_job "$r" || say "setup: the prep job could not be queued"; fi
          fi
        else
          mkjob game_front >/dev/null || true   # the game window to the front before each scenario
        fi
        if mj=$(mkjob mark "{\"scn\":\"$sc\"}"); then dset job "$mj"; dset job_timeout ""; phase scn_kick; return; fi
        dset mark_at "$(now)"
      fi
      if [ "$(scn_seed "$sc")" = equip_city ]; then
        # never seed the build the hero already wears (then the app has nothing to change): the other one, whatever the last run did
        pb=$("$HERE/lab-seed.sh" "$dev" --pick "$build" 2>/dev/null) || pb=""
        if [[ "$pb" =~ ^[ab]$ ]] && [ "$pb" != "$build" ]; then
          say "equip build $build -> $pb (the other one is worn now)"; build=$pb
          q -v r="$RUN" -v b="$build" <<< "update public.lab_runs set build = :'b' where id = :'r'::uuid" >/dev/null
        fi
      fi
      if [ "$sc" = explore ]; then
        # the explorer (src/Explorer.cs): no request row; the start set rotates over the runs (rot = explore runs finished before on this device)
        rot=$(q -v d="$DEV" <<< "select count(*) from public.lab_runs where device_id = :'d'::uuid and 'explore' = any(scenarios) and state in ('done', 'failed', 'timeout')")
        j=$(mkjob explore "{\"cmd\":\"start\",\"budget\":$EXPLORE_BUDGET,\"rot\":${rot:-0},\"run\":\"$RUN\"}") || { step explore false 0 "the job could not be queued"; FAILED=1; next_scn; return; }
        dset job "$j"; dset job_timeout ""; dset explore_rot "${rot:-0}"; dset graph_version "$(graph_ver)"; dset row ""; dset seeded_at "$(now)"; phase explore_start; return
      fi
      if [ "$sc" = sell_after_restart ] && [ -z "$(dget sar_ready)" ]; then
        r=$(mkjob restart_app) && dset job "$r" && dset job_timeout "" && phase scn_restart; return
      fi
      seederr=$(mktemp)
      row=$("$HERE/lab-seed.sh" "$dev" "$(scn_seed "$sc")" "$build" 2>"$seederr") || row=""
      sw=$(grep -m1 WARNING "$seederr" | sed -E 's/^lab-seed: WARNING //'); cat "$seederr" >&2; rm -f "$seederr"
      if [ -z "$row" ] && [ -n "$sw" ]; then warn "$sc" 0 "$sw"; next_scn; return; fi
      if [ -z "$row" ]; then step "$sc" false 0 "seed failed (fixture missing?)"; FAILED=1; next_scn; return; fi
      dset row "$row"; dset seeded_at "$(now)"; dset fa_n 0; dset fa_at "$(now)"; dset fa_poll 0; RP_AT=""; RP_LINE=""; RP_N=0; phase wait_scn ;;
    scn_mark)
      # the mark job is done (or failed / expired / 30 s passed: the scenario goes on, a mark is a help, never a verdict) -> seed
      j=$(dget job); st=$(jstate "$j")
      case "$st" in
        done|failed|expired|cancelled) phase scenario ;;
        *) if [ "$(phase_age)" -gt 30 ]; then say "mark: no answer in 30 s"; phase scenario; fi ;;
      esac ;;
    scn_rec)
      # the recording is a help, never a verdict: no answer in 40 s (old runner, old app) and the scenario goes on without one
      j=$(dget job); st=$(jstate "$j")
      case "$st" in
        done) id=$(jres "$j" rec); [[ "$id" =~ $LAB_REC_RX ]] && dset rec_id "$id"; say "recording ${id:-?} started"; phase scenario ;;
        failed|expired|cancelled) say "recording not started: $(jres "$j" error)$(jres "$j" state)"
          # a hint scenario's auto click is turned off by this command only: without its answer the scenario is not run
          sc=${scenarios[$(dget scn_idx)]}; if is_hint "$sc"; then warn "$sc" 0 "режим подсказок не включён (команда записи не прошла)"; next_scn; else phase scenario; fi ;;
        *) if [ "$(phase_age)" -gt 40 ]; then
             q -v j="$j" <<< "update public.lab_jobs set state = 'cancelled', finished_at = now() where id = :'j'::uuid and state = 'queued'" >/dev/null
             say "recording: no answer in 40 s"
             sc=${scenarios[$(dget scn_idx)]}; if is_hint "$sc"; then warn "$sc" 0 "режим подсказок не включён (нет ответа на команду записи)"; next_scn; else phase scenario; fi
           fi ;;
      esac ;;
    scn_kick)
      # the kick-off's last job (the mark) is answered: read the answers of the recording, the city and the prep (queued before it, so they are over)
      i=$(dget scn_idx); sc=${scenarios[$i]}; j=$(dget job); st=$(jstate "$j")
      case "$st" in
        done|failed|expired|cancelled) ;;
        *) if [ "$(phase_age)" -gt 150 ]; then say "kick-off: no answer in 150 s"; else return; fi ;;
      esac
      dset mark_at "$(now)"
      rj=$(dget rec_job)
      if [ -n "$rj" ]; then
        if [ "$(jstate "$rj")" = done ]; then id=$(jres "$rj" rec); [[ "$id" =~ $LAB_REC_RX ]] && dset rec_id "$id"; say "recording ${id:-?} started"
        else
          say "recording not started: $(jres "$rj" error)$(jstate "$rj")"
          # a hint scenario's auto click is turned off by this command only: without its answer the scenario is not run
          if is_hint "$sc"; then warn "$sc" 0 "режим подсказок не включён (команда записи не прошла)"; next_scn; return; fi
        fi
      fi
      cj=$(dget city_job)
      if [ -n "$cj" ]; then
        if [ "$(jstate "$cj")" = done ]; then say "city: reached ($sc)"; dset setup_ok 1
        else
          why="$(jres "$cj" why)$(jres "$cj" error)"; [ -n "$why" ] || why=$(jstate "$cj"); say "city: NOT reached before $sc: $why"
          # an old app or runner does not know the command: no setup for the rest of the run (the prep scenarios are skipped then)
          setup_failed "$why"
        fi
      fi
      pj=$(dget prep_job); prep=$(scn_prep "$sc")
      if [ -n "$pj" ]; then
        if [ "$(jstate "$pj")" = done ]; then say "prep $prep: $(jres "$pj" why)"; dset setup_ok 1; dset prep_note "$(jres "$pj" why)"
        else
          why="$(jres "$pj" why)$(jres "$pj" error)"; [ -n "$why" ] || why=$(jstate "$pj")
          setup_failed "$why"
          warn "$sc" 0 "подготовка «$prep» не удалась: ${why:0:160}; сценарий не запускался (приложению нечего показывать, пока игра не в нужном состоянии)"; next_scn; return
        fi
      fi
      phase scenario ;;
    scn_setup)
      # the app's answer to "city" / "prep <name>" (it comes when its clicks are over: city 30 s at most, a prep 20 s; the runner waits 45 s for it)
      i=$(dget scn_idx); sc=${scenarios[$i]}; j=$(dget job); st=$(jstate "$j"); stage=$(dget setup_stage)
      case "$st" in
        done|failed|expired|cancelled) ;;
        *) if [ "$(phase_age)" -gt 60 ]; then st=expired; else return; fi ;;
      esac
      why="$(jres "$j" why)$(jres "$j" error)"; [ -n "$why" ] || why=$st
      if [ "$stage" = city ]; then
        if [ "$st" = done ]; then say "city: reached ($sc)"
        else
          say "city: NOT reached before $sc: $why"
          # an old app or runner does not know the command: no setup for the rest of the run (a prep scenario is then skipped below)
          setup_failed "$why"
        fi
        prep=$(scn_prep "$sc")
        if [ -n "$prep" ]; then
          if [ -n "$(dget setup_off)" ]; then warn "$sc" 0 "подготовка «$prep» не выполнена: приложение или раннер не знает команду (нужны тестовая сборка и раннер v25); сценарий не запускался"; next_scn; return; fi
          if r=$(mkjob rec "{\"cmd\":\"prep\",\"name\":\"$prep\"}"); then dset job "$r"; dset setup_stage prep; phase scn_setup; return; fi
          warn "$sc" 0 "подготовка «$prep»: команду не удалось поставить в очередь; сценарий не запускался"; next_scn; return
        fi
        phase scenario
      else
        prep=$(scn_prep "$sc")
        if [ "$st" = done ]; then say "prep $prep: $(jres "$j" why)"; dset prep_note "$(jres "$j" why)"; phase scenario
        else
          setup_failed "$why"
          warn "$sc" 0 "подготовка «$prep» не удалась: ${why:0:160}; сценарий не запускался (приложению нечего показывать, пока игра не в нужном состоянии)"; next_scn
        fi
      fi ;;
    scn_check)
      # a filter scenario's plan is done: its verdict reads the app's own log (the runner uploaded the newest lines; the lines after the prep's end)
      i=$(dget scn_idx); sc=${scenarios[$i]}; j=$(dget chk_job); st=$(jstate "$j"); sa=$(dget seeded_at); label=$sc
      case "$st" in
        done|failed|expired|cancelled) ;;
        *) if [ "$(phase_age)" -le 45 ]; then return; fi ;;
      esac
      txt=""; [ "$st" = done ] && txt=$(q -v j="$j" <<< "select convert_from(data, 'UTF8') from public.lab_files where job_id = :'j'::uuid order by created_at desc limit 1" 2>/dev/null)
      verdict=$(scn_log_verdict "$sc" <<< "$txt"); vk=${verdict%%|*}; vn=${verdict#*|}
      if [ "$vk" = fail ]; then step "$label" false "$(( $(now) - ${sa:-0} ))" "$vn"; FAILED=1; next_scn
      else step "$label" true "$(( $(now) - ${sa:-0} ))" "$(scn_pass_note "$sc")${vn:+; $vn}"; next_scn; fi ;;
    lab_settings)
      j=$(dget job); st=$(jstate "$j")
      case "$st" in
        done) say "app settings forced (auto click on): acked"; phase scenario ;;
        failed|expired|cancelled) say "app settings: no ack ($(jres "$j" error)$(jres "$j" state)); each scenario's start forces them too"; phase scenario ;;
        *) if [ "$(phase_age)" -gt 40 ]; then say "app settings: no ack in 40 s"; phase scenario; fi ;;
      esac ;;
    scn_unhint)
      # after a hint scenario: the app's ack of the stop command = auto click is back on; no ack in 40 s = the app's own 3 min fallback restores it
      j=$(dget job); st=$(jstate "$j")
      case "$st" in
        done) say "hint mode off (app acked)"; phase scenario ;;
        failed|expired|cancelled) say "hint mode off: no ack ($(jres "$j" error)$(jres "$j" state)), the app restores auto click itself"; phase scenario ;;
        *) if [ "$(phase_age)" -gt 40 ]; then say "hint mode off: no ack in 40 s"; phase scenario; fi ;;
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
    explore_start)
      if wait_job explore x 60; then
        j=$(dget job)
        if [ "$JOBOK" = 1 ]; then say "explore: started (rotation $(dget explore_rot), budget $EXPLORE_BUDGET)"; dset explore_poll 0; dset job ""; phase explore_run
        else [ -n "$(dget job_timeout)" ] || step explore false "$(jsecs "$j")" "start: $(jres "$j" error)$(jres "$j" state)"; FAILED=1; next_scn; fi
      fi ;;
    explore_run)
      # every EXPLORE_POLL s one result job: the runner uploads explore.json once the app's run is over (state done / stopped)
      j=$(dget job); sa=$(dget seeded_at)
      if [ -n "$j" ]; then
        st=$(jstate "$j")
        case "$st" in
          done|failed|expired|cancelled)
            dset job ""
            es=$(jres "$j" state)
            if [ "$st" = done ] && { [ "$es" = done ] || [ "$es" = stopped ]; }; then
              note="форм $(jres "$j" visited) (без расхождений $(jres "$j" okforms)), расхождений $(jres "$j" mismatches), всплывающих закрыто $(jres "$j" popups), нажатий $(jres "$j" presses), $(jres "$j" secs) с; файл explore-${RUN:0:8}.json: tools/lab/explore-diff.sh"
              if [ "$es" = done ]; then step explore true "$(( $(now) - ${sa:-0} ))" "$note"
              else warn explore "$(( $(now) - ${sa:-0} ))" "$note; остановка: $(jres "$j" why)"; fi
              next_scn; return
            fi ;;
        esac
      elif [ $(( $(now) - $(dget explore_poll) )) -ge "$EXPLORE_POLL" ]; then
        r=$(mkjob explore "{\"cmd\":\"result\",\"run\":\"$RUN\"}") && dset job "$r"; dset explore_poll "$(now)"
      fi
      if [ $(( $(now) - ${sa:-0} )) -gt "$EXPLORE_MAX" ]; then
        mkjob explore '{"cmd":"stop"}' >/dev/null || true
        fail_scn explore "не закончился за $(( EXPLORE_MAX / 60 )) мин" "" ""
      fi ;;
    wait_scn)
      i=$(dget scn_idx); sc=${scenarios[$i]}; row=$(dget row)
      tbl=$(scn_tbl "$sc")
      case "$sc" in equip_city) maxs=600; label="$sc (build ${build^^})" ;; equip_*) maxs=600; label="$sc" ;; sell_*) maxs=480; label="$sc" ;; *) maxs=300; label="$sc" ;; esac
      st=$(q -v r="$row" <<< "select status || '|' || coalesce(round(extract(epoch from done_at - created_at))::int, 0) from public.$tbl where id = :'r'::uuid")
      if is_hint "$sc"; then
        # hint mode (auto click off, nobody clicks): the frame audit's shots of this scenario (src/FrameAudit.cs) are the verdict. Pass = shots of
        # 3 steps, or at least one and nothing new for FA_QUIET s (the next step waits on the player's click), and no FLAG; then cancelled
        sa=$(dget seeded_at)
        if [ "${st%%|*}" = done ]; then fail_scn "$label" "в режиме подсказок запрос выполнен сам: автоклик не выключился" "$tbl" "$row"; return; fi
        if [ $(( $(now) - $(dget fa_poll) )) -ge 4 ]; then
          dset fa_poll "$(now)"
          read -r fn fk fs ff <<< "$(bash "$HERE/frame-audit.sh" "$RUN" --count "$sc" 2>/dev/null)"
          fn=${fn:-0}; fk=${fk:-0}; fs=${fs:-0}
          [ "$fn" != "$(dget fa_n)" ] && { dset fa_n "$fn"; dset fa_at "$(now)"; }
          if [ "$fk" -gt 0 ]; then fail_scn "$label" "рамка под подозрением ($fk из $fn): ${ff:0:180}" "$tbl" "$row"; return; fi
          if [ "$fs" -ge 3 ] || { [ "$fn" -ge 1 ] && [ $(( $(now) - $(dget fa_at) )) -ge "$FA_QUIET" ]; }; then
            # equip_hint_filter_open: the hero grid's filter pop-up is open; the control that closes it (the funnel, hero.filter / hero.filterClose) has to carry a
            # frame - a step label with no frame on it is the bug this scenario looks for (lab 4512f342)
            if [ "$sc" = equip_hint_filter_open ] && ! bash "$HERE/frame-audit.sh" "$RUN" --targets "$sc" 2>/dev/null | grep -qE '^hero\.filter(Close)?$'; then
              fail_scn "$label" "панель фильтра героев открыта, подсказка есть, а рамки на кнопке, которая её закрывает, нет (снято рамок: $fn)" "$tbl" "$row"; return
            fi
            note="рамок проверено $fn (шагов $fs), под подозрением нет; без кликов, запрос отменён"
            [ "$sc" = equip_hint_filter_open ] && note="фильтр героев открыт: рамка стоит на кнопке, закрывающей панель (подпись без рамки не найдена); без кликов, запрос отменён. $note"
            step "$label" true "$(( $(now) - ${sa:-0} ))" "$note"; next_scn; return
          fi
        fi
        if [ "$(phase_age)" -gt "$FA_MAX" ]; then fail_scn "$label" "за $FA_MAX с снято рамок: $(dget fa_n) (нужна хотя бы одна)" "$tbl" "$row"; fi
        return
      fi
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
        # no final line from the runner (its mark / offset lost it): the verdict comes from the app's collected log (FinRe: "sell: done selected a/b missing m extra e"),
        # asked for once before a "no progress" stop
        if [ -z "$fin" ] && [ "$(dget sl_for)" = "$(dget scn_idx)" ]; then
          sj=$(dget sl_job); sjs=$(jstate "$sj")
          if [[ "$sjs" =~ ^(done|failed|expired|cancelled)$ ]] || [ "$(( $(now) - $(dget sl_at) ))" -gt 30 ]; then
            fl=$(q -v d="$DEV" -v s="${sa:-0}" <<< "select l.lines from public.app_test_logs l join public.lab_devices dv on dv.account_id = l.account_id where dv.id = :'d'::uuid and l.at >= to_timestamp(:'s'::bigint) order by l.id" | grep -oE '(^|[^t] )sell: done selected [0-9]+/[0-9]+ missing [0-9]+ extra [0-9]+' | tail -1)
            [ -n "$fl" ] && { fin=sell; say "sell: the final line is taken from the app's log"; }
          fi
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
        elif [ "$w" != 0 ] && [ "$(dget sl_for)" != "$(dget scn_idx)" ]; then
          # before a "no progress" stop: the app's log is uploaded and read once (the runner may have missed the final line)
          dset sl_for "$(dget scn_idx)"; dset sl_at "$(now)"; dset sl_job "$(mkjob upload_logs '{"lines":1000}')"
        elif [ "$w" != 0 ] && ! [[ "$(jstate "$(dget sl_job)")" =~ ^(done|failed|expired|cancelled)$ ]] && [ "$(( $(now) - $(dget sl_at) ))" -le 30 ]; then :   # the log upload is awaited
        elif [ "$w" != 0 ]; then fail_scn "$label" "$WHY" "$tbl" "$row"
        elif [ "$(phase_age)" -gt "$maxs" ]; then fail_scn "$label" "not done in ${maxs} s" "$tbl" "$row"; fi
        return
      fi
      # equip_toggle_auto_late: the plan waits with auto click off; after 10 s the "settings" command turns it on (the page picks the plan up)
      if [ "$sc" = equip_toggle_auto_late ] && [ -z "$(dget late_sent)" ]; then
        sa=$(dget seeded_at)
        if [ "${st%%|*}" = done ]; then fail_scn "$label" "план выполнен при выключенном автоклике: проверка включения ничего не доказала" "$tbl" "$row"; return; fi
        if [ -n "$sa" ] && [ $(( $(now) - sa )) -ge 10 ]; then
          if lj=$(mkjob rec '{"cmd":"settings"}'); then dset late_sent "$(now)"; dset late_job "$lj"; say "auto click switched on 10 s after the plan was seeded"
          else fail_scn "$label" "команду включения автоклика не удалось поставить в очередь" "$tbl" "$row"; fi
        fi
        return
      fi
      case "$st" in
        done\|*) case "$sc" in
                    equip_hero_filter_open|equip_hero_filter_closed|equip_item_filter_open)
                      if cj=$(mkjob upload_logs '{"lines":1500}'); then dset chk_job "$cj"; phase scn_check; return; fi ;;
                  esac
                  note=$(scn_pass_note "$sc")
                  [ "$sc" = equip_toggle_auto_late ] && note="$note (план выполнен за $(( $(now) - $(dget late_sent) )) с после включения)"
                  step "$label" true "${st#*|}" "$note"; next_scn ;;
        cancelled\|*) step "$label" false "$(phase_age)" "the request was cancelled"; FAILED=1; next_scn ;;
        *) sa=$(dget seeded_at); ma=$(dget mark_at); [ -n "$ma" ] || ma=$sa
           # runner v9+: the app reached its final step (the item is selected, the last press is the player's) = Locate passed
           if [ "$tbl" = item_locates ]; then
             fin=$(dstat "coalesce(status->>'done', '')")
             # the final step of each purpose (runner v13+ reports art): ascend = AscendReady, recast = Done, artPromote = ArtPromote
             case "$sc" in locate|locate_from_bulk_sale) fok='^(ascend|ready)$' ;; locate_recast|locate_recast_item_filter) fok='^ready$' ;; locate_art) fok='^art$' ;; *) fok='^$' ;; esac
             if [ -n "$fin" ] && [[ "$fin" =~ $fok ]]; then
               case "$fin" in ascend) fnote="вещь выбрана в «Вознесении», осталось нажать «Вознести»" ;; art) fnote="копии выбраны в «Мастерской», осталось нажать «Продвинуть»" ;;
                 *) case "$sc" in locate_recast*) fnote="вещь выбрана в «Преобразовании», осталось нажать «Улучшить»" ;; *) fnote="вещь найдена, последний шаг за игроком" ;; esac ;; esac
               case "$sc" in locate_from_bulk_sale) fnote="$fnote (игра стояла в «Массовой продаже»)" ;; locate_recast_item_filter) fnote="$fnote (фильтр вещей был включён)" ;; esac
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

# advance the run: keep driving it in this process (3 s tick) until it ends or its deadline (timeout_min, from started_at); the flock (auto.lock)
# is held meanwhile, so the cron passes of the next minutes exit at once
advance() {
  init_run
  local t0 st p0 p1; t0=$(now)
  while :; do
    p0=$(rget phase); tick
    [ "$OFFLINE" = 0 ] || { [ "$auto" = 0 ] || { say "run ${RUN:0:8}: the laptop is offline, the run waits (next pass)"; return; }; }
    [ -z "$FINAL" ] || { say "run ${RUN:0:8} ended (exit code $FINAL), $(( $(now) - t0 )) s in this pass"; return; }
    st=$(rget state); [[ "$st" =~ ^(queued|running)$ ]] || { say "run ${RUN:0:8} is $st: leaving it"; return; }
    p1=$(rget phase); [ "$p0" != "$p1" ] || sleep "$tick"   # a phase change goes straight on
  done
}

auto_dev() {   # $1 = device name
  dev=$1; DEV=$(dev_id "$dev") || return 0
  exec 9>"$LAB_DIR/loop-$dev.lock"
  flock -n 9 || { exec 9>&-; return 0; }          # a hand-started --until-done owns the device
  local gr st rv rn
  gr=$(dstat "coalesce(status->>'game_running', 'false')")
  # a newer published runner than the one the laptop reports -> ask it to update (one job at a time)
  rv=$(jq -r '.version // empty' "$FEED/lab/WardsageLab.json" 2>/dev/null); rn=$(dstat "coalesce(status->>'runner', '')")
  if [[ "$rv" =~ ^[0-9]{1,9}$ ]] && [[ "$rn" =~ ^[0-9]{1,9}$ ]] && [ "$rv" -gt "$rn" ]; then
    q -v d="$DEV" <<< "insert into public.lab_jobs (device_id, action, args, expires_at)
      select :'d'::uuid, 'update_runner', '{}'::jsonb, now() + interval '20 minutes'
      where not exists (select 1 from public.lab_jobs where device_id = :'d'::uuid and action = 'update_runner' and (state in ('queued', 'claimed') or created_at > now() - interval '10 minutes'))" >/dev/null
  fi
  RUN=$(q -v d="$DEV" <<< "select id from public.lab_runs where device_id = :'d'::uuid and state in ('queued', 'running') order by created_at limit 1")
  if [ -n "$RUN" ]; then
    st=$(rget state)
    # a requested run starts when the game runs; a run in progress is always advanced (it waits itself while the laptop is offline)
    if [ "$st" = running ] || [ "$gr" = true ]; then advance; fi
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
