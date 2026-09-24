# RealmForge Extractor

Reads a Watcher of Realms account (heroes, gear, artifacts, faction rewards) from the memory of the
running game and sends it to the RealmForge site. Windows only; **read-only** (`ReadProcessMemory`).
Player instructions (RU/EN): [README.txt](README.txt).

What players get (`dist/RealmForge-Extractor.zip`):

| File | |
|---|---|
| `Run-RealmForge.bat` | starts the script with a hidden console |
| `RealmForge-Extractor.ps1` | the program: PowerShell 5.1 + C# 5 compiled in memory by `Add-Type` |
| `README.txt` | what it does, why admin rights, troubleshooting |

Nothing to install: Windows 10/11 ship PowerShell 5.1 and .NET Framework 4.x.

## Layout

```
src/MemoryReader.cs     v0.4 Lua-table scanner, unchanged except for small hooks (see its header)
src/GameInfo.cs         game process, exe path, version from realversion.xml
src/Extractor.cs        pipeline: find game -> read -> validate/count -> save copy
src/SyncClient.cs       POST {site}/api/sync (gzip, Bearer code, TLS 1.2, 60 s)
src/Config.cs           %APPDATA%\RealmForge\config.json
src/CodeProtector.cs    DPAPI (CurrentUser) for the sync code
src/Strings.cs          RU/EN texts
src/MainForm.cs         the WinForms window (BackgroundWorker for all slow work)
src/App.cs              entry point: hides the console, STA, DPI
src/RealmForge-Extractor.template.ps1   elevation + Add-Type + start
tools/build.mjs         src/ -> RealmForge-Extractor.ps1 (UTF-8 BOM, CRLF); --zip -> dist/
tests/                  mock server, C# tests, compile checks, UI screenshots
```

`RealmForge-Extractor.ps1` is generated: edit `src/`, then `node tools/build.mjs`.
All C# must stay **C# 5** (the compiler built into .NET Framework): no `$"..."`, `?.`, `=>` members, `nameof`.

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
