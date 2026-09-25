RealmForge 1.0
==============

ЧТО ЭТО
Программа для Watcher of Realms (Windows 10/11). Читает из запущенной игры ваших героев и
снаряжение и отправляет снимок на сайт RealmForge (https://realmforge-wor.vercel.app), где их
можно смотреть и подбирать снаряжение. Помощник «Переодевание» подсказывает поверх игры,
какой предмет нажать, чтобы надеть сборку с сайта.

КАК ЗАПУСТИТЬ
1. Распакуйте архив и запустите RealmForge.exe.
2. Windows может показать «Windows защитила ваш компьютер»: у программы пока нет платной цифровой
   подписи. Нажмите «Подробнее» → «Выполнить в любом случае».
3. Windows спросит, разрешить ли программе вносить изменения, — нажмите «Да». Права администратора
   нужны, потому что игра работает с ними: без них память игры не прочитать.
4. На сайте войдите, откройте «Настройки» и скопируйте код синхронизации в программу.

ТОЛЬКО ЧТЕНИЕ
* Программа только ЧИТАЕТ память игры (ReadProcessMemory). Она ничего не записывает в игру,
  ничего не нажимает за вас, не меняет файлы игры и не обращается к серверам игры.
* Сетевые запросы — только на адрес сайта RealmForge. Никакой телеметрии.
* Код синхронизации хранится зашифрованным (Windows DPAPI) в %APPDATA%\RealmForge.
* Интерфейс работает на встроенном в Windows движке Microsoft Edge WebView2.
* Исходный код открыт: https://github.com/AlexisKozlov/realmforge-extractor

УДАЛЕНИЕ
Удалите RealmForge.exe и папки %APPDATA%\RealmForge и %LOCALAPPDATA%\RealmForge.

Фанатский проект, не связан с разработчиком игры.

----------------------------------------------------------------------------------------------
RealmForge 1.0 (English)

Unzip and run RealmForge.exe. Windows SmartScreen may say "Windows protected your PC" (the app has no
paid code signature yet): click "More info" -> "Run anyway". Then allow administrator rights: the game
runs as administrator, so reading its memory needs them too. Paste the sync code from the site settings.
The program only READS game memory, never writes to the game, never presses anything for you, and talks
only to the RealmForge site. Source: https://github.com/AlexisKozlov/realmforge-extractor
