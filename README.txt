RealmForge Extractor 0.6
========================

(English below)

ЧТО ЭТО
-------
Небольшая программа для Watcher of Realms (Windows). Она читает из памяти запущенной игры
ваших героев, снаряжение, артефакты и награды фракций и отправляет их на сайт RealmForge
(https://realmforge-wor.vercel.app), где их можно смотреть и подбирать снаряжение.

ТОЛЬКО ЧТЕНИЕ
-------------
* Программа только ЧИТАЕТ память игры (функция Windows ReadProcessMemory). Она ничего не
  записывает в игру, не меняет файлы игры и не обращается к серверам игры.
* Сетевые запросы — только на адрес сайта, указанный в окне (по умолчанию
  https://realmforge-wor.vercel.app). Никакой телеметрии и сторонних сервисов.
* Исходный код открыт: RealmForge-Extractor.ps1 — обычный текстовый файл, его можно прочитать.

ЗАЧЕМ ПРАВА АДМИНИСТРАТОРА
--------------------------
Игра запускается с правами администратора, а Windows разрешает читать память такого процесса
только программе с теми же правами. Поэтому при запуске появится запрос Windows (UAC) —
ответьте «Да». Без этого будет ошибка «Нет прав на чтение памяти игры».

КАК ПОЛЬЗОВАТЬСЯ
----------------
1. Распакуйте архив целиком в любую папку.
2. Запустите игру и дождитесь главного экрана (не экрана загрузки).
3. На сайте RealmForge войдите в аккаунт и откройте раздел «Синхронизация» — там есть
   код вида rf_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX. Скопируйте его.
4. Дважды щёлкните Run-RealmForge.bat, разрешите запрос администратора.
5. Вставьте код в поле «Код синхронизации» и нажмите «Синхронизировать».
   Чтение памяти занимает около 40 секунд. Затем нажмите «Открыть сайт».

Код запоминается (в %APPDATA%\RealmForge\config.json, в зашифрованном виде — Windows DPAPI,
расшифровать его можно только под вашей учётной записью на этом ПК). Никому не показывайте
код: с ним можно загрузить данные в ваш аккаунт RealmForge. Если код попал к кому-то —
получите новый на сайте.

Без кода кнопка называется «Только сохранить файл»: данные сохраняются в
Документы\RealmForge\account.json и никуда не отправляются. Галочка «Сохранить копию
account.json» делает то же самое вместе с отправкой.

Адрес сайта можно поменять в блоке «Дополнительно» (обычно не нужно).

ПЕРЕОДЕВАНИЕ (помощник)
-----------------------
На сайте в «Оптимизаторе» нажмите «Надеть в игре». В экстракторе нажмите «Переодевание»:
откроется небольшое окно поверх игры. Откройте в игре героя → Снаряжение → слот, и окно
подскажет, какой предмет нажать (ряд и место в списке) и какой фильтр снять. Надели —
строка отмечается сама, после 5 предметов сборка отмечается выполненной на сайте.
Первый поиск окна снаряжения занимает около минуты. Помощник только читает игру:
предметы надеваете вы сами, программа ничего не нажимает и ничего не меняет в игре.

ЕСЛИ ЧТО-ТО НЕ ТАК
------------------
* Windows пишет «Система Windows защитила ваш компьютер» — нажмите «Подробнее» →
  «Выполнить в любом случае» (файл скачан из интернета и не подписан).
* «Игра не запущена» — запустите Watcher of Realms и дождитесь главного экрана.
* «Нет прав на чтение памяти игры» — нажмите «Перезапустить от администратора» и ответьте
  «Да» на запрос Windows.
* «Данные аккаунта не найдены» — войдите в игру полностью (главный экран) и повторите.
* «Код синхронизации не принят (401)» — скопируйте код на сайте заново (возможно, он
  был заменён) и вставьте его.
* «Слишком частая синхронизация (429)» — подождите указанное время.
* «Сайт недоступен» / «не ответил за 60 секунд» — проверьте интернет, антивирус/файрвол
  и адрес сайта в «Дополнительно».
* «Сайт не принял формат данных» / «Неожиданный ответ» — скачайте свежую версию экстрактора
  на сайте.
* Кнопка «Журнал» открывает подробный журнал последнего чтения
  (%APPDATA%\RealmForge\last_run.log) — приложите его, если пишете разработчикам.
* Окно не появляется совсем — откройте PowerShell от имени администратора и выполните:
  powershell -ExecutionPolicy Bypass -File "<путь>\RealmForge-Extractor.ps1" -NoElevate
  и пришлите текст ошибки.

Требования: Windows 10 или 11 (PowerShell 5.1 и .NET Framework 4.5+ уже встроены).
Ничего устанавливать не нужно. Чтобы удалить — удалите папку с программой,
Документы\RealmForge и %APPDATA%\RealmForge.

Фанатский проект, не связан с разработчиком игры.


===========================================================================================

WHAT IT IS
----------
A small tool for Watcher of Realms (Windows). It reads your heroes, gear, artifacts and faction
rewards from the memory of the running game and sends them to the RealmForge site
(https://realmforge-wor.vercel.app), where you can browse them and optimize your gear.

READ-ONLY
---------
* The program only READS the game's memory (the Windows ReadProcessMemory function). It never
  writes to the game, never changes game files and never talks to the game servers.
* Network requests go only to the site address shown in the window
  (https://realmforge-wor.vercel.app by default). No telemetry, no third-party services.
* The code is open: RealmForge-Extractor.ps1 is a plain text file you can read.

WHY ADMINISTRATOR RIGHTS
------------------------
The game runs as administrator, and Windows lets only a program with the same rights read the
memory of such a process. So Windows will ask for permission (UAC) at start — answer "Yes".
Without it you get "No permission to read the game memory".

HOW TO USE
----------
1. Unpack the whole archive into any folder.
2. Start the game and wait for the main screen (not the loading screen).
3. On the RealmForge site sign in and open the Sync section: it shows a code like
   rf_XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX. Copy it.
4. Double-click Run-RealmForge.bat and allow the administrator prompt.
5. Switch the window to EN if you like (top right), paste the code into "Sync code" and press
   "Sync". Reading memory takes about 40 seconds. Then press "Open the site".

The code is remembered (in %APPDATA%\RealmForge\config.json, encrypted with Windows DPAPI: only
your Windows account on this PC can decrypt it). Do not share the code: it allows uploading data
to your RealmForge account. If it leaked, get a new one on the site.

Without a code the button says "Only save the file": the data is saved to
Documents\RealmForge\account.json and not sent anywhere. The "Save a copy of account.json"
checkbox does the same in addition to syncing.

The site address can be changed under "Advanced" (normally not needed).

EQUIP HELPER
------------
On the site, press "Equip in game" in the Optimizer. In the extractor press "Equip helper": a small
window opens on top of the game. Open the hero in the game -> Gear -> a slot, and the window shows
which item to press (row and position in the list) and which filter to turn off. Once an item is on,
its line is ticked; after all items the build is marked done on the site. The first search for the
gear screen takes about a minute. The helper only reads the game: you put the items on yourself,
the program presses nothing and changes nothing in the game.

TROUBLESHOOTING
---------------
* "Windows protected your PC" — click "More info" → "Run anyway" (the file comes from the
  internet and is not signed).
* "The game is not running" — start Watcher of Realms and wait for the main screen.
* "No permission to read the game memory" — press "Restart as administrator" and answer "Yes".
* "No account data found" — get fully into the game (main screen) and try again.
* "The sync code was rejected (401)" — copy the code from the site again (it may have been
  replaced) and paste it.
* "Syncing too often (429)" — wait for the time shown.
* "The site is unreachable" / "did not answer within 60 seconds" — check your connection,
  antivirus/firewall and the site address under "Advanced".
* "The site did not accept the data format" / "Unexpected reply" — download a fresh extractor
  from the site.
* The "Log" button opens the detailed log of the last run (%APPDATA%\RealmForge\last_run.log);
  attach it when you contact the developers.
* No window at all — open PowerShell as administrator and run
  powershell -ExecutionPolicy Bypass -File "<path>\RealmForge-Extractor.ps1" -NoElevate
  and send us the error text.

Requirements: Windows 10 or 11 (PowerShell 5.1 and .NET Framework 4.5+ are built in).
Nothing to install. To remove it, delete the program folder, Documents\RealmForge and
%APPDATA%\RealmForge.

Fan project, not affiliated with the game developer.
