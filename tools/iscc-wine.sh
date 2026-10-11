#!/usr/bin/env bash
# Runs Inno Setup's ISCC.exe under Wine on Linux, converting absolute path arguments to Windows paths.
# tools/release-signed.sh passes this script to release-app.mjs as --iscc.
# Wine prefix: $WINEPREFIX (default ~/.wine-inno).
set -euo pipefail

ISCC="${ISCC_EXE:-$HOME/wardsage/work/tools/InnoSetup/ISCC.exe}"
WINE="${WINE:-/usr/lib/wine/wine64}"
export WINEPREFIX="${WINEPREFIX:-$HOME/.wine-inno}" WINEDEBUG=-all WINEDLLOVERRIDES="mscoree,mshtml="
unset DISPLAY

args=()
for a in "$@"; do
  if [[ "$a" == /* && -e "$a" ]]; then
    a="$("$WINE" winepath -w "$a" 2>/dev/null)"
  fi
  args+=("$a")
done
exec "$WINE" "$ISCC" "${args[@]}"
