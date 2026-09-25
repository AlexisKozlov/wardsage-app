# RealmForge Extractor

Reads a Watcher of Realms account (heroes, gear, artifacts, faction rewards) from the memory of the
running game and sends it to the RealmForge site. Windows only; the game's memory is only read (`ReadProcessMemory`).
The desktop app's equip helper can also open the gear slot, scroll the list and click the item with the mouse
(`src/AutoPilot.cs`, Settings → «Автонажатие», on by default; «Заменить» is always the player's), and sends the account
by itself after gear changes (Settings → «Автосинхронизация»).
Player instructions (RU/EN): [README.txt](README.txt).

What players get (`dist/RealmForge.zip`): **`RealmForge.exe`** — one file (≈1.3 MB), .NET Framework 4.6.2+,
x64, asks for administrator rights (the game runs elevated). The interface is HTML/CSS (`ui/`) rendered by the
Microsoft Edge **WebView2** Runtime that ships with Windows 10/11; the WebView2 SDK (`vendor/webview2`, Microsoft-signed)
and `ui/` are embedded and unpacked to `%LOCALAPPDATA%\RealmForge\app\<build>\` on start.

## Layout

```
ui/                     the interface: index.html, app.css, app.js (screens), guide.js (equip hints), i18n.js,
                        mock.js (browser preview only, not shipped), fonts (Cinzel, Alegreya Sans — OFL)
app/Program.cs          entry: single instance, unpack resources, WebView2 runtime check
app/AppWindow.cs        the window: WebView2, dark frame, «Поверх игры» compact always-on-top mode
app/HostBridge.cs       page <-> program messages (see its header): sync, codes, plans, equip helper polling
src/MemoryReader.cs     Lua-table scanner (read-only)
src/EquipScan.cs        equip helper: finds the gear-list panel once, then re-reads it (read-only)
src/GameInfo.cs         game process, exe path, version from realversion.xml
src/Extractor.cs        pipeline: find game -> read -> validate/count -> save copy
src/SyncClient.cs       POST {site}/api/sync (gzip, Bearer code, TLS 1.2, 60 s)
src/PlansClient.cs      GET/POST {site}/api/extractor/plans (builds sent with «Надеть в игре»)
src/Config.cs           %APPDATA%\RealmForge\config.json (+ last sync), CodeProtector.cs: DPAPI for the code
tools/build-app.sh      -> dist/RealmForge.exe (Roslyn from the .NET 8 SDK + Mono's .NET Framework reference assemblies)
tools/pack-app.sh       -> dist/RealmForge.zip (exe + README)
tests/                  C# core tests (.NET 8 + Mono), guide.test.mjs, mock server, compile checks
```

Preview the interface without Windows: serve `ui/` and open `index.html?s=idle|reading|done|error|onboard|equip|pick|hidden|compact|settings`.

Legacy 0.x (PowerShell script, `src/MainForm.cs`, `src/HelperForm.cs`, `tools/build.mjs`) is still built by the tests
but no longer shipped.

## Sync contract

`POST {site}/api/sync` with `Authorization: Bearer rf_<32 base62>`, `Content-Type: application/json`,
`Content-Encoding: gzip`, `X-RF-Extractor: 0.5`, `User-Agent: RealmForge-Extractor/0.5`; body = gzip of the
UTF-8 `account.json`. Replies: `200 {ok, snapshotId, heroes, items, artifacts, viewUrl}`, `401`, `413`, `415`,
`422 {details}`, `429` (`Retry-After`), `5xx`. Redirects are not followed (a redirected POST loses its body).

## Checks (Linux, no Windows needed)

```
npm install                # tree-sitter PowerShell grammar for the syntax check (optional)
bash tests/run-tests.sh    # build check, C# 5 compile vs .NET Framework 4.5 API, tests vs mock server
tests/ui/preview.sh        # screenshots of every window state (Mono + Xvfb)
node tests/mock-server.mjs # the mock /api/sync on :3999, for manual runs
```

Requires the .NET 8 SDK (its Roslyn `csc.dll` is used directly, no NuGet) and, for the .NET Framework
reference assemblies and the second test run, Mono (`mono-devel libmono-system-windows-forms4.0-cil`).
