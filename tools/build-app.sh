#!/usr/bin/env bash
# Builds dist/RealmForge.exe: one Windows executable (.NET Framework 4.6.2+, x64) with the interface (ui/)
# and the WebView2 SDK embedded as resources. Runs on Linux with the .NET 8 SDK's Roslyn compiler and the
# .NET Framework reference assemblies of Mono (no Windows, no NuGet needed).
#
#   tools/build-app.sh            -> dist/RealmForge.exe
set -euo pipefail
cd "$(dirname "$0")/.."

# Linux: the .NET SDK's Roslyn + Mono's 4.6.2 reference assemblies. Windows (Git Bash): the installed .NET SDK's Roslyn +
# the reference assemblies of the NuGet package Microsoft.NETFramework.ReferenceAssemblies.net462 (FX=... to point at them).
if [ -d /usr/lib/dotnet/sdk ]; then
  SDK_CSC=$(ls -d /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | tail -1)
  FX=${FX:-/usr/lib/mono/4.6.2-api}
else
  SDK_CSC=$(cygpath -w "$(ls -d "/c/Program Files/dotnet/sdk/"*/Roslyn/bincore/csc.dll | tail -1)")
  FX=${FX:-/d/RealmForge/work/tools/net462/build/.NETFramework/v4.6.2}
fi
CSC=(dotnet "$SDK_CSC" -nologo -noconfig)
WV=vendor/webview2
OUT=build/app
rm -rf "$OUT" && mkdir -p "$OUT/ui" dist

( cd "$WV" && sha256sum -c --quiet SHA256SUMS )

# interface: everything in ui/ except the browser-preview mock
cp -r ui/. "$OUT/ui/"
rm -f "$OUT/ui/mock.js"
sed -i '/mock.js/d' "$OUT/ui/index.html"

RES=()
while IFS= read -r f; do RES+=("-resource:$f,ui/${f#$OUT/ui/}"); done < <(find "$OUT/ui" -type f | sort)
RES+=("-resource:app/res/frame.png,overlay/frame.png")
RES+=("-resource:app/res/heroes_btn.png,overlay/heroes_btn.png")
RES+=("-resource:app/res/hero_tags.json,overlay/hero_tags.json")
for f in Microsoft.Web.WebView2.Core.dll Microsoft.Web.WebView2.WinForms.dll WebView2Loader.dll; do RES+=("-resource:$WV/$f,bin/$f"); done

CORE="src/MemoryReader.cs src/EquipScan.cs src/MiniJson.cs src/GameInfo.cs src/Extractor.cs src/SyncClient.cs src/PlansClient.cs src/BridgeClient.cs src/ListTracker.cs src/AutoPilot.cs src/FilterPilot.cs src/HeroPilot.cs src/LiveTables.cs src/Config.cs src/CodeProtector.cs"
APP="app/Program.cs app/AppWindow.cs app/HostBridge.cs app/Overlay.cs app/HintGeometry.cs app/Updater.cs app/AssemblyInfo.cs"

"${CSC[@]}" -langversion:7.3 -target:winexe -platform:x64 -optimize+ -deterministic -nostdlib -warnaserror -nowarn:1701,1702 \
  -r:$FX/mscorlib.dll -r:$FX/System.dll -r:$FX/System.Core.dll -r:$FX/System.Drawing.dll -r:$FX/System.Windows.Forms.dll \
  -r:$FX/System.Security.dll -r:$WV/Microsoft.Web.WebView2.Core.dll -r:$WV/Microsoft.Web.WebView2.WinForms.dll \
  -win32icon:app/res/icon.ico -win32manifest:app/res/app.manifest \
  "${RES[@]}" -out:dist/RealmForge.exe $CORE $APP

ls -la dist/RealmForge.exe
