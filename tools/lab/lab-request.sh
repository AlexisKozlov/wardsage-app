#!/usr/bin/env bash
# Queues one test-lab run for a device (staging). The run starts when lab-loop.sh --until-done <device> is started by hand
# (or by the cron pass lab-loop.sh --auto when the device is online).
#   lab-request.sh <device>                       all scenarios (LAB_SCENARIOS in lab-common.sh), in their order
#   lab-request.sh <device> --only a,b            only these, in the default order
#   lab-request.sh <device> <scenario>...         these, in the given order
#   lab-request.sh <device> --extra               the NON-STANDARD scenarios (LAB_SCENARIOS_EXTRA in lab-common.sh: the game set up by the app's "prep" command first;
#                                                 filters left open, a tab or a mode other than the start's ...), outside the default run and the build gate
#   lab-request.sh <device> --fuzz N [--seed S] [--nav on|off]
#                                                 the random-start fuzz (docs/specs/navigator.md): N runs of equip_random_start with the seeds S .. S+N-1 (default S = 1; the same
#                                                 seeds on the same build repeat the same starts), the app's navCore flag set by the settings command at the run's start (--nav;
#                                                 the app may not have it yet: then no effect; off = the current behaviour). The report's line:
#                                                 «Успешные запуски со случайного экрана: X/N (Y%), зависаний Z»
# Scenarios: equip_city_hint, equip_city, equip_hide_worn, equip_artifact, locate_hint, locate, ... (the _hint ones: auto click off, hints only,
# the frame audit checks their frames);
# all: equip_city, equip_hide_worn, equip_artifact, locate, locate_recast, locate_art, sell_mark, sell_after_restart;
# not in the default run: --extra (above), explore (the explorer over the game's safe UI, ~10 min; e.g. lab-request.sh laptop --only explore).
# The equip build alternates by itself: A, then B, then A ... (fixtures equip_a / equip_b, see lab-fixture.sh).
set -euo pipefail
. "$(dirname "$0")/lab-common.sh"

dev=${1:-}; shift || true
[ -n "$dev" ] || { sed -n 2,8p "$0" >&2; exit 2; }
id=$(dev_id "$dev")
fuzzdata='{}'
if [ "${1:-}" = --fuzz ]; then
  n=${2:-}; shift 2 || true; seed=1; nav=""
  [[ "$n" =~ ^[0-9]{1,3}$ ]] && [ "$n" -ge 1 ] && [ "$n" -le 200 ] || { echo "--fuzz needs a number of runs, 1..200" >&2; exit 2; }
  while [ $# -gt 0 ]; do
    case "$1" in
      --seed) seed=${2:-}; [[ "$seed" =~ ^[0-9]{1,8}$ ]] || { echo "--seed needs a number" >&2; exit 2; }; shift 2 ;;
      --nav) nav=${2:-}; [[ "$nav" =~ ^(on|off)$ ]] || { echo "--nav needs on or off" >&2; exit 2; }; shift 2 ;;
      *) echo "unknown fuzz option '$1'" >&2; exit 2 ;;
    esac
  done
  fzs=(); for (( k = 0; k < n; k++ )); do fzs+=(equip_random_start); done
  set -- "${fzs[@]}"
  fuzzdata=$(jq -nc --argjson s "$seed" --arg nav "$nav" '{fuzz_seed: $s} + (if $nav != "" then {nav: $nav} else {} end)')
elif [ "${1:-}" = --only ]; then
  want=",${2:-},"; [ "$want" != ",," ] || { echo "--only needs a list" >&2; exit 2; }
  for s in ${2//,/ }; do case " $LAB_ALL_SCENARIOS " in *" $s "*) ;; *) echo "unknown scenario '$s' ($LAB_ALL_SCENARIOS)" >&2; exit 2 ;; esac; done
  sel=(); for s in $LAB_ALL_SCENARIOS; do [[ "$want" == *",$s,"* ]] && sel+=("$s"); done
  set -- "${sel[@]}"
elif [ "${1:-}" = --extra ]; then
  [ $# -eq 1 ] || { echo "--extra takes no list (use --only for some of them)" >&2; exit 2; }
  set -- $LAB_SCENARIOS_EXTRA
fi
[ $# -gt 0 ] || set -- $LAB_SCENARIOS
for s in "$@"; do
  case " $LAB_ALL_SCENARIOS " in *" $s "*) ;; *) echo "unknown scenario '$s' ($LAB_ALL_SCENARIOS)" >&2; exit 2 ;; esac
done
arr="{$(IFS=,; echo "$*")}"

open=$(q -v d="$id" <<< "select count(*) from public.lab_runs where device_id = :'d'::uuid and state in ('queued', 'running')")
[ "$open" = 0 ] || { echo "device '$dev' already has an open run" >&2; exit 1; }
q -v d="$id" -v s="$arr" -v fz="$fuzzdata" <<< "
  insert into public.lab_runs (device_id, scenarios, build, data)
  values (:'d'::uuid, :'s'::text[],
    coalesce((select case build when 'a' then 'b' else 'a' end from public.lab_runs
              where device_id = :'d'::uuid and state <> 'cancelled' order by created_at desc limit 1), 'a'), :'fz'::jsonb)
  returning 'run queued: scenarios ' || array_to_string(scenarios, ', ') || ', equip build ' || upper(build)"
echo "the cron pass (lab-loop.sh --auto) starts it when the laptop is online; or run by hand: lab-loop.sh --until-done $dev"
