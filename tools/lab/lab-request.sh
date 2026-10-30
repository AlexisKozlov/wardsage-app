#!/usr/bin/env bash
# Queues one test-lab run for a device (staging). The run starts when lab-loop.sh --until-done <device> is started by hand
# (or by the cron pass lab-loop.sh --auto when the device is online).
#   lab-request.sh <device>                       all scenarios (LAB_SCENARIOS in lab-common.sh), in their order
#   lab-request.sh <device> --only a,b            only these, in the default order
#   lab-request.sh <device> <scenario>...         these, in the given order
# Scenarios: equip_city, equip_hide_worn, equip_artifact, locate, locate_recast, locate_art, sell_mark, sell_after_restart;
# not in the default run: explore (the explorer over the game's safe UI, ~10 min; e.g. lab-request.sh laptop --only explore).
# The equip build alternates by itself: A, then B, then A ... (fixtures equip_a / equip_b, see lab-fixture.sh).
set -euo pipefail
. "$(dirname "$0")/lab-common.sh"

dev=${1:-}; shift || true
[ -n "$dev" ] || { sed -n 2,8p "$0" >&2; exit 2; }
id=$(dev_id "$dev")
if [ "${1:-}" = --only ]; then
  want=",${2:-},"; [ "$want" != ",," ] || { echo "--only needs a list" >&2; exit 2; }
  for s in ${2//,/ }; do case " $LAB_SCENARIOS $LAB_EXTRA_SCENARIOS " in *" $s "*) ;; *) echo "unknown scenario '$s' ($LAB_SCENARIOS $LAB_EXTRA_SCENARIOS)" >&2; exit 2 ;; esac; done
  sel=(); for s in $LAB_SCENARIOS $LAB_EXTRA_SCENARIOS; do [[ "$want" == *",$s,"* ]] && sel+=("$s"); done
  set -- "${sel[@]}"
fi
[ $# -gt 0 ] || set -- $LAB_SCENARIOS
for s in "$@"; do
  case " $LAB_SCENARIOS $LAB_EXTRA_SCENARIOS " in *" $s "*) ;; *) echo "unknown scenario '$s' ($LAB_SCENARIOS $LAB_EXTRA_SCENARIOS)" >&2; exit 2 ;; esac
done
arr="{$(IFS=,; echo "$*")}"

open=$(q -v d="$id" <<< "select count(*) from public.lab_runs where device_id = :'d'::uuid and state in ('queued', 'running')")
[ "$open" = 0 ] || { echo "device '$dev' already has an open run" >&2; exit 1; }
q -v d="$id" -v s="$arr" <<< "
  insert into public.lab_runs (device_id, scenarios, build)
  values (:'d'::uuid, :'s'::text[],
    coalesce((select case build when 'a' then 'b' else 'a' end from public.lab_runs
              where device_id = :'d'::uuid and state <> 'cancelled' order by created_at desc limit 1), 'a'))
  returning 'run queued: scenarios ' || array_to_string(scenarios, ', ') || ', equip build ' || upper(build)"
echo "the cron pass (lab-loop.sh --auto) starts it when the laptop is online; or run by hand: lab-loop.sh --until-done $dev"
