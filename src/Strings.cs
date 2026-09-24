// RealmForge extractor - interface texts (Russian by default, English).

using System;
using System.Collections.Generic;

namespace RealmForge {
  public static class Strings {
    public static string Lang = "ru";

    static readonly Dictionary<string, string[]> T = new Dictionary<string, string[]> {
      // key                    ru                                                         en
      { "subtitle",           new[] { "Экстрактор аккаунта · v" + RFX.ExtractorVersion, "Account extractor · v" + RFX.ExtractorVersion } },
      { "code_label",         new[] { "Код синхронизации", "Sync code" } },
      { "code_show",          new[] { "Показать", "Show" } },
      { "code_hint",          new[] { "Код выдаёт сайт RealmForge: войдите и откройте раздел «Синхронизация». Без кода можно только сохранить файл.",
                                      "Get the code on the RealmForge site: sign in and open the Sync section. Without a code you can only save the file." } },
      { "code_ok",            new[] { "✓ Код в порядке", "✓ The code looks right" } },
      { "code_bad",           new[] { "Код: rf_ и ещё 32 латинские буквы или цифры (сейчас символов: {0} из 35)",
                                      "A code is rf_ followed by 32 Latin letters or digits ({0} of 35 characters)" } },
      { "save_copy",          new[] { "Сохранить копию account.json (Документы\\RealmForge)", "Save a copy of account.json (Documents\\RealmForge)" } },
      { "advanced",           new[] { "Дополнительно", "Advanced" } },
      { "site_label",         new[] { "Адрес сайта", "Site address" } },
      { "site_reset",         new[] { "По умолчанию", "Default" } },
      { "site_invalid",       new[] { "Неверный адрес сайта (раздел «Дополнительно»).", "Invalid site address (see Advanced)." } },
      { "site_https",         new[] { "Адрес сайта должен начинаться с https:// (http — только для localhost).",
                                      "The site address must start with https:// (http only for localhost)." } },
      { "btn_sync",           new[] { "Синхронизировать", "Sync" } },
      { "btn_save_only",      new[] { "Только сохранить файл", "Only save the file" } },
      { "btn_busy",           new[] { "Подождите…", "Please wait…" } },
      { "btn_open_site",      new[] { "Открыть сайт", "Open the site" } },
      { "btn_open_folder",    new[] { "Открыть папку", "Open folder" } },
      { "btn_restart_admin",  new[] { "Перезапустить от администратора", "Restart as administrator" } },
      { "btn_log",            new[] { "Журнал", "Log" } },
      { "step_find",          new[] { "Ищу игру", "Looking for the game" } },
      { "step_read",          new[] { "Читаю память (~40 с)", "Reading memory (~40 s)" } },
      { "step_send",          new[] { "Отправляю", "Sending" } },
      { "step_save",          new[] { "Сохраняю файл", "Saving the file" } },
      { "step_done",          new[] { "Готово", "Done" } },
      { "seconds",            new[] { "{0} с", "{0} s" } },
      { "st_ready",           new[] { "Запустите игру, дождитесь главного экрана и нажмите «{0}».",
                                      "Start the game, wait for the main screen and press \"{0}\"." } },
      { "st_find",            new[] { "Ищу процесс Watcher of Realms…", "Looking for the Watcher of Realms process…" } },
      { "st_read",            new[] { "Читаю память игры, это около 40 секунд. Игру можно не трогать.",
                                      "Reading game memory, about 40 seconds. You can leave the game as it is." } },
      { "st_send",            new[] { "Отправляю данные на сайт…", "Sending the data to the site…" } },
      { "st_save",            new[] { "Сохраняю account.json…", "Saving account.json…" } },
      { "done",               new[] { "Готово: {0}, {1}, {2}.", "Done: {0}, {1}, {2}." } },
      { "done_saved",         new[] { "Файл: {0}", "File: {0}" } },
      { "game_version",       new[] { "Версия игры: {0}", "Game version: {0}" } },
      { "err_not_running",    new[] { "Игра не запущена. Запустите Watcher of Realms, дождитесь главного экрана и нажмите ещё раз.",
                                      "The game is not running. Start Watcher of Realms, wait for the main screen and try again." } },
      { "err_access",         new[] { "Нет прав на чтение памяти игры: она запущена от администратора. Перезапустите RealmForge от администратора (кнопка ниже или Run-RealmForge.bat).",
                                      "No permission to read the game memory: the game runs as administrator. Restart RealmForge as administrator (button below or Run-RealmForge.bat)." } },
      { "err_open",           new[] { "Не удалось открыть процесс игры (код Windows {0}). Попробуйте перезапустить игру и RealmForge.",
                                      "Could not open the game process (Windows code {0}). Try restarting the game and RealmForge." } },
      { "err_no_data",        new[] { "Данные аккаунта не найдены в памяти. Войдите в игру до главного экрана (не экран загрузки) и повторите.",
                                      "No account data found in memory. Get into the game up to the main screen (not the loading screen) and try again." } },
      { "err_failed",         new[] { "Не удалось прочитать память игры: {0}", "Could not read the game memory: {0}" } },
      { "err_token",          new[] { "Код синхронизации не принят (401). Скопируйте код на сайте заново и вставьте его сюда.",
                                      "The sync code was rejected (401). Copy the code from the site again and paste it here." } },
      { "err_rate",           new[] { "Слишком частая синхронизация (429). Повторите через {0}.", "Syncing too often (429). Try again in {0}." } },
      { "err_rate_nowait",    new[] { "Слишком частая синхронизация (429). Подождите несколько минут.", "Syncing too often (429). Wait a few minutes." } },
      { "err_too_large",      new[] { "Сайт отклонил данные: слишком большой объём (413). Сохраните копию и сообщите разработчикам.",
                                      "The site rejected the data as too large (413). Save a copy and tell the developers." } },
      { "err_media",          new[] { "Сайт не принял формат данных (415). Скорее всего, экстрактор устарел: скачайте новую версию на сайте.",
                                      "The site did not accept the data format (415). The extractor is probably outdated: download a new one from the site." } },
      { "err_payload",        new[] { "Сайт не смог разобрать данные (HTTP {0}). Повторите на главном экране игры; если не помогло — обновите экстрактор.",
                                      "The site could not process the data (HTTP {0}). Try again on the game's main screen; if that fails, update the extractor." } },
      { "err_server",         new[] { "Ошибка на стороне сайта (HTTP {0}). Попробуйте позже.", "Site error (HTTP {0}). Try again later." } },
      { "err_redirect",       new[] { "Сайт перенаправляет на другой адрес: {0}. Укажите его в «Дополнительно».",
                                      "The site redirects to another address: {0}. Enter it under Advanced." } },
      { "err_timeout",        new[] { "Сайт не ответил за 60 секунд. Проверьте интернет и попробуйте ещё раз.",
                                      "The site did not answer within 60 seconds. Check your connection and try again." } },
      { "err_unreachable",    new[] { "Сайт недоступен. Проверьте интернет и адрес в «Дополнительно». ({0})",
                                      "The site is unreachable. Check your connection and the address under Advanced. ({0})" } },
      { "err_unexpected",     new[] { "Неожиданный ответ сайта (HTTP {0}). Проверьте адрес сайта или обновите экстрактор.",
                                      "Unexpected reply from the site (HTTP {0}). Check the site address or update the extractor." } },
      { "err_save",           new[] { "Не удалось сохранить файл: {0}", "Could not save the file: {0}" } },
      { "err_internal",       new[] { "Внутренняя ошибка: {0}", "Internal error: {0}" } },
      { "details",            new[] { "Подробности: {0}", "Details: {0}" } },
      { "confirm_close",      new[] { "Экстрактор ещё работает. Закрыть окно?", "The extractor is still working. Close the window?" } },
      { "footer",             new[] { "Только чтение: программа читает память игры и ничего в ней не меняет.",
                                      "Read-only: the program reads game memory and never changes anything in it." } },
      { "minutes",            new[] { "{0} мин", "{0} min" } },
      // equip helper («Переодевание»)
      { "btn_equip",          new[] { "Переодевание", "Equip helper" } },
      { "eq_title",           new[] { "Переодевание", "Equip helper" } },
      { "eq_plan",            new[] { "Сборка", "Build" } },
      { "eq_reload",          new[] { "Обновить сборки", "Reload builds" } },
      { "eq_rescan",          new[] { "Найти заново", "Search again" } },
      { "eq_skip",            new[] { "Убрать сборку", "Remove build" } },
      { "eq_loading",         new[] { "Загружаю сборки с сайта…", "Loading builds from the site…" } },
      { "eq_none",            new[] { "Сборок нет. На сайте в «Оптимизаторе» нажмите «Надеть в игре», затем «Обновить сборки».",
                                      "No builds. On the site, press “Equip in game” in the Optimizer, then “Reload builds”." } },
      { "eq_net",             new[] { "Не удалось загрузить сборки: {0}", "Could not load the builds: {0}" } },
      { "eq_code",            new[] { "Код синхронизации не подходит — выпустите новый в настройках сайта.", "The sync code is not accepted: issue a new one in the site settings." } },
      { "eq_scanning",        new[] { "Ищу окно снаряжения в игре (~1 мин, один раз)…", "Looking for the gear screen in the game (~1 min, once)…" } },
      { "eq_scan_fail",       new[] { "Не удалось прочитать игру. Запустите игру и нажмите «Найти заново».", "Could not read the game. Start the game and press “Search again”." } },
      { "eq_no_panel",        new[] { "Откройте в игре любого героя → Снаряжение → нажмите на слот, затем «Найти заново».",
                                      "In the game open any hero → Gear → press a slot, then “Search again”." } },
      { "eq_closed",          new[] { "Игра закрыта.", "The game is closed." } },
      { "eq_open_hero",       new[] { "Откройте в игре героя «{0}» → Снаряжение.", "In the game open “{0}” → Gear." } },
      { "eq_open_slot",       new[] { "Нажмите на слот «{0}».", "Press the “{0}” slot." } },
      { "eq_pick",            new[] { "Нажмите предмет: ряд {0}, {1}-й слева, затем «Надеть» / «Заменить».",
                                      "Press the item: row {0}, #{1} from the left, then “Equip” / “Replace”." } },
      { "eq_pick_row",        new[] { "Нажмите предмет в ряду {0}, затем «Надеть» / «Заменить».", "Press the item in row {0}, then “Equip” / “Replace”." } },
      { "eq_scroll",          new[] { "Прокрутите список вниз до ряда {0}.", "Scroll the list down to row {0}." } },
      { "eq_hidden_equipped", new[] { "Предмет сейчас на герое {0}. В фильтре списка снимите «Скрыть надетое».",
                                      "The item is on {0}. In the list filter, turn off “Hide equipped”." } },
      { "eq_other_hero",      new[] { "другом", "another hero" } },
      { "eq_hidden_enh",      new[] { "Список скрывает прокачанные предметы — снимите этот фильтр.", "The list hides enhanced items: turn that filter off." } },
      { "eq_hidden_filter",   new[] { "Предмет скрыт фильтром — сбросьте фильтры списка.", "The item is hidden by a filter: reset the list filters." } },
      { "eq_not_in_list",     new[] { "Предмета нет в списке. Возможно, он продан — синхронизируйте аккаунт и подберите сборку заново.",
                                      "The item is not in the list. It may be gone: sync the account and optimize again." } },
      { "eq_done",            new[] { "Готово ✓ Сборка надета.", "Done ✓ The build is on." } },
      { "eq_done_sent",       new[] { "Готово ✓ Сборка надета и отмечена на сайте.", "Done ✓ The build is on and marked on the site." } },
      { "eq_progress",        new[] { "Надето {0} из {1}", "{0} of {1} on" } },
      { "eq_note",            new[] { "Помощник только читает игру и подсказывает. Предметы надеваете вы.",
                                      "The helper only reads the game and gives hints. You put the items on yourself." } },
    };

    public static string Get(string key) {
      string[] v;
      if (!T.TryGetValue(key, out v)) return key;
      return Lang == "en" ? v[1] : v[0];
    }

    public static string Format(string key, params object[] args) { return string.Format(Get(key), args); }

    // "5 героев" / "1 hero"
    public static string Count(int n, string ruOne, string ruFew, string ruMany, string enOne, string enMany) {
      if (Lang == "en") return n + " " + (n == 1 ? enOne : enMany);
      int m10 = n % 10, m100 = n % 100;
      string w = (m10 == 1 && m100 != 11) ? ruOne : (m10 >= 2 && m10 <= 4 && (m100 < 12 || m100 > 14)) ? ruFew : ruMany;
      return n + " " + w;
    }

    public static string Heroes(int n) { return Count(n, "герой", "героя", "героев", "hero", "heroes"); }
    public static string Items(int n) { return Count(n, "предмет", "предмета", "предметов", "item", "items"); }
    public static string Artifacts(int n) { return Count(n, "артефакт", "артефакта", "артефактов", "artifact", "artifacts"); }

    public static string Wait(int seconds) {
      if (seconds < 60) return Format("seconds", Math.Max(1, seconds));
      return Format("minutes", (seconds + 59) / 60);
    }
  }
}
