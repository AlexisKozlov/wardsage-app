#!/usr/bin/env bash
# Shared helpers for the test lab server scripts (source this file). STAGING only.
# Never prints tokens, ids of users or accounts.

LAB_DB_CONTAINER=wardsage-staging-db           # the ONLY database these tools talk to
LAB_DIR=${LAB_DIR:-$HOME/wardsage/work/lab}
LAB_ENV=/srv/wardsage/staging/supabase/.env     # staging Supabase env (JWT secret, anon key)
mkdir -p "$LAB_DIR"

case "$LAB_DB_CONTAINER" in *prod*) echo "lab: refusing to use a prod container" >&2; exit 2 ;; esac

# q [psql args]: SQL from stdin, plain unaligned output. Use -v name=value and :'name' for values.
q() { sudo docker exec -i "$LAB_DB_CONTAINER" psql -U supabase_admin -d postgres -X -q -At -v ON_ERROR_STOP=1 "$@"; }

# env_get KEY: one value of the staging env file (printed to stdout only for the caller's variable; never echo it)
env_get() { sudo grep -E "^$1=" "$LAB_ENV" | head -1 | cut -d= -f2- | tr -d "\"' \r"; }

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
LAB_SCENARIOS="equip_city equip_hide_worn equip_artifact locate locate_recast locate_art sell_mark sell_after_restart equip_city_hint locate_hint"  # the _hint ones last: they switch auto click off
# scenarios run only when asked (lab-request.sh <device> --only explore) or by the --auto pass on their own schedule: explore = the test app's
# explorer (src/Explorer.cs: the game's safe UI against the UI graph; once a day and on every new game version, lab-loop.sh explore_due)
LAB_EXTRA_SCENARIOS="explore"

# ---- the test app's lab recordings (src/LabRec.cs): files only, as the site stores them (lib/testRecording/handler.ts of wardsage-web):
# $LAB_REC_ROOT/<user>/<account>/<rec>/<part>.zip, owned by deploy (mode 700): read and deleted as deploy. Only lab ids are touched.
LAB_REC_ROOT=${LAB_REC_ROOT:-/srv/wardsage/staging/recordings}
LAB_REC_RX='^lab-[0-9]{8}-[0-9]{6}-[0-9a-f]{6}$'
# rec_dir <id>: the recording's folder (empty when it is not there)
rec_dir() { [[ "$1" =~ $LAB_REC_RX ]] || return 1; sudo -n -u deploy find "$LAB_REC_ROOT" -mindepth 3 -maxdepth 3 -type d -name "$1" -print -quit 2>/dev/null; }
# rec_delete <id>...: deletes these lab recordings (a passed scenario's)
rec_delete() { local id; for id in "$@"; do [[ "$id" =~ $LAB_REC_RX ]] || continue; sudo -n -u deploy find "$LAB_REC_ROOT" -mindepth 3 -maxdepth 3 -type d -name "$id" -prune -exec rm -rf -- {} + 2>/dev/null || true; done; }
# rec_prune [days]: deletes lab recordings whose last part is older than <days> (default 2); with LAB_REC_PRUNE_ALL=1 every staging recording
# that old (the RecReplay fixtures are copies in the repo / ~/wardsage/work/recordings-ro, never under LAB_REC_ROOT)
rec_prune() {
  local min=$(( ${1:-2} * 1440 )) pat='lab-*'
  [ "${LAB_REC_PRUNE_ALL:-1}" = 1 ] && pat='*'
  sudo -n -u deploy find "$LAB_REC_ROOT" -mindepth 3 -maxdepth 3 -type d -name "$pat" -mmin +"$min" -prune -exec rm -rf -- {} + 2>/dev/null || true
}
