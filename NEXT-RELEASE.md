# Next app release

Changes committed but not released yet (releases are batched, about weekly). Add a line here with every app change;
at the release the lines become the update notes (--ru / --en of tools/release-app.mjs) and the site's changelog
entry, then this list is emptied.

Release: `node tools/bump-version.mjs` (also moves the lines below into `ui/changelog.js` and `tools/.release-notes.json`, and empties
this list) → `node tools/build.mjs` → `bash tools/build-app.sh` → `bash ../tools/run-core-tests.sh` → check `dist/Wardsage.exe`
ProductVersion → `node tools/release-app.mjs --key D:/RealmForge/keys/realmforge-update-key.pem --web ../realmforge-web` (the notes come from
`tools/.release-notes.json`; `--ru` / `--en` override them) → commit both repos.

Optional `### Heading` lines inside a `## RU` / `## EN` section split the update window's notes into titled groups (the headings are left out of the «What's new» page).

## RU

- Программа сообщает сайту, какие обновления игры скачаны (только адреса файлов обновлений из журнала игры) — чтобы данные сайта обновлялись быстрее.
- Окно программы больше не падает с ошибкой «Operation aborted», если Windows запустила программу после перезагрузки или сна: встроенный браузер перезапускается сам.

## EN

- The app tells the site which game updates were downloaded (only the update file addresses from the game's log), so the site's data updates sooner.
- The app window no longer fails with “Operation aborted” when Windows starts it after a reboot or sleep: the built-in browser restarts by itself.
