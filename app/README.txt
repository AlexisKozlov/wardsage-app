RealmForge 1.5
==============

ЧТО ЭТО
Программа для Watcher of Realms (Windows 10/11). Читает из запущенной игры ваших героев, снаряжение и призыв
и отправляет снимок на сайт RealmForge (https://realmforge-wor.vercel.app): там оценки вещей, подбор комплектов,
чистка склада и шансы призыва. Сборки с сайта программа сама надевает в игре.

КАК ЗАПУСТИТЬ
1. Установите RealmForge-Setup.exe (ярлыки появятся в «Пуске» и на рабочем столе). Дальше программа обновляется сама.
2. Windows может показать «Windows защитила ваш компьютер»: у программы пока нет платной цифровой
   подписи. Нажмите «Подробнее» → «Выполнить в любом случае».
3. Запустите игру, потом RealmForge. Windows спросит, разрешить ли программе вносить изменения, — нажмите «Да».
   Права администратора нужны, потому что игра работает с ними: без них память игры не прочитать.
4. На сайте войдите (по почте и паролю или через Google/Discord), откройте «Настройки», создайте код
   синхронизации, вставьте его в программу и нажмите «Сохранить».

ЧТО ПРОГРАММА ДЕЛАЕТ С ИГРОЙ
* Программа только ЧИТАЕТ память игры (ReadProcessMemory) и картинку экрана. Она ничего не записывает в игру,
  не меняет файлы игры и не обращается к серверам игры.
* Автонажатие (Настройки, включено по умолчанию): после «Надеть в игре» на сайте программа сама открывает героя,
  выставляет фильтр игры и выбирает нужные вещи — обычными движениями мыши, как игрок, и только пока окно игры
  впереди, а вы не двигаете мышь. Вещи, надетые на других героев, снимаются, только если вы нажали «Снять с других
  героев и надеть». Кнопку «Заменить» нажимаете вы — или программа, если включить «Автоматически подтверждать замену»
  (выключено по умолчанию).
* Чистка склада: программа открывает «Инвентарь» → «Массовая продажа» и выделяет вещи, отмеченные на сайте.
  «Продать» нажимаете только вы.
* Правила игры могут запрещать автоматизацию — если не хотите рисковать, выключите автонажатие.
* Автосинхронизация (Настройки, включена по умолчанию): после смены снаряжения и раз в 5 минут, пока игра запущена,
  программа сама отправляет снимок аккаунта на сайт.
* Сетевые запросы — только на адрес сайта RealmForge. Никакой телеметрии. Отчёты об ошибках (тип сбоя, версии,
  последние 300 строк журнала без кода синхронизации) — только если включить их в настройках; по умолчанию выключено.
* Код синхронизации хранится зашифрованным (Windows DPAPI) в %APPDATA%\RealmForge.
* Интерфейс работает на встроенном в Windows движке Microsoft Edge WebView2.
* Исходный код открыт: https://github.com/AlexisKozlov/realmforge-extractor

УДАЛЕНИЕ
«Параметры Windows» → «Приложения» → RealmForge → «Удалить». Настройки лежат в %APPDATA%\RealmForge и
%LOCALAPPDATA%\RealmForge — их можно удалить вручную.

Фанатский проект, не связан с разработчиком игры.

----------------------------------------------------------------------------------------------
RealmForge 1.5 (English)

Install RealmForge-Setup.exe; the app then updates itself. Windows SmartScreen may say "Windows protected your PC"
(the app has no paid code signature yet): click "More info" -> "Run anyway". Start the game, then RealmForge, and
allow administrator rights: the game runs as administrator, so reading its memory needs them too. On the site sign in
(e-mail and password, or Google/Discord), create the sync code in Settings, paste it into the app and press "Save".
The program only READS game memory and the screen, never writes to the game and talks only to the RealmForge site.
Auto click (Settings, on by default): after "Equip in game" on the site it opens the hero, sets the game's filter and
picks the items with the mouse like a player, only while the game is in front and you leave the mouse alone. "Replace"
is pressed by you, or by the app if you turn on auto-confirm (off by default). Storage cleanup only selects the items
in the game's bulk sale; you press "Sell". The game's rules may forbid automation - turn auto click off if you do not
want the risk. Auto sync (on by default) sends the account after gear changes and every 5 minutes while the game runs.
Uninstall: Windows Settings -> Apps -> RealmForge. Source: https://github.com/AlexisKozlov/realmforge-extractor
