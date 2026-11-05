#!/usr/bin/env bash
# Shared helpers for the test lab server scripts (source this file). STAGING only.
# Never prints tokens, ids of users or accounts.

LAB_DB_CONTAINER=wardsage-staging-db           # the ONLY database these tools talk to
# Where the lab lives. DEFAULT: the test server (docker, the staging Supabase, the recordings and the test feed are there; reached over ONE multiplexed
# ssh connection). LAB_SSH="" (empty) = the old local mode: docker / sudo / files on THIS host. LAB_SSH=user@host = another remote.
LAB_SSH=${LAB_SSH-root@13.140.221.207}
LAB_SSH_KEY=${LAB_SSH_KEY:-$HOME/.ssh/id_lab_ed25519}
LAB_SSH_OPTS=(-o ControlMaster=auto -o "ControlPath=$HOME/.ssh/cm-lab" -o ControlPersist=600 -o BatchMode=yes -o ConnectTimeout=15 -o ServerAliveInterval=20 -i "$LAB_SSH_KEY")
LAB_DIR=${LAB_DIR:-$HOME/wardsage/work/lab}
LAB_ENV=/srv/wardsage/staging/supabase/.env     # staging Supabase env (JWT secret, anon key; on the lab server, or here in local mode)
mkdir -p "$LAB_DIR"

case "$LAB_DB_CONTAINER" in *prod*) echo "lab: refusing to use a prod container" >&2; exit 2 ;; esac

# rsh <command and args...>: runs on the lab server (ssh, every argument quoted for the remote shell; stdin passes through).
# Without LAB_SSH (local mode) the command runs here under sudo (docker, root-only files).
rsh() {
  if [ -n "$LAB_SSH" ]; then
    local c; c=$(printf '%q ' "$@")
    ssh "${LAB_SSH_OPTS[@]}" "$LAB_SSH" "$c"
  else
    sudo -n "$@"
  fi
}

# q [psql args]: SQL from stdin, plain unaligned output. Use -v name=value and :'name' for values.
q() { rsh docker exec -i "$LAB_DB_CONTAINER" psql -U supabase_admin -d postgres -X -q -At -v ON_ERROR_STOP=1 "$@"; }

# env_get KEY: one value of the staging env file (printed to stdout only for the caller's variable; never echo it)
env_get() { rsh grep -E "^$1=" "$LAB_ENV" | head -1 | cut -d= -f2- | tr -d "\"' \r"; }

# dev_id <device name>: the device uuid; fails when unknown
dev_id() {
  [[ "$1" =~ ^[a-z0-9-]{1,32}$ ]] || { echo "lab: bad device name" >&2; return 1; }
  local id; id=$(q -v n="$1" <<< "select id from public.lab_devices where name = :'n'") || return 1
  [ -n "$id" ] || { echo "lab: unknown device '$1'" >&2; return 1; }
  echo "$id"
}

# tg <text>: one Telegram message through the alerts bot (token read from alerts.env, never printed)
tg() {
  local env tok chat
  env=$(sudo -n cat /etc/wardsage/alerts.env 2>/dev/null) || { echo "lab: cannot read alerts.env" >&2; return 1; }
  tok=$(sed -nE 's/^\s*(export\s+)?TG_TOKEN=//p' <<<"$env" | head -1 | tr -d "\"' \r")
  chat=$(sed -nE 's/^\s*(export\s+)?TG_CHAT_ID=//p' <<<"$env" | head -1 | tr -d "\"' \r")
  [ -n "$tok" ] && [ -n "$chat" ] || { echo "lab: telegram token/chat missing" >&2; return 1; }
  curl -fsS -m 20 -o /dev/null "https://api.telegram.org/bot$tok/sendMessage" \
    --data-urlencode "chat_id=$chat" --data-urlencode "text=$1" --data-urlencode "disable_web_page_preview=true" 2>/dev/null \
    || { echo "lab: telegram send failed" >&2; return 1; }
}

