# Next app release

Changes committed but not released yet (releases are batched, about weekly). Add a line here with every app change;
at the release the lines become the update notes (--ru / --en of tools/release-app.mjs) and the site's changelog
entry, then this list is emptied.

Release: `node tools/bump-version.mjs` (also moves the lines below into `ui/changelog.js` and `tools/.release-notes.json`, and empties
this list) → `node tools/build.mjs` → `bash tools/build-app.sh` → `bash ../tools/run-core-tests.sh` → check `dist/Wardsage.exe`
ProductVersion → `node tools/release-app.mjs --key D:/RealmForge/keys/realmforge-update-key.pem --web ../realmforge-web` (the notes come from
`tools/.release-notes.json`; `--ru` / `--en` override them) → commit both repos.

Optional `### Heading` lines inside a `## RU` / `## EN` section split the update window's notes into titled groups (the headings are left out of the «What's new» page).

## Проверить в игре (до выпуска «Найти в игре», следующая версия)

Заметка для следующей версии (вернуть в RU/EN, когда сайт включит кнопку): «Найти в игре» для Преобразования: нажми кнопку на сайте, и программа обведёт вещь рамкой в Инвентаре и подскажет, как открыть её в Литейной. Программа только показывает, нажимаешь ты сам.

«Найти в игре» (Преобразование) сделано только на чтении и подсказках, ни одного клика. Не проверяется без живой игры (не превращать эти пункты
в строки заметок; список ниже без «-», чтобы bump-version.mjs не взял их в заметки):

* Вариант экрана на каждом из двух аккаунтов: Литейная → «Преобразовать» или экран самоцветов (функции 7 и 8). Какие экраны программа
  видит (RecastScan.cs, LiveTables.cs) и как они читаются, проверяется только вживую; после патча игры проверять заново.
* Инвентарь в обычном режиме: показывает ли надетые вещи, видна ли выбранная вещь после клика, рамка на 1920x1009 и 1600x1000,
  прокрутка вверх и вниз.
* Геометрия сетки Литейной не замерена: на экране преобразования только текст и табличка (без рамки на ячейке). Положение таблички (левый верх списка)
  подобрано без игры. Замер: «Диагностика рамки» (frame.log), затем рамка в сетке Литейной (этап 2).
* Левая кнопка карточки вещи (Самоцветы или Литейная): тексты и координаты не замерены, подсказка пока только текстом.
* Признак «висит результат прошлого преобразования» пока не читается: сначала снять его живьём.
* Иконки слотов слева и фильтр Литейной (поп-ап выравнивается по кнопке): читаются как список или набор, проверить.
* Задержка: заявка обновляется с опросом планов (8 с, пока она открыта); экраны ищутся не чаще раза в 8 с.
* Английские названия кнопок игры (Литейная, Преобразовать, Фильтр) в ui/i18n.js взяты по смыслу: сверить с английским клиентом.

## Проверить в игре (данные для версии игры с сайта)

* Тестовая сборка с игрой 1.6.39.829.1: в журнале «layout data for game version 1.6.39.829.1: from the site», затем начало боя
  («battle start»), запись боя и итог боя (статистика героев), тренер боссов, арена (соперники, запись боя).
* Переодевание: экран героя находится («hero screen»), «Надеть» и подсказки работают; «Найти в игре» и чистка склада: Инвентарь находится.
* Без сети после первого запуска: данные берутся из сохранённой копии (папка layout рядом с config.json).
* После следующего обновления игры, пока данных для новой версии нет: заметка «Часть функций недоступна…», синхронизация аккаунта
  работает; не позже 15 минут после появления данных на сайте заметка уходит сама.

## RU

- Герои, которых ты ещё ни разу не прокачивал и не одевал, теперь тоже попадают на сайт — раньше программа их пропускала, и на сайте героев было меньше, чем в игре.
- Переодевание: программа узнаёт открытого героя и тогда, когда экран героя открыт после инвентаря или другого героя, и сама перепроверяет игру, если шаг «открой героя» не проходит, — перезапуск программы больше не нужен.
- При первом запуске программа показывает короткое приветствие с Пользовательским соглашением; нужно один раз нажать «Принимаю».
- Папки настроек программы теперь называются Wardsage; всё переносится само.
- Установщик: можно сразу выбрать, запускать ли программу вместе с Windows и отправлять ли отчёты об ошибках.

## EN

- Heroes you have never levelled or equipped now reach the site too: the app used to skip them, so the site showed fewer heroes than the game.
- Equipping: the app now recognises the open hero even when the hero screen was opened after the inventory or another hero, and re-checks the game by itself when the «open the hero» step does not clear, so restarting the app is no longer needed.
- On the first start the app shows a short welcome card with the Terms of Use; press «I accept» once.
- The app's settings folders are now named Wardsage; everything moves over by itself.
- Installer: you can now choose right away whether the app starts with Windows and whether it sends error reports.
