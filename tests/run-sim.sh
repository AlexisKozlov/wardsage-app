#!/usr/bin/env bash
# The game sim (tests/GameSim.cs) alone: a quick run, or a long one for the night.
#
#   tests/run-sim.sh [seeds] [--scan] [--timing] (default 2000; --timing: the reader's costs and the pilots' times, --scan: the old region-scan reader)
#   tests/run-sim.sh --soak N [--slow-only]    (N seeds, in blocks of 5000, each block's summary printed)
#   (--real after N: the start pop-ups and the game's own pop-ups at a realistic rate - the done-rate a player sees; the default mix stresses the rules)
#   tests/run-sim.sh --seed S                  (one seed with its whole trace: what the game and the pilot did)
#   tests/run-sim.sh --list TEXT [seeds]       (the seeds whose outcome contains TEXT, e.g. "slow wait:hero_failed")
set -euo pipefail
cd "$(dirname "$0")/.."
SDK_CSC=$(ls -d /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | tail -1)
CSC="dotnet $SDK_CSC -nologo -noconfig"
NET8=$(ls -d /usr/lib/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net8.0 | tail -1)
NET8_RT=$(ls /usr/lib/dotnet/shared/Microsoft.NETCore.App/ | grep '^8\.' | tail -1)
CORE=$(sed -n 's/^CORE="\(.*\)"$/\1/p' tests/run-tests.sh)
OUT=build; mkdir -p "$OUT"
NET8_REFS=$(for f in "$NET8"/*.dll; do printf -- '-r:%s ' "$f"; done)
$CSC -langversion:5 -target:library -nostdlib -warnaserror -nowarn:SYSLIB0014 $NET8_REFS -out:"$OUT/RealmForge.Core.dll" $CORE tests/CodeProtectorStub.cs
$CSC -target:exe -nostdlib -nowarn:SYSLIB0014 $NET8_REFS -r:"$OUT/RealmForge.Core.dll" -out:"$OUT/CoreTests.dll" tests/CoreTests.cs tests/GameSim.cs
printf '{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "%s" } } }\n' "$NET8_RT" > "$OUT/CoreTests.runtimeconfig.json"
SIM="dotnet exec --runtimeconfig $OUT/CoreTests.runtimeconfig.json $OUT/CoreTests.dll --sim"
case "${1:-}" in
  --soak)
    N=${2:?seeds}; shift 2; rc=0
    for ((from = 0; from < N; from += 5000)); do
      n=$(( N - from < 5000 ? N - from : 5000 ))
      $SIM $n --from $((10000 + from)) "$@" || rc=1
    done
    exit $rc ;;
  --seed) $SIM --seed "$2" --verbose "${@:3}" ;;
  --list) $SIM "${3:-2000}" --list "$2" ;;
  *) $SIM "${1:-2000}" "${@:2}" ;;
esac