# every scenario the lab knows, in the default run order (the storage cleanup last: it leaves the game in «Массовая продажа»;
# the restart one at the very end). lab-request.sh with no list and the auto runs on a new test build run all of them.
# equip_city_hint / locate_hint: the same requests with auto click off (the recorder command names the scenario, the test app turns its auto
# click off for it, app/HostBridge.cs LabScenario): the app only shows hints, nobody clicks; the frame audit's shots of the first steps are the
# verdict (src/FrameAudit.cs, tools/lab/frame-audit.sh), then the request is cancelled
LAB_SCENARIOS="equip_city equip_hide_worn equip_artifact equip_city_hint locate locate_recast locate_art locate_hint sell_mark sell_after_restart"  # each _hint one right after a scenario of its own flow (the game stands on the hero / foundry screens there); the sell ones last: they leave the game on «Массовая продажа», where a hint scenario (auto click off, nobody clicks) only frames «Назад» and never advances
# scenarios run only when asked (lab-request.sh <device> --only explore) or by the --auto pass on their own schedule: explore = the test app's
# explorer (src/Explorer.cs: the game's safe UI against the UI graph; once a day and on every new game version, lab-loop.sh explore_due)
LAB_EXTRA_SCENARIOS="explore"

# NON-STANDARD scenarios (the game is set up in a way the standard ones never meet): requested only with lab-request.sh <device> --extra (all of them) or
# --only <names>, never part of the default run, and their verdicts stay out of the build gate's build-<N>.status (an "extra" line there, no OVERALL effect).
# Each = [prep] + seed + verdict. The prep (the test app's recorder command "prep <name>", src/LabRec.cs LabPrep; runner v25+) puts the game into the start
# state through the app's own click gate; "city" runs first before EVERY scenario (the app's own road back to the city). The app has to cope; a scenario
# that fails because of the app is the finding.
LAB_SCENARIOS_EXTRA="equip_hero_filter_open equip_hero_filter_closed equip_item_filter_open equip_from_materials equip_from_bulk_sale equip_from_artifact locate_from_bulk_sale locate_recast_item_filter sell_from_materials equip_toggle_auto_late equip_hint_filter_open equip_class_filter_gear equip_two_same_class equip_hide_worn_restore"
# the random-start fuzz of docs/specs/navigator.md: lab-request.sh <device> --fuzz N [--seed S] [--nav on|off] queues N of these (seeds S .. S+N-1; the run's data holds
# fuzz_seed and nav). The prep random_start:<seed> (src/LabRec.cs) puts the game on a seeded random safe start; then the normal equip plan, auto click on.
# PASS = the plan is done, no hang (180 s), no forbidden click. The report's line: «Успешные запуски со случайного экрана: X/N (Y%), зависаний Z».
LAB_SCENARIOS_FUZZ="equip_random_start"
# every scenario name the tools accept
LAB_ALL_SCENARIOS="$LAB_SCENARIOS $LAB_SCENARIOS_EXTRA $LAB_SCENARIOS_FUZZ $LAB_EXTRA_SCENARIOS"

