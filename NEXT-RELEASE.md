# Next app release

Changes committed but not released yet (releases are batched, about weekly). Add a line here with every app change;
at the release the lines become the update notes (--ru / --en of tools/release-app.mjs) and the site's changelog
entry, then this list is emptied.

Release: `node tools/bump-version.mjs` (also moves the lines below into `ui/changelog.js` and `tools/.release-notes.json`, and empties
this list) → `node tools/build.mjs` → `bash tools/build-app.sh` → `bash ../tools/run-core-tests.sh` → check `dist/Wardsage.exe`
ProductVersion → `node tools/release-app.mjs --key D:/RealmForge/keys/realmforge-update-key.pem --web ../realmforge-web` (the notes come from
`tools/.release-notes.json`; `--ru` / `--en` override them) → commit both repos.

Optional `### Heading` lines inside a `## RU` / `## EN` section split the update window's notes into titled groups (the headings are left out of the «What's new» page).

## Проверить в игре (1.6.68: только подсказки, программа ничего не нажимает)

Автонажатие вырезано, кода ввода в программе нет. Проверять живьём (список без «-», чтобы bump-version.mjs не взял его в заметки):

* «Надеть в игре»: подсказки идут по цепочке герой → «Снаряжение» → слот → вещь → «Заменить»: рамка и рука на нужной кнопке, нажимает игрок; ни одного нажатия от программы.
* «Найти в игре» (вознесение, преобразование, продвижение артефакта) и чистка склада (вещи и артефакты): рамки на нужных кнопках и ячейках, шаги меняются сами, когда игрок нажал.
* На экране, с которого путь не идёт: одна подсказка «Вернитесь в город» с рамкой на кнопке выхода (или «Нажмите Esc» текстом).
* Обновление: докачка по 256 КБ, обрыв связи не обнуляет загрузку (файл *.part в папке update); после неудачи паузы 1, 5, 30 минут, шесть неудач подряд, затем следующая проверка через 6 часов.
* Настройки: пунктов «Автонажатие» и «Автоматически подтверждать замену» нет; старый config.json с этими ключами читается без ошибки.

## RU

## EN
