# Next app release

Changes committed but not released yet (releases are batched, about weekly). Add a line here with every app change;
at the release the lines become the update notes (--ru / --en of tools/release-app.mjs) and the site's changelog
entry, then this list is emptied.

Release: `node tools/bump-version.mjs` (also moves the lines below into `ui/changelog.js` and `tools/.release-notes.json`, and empties
this list) → `node tools/build.mjs` → `bash tools/build-app.sh` → `bash ../tools/run-core-tests.sh` → check `dist/RealmForge.exe`
ProductVersion → `node tools/release-app.mjs --key D:/RealmForge/keys/realmforge-update-key.pem --web ../realmforge-web` (the notes come from
`tools/.release-notes.json`; `--ru` / `--en` override them) → commit both repos.

## RU
- Сворачивание в трей: свёрнутое окно прячется в трей рядом с часами, программа продолжает работать; открыть — щелчком по значку, выход — правой кнопкой (можно выключить в настройках).
- Раздел «Что нового»: изменения программы по версиям.
- Новое обновление предлагается окном поверх программы (можно «Позже»).
- Запуск вместе с Windows (в настройках, по умолчанию выключен): программа стартует сразу в трей и сама синхронизирует аккаунт, когда запущена игра.
- Подробная запись боёв арены (урон героев по секундам, движение и здоровье монстров) отправляется на сайт, чтобы симуляция арены совпадала с игрой точнее.

## EN
- Minimize to the tray: the minimized window hides in the tray next to the clock and the program keeps working; click the icon to open it, right-click to exit (can be turned off in the settings).
- A «What's new» page: the program's changes by version.
- A new update is offered in a window over the program (you can choose «Later»).
- Start with Windows (in the settings, off by default): the program starts straight in the tray and syncs the account by itself while the game runs.
- A detailed record of arena fights (the heroes' damage second by second, the monsters' movement and health) is sent to the site so that the arena simulation matches the game more closely.
