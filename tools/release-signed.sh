#!/usr/bin/env bash
# Runs tools/release-app.mjs with the update-signing key stored encrypted.
# The key is decrypted into /dev/shm (RAM) only for the run and shredded after.
# Usage: tools/release-signed.sh [--test] [--ru text] [--en text] [...]
# All arguments are passed through to release-app.mjs.
set -euo pipefail

ENC="${WARDSAGE_KEY_ENC:-$HOME/wardsage/keys/update-key.pem.enc}"
WEB="${WARDSAGE_WEB:-$HOME/wardsage/wardsage-web}"
APP="$(cd "$(dirname "$0")/.." && pwd)"

if [ ! -f "$ENC" ]; then
  echo "Encrypted key not found: $ENC" >&2
  exit 1
fi
if [ ! -d /dev/shm ]; then
  echo "/dev/shm is not a directory; refusing to write the key to disk" >&2
  exit 1
fi

umask 077
KEY="$(mktemp /dev/shm/wsk.XXXXXXXX)"
CLEANED=0
cleanup() {
  if [ "$CLEANED" = 0 ]; then
    CLEANED=1
    if [ -e "$KEY" ]; then
      shred -u "$KEY" 2>/dev/null || rm -f "$KEY"
    fi
  fi
}
trap cleanup EXIT INT TERM

if ! openssl enc -d -aes-256-cbc -pbkdf2 -iter 600000 -in "$ENC" -out "$KEY"; then
  echo "wrong passphrase or damaged key file" >&2
  exit 1
fi

if ! grep -q 'PRIVATE KEY' "$KEY"; then
  echo "wrong passphrase or damaged key file (no PEM private key found)" >&2
  exit 1
fi

cd "$APP"
rc=0
node tools/release-app.mjs --key "$KEY" --web "$WEB" "$@" || rc=$?
exit "$rc"
