#!/usr/bin/env bash
# dist/Wardsage.zip = Wardsage.exe + README.txt (CRLF)
set -euo pipefail
cd "$(dirname "$0")/.."
rm -rf build/pack && mkdir -p build/pack
cp dist/Wardsage.exe build/pack/
sed 's/$/\r/' app/README.txt > build/pack/README.txt
( cd build/pack && rm -f ../../dist/Wardsage.zip && zip -X -q ../../dist/Wardsage.zip Wardsage.exe README.txt )
ls -la dist/Wardsage.zip
