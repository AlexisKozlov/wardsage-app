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
LAB_SCENARIOS="equip_city equip_hide_worn equip_artifact locate locate_recast locate_art sell_mark sell_after_restart"
