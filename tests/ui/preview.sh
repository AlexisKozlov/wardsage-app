#!/usr/bin/env bash
# Screenshots of the window in every state, rendered by Mono WinForms on a virtual X display.
# Mono's theme and fonts differ from Windows: use this to review layout and texts, not pixels.
# Needs: mono-devel, libmono-system-windows-forms4.0-cil, libgdiplus, xvfb, imagemagick.
#   tests/ui/preview.sh            -> build/shots/<state>-<lang>.png
set -euo pipefail
cd "$(dirname "$0")/../.."
FX=/usr/lib/mono/4.5-api
CSC="dotnet $(ls -d /usr/lib/dotnet/sdk/*/Roslyn/bincore/csc.dll | tail -1) -nologo -noconfig"
mkdir -p build/shots
node tests/extract-csharp.mjs > build/embedded.cs
$CSC -langversion:5 -nostdlib -target:winexe -r:$FX/mscorlib.dll -r:$FX/System.dll -r:$FX/System.Core.dll \
  -r:$FX/System.Windows.Forms.dll -r:$FX/System.Drawing.dll -r:$FX/System.Security.dll \
  -out:build/UiPreview.exe build/embedded.cs tests/ui/UiPreview.cs
Xvfb :98 -screen 0 700x900x24 >/dev/null 2>&1 & XVFB=$!
trap 'kill $XVFB 2>/dev/null || true' EXIT
sleep 1
export DISPLAY=:98
shot() {
  mono build/UiPreview.exe "$1" "$2" >/dev/null 2>&1 & local pid=$!
  sleep 2.8
  import -window root -crop 560x860+0+0 "build/shots/$1-$2.png"
  convert "build/shots/$1-$2.png" -trim +repage "build/shots/$1-$2.png"
  wait $pid || true
}
for s in idle code reading done done-save err-game err-admin err-token err-rate; do shot $s ru; done
for s in idle done err-admin; do shot $s en; done
echo "screenshots in build/shots/"
