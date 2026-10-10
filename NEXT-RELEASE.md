# Next app release

Changes committed but not released yet (releases are batched, about weekly). Add a line here with every app change;
at the release the lines become the update notes (--ru / --en of tools/release-app.mjs) and the site's changelog
entry, then this list is emptied.

Release: `node tools/bump-version.mjs` → `node tools/build.mjs` → `bash tools/build-app.sh` →
`bash ../tools/run-core-tests.sh` → check `dist/RealmForge.exe` ProductVersion → `node tools/release-app.mjs --key
D:/RealmForge/keys/realmforge-update-key.pem --web ../realmforge-web --ru "…" --en "…"` → commit both repos.

## RU
- Сворачивание в трей: свёрнутое окно прячется в трей рядом с часами, программа продолжает работать; открыть — щелчком по значку, выход — правой кнопкой (можно выключить в настройках).
- Запуск вместе с Windows (в настройках, по умолчанию выключен): программа стартует сразу в трей и сама синхронизирует аккаунт, когда запущена игра.

## EN
- Minimize to the tray: the minimized window hides in the tray next to the clock and the program keeps working; click the icon to open it, right-click to exit (can be turned off in the settings).
- Start with Windows (in the settings, off by default): the program starts straight in the tray and syncs the account by itself while the game runs.
