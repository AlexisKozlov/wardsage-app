# Reads the open Watcher of Realms account and writes one JSON file.
# No window, no request to the site. The game must be running.
# Run from an elevated PowerShell if OpenProcess is denied:
#   powershell -ExecutionPolicy Bypass -File tools\dump-account.ps1 C:\path\account.json
param([Parameter(Mandatory = $true)][string]$OutFile)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { Write-Error "dotnet SDK not found: $dotnet"; exit 1 }
$sdk = Get-ChildItem 'C:\Program Files\dotnet\sdk' -Directory | Where-Object { $_.Name -like '8.*' } | Sort-Object Name | Select-Object -Last 1
$csc = Join-Path $sdk.FullName 'Roslyn\bincore\csc.dll'
$refRoot = Get-ChildItem 'C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref' -Directory | Sort-Object Name | Select-Object -Last 1
$refDir = Join-Path $refRoot.FullName 'ref\net8.0'
$refs = @(Get-ChildItem $refDir -Filter *.dll | ForEach-Object { '-r:' + $_.FullName })
$names = 'src/ArenaClient.cs src/ArenaOpponents.cs src/AutoPilot.cs src/BattleCapture.cs src/BattleClock.cs src/BattleTimeline.cs src/BridgeClient.cs src/Channel.cs src/Config.cs src/EquipGuide.cs src/EquipScan.cs src/ErrorReport.cs src/Extractor.cs src/FightPlanClient.cs src/FilterPilot.cs src/GameInfo.cs src/GamePacks.cs src/GuildData.cs src/GvgData.cs src/HeroPilot.cs src/Layout.cs src/ListTracker.cs src/HintLayout.cs src/LiveTables.cs src/LocateClient.cs src/LocateGuide.cs src/MemoryReader.cs src/MiniJson.cs src/NewsClient.cs src/PlansClient.cs src/RecastScan.cs src/RemoteConfig.cs src/SellClient.cs src/SellPilot.cs src/SellScan.cs src/Strings.cs src/SyncClient.cs src/TimelineBuilder.cs src/TestLog.cs src/TraceUpload.cs src/UpdateKeys.cs src/WebViewRecovery.cs app/HintGeometry.cs'.Split(' ')
$core = @($names | ForEach-Object { Join-Path $root ($_ -replace '/','\') })
$core += Join-Path $root 'tests\CodeProtectorStub.cs'
$out = Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$lib = Join-Path $out 'RealmForge.Core.dll'
$exe = Join-Path $out 'dump-account.dll'
& $dotnet exec $csc -nologo -langversion:5 -target:library -nostdlib -warnaserror -nowarn:SYSLIB0014 @refs "-out:$lib" @core
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet exec $csc -nologo -langversion:5 -target:exe -nostdlib -nowarn:SYSLIB0014 @refs "-r:$lib" "-out:$exe" (Join-Path $root 'tools\DumpMain.cs')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$rt = Get-ChildItem 'C:\Program Files\dotnet\shared\Microsoft.NETCore.App' -Directory | Where-Object { $_.Name -like '8.*' } | Sort-Object Name | Select-Object -Last 1
@{ runtimeOptions = @{ tfm = 'net8.0'; framework = @{ name = 'Microsoft.NETCore.App'; version = $rt.Name } } } | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $out 'dump-account.runtimeconfig.json') -Encoding ascii
& $dotnet exec --runtimeconfig (Join-Path $out 'dump-account.runtimeconfig.json') $exe $OutFile
exit $LASTEXITCODE
