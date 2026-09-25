RealmForge 1.3
==============

ЧТО ЭТО
Программа для Watcher of Realms (Windows 10/11). Читает из запущенной игры ваших героев и
снаряжение и отправляет снимок на сайт RealmForge (https://realmforge-wor.vercel.app), где их
можно смотреть и подбирать снаряжение. Помощник «Переодевание» подсказывает поверх игры,
какой предмет нажать, чтобы надеть сборку с сайта, и обводит нужный предмет рамкой прямо в игре
(рамка — отдельное прозрачное окно программы; для неё программа смотрит на картинку списка на экране).

КАК ЗАПУСТИТЬ
1. Распакуйте архив и запустите RealmForge.exe.
2. Windows может показать «Windows защитила ваш компьютер»: у программы пока нет платной цифровой
   подписи. Нажмите «Подробнее» → «Выполнить в любом случае».
3. Windows спросит, разрешить ли программе вносить изменения, — нажмите «Да». Права администратора
   нужны, потому что игра работает с ними: без них память игры не прочитать.
4. На сайте войдите, откройте «Настройки» и скопируйте код синхронизации в программу.

ЧТО ПРОГРАММА ДЕЛАЕТ С ИГРОЙ
* Программа только ЧИТАЕТ память игры (ReadProcessMemory) и картинку экрана в области списка. Она ничего не записывает в игру,
  не меняет файлы игры и не обращается к серверам игры.
* Автонажатие (Настройки, включено по умолчанию): в «Переодевании» программа сама открывает нужный слот, прокручивает
  список колесом мыши и нажимает нужную вещь — обычными движениями мыши, как игрок, и только пока окно игры впереди, а вы
  не двигаете мышь. Кнопку «Заменить» всегда нажимаете вы. Правила игры могут запрещать автоматизацию — если не хотите
  рисковать, выключите автонажатие.
* Автосинхронизация (Настройки, включена по умолчанию): после смены снаряжения и раз в 5 минут, пока игра запущена,
  программа сама отправляет снимок аккаунта на сайт — сайт знает, что на ком надето.
* Сетевые запросы — только на адрес сайта RealmForge. Никакой телеметрии.
* Код синхронизации хранится зашифрованным (Windows DPAPI) в %APPDATA%\RealmForge.
* Интерфейс работает на встроенном в Windows движке Microsoft Edge WebView2.
* Исходный код открыт: https://github.com/AlexisKozlov/realmforge-extractor

УДАЛЕНИЕ
Удалите RealmForge.exe и папки %APPDATA%\RealmForge и %LOCALAPPDATA%\RealmForge.

Фанатский проект, не связан с разработчиком игры.

----------------------------------------------------------------------------------------------
RealmForge 1.3 (English)

Unzip and run RealmForge.exe. Windows SmartScreen may say "Windows protected your PC" (the app has no
paid code signature yet): click "More info" -> "Run anyway". Then allow administrator rights: the game
runs as administrator, so reading its memory needs them too. Paste the sync code from the site settings.
The program only READS game memory (and looks at the gear list on the screen to frame the right item),
never writes to the game and talks only to the RealmForge site. Auto click (Settings, on by default): the equip
helper opens the slot, scrolls the list and clicks the item with the mouse like a player, only while the game is in
front and you leave the mouse alone; you always press "Replace". The game's rules may forbid automation - turn it off
if you do not want the risk. Auto sync (on by default) sends the account to the site after gear changes and every
5 minutes while the game runs. Source: https://github.com/AlexisKozlov/realmforge-extractor
