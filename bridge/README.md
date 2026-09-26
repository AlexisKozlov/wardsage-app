# RealmForge.Bridge

Локальный IPC-сервис (.NET 8, Kestrel): мост между локальным веб-дашбордом и системным воркером.
Слушает только loopback — `http://localhost:5055`.

## Сборка и запуск

```
dotnet build -c Release
dotnet bin/Release/net8.0/RealmForge.Bridge.dll            # или: dotnet run -c Release
dotnet publish -c Release -r win-x64 --self-contained -o out   # один каталог без установленного .NET
```

Любую настройку из `appsettings.json` (секция `Bridge`) можно переопределить аргументом:
`--Bridge:Port=5056`, `--Bridge:StateFile=D:/data/state.json`. Справочник игры для имён и слотов:
`--Bridge:ReferenceDir=D:/RealmForge/reference/data` — без него `equipment.equip` отклоняется.
Остановка — Ctrl+C / SIGTERM: новые команды сразу получают 503, выполняемая команда получает отменённый
`CancellationToken`, всё оставшееся в очереди помечается `canceled`.

Запускать от имени пользователя, а не как службу Windows: служба работает в сессии 0 и не может управлять
мышью и окнами на рабочем столе игрока.

## Доступ из браузера

- CORS: `http://localhost:*`, `http://127.0.0.1:*` (и https) и `Origin: null` (дашборд, открытый из файла).
  Запросы с любого другого Origin получают **403**, а не просто ответ без CORS-заголовка.
- Каждый запрос к `/api` должен нести заголовок `X-RealmForge-Token`. Токен создаётся при первом запуске в
  `%LOCALAPPDATA%\RealmForge\bridge-token.txt` (путь пишется в лог). Без него любой сайт мог бы «вслепую»
  отправлять команды на localhost: CORS запрещает только чтение ответа, а `Origin: null` присылает любой
  sandbox-iframe. Отключить можно (`RequireToken: false`), но не нужно.
- `Host` должен быть `localhost` / `127.0.0.1` / `[::1]` — защита от DNS rebinding.

```js
const api = (path, init = {}) => fetch(`http://localhost:5055/api${path}`, {
  ...init, headers: { 'X-RealmForge-Token': token, 'Content-Type': 'application/json', ...init.headers },
});
```

## API

| Запрос | Ответ |
|---|---|
| `GET /api/state` | `200 {version, updatedAt, data, account, queue:{pending, capacity, accepting}, host:{connected, lastSeen}}`; `account` — снимок аккаунта (ниже), `null` пока программа его не прислала |
| `POST /api/actions/apply` | `202` + `Location: /api/jobs/{id}` и статус задачи; `400` — ошибки по каждой команде (ничего не поставлено в очередь); `413` — тело больше `MaxRequestBodyBytes`; `415` — не `application/json`; `503` — очередь полна (`Retry-After`) или сервис останавливается |
| `GET /api/jobs/{id}` | `200` статус задачи и каждой команды: `queued/running/succeeded/failed/canceled`, у команд ещё `pending/skipped`; `404` |

Тело `apply` — массив команд, выполняются по порядку, первая ошибка останавливает задачу (остальные `skipped`):

```json
[
  { "id": "c1", "type": "state.set",    "params": { "path": "account.gear.slot0", "value": { "uid": 42 } } },
  { "id": "c2", "type": "state.remove", "params": { "path": "note" } }
]
```

`id` — 1..64 символа `[A-Za-z0-9._:-]`, уникален в пакете; `type` — зарегистрированный обработчик; `params` —
объект (по умолчанию `{}`). Лишние и повторяющиеся поля — ошибка.

## Снимок аккаунта (`account` в `/api/state`)

```
{ version, capturedAt, updatedAt, gameVersion, provisional,
  heroes: [{ id, baseId, name, level, stars, power, equipped: [{slot, itemId}], artifactId }],
  items:  [{ id, kind: gear|artifact, itemId, name, slotType, setId, rarity, level, locked,
             primaryStat: {poolId, statId, name, isPercent, rawValue, value, rolls}, substats: [...], heroId }] }
```

`id` героя — его uid (baseId × 100000 + копия), `id` предмета — uid в игре. `slot`: `weapon`, `armor`, `bracer`,
`amulet`, `ring` (0..4 в игре). `rarity` — звёзды предмета (`iStarLvl`). Правила разбора — те же, что у сайта
(`lib/game/normalize.ts`). `provisional: true` — на снимок уже наложены результаты экипировки, следующее чтение
игры их подтвердит.

## Экипировка: `equipment.equip`

```json
[{ "id": "e1", "type": "equipment.equip",
   "params": { "heroId": 229000000, "slots": [{ "slot": "weapon", "itemId": 42 }, { "slot": 2, "itemId": "408" }] } }]
