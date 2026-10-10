# Next app release

Changes committed but not released yet (releases are batched, about weekly). Add a line here with every app change;
at the release the lines become the update notes (--ru / --en of tools/release-app.mjs) and the site's changelog
entry, then this list is emptied.

Release: `node tools/bump-version.mjs` (also moves the lines below into `ui/changelog.js` and `tools/.release-notes.json`, and empties
this list) → `node tools/build.mjs` → `bash tools/build-app.sh` → `bash ../tools/run-core-tests.sh` → check `dist/RealmForge.exe`
ProductVersion → `node tools/release-app.mjs --key D:/RealmForge/keys/realmforge-update-key.pem --web ../realmforge-web` (the notes come from
`tools/.release-notes.json`; `--ru` / `--en` override them) → commit both repos.

## RU

- Автосинхронизация больше не показывает ошибку «Не удалось прочитать данные», пока игра ещё грузится: программа ждёт загрузки аккаунта и повторяет попытку сама.
- Помощник «Надеть» теперь закрывает боковую панель фильтра (по второстепенным характеристикам) и не застревает на выборе предмета.
- После сбоя на стороне сайта (ошибка 5xx) синхронизация повторяется через минуту, а не через 5 минут.
- Трей: в него теперь прячет крестик (программа продолжает работать), а сворачивание оставляет её на панели задач, как обычно; выход — правой кнопкой по значку → «Выход».

## EN

- Auto sync no longer shows the "Could not read the data" error while the game is still loading: the app waits for the account to load and retries by itself.
- The equip helper now closes the filter side panel (sub-stat picks) and no longer gets stuck when picking an item.
- After a site-side failure (5xx) the sync retries in a minute instead of five.
- The tray: the close button now hides the program there (it keeps working), while minimizing keeps it on the taskbar as usual; exit: right-click the icon → «Exit».
