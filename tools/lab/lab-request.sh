#!/usr/bin/env bash
# Queues one test-lab run for a device (staging). The run starts when lab-loop.sh --until-done <device> is started by hand.
#   lab-request.sh <device> [equip_city] [locate]      (default: both, in this order)
# The equip build alternates by itself: A, then B, then A ... (fixtures equip_a / equip_b, see lab-fixture.sh).
set -euo pipefail
. "$(dirname "$0")/lab-common.sh"

dev=${1:-}; shift || true
[ -n "$dev" ] || { sed -n 2,5p "$0" >&2; exit 2; }
id=$(dev_id "$dev")
[ $# -gt 0 ] || set -- equip_city locate
for s in "$@"; do
  case "$s" in equip_city|locate) ;; *) echo "unknown scenario '$s' (equip_city, locate)" >&2; exit 2 ;; esac
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
echo "now switch the laptop on, start the game, double-click the lab shortcut, then run: lab-loop.sh --until-done $dev"