```

`heroId`/`itemId` — положительные целые числом или строкой (uid артефактов не влезают в double JS), `slot` — имя
или 0..4, 1..5 слотов без повторов. Сразу при приёме (400) и ещё раз перед выполнением проверяется по снимку:
герой и предметы есть, предмет подходит к слоту. Надевать можно и вещь с другого героя — в игре она с него снимется.

Выполнение (`Equipment/HostEquipmentService.cs`): мост передаёт команду программе RealmForge, та проводит игрока
по гайду (открывает героя, слот, ставит фильтр игры, кликает вещь; «Заменить» — игрок или программа, если в её настройках
включено автоподтверждение) и отвечает. Команда `succeeded` —
вещи на герое, снимок сразу обновлён: вещь ушла с прежнего владельца, то, что было на герое в этих слотах, — в сумку.
`failed` с причиной: `Cancelled` (игрок закрыл гайд), `HostUnavailable` (программа не подключена или пропала),
`TimedOut` (`EquipTimeoutSeconds`, 10 мин), `Rejected` (снимок изменился). Уже надетое — `succeeded` без обращения к
программе.

## API для программы (`/api/host/*`)

Программа — клиент моста, своего сервера у неё нет. Тот же токен (файл в `%LOCALAPPDATA%`, пользователь тот же);
запросы с заголовком `Origin` (из браузера) отклоняются — страница не может выдать себя за программу.

| Запрос | Что делает |
|---|---|
| `PUT /api/host/snapshot` | тело — `account.json` как есть (до 32 МБ); `X-Captured-At` — когда НАЧАЛОСЬ чтение игры, `X-Game-Version`. `200 {version, heroes, items}`; `409` — чтение началось раньше последней экипировки через мост (оно вернуло бы вещи назад), снимок не заменён |
| `GET /api/host/commands?wait=25` | long poll (до 30 с) → `{commands:[{id, type:"equip", issuedAt, payload:{commandId, heroId, heroName, slots:[{slot, itemId, setId, mainStatId}]}}]}` (`setId`/`mainStatId` — для фильтра игры, могут быть `null`). Заодно отметка «программа жива»: не опрашивала `HostOfflineAfterSeconds` (60 с) — считается отключённой |
| `POST /api/host/commands/{id}/result` | `{status: "done"\|"cancelled"\|"failed", message?}` → `204`; `404` — ответ уже не ждут |

### Сторона RealmForge.exe

- `src/BridgeClient.cs`: `BridgeClient` отправляет снимок, забирает команды и отвечает на них (`HttpWebRequest`, без
  прокси, токен из файла). `BridgePoller` — фоновый поток long poll. Пока мост не ответил ни разу, программа
  спрашивает без ожидания и повторяет попытки через 2 → 4 → … → 30 с. `Stop()` обрывает ждущий запрос, поток
  завершается за миллисекунды.
- `app/HostBridge.cs`: после каждого чтения игры (ручного, автосинхронизации, после переодевания) снимок уходит в
  мост с `X-Captured-At` = начало чтения. Когда мост подключён, автосинхронизация читает игру и без кода сайта
  (тогда только для моста). Команда `equip` превращается в план `bridge:<id>` на странице. `equip.finish` отвечает
  мосту `done`/`cancelled`. Закрытие игры даёт `failed`, закрытие программы — `cancelled`: мост узнаёт сразу, а не
  через 10 минут.
- `ui/`: план из моста идёт по тому же гайду (рамка, автонажатие, подсказки). От моста приходят только слот и uid,
  поэтому вместо названия показано «Предмет #uid». Через 4 с после выполнения план убирается, а при
  перезагрузке сборок с сайта не теряется (`RFGuide.mergePlans`).

## Подключение воркера

Новый тип команды — класс с `IActionHandler` (`Actions/IActionHandler.cs`): `Validate` проверяет `params` при
приёме запроса, `ExecuteAsync` выполняется на воркере (по одной команде, с `CancellationToken`). Регистрация —
одна строка в `Program.cs`: `builder.Services.AddSingleton<IActionHandler, MyHandler>();`.
Встроенные `state.set` / `state.remove` меняют данные, которые отдаёт `GET /api/state`.

## Файлы

```
Program.cs                      хост, Kestrel (loopback), DI, graceful shutdown
Endpoints.cs                    /api/state, /api/actions/apply, /api/jobs/{id}
BridgeOptions.cs                настройки (appsettings.json → Bridge)
Security/LocalAccessMiddleware  Host, Origin, CORS/preflight, токен
Security/AccessToken            токен (создание, сравнение за постоянное время)
Actions/ActionBatchParser       разбор и строгая валидация пакета команд
Actions/IActionHandler          контракт обработчика + реестр
Actions/StateActionHandlers     state.set, state.remove
Actions/EquipActionHandler      equipment.equip: параметры, проверка по снимку, вызов IEquipmentService
Account/AccountSnapshot         DTO снимка: герои, предметы, слоты
Account/AccountSnapshotMapper   account.json → DTO (правила сайта)
Account/AccountSnapshotStore    текущий снимок: замена чтением программы, сдвиг вещей после экипировки
Account/GameReference           справочник: имена, слоты и комплекты вещей, статы пулов
Equipment/IEquipmentService     контракт экипировки; HostEquipmentService — через RealmForge.exe
Equipment/EquipPlanChecker      герой и вещи есть в снимке, вещь подходит к слоту
Host/HostLink                   очередь команд для программы, ожидание ответа, «программа жива»
Host/HostEndpoints              /api/host/snapshot, /api/host/commands, /api/host/commands/{id}/result
Queue/ActionQueue               ограниченный Channel<ActionJob>
Queue/ActionWorker              BackgroundService: выполнение, отмена при остановке
Queue/ActionJob, JobStore       статусы задач и команд
State/StateStore                данные + неизменяемый снимок для чтения без блокировок
```
