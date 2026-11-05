#!/usr/bin/env bash
# Publishes the lab runner for its self-update: signs tools/lab/WardsageLab.ps1 with the TEST update key (the one
# test-autorelease.sh signs test builds with) and copies it plus WardsageLab.json {version,size,sha256,sig} to the
# test feed, served at <updateHost>/downloads/test/lab/ (Caddy: /downloads/test/* -> /srv/wardsage/test-feed on the lab server, see lab-common.sh:
# the files go there over ssh/rsync; with LAB_SSH="" or TEST_FEED=<dir> to a local directory).
#   publish-runner.sh [--force]
# The version is the $RunnerVersion line of the script: the runners update only to a HIGHER one, so bump it first.
# Env: LAB_SSH (default root@13.140.221.207; empty = local), TEST_FEED (a local feed dir), LAB_KEY (default ~/wardsage/keys/test-update-key.pem).
# The key is only read by openssl; nothing of it is printed or copied.
set -euo pipefail
HERE=$(dirname "$(readlink -f "$0")")
. "$HERE/lab-common.sh"
SRC=$HERE/WardsageLab.ps1
KEY=${LAB_KEY:-$HOME/wardsage/keys/test-update-key.pem}
force=0; [ "${1:-}" = "--force" ] && force=1

[ -f "$SRC" ] || { echo "no $SRC" >&2; exit 1; }
[ -f "$KEY" ] || { echo "no test key" >&2; exit 1; }
# the runner writes the bytes as they are: plain ASCII only (Windows PowerShell 5.1 reads a BOM-less file as ANSI)
if LC_ALL=C grep -q -P '[^\x00-\x7F]' "$SRC"; then echo "WardsageLab.ps1 must be ASCII only" >&2; exit 1; fi
ver=$(sed -nE "s/^\\\$RunnerVersion\s*=\s*'([0-9]{1,9})'.*/\1/p" "$SRC" | head -1)
[ -n "$ver" ] || { echo "no \$RunnerVersion in the script" >&2; exit 1; }

tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
size=$(stat -c %s "$SRC")
sha=$(sha256sum "$SRC" | cut -d' ' -f1)
openssl dgst -sha256 -sign "$KEY" -out "$tmp/sig.bin" "$SRC"                       # RSA PKCS#1 v1.5 + SHA-256
openssl pkey -in "$KEY" -pubout -out "$tmp/pub.pem" 2>/dev/null
openssl dgst -sha256 -verify "$tmp/pub.pem" -signature "$tmp/sig.bin" "$SRC" >/dev/null || { echo "signature does not verify" >&2; exit 1; }
sig=$(base64 -w0 < "$tmp/sig.bin")
jq -n --arg v "$ver" --argjson s "$size" --arg h "$sha" --arg g "$sig" '{version: $v, size: $s, sha256: $h, sig: $g}' > "$tmp/WardsageLab.json"

oldj=$(feed_cat lab/WardsageLab.json 2>/dev/null || true)
if [ -n "$oldj" ] && [ "$force" = 0 ]; then
  old=$(jq -r '.version // "0"' <<< "$oldj"); oldsha=$(jq -r '.sha256 // ""' <<< "$oldj")
  if [ "$oldsha" = "$sha" ]; then echo "runner $ver is already published (same bytes)"; exit 0; fi
  if [ "$ver" -le "$old" ] 2>/dev/null; then echo "published version is $old: bump \$RunnerVersion above it (or --force)" >&2; exit 1; fi
fi
# script first, manifest last, both atomically
feed_put "$SRC" lab/WardsageLab.ps1 || { echo "could not publish the script" >&2; exit 1; }
feed_put "$tmp/WardsageLab.json" lab/WardsageLab.json || { echo "could not publish the manifest" >&2; exit 1; }
echo "runner $ver published: $size bytes, sha256 ${sha:0:16}..., signed and verified; to ${LAB_SSH:-local} ${TEST_FEED:-$LAB_FEED_REMOTE}/lab"
