#!/usr/bin/env bash
# Seeds ONE fresh pending request for the device's game account on STAGING, copied from lab_fixtures.
#   lab-seed.sh <device> equip_city [a|b]     a new «Надеть в игре» plan (fixture equip_a / equip_b)
#   lab-seed.sh <device> --pick <a|b>        prints the build to seed: the one NOT worn now (see worn_build), else the hint
#   lab-seed.sh <device> locate               a new «Найти в игре» request (the ascend target is picked from the newest snapshot, see pick_locate)
#   lab-seed.sh <device> --pick-locate        dry run: prints the Locate target it would pick (inserts nothing)
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
# pick_locate <device id>: prints "uid<TAB>slot<TAB>set name<TAB>worn|free" of an item the game's Ascension list shows, or nothing.
# Rule = the site's (wardsage-web lib/ascension.ts): iLevel >= ADVANCE.level, iStarLvl is a key of ADVANCE.quality, iItemId has an entry
# in ADVANCE.items (data/equip-advance.json, T1 -> T2). Items not worn by a hero (iHeroId 0) come first; rotates among up to 4
# candidates by the number of locate requests the account had so far, so consecutive runs use different items.
LAB_WEB_DIR=${LAB_WEB_DIR:-$HOME/wardsage/wardsage-web}
PICK_PY='
import sys, json
web, n = sys.argv[1], int(sys.argv[2])
adv = json.load(open(web + "/data/equip-advance.json"))
ref = json.load(open(web + "/data/gamedata.json"))["ref"]
eq = json.load(sys.stdin) or []
cand = []
for e in eq:
    if int(e.get("iLevel") or 0) < adv["level"] or str(e.get("iStarLvl")) not in adv["quality"] or str(e.get("iItemId")) not in adv["items"]: continue
    cand.append((1 if int(e.get("iHeroId") or 0) else 0, int(e["iItemUid"]), e))
if not cand: sys.exit(0)
cand.sort(key=lambda c: (c[0], c[1]))
pool = [c for c in cand if c[0] == cand[0][0]][:4]
w, uid, e = pool[n % len(pool)]
t = ref["equipment"].get(str(e["iItemId"])) or {}
slots = ["Weapon", "Armor", "Bracer", "Amulet", "Ring"]
sl = t.get("sl"); sl = slots[sl] if isinstance(sl, int) and 0 <= sl < 5 else "?"
sn = ((ref["sets"].get(str(t.get("s"))) or {}).get("n") or {}).get("en") or "?"
print("%d\t%s\t%s\t%s" % (uid, sl, sn, "worn" if w else "free"))
'
pick_locate() {
  local eq n
  eq=$(q -v d="$1" <<< "select raw->'equipment' from public.snapshots sn join public.lab_devices dv on dv.account_id = sn.account_id
                        where dv.id = :'d'::uuid order by sn.created_at desc limit 1") || return 0
  n=$(q -v d="$1" <<< "select count(*) from public.item_locates l join public.lab_devices dv on dv.account_id = l.account_id where dv.id = :'d'::uuid") || n=0
  [ -n "$eq" ] || return 0
  python3 -I -c "$PICK_PY" "$LAB_WEB_DIR" "${n:-0}" <<< "$eq" 2>/dev/null || true
}
if [ "$what" = --pick-locate ]; then
  r=$(pick_locate "$(dev_id "$dev")")
  [ -n "$r" ] && echo "$r" || echo "no ascendable item (T1 +16) in the newest snapshot"
  exit 0
fi
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
    # the fixture's item may be gone from the Ascension list (already ascended): pick a valid one from the snapshot; none -> skip
    pick=$(pick_locate "$id")
    if [ -z "$pick" ]; then echo "lab-seed: WARNING no ascendable item (T1 +16) in the snapshot, Locate skipped" >&2; exit 3; fi
    IFS=$'\t' read -r uid slot setn worn <<< "$pick"
    echo "lab-seed: locate target uid=$uid slot=$slot set=$setn ($worn)" >&2
    q -v d="$id" -v u="$uid" <<< "
      begin;
      update public.item_locates set status = 'cancelled', done_at = now()
        where status = 'pending' and account_id = (select account_id from public.lab_devices where id = :'d'::uuid);
      insert into public.item_locates (user_id, account_id, item_uid, purpose)
        select a.user_id, a.id, :'u'::bigint, x.payload->>'purpose'
        from public.lab_devices dv
        join public.game_accounts a on a.id = dv.account_id
        join public.lab_fixtures x on x.device_id = dv.id and x.name = 'locate'
        where dv.id = :'d'::uuid
        returning id;
      commit;" ;;
  *) echo "unknown scenario" >&2; exit 2 ;;
esac | { read -r row || true; [ -n "${row:-}" ] && echo "$row" || { echo "seed failed: no fixture or no game account for '$dev'" >&2; exit 1; }; }
