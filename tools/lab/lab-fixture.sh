#!/usr/bin/env bash
# Fixtures of the test lab (staging): what lab-seed.sh inserts for the device's game account.
#   lab-fixture.sh capture <device> <equip_a|equip_b|locate> [skip]
#       equip_*: copies the account's most recent equip plan (any status; "skip" = how many newer ones to pass over)
#       locate : copies the account's most recent «Найти в игре» request
#       Make the build / request on the staging site as the device's account first, then capture it.
#   lab-fixture.sh list <device>
set -euo pipefail
. "$(dirname "$0")/lab-common.sh"

cmd=${1:-}; dev=${2:-}; name=${3:-}; skip=${4:-0}
[ -n "$dev" ] || { sed -n 2,9p "$0" >&2; exit 2; }
id=$(dev_id "$dev")
case "$cmd" in
  list)
    q -v d="$id" <<< "select name, captured_at::timestamp(0), left(payload::text, 60) from public.lab_fixtures where device_id = :'d'::uuid order by name"
    ;;
  capture)
    [[ "$skip" =~ ^[0-9]{1,3}$ ]] || { echo "bad skip" >&2; exit 2; }
    acct=$(q -v d="$id" <<< "select coalesce(account_id::text, '') from public.lab_devices where id = :'d'::uuid")
    [ -n "$acct" ] || { echo "device has no game account (lab-mint-token.sh --account)" >&2; exit 1; }
    case "$name" in
      equip_a|equip_b)
        n=$(q -v d="$id" -v n="$name" -v s="$skip" <<< "
          with src as (
            select hero_uid, hero_name, hero_name_en, items from public.equip_plans
            where account_id = (select account_id from public.lab_devices where id = :'d'::uuid)
            order by created_at desc offset :'s'::int limit 1),
          up as (
            insert into public.lab_fixtures (device_id, name, payload)
            select :'d'::uuid, :'n', jsonb_build_object('hero_uid', hero_uid, 'hero_name', hero_name, 'hero_name_en', hero_name_en, 'items', items) from src
            on conflict (device_id, name) do update set payload = excluded.payload, captured_at = now()
            returning 1)
          select count(*) from up") ;;
      locate)
        n=$(q -v d="$id" -v s="$skip" <<< "
          with src as (
            select item_uid, purpose from public.item_locates
            where account_id = (select account_id from public.lab_devices where id = :'d'::uuid)
            order by created_at desc offset :'s'::int limit 1),
          up as (
            insert into public.lab_fixtures (device_id, name, payload)
            select :'d'::uuid, 'locate', jsonb_build_object('item_uid', item_uid, 'purpose', purpose) from src
            on conflict (device_id, name) do update set payload = excluded.payload, captured_at = now()
            returning 1)
          select count(*) from up") ;;
      *) echo "name must be equip_a, equip_b or locate" >&2; exit 2 ;;
    esac
    [ "$n" = 1 ] && echo "fixture '$name' captured for '$dev'" || { echo "nothing to capture: the account has no such saved row" >&2; exit 1; }
    ;;
  *) sed -n 2,9p "$0" >&2; exit 2 ;;
esac
