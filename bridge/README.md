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
`--Bridge:Port=5056`, `--Bridge:StateFile=D:/data/state.json`.
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
| `GET /api/state` | `200 {version, updatedAt, data, queue:{pending, capacity, accepting}}` |
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
Queue/ActionQueue               ограниченный Channel<ActionJob>
Queue/ActionWorker              BackgroundService: выполнение, отмена при остановке
Queue/ActionJob, JobStore       статусы задач и команд
State/StateStore                данные + неизменяемый снимок для чтения без блокировок
```
