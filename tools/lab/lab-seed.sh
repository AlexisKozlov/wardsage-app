#!/usr/bin/env bash
# Seeds ONE fresh pending request for the device's game account on STAGING, copied from lab_fixtures.
#   lab-seed.sh <device> equip_city [a|b]     a new «Надеть в игре» plan (fixture equip_a / equip_b)
#   lab-seed.sh <device> --pick <a|b>        prints the build to seed: the one NOT worn now (see worn_build), else the hint
#   lab-seed.sh <device> locate               a new «Найти в игре» request (the ascend target is picked from the newest snapshot, see pick_locate)
#   lab-seed.sh <device> --pick-locate        dry run: prints the Locate target it would pick (inserts nothing)
#   lab-seed.sh <device> sell_mark            a new «Чистка склада» list of junk gear from the newest snapshot (see pick_sell)
#   lab-seed.sh <device> --pick-sell          dry run: prints the sell list it would seed (inserts nothing)
#   lab-seed.sh <device> equip_hide_worn      an equip plan of ONE item worn by another hero (the app unticks «Скрыть надетое»), from the snapshot
#   lab-seed.sh <device> equip_artifact       an equip plan of the hero's worn gear + an artifact it does not wear (only the artifact changes)
#   lab-seed.sh <device> locate_recast        a «Найти в игре» request, purpose recast (a recastable item from the snapshot)
#   lab-seed.sh <device> locate_art           a «Найти в игре» request, purpose artPromote (a promotable artifact from the snapshot)
#   lab-seed.sh <device> --pick-hide-worn | --pick-artifact | --pick-recast | --pick-art   dry runs of these pickers
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
# snap_pd <device id>: the newest snapshot's page_data (or nothing)
snap_pd() { q -v d="$1" <<< "select page_data from public.snapshots sn join public.lab_devices dv on dv.account_id = sn.account_id
                              where dv.id = :'d'::uuid and sn.page_data is not null order by sn.created_at desc limit 1" 2>/dev/null || true; }
# nlocates <device id> <purpose>: locate requests of that purpose so far (the pickers rotate among their first 4 candidates by it)
nlocates() { q -v d="$1" -v p="$2" <<< "select count(*) from public.item_locates l join public.lab_devices dv on dv.account_id = l.account_id
                                       where dv.id = :'d'::uuid and l.purpose = :'p'" 2>/dev/null || echo 0; }
# pick_recast <device id>: "uid<TAB>slot<TAB>level<TAB>stars<TAB>worn|free" of an item the site lets recast (lib/recast.ts recastItem,
# data/recast.json rule: iStarLvl in rule.stars, level >= rule.minLevel, >= rule.subs sub stats, each of a known pool for that quality).
# Not worn first; never a lab fixture item; rotates among up to 4.
PICK_RECAST_PY='
import sys, json
web, n = sys.argv[1], int(sys.argv[2]); keep = set(int(x) for x in sys.argv[3].split(",") if x.strip().isdigit())
rc = json.load(open(web + "/data/recast.json")); r = rc["rule"]
pd = json.load(sys.stdin) or {}
def ok(i):
    st = i.get("st") or 0; xs = i.get("x") or []
    if st not in r["stars"] or int(i.get("lv") or 0) < r["minLevel"] or len(xs) < r["subs"]: return False
    for x in xs[:4]:
        pool = rc["byStat"].get(str(x[0])) if x and x[0] is not None else None
        if pool is None or str(st) not in (rc["pools"].get(str(pool)) or {}).get("st", {}): return False
    return True
c = sorted([i for i in pd.get("items") or [] if ok(i) and int(i["u"]) not in keep], key=lambda i: (1 if i.get("h") else 0, int(i["u"])))
if not c: sys.exit(0)
pool = [i for i in c if bool(i.get("h")) == bool(c[0].get("h"))][:4]
i = pool[n % len(pool)]
print("%d\t%s\t%d\t%d\t%s" % (int(i["u"]), i.get("sl"), int(i.get("lv") or 0), int(i.get("st") or 0), "worn" if i.get("h") else "free"))
'
# pick_art <device id>: "uid<TAB>name<TAB>stage<TAB>worn|free" of an artifact the site lets promote (lib/locate/artifact.ts promotable:
# stage st < MAX_STAGE 5, lib/artifact-plan.ts). Not worn first, then the highest stage; rotates among up to 4.
PICK_ART_PY='
import sys, json
n = int(sys.argv[1])
pd = json.load(sys.stdin) or {}
arts = pd.get("arts") or []
spare = {}
for a in arts:
    if not a.get("h"): spare[a.get("id")] = spare.get(a.get("id"), 0) + 1
# an artifact with a spare copy of its own kind first (the promotion takes copies), then not worn, then the highest stage
c = sorted([a for a in arts if int(a.get("st") or 0) < 5], key=lambda a: (0 if spare.get(a.get("id"), 0) - (0 if a.get("h") else 1) > 0 else 1, 1 if a.get("h") else 0, -int(a.get("st") or 0), -int(a.get("lv") or 0), a["u"]))
if not c: sys.exit(0)
pool = c[:4]
a = pool[n % len(pool)]
print("%s\t%s\t%d\t%s" % (a["u"], (a.get("n") or {}).get("en") or "?", int(a.get("st") or 0), "worn" if a.get("h") else "free"))
'
# pick_equip <device id> <hide_worn|artifact>: the JSON {hero_uid, hero_name, hero_name_en, items, note} of an equip plan for the hero of
# the equip fixtures, built from the newest snapshot's page_data (plan items as lib/plans/display.ts stores them: slot, uid, name, setName,
# setId, level, stars, mainStat, fromHeroUid, fromHeroName, icon, cur).
#   hide_worn: ONE item worn by ANOTHER hero (the app unticks «Скрыть надетое» to find it); the strongest one, rotating among 4.
#   artifact:  the hero's own 5 worn items (nothing to change) + an artifact the hero does not wear (items[0].artifact {uid, id}, as the
#              site's POST /api/plans): only the artifact goes on. Only one the hero CAN wear (the site's canWear, lib/artifacts.ts, on
#              data/artifact-fx.json: an exclusive one only for its own hero, else no class mask or the hero's class in it) and free (worn by
#              nobody); one worn by another hero (the take-over pop-up) only when no free one fits.
PICK_EQUIP_PY='
import sys, json
mode, n, fx = sys.argv[1], int(sys.argv[2]), json.loads(sys.argv[3])
afx = json.load(open(sys.argv[4] + "/data/artifact-fx.json")) if len(sys.argv) > 4 else {}
pd = json.load(sys.stdin) or {}
hero = int(fx["hero_uid"]); sets = pd.get("sets") or {}
names = {int(h["u"]): h.get("n") or {} for h in pd.get("heroes") or [] if h.get("u") is not None}
SL = [("Weapon", "Оружие"), ("Armor", "Броня"), ("Bracer", "Наручи"), ("Amulet", "Амулет"), ("Ring", "Кольцо")]
def lt(o): o = o or {}; return {"ru": o.get("ru") or o.get("en") or "", "en": o.get("en") or o.get("ru") or ""}
items = [i for i in pd.get("items") or [] if isinstance(i.get("sl"), int) and 0 <= i["sl"] < 5]
mine = {i["sl"]: i for i in items if int(i.get("h") or 0) == hero}
def plan_item(i, frm):
    st = sets.get(str(i.get("s"))) or {}
    it = {"slot": i["sl"], "uid": int(i["u"]), "slotName": {"en": SL[i["sl"]][0], "ru": SL[i["sl"]][1]}, "name": lt(i.get("n")),
          "setName": lt(st.get("n")) if st else None, "setId": i.get("s"), "level": int(i.get("lv") or 0), "stars": int(i.get("st") or 0),
          "mainStat": {"en": "", "ru": ""}, "icon": i.get("ic"), "fromHeroUid": frm, "fromHeroName": lt(names.get(frm)).get("ru") if frm else None}
    c = mine.get(i["sl"])
    if c is not None and int(c["u"]) != int(i["u"]): it["cur"] = {"uid": int(c["u"]), "name": lt(c.get("n")), "level": int(c.get("lv") or 0), "stars": int(c.get("st") or 0)}
    return it
if mode == "hide_worn":
    c = sorted([i for i in items if int(i.get("h") or 0) not in (0, hero)], key=lambda i: (-int(i.get("st") or 0), -int(i.get("lv") or 0), int(i["u"])))
    if not c: sys.exit(0)
    i = c[:4][n % len(c[:4])]
    out = [plan_item(i, int(i["h"]))]
    note = "uid %d slot %d from hero %d (%s)" % (int(i["u"]), i["sl"], int(i["h"]), lt(names.get(int(i["h"])))["en"])
else:
    if len(mine) < 1: sys.exit(0)
    worn = [a["u"] for a in pd.get("arts") or [] if int(a.get("h") or 0) == hero]
    hr = next((h for h in pd.get("heroes") or [] if int(h.get("u") or 0) == hero), {})
    hid, hc = int(hr.get("id") or 0), int(hr.get("c") or 0)
    # the site canWear (lib/artifacts.ts): why it fits, or None
    def fits(a):
        m = afx.get(str(a.get("id")))
        if not m: return None
        if int(m.get("x") or 0): return "exclusive to this hero (id %d)" % hid if int(m["x"]) == hid else None
        p = int(m.get("p") or 0)
        if not p: return "any class"
        return "class mask %d has the hero class %d" % (p, hc) if hc & p else None
    ok = [a for a in pd.get("arts") or [] if int(a.get("h") or 0) != hero and fits(a)]
    free = [a for a in ok if not int(a.get("h") or 0)]
    c = sorted(free or ok, key=lambda a: (-int(a.get("st") or 0), -int(a.get("lv") or 0), a["u"]))
    if not c: sys.exit(0)
    a = c[:4][n % len(c[:4])]
    out = [plan_item(mine[s], 0) for s in sorted(mine)]
    out[0]["artifact"] = {"uid": int(a["u"]), "id": int(a["id"])}
    note = "artifact %s %s id %s (%s; eligible: %s; %d of %d artifacts fit, %d free), the hero (id %d, class %d) wears %s" % (
        a["u"], lt(a.get("n"))["en"], a["id"], "free" if free else "worn by hero %s: take-over, no free one fits" % a.get("h"), fits(a),
        len(ok), len(pd.get("arts") or []), len(free), hid, hc, ",".join(str(w) for w in worn) or "none")
print(json.dumps({"hero_uid": hero, "hero_name": fx.get("hero_name"), "hero_name_en": fx.get("hero_name_en"), "items": out, "note": note}, ensure_ascii=False))
'
fx_keep() { q -v d="$1" <<< "select coalesce(string_agg(u, ','), '') from (select (i->>'uid') u from public.lab_fixtures f, jsonb_array_elements(coalesce(f.payload->'items', '[]'::jsonb)) i
  where f.device_id = :'d'::uuid union all select f.payload->>'item_uid' from public.lab_fixtures f where f.device_id = :'d'::uuid) x where u ~ '^[0-9]+$'" 2>/dev/null || true; }
pick_recast() { local pd; pd=$(snap_pd "$1"); [ -n "$pd" ] || return 0; python3 -I -c "$PICK_RECAST_PY" "$LAB_WEB_DIR" "$(nlocates "$1" recast)" "$(fx_keep "$1")" <<< "$pd" 2>/dev/null || true; }
pick_art() { local pd; pd=$(snap_pd "$1"); [ -n "$pd" ] || return 0; python3 -I -c "$PICK_ART_PY" "$(nlocates "$1" artPromote)" <<< "$pd" 2>/dev/null || true; }
pick_equip() {
  local pd fx n
  pd=$(snap_pd "$1"); [ -n "$pd" ] || return 0
  fx=$(q -v d="$1" <<< "select payload - 'items' from public.lab_fixtures where device_id = :'d'::uuid and name in ('equip_a', 'equip_b') order by name limit 1") || return 0
  [ -n "$fx" ] || return 0
  n=$(q -v d="$1" <<< "select count(*) from public.equip_plans p join public.lab_devices dv on dv.account_id = p.account_id where dv.id = :'d'::uuid") || n=0
  python3 -I -c "$PICK_EQUIP_PY" "$2" "${n:-0}" "$fx" "$LAB_WEB_DIR" <<< "$pd" 2>/dev/null || true
}
case "$what" in
  --pick-recast) r=$(pick_recast "$(dev_id "$dev")"); echo "${r:-no recastable item in the newest snapshot}"; exit 0 ;;
  --pick-art) r=$(pick_art "$(dev_id "$dev")"); echo "${r:-no promotable artifact in the newest snapshot}"; exit 0 ;;
  --pick-hide-worn|--pick-artifact) m=${what#--pick-}; m=${m//-/_}; r=$(pick_equip "$(dev_id "$dev")" "$m")
    [ -n "$r" ] && python3 -I -c 'import sys,json; p=json.load(sys.stdin); print(p["note"]); [print("  slot %d uid %d from %s%s" % (i["slot"], i["uid"], i["fromHeroUid"], " artifact %s" % i["artifact"] if "artifact" in i else "")) for i in p["items"]]' <<< "$r" || echo "nothing to seed"
    exit 0 ;;
esac
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
case "$what" in equip_city|equip_hide_worn|equip_artifact) names="'equip_a','equip_b'" ;; locate) names="'locate'" ;; locate_recast|locate_art|sell_mark|sell_after_restart) names="" ;; *) echo "unknown scenario" >&2; exit 2 ;; esac
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
  equip_hide_worn|equip_artifact)
    plan=$(pick_equip "$id" "${what#equip_}")
    if [ -z "$plan" ]; then echo "lab-seed: WARNING nothing in the snapshot for $what, skipped" >&2; exit 3; fi
    echo "lab-seed: $what $(python3 -I -c 'import sys,json; print(json.load(sys.stdin)["note"])' <<< "$plan")" >&2
    [ -n "${LAB_DRY_SEED:-}" ] && { echo "lab-seed: DRY, not inserted: $plan" >&2; exit 3; }
    q -v d="$id" -v p="$plan" <<< "
      begin;
      update public.equip_plans set status = 'cancelled', done_at = now()
        where status = 'pending' and account_id = (select account_id from public.lab_devices where id = :'d'::uuid);
      insert into public.equip_plans (user_id, account_id, hero_uid, hero_name, hero_name_en, items)
        select a.user_id, a.id, (:'p'::jsonb->>'hero_uid')::bigint, :'p'::jsonb->>'hero_name', :'p'::jsonb->>'hero_name_en', :'p'::jsonb->'items'
        from public.lab_devices dv join public.game_accounts a on a.id = dv.account_id
        where dv.id = :'d'::uuid
        returning id;
      commit;" ;;
  locate_recast|locate_art)
    if [ "$what" = locate_recast ]; then pick=$(pick_recast "$id"); purpose=recast; else pick=$(pick_art "$id"); purpose=artPromote; fi
    if [ -z "$pick" ]; then echo "lab-seed: WARNING no eligible target ($purpose) in the snapshot, $what skipped" >&2; exit 3; fi
    uid=${pick%%$'\t'*}
    echo "lab-seed: $what target $pick" | tr '\t' ' ' >&2
    [ -n "${LAB_DRY_SEED:-}" ] && { echo "lab-seed: DRY, not inserted" >&2; exit 3; }
    q -v d="$id" -v u="$uid" -v p="$purpose" <<< "
      begin;
      update public.item_locates set status = 'cancelled', done_at = now()
        where status = 'pending' and account_id = (select account_id from public.lab_devices where id = :'d'::uuid);
      insert into public.item_locates (user_id, account_id, item_uid, purpose)
        select a.user_id, a.id, :'u'::bigint, :'p'
        from public.lab_devices dv join public.game_accounts a on a.id = dv.account_id
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
