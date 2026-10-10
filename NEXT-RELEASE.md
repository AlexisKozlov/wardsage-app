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
- Окно новой версии: список изменений по пунктам, кнопки в стиле игры.
- Программа сама переименовывает файл в Wardsage.exe и обновляет название в списке приложений Windows, ярлыки и автозапуск.
- Программа отправляет на сайт таблицу гильдии: атаки и урон участников по Матрице и гильдейскому боссу (для страницы «Гильдия»).

## EN
- The new version window: changes as a list, buttons in the game's style.
- The app renames its file to Wardsage.exe by itself and updates its name in the Windows apps list, the shortcuts and autostart.
- The app sends the guild table to the site: members' attacks and damage on the Matrix and the guild boss (for the «Guild» page).
