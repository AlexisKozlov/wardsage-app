#!/usr/bin/env bash
# dist/RealmForge.zip = RealmForge.exe + README.txt (CRLF)
set -euo pipefail
cd "$(dirname "$0")/.."
rm -rf build/pack && mkdir -p build/pack
cp dist/RealmForge.exe build/pack/
sed 's/$/\r/' app/README.txt > build/pack/README.txt
( cd build/pack && rm -f ../../dist/RealmForge.zip && zip -X -q ../../dist/RealmForge.zip RealmForge.exe README.txt )
ls -la dist/RealmForge.zip
