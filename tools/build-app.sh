#!/usr/bin/env bash
# Builds dist/RealmForge.exe: one Windows executable (.NET Framework 4.6.2+, x64) with the interface (ui/)
# and the WebView2 SDK embedded as resources. Runs on Linux with the .NET 8 SDK's Roslyn compiler and the
# .NET Framework reference assemblies of Mono (no Windows, no NuGet needed).
#
#   tools/build-app.sh            -> dist/RealmForge.exe
set -euo pipefail
cd "$(dirname "$0")/.."

SDK_CSC=$(ls -d /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | tail -1)
CSC="dotnet $SDK_CSC -nologo -noconfig"
FX=/usr/lib/mono/4.6.2-api
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
for f in Microsoft.Web.WebView2.Core.dll Microsoft.Web.WebView2.WinForms.dll WebView2Loader.dll; do RES+=("-resource:$WV/$f,bin/$f"); done

CORE="src/MemoryReader.cs src/EquipScan.cs src/MiniJson.cs src/GameInfo.cs src/Extractor.cs src/SyncClient.cs src/PlansClient.cs src/Config.cs src/CodeProtector.cs"
APP="app/Program.cs app/AppWindow.cs app/HostBridge.cs app/AssemblyInfo.cs"

$CSC -langversion:7.3 -target:winexe -platform:x64 -optimize+ -deterministic -nostdlib -warnaserror -nowarn:1701,1702 \
  -r:$FX/mscorlib.dll -r:$FX/System.dll -r:$FX/System.Core.dll -r:$FX/System.Drawing.dll -r:$FX/System.Windows.Forms.dll \
  -r:$FX/System.Security.dll -r:$WV/Microsoft.Web.WebView2.Core.dll -r:$WV/Microsoft.Web.WebView2.WinForms.dll \
  -win32icon:app/res/icon.ico -win32manifest:app/res/app.manifest \
  "${RES[@]}" -out:dist/RealmForge.exe $CORE $APP

ls -la dist/RealmForge.exe