# is_hint <scenario>: a hint-mode scenario (auto click off, nobody clicks; the frame audit is the verdict): *_hint and equip_hint_* (src/LabRec.cs IsHintScenario)
is_hint() { case "$1" in *_hint|equip_hint_*) return 0 ;; esac; return 1; }
# scn_seed <scenario>: the lab-seed.sh kind of the scenario's request
scn_seed() {
  case "$1" in
    equip_hero_filter_open|equip_hero_filter_closed|equip_item_filter_open|equip_from_materials|equip_from_bulk_sale|equip_from_artifact|equip_toggle_auto_late|equip_hint_filter_open|equip_random_start|equip_class_filter_gear|equip_two_same_class) echo equip_city ;;
    equip_hide_worn_restore) echo equip_hide_worn ;;
    locate_from_bulk_sale) echo locate ;;
    locate_recast_item_filter) echo locate_recast ;;
    sell_from_materials) echo sell_mark ;;
    *) echo "${1%_hint}" ;;
  esac
}
# scn_prep <scenario> [seed]: the prep the app runs before the seed (empty = none); the seed is the fuzz scenario's
scn_prep() {
  case "$1" in
    equip_random_start) echo "random_start:${2:-1}" ;;
    equip_hero_filter_open|equip_hint_filter_open) echo hero_filter ;;
    equip_hero_filter_closed) echo hero_filter_closed ;;
    equip_item_filter_open|locate_recast_item_filter) echo item_filter ;;
    equip_from_materials|sell_from_materials) echo start_inventory_materials ;;
    equip_from_bulk_sale|locate_from_bulk_sale) echo start_bulk_sale ;;
    equip_from_artifact) echo start_artifact ;;
    equip_class_filter_gear) echo class_filter_gear ;;   # NEW prep (runner v29 whitelist; the app's LabPrep must know it too)
    equip_hide_worn_restore) echo hide_worn_on ;;
  esac
}
# scn_limit <scenario>: the PASS time limit in seconds of one plan (empty = none): the 3 navigator scenarios of the equip-hardening set
scn_limit() { case "$1" in equip_class_filter_gear|equip_two_same_class|equip_hide_worn_restore) echo 35 ;; esac; }
# scn_title <scenario>: the line's name in the report (Russian); empty = not an extra scenario
scn_title() {
  case "$1" in
    equip_random_start*) echo "Случайный старт игры (seed ${1#equip_random_start }): надеть сборку" ;;
    equip_hero_filter_open) echo "Надеть при открытом фильтре героев" ;;
    equip_hero_filter_closed) echo "Надеть при включённом фильтре героев (панель закрыта)" ;;
    equip_item_filter_open) echo "Надеть при открытом фильтре вещей" ;;
    equip_from_materials) echo "Надеть, игра стоит на вкладке «Материалы»" ;;
    equip_from_bulk_sale) echo "Надеть, игра стоит в «Массовой продаже»" ;;
    equip_from_artifact) echo "Надеть, игра стоит на экране артефактов" ;;
    locate_from_bulk_sale) echo "Найти в игре из «Массовой продажи»" ;;
    locate_recast_item_filter) echo "Найти в игре: преобразование при включённом фильтре вещей" ;;
    sell_from_materials) echo "Чистка склада со вкладки «Материалы»" ;;
    equip_toggle_auto_late) echo "Автоклик включён позже, когда план уже ждёт" ;;
    equip_hint_filter_open) echo "Подсказки без автоклика: открытый фильтр героев" ;;
    equip_class_filter_gear) echo "Надеть при фильтре героев по чужому классу и открытом фильтре вещей" ;;
    equip_two_same_class) echo "Надеть двух героев одного класса и фракции подряд" ;;
    equip_hide_worn_restore) echo "Надеть при включённом «Скрыть надетое»: флаг возвращён" ;;
  esac
}
# scn_pass_note <scenario>: the one-line human reason of a PASS (Russian)
scn_pass_note() {
  case "$1" in
    equip_hero_filter_open) echo "фильтр героев был открыт с отмеченной фракцией: приложение сбросило его («Все»), закрыло панель и довело план до конца" ;;
    equip_hero_filter_closed) echo "фильтр героев был включён, панель закрыта: приложение нашло героя, сбросило фильтр и довело план до конца" ;;
    equip_item_filter_open) echo "фильтр вещей (комплект) был открыт: приложение справилось с ним и довело план до конца" ;;
    equip_from_materials) echo "игра стояла на вкладке «Материалы»: приложение дошло до героя и довело план до конца" ;;
    equip_from_bulk_sale) echo "игра стояла в «Массовой продаже»: приложение вышло из неё, дошло до героя и довело план до конца" ;;
    equip_from_artifact) echo "игра стояла на экране артефактов: приложение дошло до героя и довело план до конца" ;;
    equip_random_start) echo "игра была поставлена на случайный безопасный экран: приложение дошло до героя и довело план до конца" ;;
    equip_toggle_auto_late) echo "автоклик был выключен, через 10 с включён командой: приложение подхватило уже идущий план и довело его до конца" ;;
    equip_class_filter_gear) echo "фильтр героев по классу не подходил герою, фильтр вещей был открыт с чужим выбором: сборка пройдена, без halt / reset_failed и слепых протяжек" ;;
    equip_two_same_class) echo "два героя одного класса и фракции подряд: оба плана выполнены, без halt / reset_failed и слепых протяжек" ;;
    equip_hide_worn_restore) echo "«Скрыть надетое» было включено до старта: приложение собрало героя без halt / reset_failed и слепых протяжек" ;;
  esac
}

