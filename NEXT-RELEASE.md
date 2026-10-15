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

- Подсказка при переодевании: если в игре открыт фильтр, программа сначала просит его закрыть, а не рисует рамку поверх него; стрелка и число рядов следят за прокруткой без лишних нажатий; если вещь далеко в списке, программа советует выбрать в фильтре её комплект или главный стат. Если фильтр открыт по совету, программа подсвечивает, что в нём выбрать. Герой из сборки подсвечивается и в списке героев. Рамка стоит точно на нужной вещи; подсказки выглядят по-новому: золотая рамка и табличка с шагом, а если вещь за краем списка — золотая полоса с отметкой, где она. Если надета не та вещь из сборки, программа об этом скажет.
- Если загрузка обновления оборвалась, программа сама попробует снова через 20 секунд.

## EN

- Equip helper: with the game's filter open the app asks to close it first instead of drawing the frame over it; the arrow and the row count follow the scrolling without extra clicks; when the item is far down the list the app suggests filtering by its set or main stat. With the filter opened on that tip, the app lights what to pick in it. The build's hero is pointed at in the hero list too. The frame lands exactly on the item; the hints have a new look: a gold frame with a step plate, and a gold strip with a mark when the item is off screen. If an item not from the build is put on, the app says so.
- If an update download drops, the app tries again by itself in 20 seconds.
