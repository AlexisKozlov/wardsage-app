#!/usr/bin/env bash
# Mints (or rotates) the token of a test-lab device on STAGING.
#   lab-mint-token.sh <device> [--account <game account id or unique label>] [--rotate] [--days N]
# Creates the lab_devices row when missing. The result is a one-line "bundle" (base64 JSON with the API host, the
# public anon key and the device JWT) written to ~/wardsage/work/lab/tokens/<device>.bundle with mode 600. Nothing
# secret is printed. --rotate raises token_gen: every older token of that device stops working.
set -euo pipefail
. "$(dirname "$0")/lab-common.sh"

dev=${1:-}; shift || true
acct=""; rotate=0; days=180
while [ $# -gt 0 ]; do
  case "$1" in
    --account) acct=${2:-}; shift 2 ;;
    --rotate) rotate=1; shift ;;
    --days) days=${2:-180}; shift 2 ;;
    *) echo "unknown option" >&2; exit 2 ;;
  esac
done
[[ "$dev" =~ ^[a-z0-9-]{1,32}$ ]] || { echo "usage: lab-mint-token.sh <device> [--account <id|label>] [--rotate] [--days N]" >&2; exit 2; }
[[ "$days" =~ ^[0-9]{1,3}$ ]] || { echo "bad --days" >&2; exit 2; }

q -v n="$dev" <<< "insert into public.lab_devices (name) values (:'n') on conflict (name) do nothing" >/dev/null
if [ -n "$acct" ]; then
  cnt=$(q -v a="$acct" <<< "select count(*) from public.game_accounts where id::text = :'a' or label = :'a' or player_name = :'a'")
  [ "$cnt" = 1 ] || { echo "account '$acct' matches $cnt game accounts (need exactly 1)" >&2; exit 1; }
  q -v n="$dev" -v a="$acct" <<< "update public.lab_devices set account_id = (select id from public.game_accounts where id::text = :'a' or label = :'a' or player_name = :'a') where name = :'n'" >/dev/null
fi
if [ "$rotate" = 1 ]; then
  q -v n="$dev" <<< "update public.lab_devices set token_gen = token_gen + 1 where name = :'n'" >/dev/null
fi
row=$(q -v n="$dev" -F '|' <<< "select id, token_gen from public.lab_devices where name = :'n'")
id=${row%%|*}; gen=${row##*|}

secret=$(env_get JWT_SECRET); anon=$(env_get ANON_KEY)
[ -n "$secret" ] && [ -n "$anon" ] || { echo "cannot read the staging JWT secret / anon key" >&2; exit 1; }
host=${LAB_API_HOST:-https://api-test.wardsage.com}

mkdir -p "$LAB_DIR/tokens"; chmod 700 "$LAB_DIR/tokens"
out="$LAB_DIR/tokens/$dev.bundle"
umask 077
# the secret and ids travel through the environment of one python process, never through argv
LAB_SECRET="$secret" LAB_ANON="$anon" LAB_ID="$id" LAB_GEN="$gen" LAB_DAYS="$days" LAB_HOST="$host" LAB_DEV="$dev" python3 -I -c '
import os, json, hmac, hashlib, base64, time
b = lambda x: base64.urlsafe_b64encode(x).rstrip(b"=")
now = int(time.time())
head = b(json.dumps({"alg": "HS256", "typ": "JWT"}, separators=(",", ":")).encode())
body = b(json.dumps({"role": "lab_device", "sub": os.environ["LAB_ID"], "gen": int(os.environ["LAB_GEN"]),
                     "iat": now, "exp": now + 86400 * int(os.environ["LAB_DAYS"])}, separators=(",", ":")).encode())
sig = b(hmac.new(os.environ["LAB_SECRET"].encode(), head + b"." + body, hashlib.sha256).digest())
jwt = (head + b"." + body + b"." + sig).decode()
bundle = {"host": os.environ["LAB_HOST"], "device": os.environ["LAB_DEV"], "apikey": os.environ["LAB_ANON"], "token": jwt}
print(base64.b64encode(json.dumps(bundle).encode()).decode())
' > "$out.tmp"
mv "$out.tmp" "$out"; chmod 600 "$out"
echo "device '$dev' (generation $gen): bundle written to $out"
