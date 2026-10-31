#!/usr/bin/env bash
# Static and network checks that can run without Windows.
#
#   tests/run-tests.sh [account.json]     (an optional real account.json is used as the payload)
#
# Needs: node, the .NET 8 SDK (its Roslyn csc is used directly, no NuGet), and optionally Mono
# (apt: mono-devel libmono-system-windows-forms4.0-cil) for the .NET Framework 4.5 reference
# assemblies and a second run of the tests on a .NET Framework-compatible runtime.
set -euo pipefail
cd "$(dirname "$0")/.."

SDK_CSC=$(ls -d /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | tail -1)
CSC="dotnet $SDK_CSC -nologo -noconfig"
NET8=$(ls -d /usr/lib/dotnet/packs/Microsoft.NETCore.App.Ref/*/ref/net8.0 | tail -1)
NET8_RT=$(ls /usr/lib/dotnet/shared/Microsoft.NETCore.App/ | grep '^8\.' | tail -1)
FX45=/usr/lib/mono/4.5-api   # Windows PowerShell 5.1 Add-Type level (not the exe build's FX: tools/build-app.sh picks its own)
PORT=3999
OUT=build
mkdir -p "$OUT"
ACCOUNT=${1:-}

step() { printf '\n=== %s\n' "$*"; }

step "1. RealmForge-Extractor.ps1: up to date with src/, UTF-8 BOM, CRLF, PowerShell syntax"
node tools/build.mjs --check
head -c 3 RealmForge-Extractor.ps1 | od -An -tx1 | grep -q 'ef bb bf' && echo "BOM ok"
node -e 'const t=require("fs").readFileSync("RealmForge-Extractor.ps1","utf8"); if (/[^\r]\n/.test(t)) { console.error("LF-only line endings found"); process.exit(1); } console.log("CRLF ok")'

if [ -d node_modules/web-tree-sitter ]; then
  node tests/ps-syntax.mjs RealmForge-Extractor.ps1
else
  echo "PowerShell syntax check SKIPPED (run: npm install)"
fi

step "2. Embedded C# compiles as C# 5 against .NET Framework 4.5 (WinForms, DPAPI), warnings = errors"
node tests/extract-csharp.mjs > "$OUT/embedded.cs"
if [ -f "$FX45/System.Windows.Forms.dll" ]; then
  # Exactly what Add-Type in Windows PowerShell 5.1 gives the compiler: mscorlib, System, System.Core
  # plus the three -ReferencedAssemblies of the script.
  $CSC -langversion:5 -nostdlib -target:library -warnaserror -warn:4 \
    -r:$FX45/mscorlib.dll -r:$FX45/System.dll -r:$FX45/System.Core.dll \
    -r:$FX45/System.Windows.Forms.dll -r:$FX45/System.Drawing.dll -r:$FX45/System.Security.dll \
    -out:"$OUT/embedded-net45.dll" "$OUT/embedded.cs"
  echo "compiled: $OUT/embedded-net45.dll"
  # control: the same compiler really rejects C# 6 at -langversion:5
  printf 'class C { string s = $"{1}"; string T() => s; }\n' > "$OUT/cs6.cs"
  if $CSC -langversion:5 -nostdlib -target:library -r:$FX45/mscorlib.dll -out:"$OUT/cs6.dll" "$OUT/cs6.cs" >/dev/null 2>&1; then
    echo "control FAILED: C# 6 code compiled at langversion 5"; exit 1
  else echo "control ok: C# 6 syntax is rejected at -langversion:5"; fi
  if command -v mcs >/dev/null; then
    mcs -langversion:5 -target:library -warnaserror -nowarn:618 -sdk:4.5 \
      -r:System.Windows.Forms.dll -r:System.Drawing.dll -r:System.Security.dll \
      -out:"$OUT/embedded-mcs.dll" "$OUT/embedded.cs"
    echo "also compiled by Mono mcs (second, independent C# 5 compiler)"
  fi
else
  echo "SKIPPED: $FX45 not found (install mono-devel for the .NET Framework reference assemblies)"
fi

CORE="src/ScreenMap.cs src/ScreenMapData.cs src/UiGraph.cs src/UiGraphData.cs src/SpriteArt.cs src/SpriteArtData.cs src/Explorer.cs src/ArenaClient.cs src/ArenaOpponents.cs src/ArtEquip.cs src/ArtGridPicture.cs src/AutoPilot.cs src/BattleCapture.cs src/BattleClock.cs src/BattleTimeline.cs src/Channel.cs src/Config.cs src/EquipGuide.cs src/EquipScan.cs src/ErrorReport.cs src/Extractor.cs src/FolderMigration.cs src/ScreenRecorder.cs src/RecInputs.cs src/FightPlanClient.cs src/FilterPilot.cs src/GameInfo.cs src/GamePacks.cs src/GameScreen.cs src/OfferForms.cs src/OfferScan.cs src/AutoRec.cs src/LabRec.cs src/FrameAudit.cs src/ClickGate.cs src/GuildData.cs src/GvgData.cs src/HeroPilot.cs src/Geometry.cs src/Layout.cs src/ListTracker.cs src/HintLayout.cs src/GuideShape.cs src/LiveTables.cs src/LuaRoot.cs src/UiButtons.cs src/FrameTargets.cs src/LocateClient.cs src/LocateFrames.cs src/LocateGuide.cs src/LocatePilot.cs src/MemoryReader.cs src/KlassScan.cs src/MiniJson.cs src/NewsClient.cs src/PlansClient.cs src/PlansWait.cs src/RecastScan.cs src/RemoteConfig.cs src/SellClient.cs src/SellHint.cs src/SellPilot.cs src/SellScan.cs src/Strings.cs src/SyncClient.cs src/TimelineBuilder.cs src/TestLog.cs src/TraceUpload.cs src/UpdateKeys.cs src/CodeWatch.cs src/CodeReport.cs src/WebViewRecovery.cs src/HeroGrades.cs src/LoadPlan.cs app/HintGeometry.cs"

step "3. Core (no WinForms) compiles as C# 5 for .NET 8; tests build"
NET8_REFS=$(for f in "$NET8"/*.dll; do printf -- '-r:%s ' "$f"; done)
# SYSLIB0014: WebRequest is obsolete on .NET 8 but is the right API on .NET Framework 4.x.
$CSC -langversion:5 -target:library -nostdlib -warnaserror -nowarn:SYSLIB0014 $NET8_REFS \
  -out:"$OUT/RealmForge.Core.dll" $CORE tests/CodeProtectorStub.cs
$CSC -target:exe -nostdlib -nowarn:SYSLIB0014 $NET8_REFS -r:"$OUT/RealmForge.Core.dll" \
  -out:"$OUT/CoreTests.dll" tests/CoreTests.cs tests/FrameStyleTests.cs tests/GameSim.cs tests/EquipBench.cs
cat > "$OUT/CoreTests.runtimeconfig.json" <<EOF
{ "runtimeOptions": { "tfm": "net8.0", "framework": { "name": "Microsoft.NETCore.App", "version": "$NET8_RT" } } }
EOF
echo "built $OUT/CoreTests.dll"

step "4. Mock server on :$PORT"
node tests/mock-server.mjs $PORT & MOCK=$!
trap 'kill $MOCK 2>/dev/null || true' EXIT
for i in $(seq 1 50); do curl -s -o /dev/null "http://127.0.0.1:$PORT/__last" && break; sleep 0.1; done

step "5. Tests on .NET 8"
dotnet exec --runtimeconfig "$OUT/CoreTests.runtimeconfig.json" "$OUT/CoreTests.dll" "http://localhost:$PORT" $ACCOUNT

step "5a. Game sim: 3000 seeds of a random start + task through the real screen state, click gate and pilots (tests/GameSim.cs; longer: tests/run-sim.sh --soak N)"
dotnet exec --runtimeconfig "$OUT/CoreTests.runtimeconfig.json" "$OUT/CoreTests.dll" --sim 3000

step "5a2. Game sim driven by the UI graph (src/UiGraphData.cs): starts on safe paths from the city, the game's own pop-ups, presses do what the client's code does; with the graph's report"
# (a gate: any rule break fails the run - the window table's z-order and the gate's read of every shown form; tests/run-sim.sh --soak N --graph for a long run)
dotnet exec --runtimeconfig "$OUT/CoreTests.runtimeconfig.json" "$OUT/CoreTests.dll" --sim 1000 --graph > "$OUT/graph-sim.txt" || { grep -v '^ *[0-9]*  [A-Z]' "$OUT/graph-sim.txt"; echo "graph-driven sim: rule breaks found"; exit 1; }
grep -v '^ *[0-9]*  [A-Z]' "$OUT/graph-sim.txt"
step "5a3. The explorer (src/Explorer.cs) on the graph-driven sim: one run visits 50+ forms by safe presses and comes back to the city; 20 runs with the game's pop-ups keep every rule"
dotnet exec --runtimeconfig "$OUT/CoreTests.runtimeconfig.json" "$OUT/CoreTests.dll" --sim 20 --explore

step "5b. Screen state + click gate: replay of recorded screens (tests/fixtures/screens)"
$CSC -target:exe -nostdlib -nowarn:SYSLIB0014 $NET8_REFS -r:"$OUT/RealmForge.Core.dll" -out:"$OUT/ScreenReplay.dll" tests/ScreenReplay.cs
cp "$OUT/CoreTests.runtimeconfig.json" "$OUT/ScreenReplay.runtimeconfig.json"
dotnet exec --runtimeconfig "$OUT/ScreenReplay.runtimeconfig.json" "$OUT/ScreenReplay.dll" tests/fixtures/screens

step "5c. Recording replay: a synthetic recording (tests/RecReplay.cs makes it with the recorder's own writers) is replayed, its drawing script runs"
$CSC -target:exe -nostdlib -nowarn:SYSLIB0014 $NET8_REFS -r:"$OUT/RealmForge.Core.dll" -out:"$OUT/RecReplay.dll" tests/RecReplay.cs
cp "$OUT/CoreTests.runtimeconfig.json" "$OUT/RecReplay.runtimeconfig.json"
rm -rf "$OUT/rec-fixture" "$OUT/rec-out" "$OUT/rec-png"
dotnet exec --runtimeconfig "$OUT/RecReplay.runtimeconfig.json" "$OUT/RecReplay.dll" --make-fixture "$OUT/rec-fixture" tests/fixtures/screens/_geometry.json
dotnet exec --runtimeconfig "$OUT/RecReplay.runtimeconfig.json" "$OUT/RecReplay.dll" "$OUT/rec-fixture" "$OUT/rec-out" > /dev/null
test -s "$OUT/rec-out/summary.txt" && test -s "$OUT/rec-out/frame_0001.json" || { echo "RecReplay wrote no summary"; exit 1; }
grep -q "^mismatches: 1\$" "$OUT/rec-out/summary.txt" || { echo "RecReplay: expected exactly the one planted mismatch"; cat "$OUT/rec-out/summary.txt"; exit 1; }
echo "replay ok (the planted mismatch found, the other frames agree)"
SHARP=/home/dev/wardsage/wardsage-web/node_modules/sharp
if [ -d "$SHARP" ]; then node tests/rec-draw.mjs "$OUT/rec-fixture" "$OUT/rec-out" "$OUT/rec-png" && test -s "$OUT/rec-png/frame_0001.png" && echo "rec-draw ok"; else echo "rec-draw SKIPPED (sharp not found)"; fi

if command -v mono >/dev/null && [ -f "$FX45/System.dll" ]; then
  step "6. Same tests on Mono (.NET Framework 4.5 API, HttpWebRequest of the Framework family, real DPAPI class)"
  $CSC -langversion:5 -nostdlib -target:exe -warnaserror -nowarn:618 \
    -r:$FX45/mscorlib.dll -r:$FX45/System.dll -r:$FX45/System.Core.dll -r:$FX45/System.Security.dll -r:$FX45/System.IO.Compression.dll \
    -out:"$OUT/CoreTests-mono.exe" $CORE src/CodeProtector.cs tests/CoreTests.cs tests/FrameStyleTests.cs tests/GameSim.cs tests/EquipBench.cs
  LANG=C.UTF-8 mono "$OUT/CoreTests-mono.exe" "http://localhost:$PORT" $ACCOUNT
fi

step "7. Desktop interface: equip helper logic, script syntax"
node tests/guide.test.mjs
node tests/news.test.mjs
node tests/forum.test.mjs
node tests/locate.test.mjs
node tests/sellhint.test.mjs
node tests/lab-rec.test.mjs
for f in ui/*.js; do node --check "$f"; done && echo "ui scripts: syntax ok"

step "8. Wardsage.exe builds (C# 7.3, .NET Framework 4.8, WebView2 SDK checksums)"
bash tools/build-app.sh

echo
echo "ALL CHECKS PASSED"
