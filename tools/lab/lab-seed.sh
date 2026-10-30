#!/usr/bin/env bash
# Seeds ONE fresh pending request for the device's game account on STAGING, copied from lab_fixtures.
#   lab-seed.sh <device> equip_city [a|b]     a new «Надеть в игре» plan (fixture equip_a / equip_b)
#   lab-seed.sh <device> --pick <a|b>        prints the build to seed: the one NOT worn now (see worn_build), else the hint
#   lab-seed.sh <device> locate               a new «Найти в игре» request (the ascend target is picked from the newest snapshot, see pick_locate)
#   lab-seed.sh <device> --pick-locate        dry run: prints the Locate target it would pick (inserts nothing)
#   lab-seed.sh <device> sell_mark            a new «Чистка склада» list of junk gear from the newest snapshot (see pick_sell)
#   lab-seed.sh <device> --pick-sell          dry run: prints the sell list it would seed (inserts nothing)
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
# pick_sell <device id>: JSON array of StoredSellItem ({uid, slot, name, setName, level, stars}, lib/sell/types.ts) of junk gear from
# the newest snapshot's page_data, or nothing. Rule (the site's sellable + offered rule, lib/sell/display.ts isSellable and
# lib/sell/candidates.ts): in the bag (h 0), not locked (lk 0), no special effect (ef empty); never an item of a lab fixture
# (equip_a / equip_b builds, the locate target) or of a pending equip plan; the lowest quality (st) first, then the lowest level;
# only items below +12; at most LAB_SELL_N (default 8). The app only marks them; «Продать» is the player's and never pressed.
SELL_N=${LAB_SELL_N:-8}
PICK_SELL_PY='
import sys, json
n = int(sys.argv[1]); keep = set(int(x) for x in sys.argv[2].split(",") if x.strip().isdigit())
pd = json.load(sys.stdin) or {}
sets = pd.get("sets") or {}
def lt(o): o = o or {}; return {"ru": o.get("ru") or o.get("en") or "", "en": o.get("en") or o.get("ru") or ""}
c = [i for i in pd.get("items") or [] if not i.get("h") and not i.get("lk") and not i.get("ef") and int(i["u"]) not in keep and int(i.get("lv") or 0) < 12]
c.sort(key=lambda i: (int(i.get("st") or 0), int(i.get("lv") or 0), int(i["u"])))
out = []
for i in c[:n]:
    st = sets.get(str(i.get("s"))) if i.get("s") is not None else None
    out.append({"uid": int(i["u"]), "slot": i.get("sl", -1) if i.get("sl") is not None else -1, "name": lt(i.get("n")),
                "setName": lt(st.get("n")) if st else None, "level": int(i.get("lv") or 0), "stars": int(i.get("st") or 0)})
if out: print(json.dumps(out, ensure_ascii=False))
'
pick_sell() {
  local pd keep
  pd=$(q -v d="$1" <<< "select page_data from public.snapshots sn join public.lab_devices dv on dv.account_id = sn.account_id
                        where dv.id = :'d'::uuid and sn.page_data is not null order by sn.created_at desc limit 1") || return 0
  [ -n "$pd" ] || return 0
  keep=$(q -v d="$1" <<< "
    select coalesce(string_agg(distinct u, ','), '') from (
      select (i->>'uid') u from public.lab_fixtures f, jsonb_array_elements(coalesce(f.payload->'items', '[]'::jsonb)) i where f.device_id = :'d'::uuid
      union all select (i->'cur'->>'uid') from public.lab_fixtures f, jsonb_array_elements(coalesce(f.payload->'items', '[]'::jsonb)) i where f.device_id = :'d'::uuid
      union all select f.payload->>'item_uid' from public.lab_fixtures f where f.device_id = :'d'::uuid
      union all select (i->>'uid') from public.equip_plans p join public.lab_devices dv on dv.account_id = p.account_id, jsonb_array_elements(p.items) i
        where dv.id = :'d'::uuid and p.status = 'pending'
      union all select l.item_uid::text from public.item_locates l join public.lab_devices dv on dv.account_id = l.account_id where dv.id = :'d'::uuid and l.status = 'pending'
    ) x where u ~ '^[0-9]+$'") || keep=""
  python3 -I -c "$PICK_SELL_PY" "$SELL_N" "$keep" <<< "$pd" 2>/dev/null || true
}
if [ "$what" = --pick-sell ]; then
  r=$(pick_sell "$(dev_id "$dev")")
  [ -n "$r" ] && python3 -I -c 'import sys,json; [print("%d\tslot %s\t+%d\t%d*\t%s" % (i["uid"], i["slot"], i["level"], i["stars"], i["name"]["en"])) for i in json.load(sys.stdin)]' <<< "$r" || echo "no junk gear in the newest snapshot"
  exit 0
fi
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
case "$what" in equip_city) names="'equip_a','equip_b'" ;; locate) names="'locate'" ;; sell_mark|sell_after_restart) names="" ;; *) echo "unknown scenario" >&2; exit 2 ;; esac
if [ -n "$names" ]; then
  have=$(q -v d="$id" <<< "select count(*) from public.lab_devices dv join public.lab_fixtures x on x.device_id = dv.id
    where dv.id = :'d'::uuid and dv.account_id is not null and x.name in ($names)")
  [ "$have" -ge 1 ] || { echo "seed failed: no fixture or no game account for '$dev'" >&2; exit 1; }
fi

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
  sell_mark|sell_after_restart)
    items=$(pick_sell "$id")
    if [ -z "$items" ]; then echo "lab-seed: WARNING no junk gear in the snapshot, sell skipped" >&2; exit 3; fi
    echo "lab-seed: sell list of $(python3 -I -c 'import sys,json; print(len(json.load(sys.stdin)))' <<< "$items") items" >&2
    [ -n "${LAB_DRY_SEED:-}" ] && { echo "lab-seed: DRY, not inserted: $items" >&2; exit 3; }
    q -v d="$id" -v it="$items" <<< "
      begin;
      update public.sell_plans set status = 'cancelled', done_at = now()
        where status = 'pending' and account_id = (select account_id from public.lab_devices where id = :'d'::uuid);
      insert into public.sell_plans (user_id, account_id, items, kind)
        select a.user_id, a.id, :'it'::jsonb, 'equip'
        from public.lab_devices dv join public.game_accounts a on a.id = dv.account_id
        where dv.id = :'d'::uuid
        returning id;
      commit;" ;;
  *) echo "unknown scenario" >&2; exit 2 ;;
esac | { read -r row || true; [ -n "${row:-}" ] && echo "$row" || { echo "seed failed: no fixture or no game account for '$dev'" >&2; exit 1; }; }
