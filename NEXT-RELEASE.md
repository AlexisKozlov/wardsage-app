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

## RU

- Гильдия: исправлена отправка неполного списка участников (после обновления игры гильдия могла не обновляться на сайте или терять часть игроков). Список без тебя больше не отправляется: программа подскажет открыть в игре список участников гильдии.

## EN

- Guild: fixed an incomplete member list being sent (after the game update the guild could stop updating on the site or lose some players). A list without you is no longer sent: the app asks you to open the guild member list in the game.
