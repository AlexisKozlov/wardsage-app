#!/usr/bin/env bash
# Seeds ONE fresh pending request for the device's game account on STAGING, copied from lab_fixtures.
#   lab-seed.sh <device> equip_city [a|b]     a new «Надеть в игре» plan (fixture equip_a / equip_b)
#   lab-seed.sh <device> --pick <a|b>        prints the build to seed: the one NOT worn now (see worn_build), else the hint
#   lab-seed.sh <device> locate               a new «Найти в игре» request
# Prints the new row id (a plan / request id, not an account id) on stdout.
#
# Why "after the app restart": the app lists the plans of its account through the long poll and starts by itself only a
# plan that is new (not seen before), created within the last 3 minutes and not before the app started (ui/guide.js
# freshPlans: FRESH_MS=3 min, since=bootAt). An older pending row is only shown, never started. So every seed first
# cancels the account's older pending rows (also the one-pending-per-hero / per-account unique indexes), then inserts
# the new row with created_at = now(). A locate request has no freshness filter: the app shows the newest pending one.
set -euo pipefail
. "$(dirname "$0")/lab-common.sh"

dev=${1:-}; what=${2:-}; build=${3:-a}
[[ "$build" =~ ^[ab]$ ]] || { echo "build must be a or b" >&2; exit 2; }

# worn_build <device id>: a or b when the hero of fixture equip_a / equip_b wears exactly that fixture's items now (the newest staging
# snapshot of the device's account: raw.equipment[].iHeroId / iItemUid), else empty (unknown / neither)
worn_build() {
  q -v d="$1" <<< "
    with s as (select raw->'equipment' eq from public.snapshots sn join public.lab_devices dv on dv.account_id = sn.account_id
               where dv.id = :'d'::uuid order by sn.created_at desc limit 1),
    w as (select f.name, (f.payload->>'hero_uid')::bigint hu,
                 array(select (i->>'uid')::bigint from jsonb_array_elements(f.payload->'items') i) want
          from public.lab_fixtures f where f.device_id = :'d'::uuid and f.name in ('equip_a', 'equip_b'))
    select substr(w.name, 7) from w, s
    where jsonb_typeof(s.eq) = 'array' and cardinality(w.want) > 0
      and w.want <@ array(select (e->>'iItemUid')::bigint from jsonb_array_elements(s.eq) e where (e->>'iHeroId')::bigint = w.hu)
    order by w.name limit 1" 2>/dev/null || true
}
if [ "$what" = --pick ]; then
  id=$(dev_id "$dev")
  worn=$(worn_build "$id")
  case "$worn" in a) echo b ;; b) echo a ;; *) echo "$build" ;; esac
  exit 0
fi
[ -n "$dev" ] && [ -n "$what" ] || { sed -n 2,6p "$0" >&2; exit 2; }
id=$(dev_id "$dev")

# nothing is cancelled unless there is something to insert
case "$what" in equip_city) names="'equip_a','equip_b'" ;; locate) names="'locate'" ;; *) echo "unknown scenario" >&2; exit 2 ;; esac
have=$(q -v d="$id" <<< "select count(*) from public.lab_devices dv join public.lab_fixtures x on x.device_id = dv.id
  where dv.id = :'d'::uuid and dv.account_id is not null and x.name in ($names)")
[ "$have" -ge 1 ] || { echo "seed failed: no fixture or no game account for '$dev'" >&2; exit 1; }

case "$what" in
  equip_city)
    # two statements in one transaction: cancel first, then insert (a data-modifying CTE pair could race on the unique index)
    q -v d="$id" -v f="equip_$build" <<< "
      begin;
      update public.equip_plans set status = 'cancelled', done_at = now()
        where status = 'pending' and account_id = (select account_id from public.lab_devices where id = :'d'::uuid);
      insert into public.equip_plans (user_id, account_id, hero_uid, hero_name, hero_name_en, items)
        select a.user_id, a.id, (x.payload->>'hero_uid')::bigint, x.payload->>'hero_name', x.payload->>'hero_name_en', x.payload->'items'
        from public.lab_devices dv
        join public.game_accounts a on a.id = dv.account_id
        join lateral (select payload from public.lab_fixtures
                      where device_id = dv.id and name in ('equip_a', 'equip_b') order by (name = :'f') desc limit 1) x on true
        where dv.id = :'d'::uuid
        returning id;
      commit;" ;;
  locate)
    q -v d="$id" <<< "
      begin;
      update public.item_locates set status = 'cancelled', done_at = now()
        where status = 'pending' and account_id = (select account_id from public.lab_devices where id = :'d'::uuid);
      insert into public.item_locates (user_id, account_id, item_uid, purpose)
        select a.user_id, a.id, (x.payload->>'item_uid')::bigint, x.payload->>'purpose'
        from public.lab_devices dv
        join public.game_accounts a on a.id = dv.account_id
        join public.lab_fixtures x on x.device_id = dv.id and x.name = 'locate'
        where dv.id = :'d'::uuid
        returning id;
      commit;" ;;
  *) echo "unknown scenario" >&2; exit 2 ;;
esac | { read -r row || true; [ -n "${row:-}" ] && echo "$row" || { echo "seed failed: no fixture or no game account for '$dev'" >&2; exit 1; }; }