# ---- the test app's lab recordings (src/LabRec.cs): files only, as the site stores them (lib/testRecording/handler.ts of wardsage-web):
# $LAB_REC_ROOT/<user>/<account>/<rec>/<part>.zip, owned by deploy (mode 700): read and deleted as deploy. Only lab ids are touched.
LAB_REC_ROOT=${LAB_REC_ROOT:-/srv/wardsage/staging/recordings}
LAB_REC_RX='^lab-[0-9]{8}-[0-9]{6}-[0-9a-f]{6}$'
# rec_dir <id>: the recording's folder (empty when it is not there)
# rec_run <command...>: runs where the recordings are: as root on the lab server (ssh), or locally as deploy
rec_run() { if [ -n "$LAB_SSH" ]; then rsh "$@"; else sudo -n -u deploy "$@"; fi; }
rec_dir() { [[ "$1" =~ $LAB_REC_RX ]] || return 1; rec_run find "$LAB_REC_ROOT" -mindepth 3 -maxdepth 3 -type d -name "$1" -print -quit 2>/dev/null </dev/null; }
# rec_delete <id>...: deletes these lab recordings (a passed scenario's)
rec_delete() { local id; for id in "$@"; do [[ "$id" =~ $LAB_REC_RX ]] || continue; rec_run find "$LAB_REC_ROOT" -mindepth 3 -maxdepth 3 -type d -name "$id" -prune -exec rm -rf -- {} + 2>/dev/null </dev/null || true; done; }
# rec_prune [days]: deletes lab recordings whose last part is older than <days> (default 2); with LAB_REC_PRUNE_ALL=1 every staging recording
# that old (the RecReplay fixtures are copies in the repo / ~/wardsage/work/recordings-ro, never under LAB_REC_ROOT)
rec_prune() {
  local min=$(( ${1:-2} * 1440 )) pat='lab-*'
  [ "${LAB_REC_PRUNE_ALL:-1}" = 1 ] && pat='*'
  rec_run find "$LAB_REC_ROOT" -mindepth 3 -maxdepth 3 -type d -name "$pat" -mmin +"$min" -prune -exec rm -rf -- {} + 2>/dev/null </dev/null || true
}

# ---- the test feed (latest.json, update/*.exe, Wardsage-Test-Setup.exe, lab/WardsageLab.{ps1,json}): Caddy serves /downloads/test/* from
# /srv/wardsage/test-feed on the lab server. TEST_FEED set = a local directory (tests, the old local mode); unset with LAB_SSH = the lab server.
LAB_FEED_REMOTE=/srv/wardsage/test-feed
feed_is_remote() { [ -n "$LAB_SSH" ] && [ -z "${TEST_FEED:-}" ]; }
# feed_cat <path in the feed>: the file's bytes (failure when missing)
feed_cat() { if feed_is_remote; then rsh cat "$LAB_FEED_REMOTE/$1" </dev/null; else cat "${TEST_FEED:-$LAB_FEED_REMOTE}/$1"; fi; }
# feed_put <local file> <path in the feed> [mode]: atomically (temp name, then mv); the feed's folders are made
feed_put() {
  local src=$1 dst=$2 mode=${3:-644} d b base
  d=$(dirname "$dst"); b=$(basename "$dst")
  if feed_is_remote; then
    base=$LAB_FEED_REMOTE
    rsh mkdir -p "$base/$d" </dev/null || return 1
    rsync -e "ssh ${LAB_SSH_OPTS[*]}" --chmod="F$mode" "$src" "$LAB_SSH:$base/$d/.$b.tmp" || return 1
    rsh mv -f "$base/$d/.$b.tmp" "$base/$dst" </dev/null
  else
    base=${TEST_FEED:-$LAB_FEED_REMOTE}
    mkdir -p "$base/$d" && cp "$src" "$base/$d/.$b.tmp" && chmod "$mode" "$base/$d/.$b.tmp" && mv -f "$base/$d/.$b.tmp" "$base/$dst"
  fi
}
