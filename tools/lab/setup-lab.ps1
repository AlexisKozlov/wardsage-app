# setup-lab.ps1 - one-time setup of the Wardsage test lab runner on this laptop (PowerShell 5.1, no admin needed).
#
#   powershell -ExecutionPolicy Bypass -File setup-lab.ps1 -TestAppPath "C:\path\to\Wardsage-Test.exe"
#
# It does exactly this and nothing else:
#   1. asks you to paste the device bundle (the one-line text made on the server by lab-mint-token.sh) and stores the
#      token and the public API key encrypted with DPAPI (only this Windows user on this PC can read them);
#   2. writes %LOCALAPPDATA%\WardsageLab\config.json (API host, device name, test app path, update host and the pinned
#      RSA moduli copied from src/UpdateKeys.cs KeyN + TestKeyN);
#   3. copies WardsageLab.ps1 next to it;
#   4. creates the desktop shortcut "Wardsage Lab" that starts the runner.
# No scheduled task, no autostart, no service. Run it again to change the token or the paths.
param(
    [Parameter(Mandatory = $true)][string]$TestAppPath,
    [string]$UpdateHost = 'https://test.wardsage.com'
)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

# Pinned public key moduli (base64url) - copied from src/UpdateKeys.cs of the app. If the keys ever change, re-copy.
$KeyN     = '6MiNF84wJ_zDGNfzUv5x11fzdDrgLEBJsZUvjuhpFYv90wVNCUbKTU7FkmGcFFz-n0SUx6n1dkVwkc98JB8qzA_2SLTBtX_WqdqFBOzm4dQubRzxkPjOWVP0i2OYZYou2G4txHDNcOi66yZXfVw1-HhtjB7osxEAKR62O2gVBx3_GKUQs_Pq08PPDx-jBvaBqt4Yrr5UoMPypU7OZKQzVv_NQ3aOV9s_8_bn5TWygaez5hgAGipdciZZ91FTOStk_yLF0RanB2CFbGQIhZckGI0HefEy7aw_LlsOkWcFOYG4QRDMgWfW1kNGrqOCETfBwXPM0Krb3jT4mXTO4pL2kbC_MYXo2vwcyi8BPZZfOtNu4DCJ0D0GXpTy4zYk8cvBhTHoY9-_xKMfoI3eMLW5WOUFzBrzNiz-oLfPsGsntbZmo1FgJDUyKI21FIc0wVCjVCZ-GkstjizfzxIeMoQMFEh3tSHn7bPmeE2L5zmDHuGIm2JgTRzfmy4DcPZQV0Zj'
$TestKeyN = '9ZmiNDqwjh93pt2GrIE-8gIB0BxH08z8XglJqknEK3LTAoEE_f0gJtqbk2le-OB2iv2R8WZboGR7bYAKa0Mt9SGt8ZtuMRnLJNnj-0K1pj7PPvAUfRhEVwZwKhqXNQEvfU8HWkuCOmkVP04OrHkhJXTILwJmbP2nZsgFjRzheaVeVb3rnkDkhTBxbcTh-Xjjqc2RqIBjw_C3JemBJhWI_ngcbY2H8hE60R6S9MIiyrxqJZBBTJrhB57vsyhWygPL1-rONNYQPtnqXlhhH1RA9c1oP2x983MbAJQqYrBp7HDnnRqXmXjv2pTfSySQsVfnyawRwM3ws4_OLg7x7Htfa33efo_sqeKSjXwcPu7CjkXDoZZmSvATwUBGNlDotubqdbRW9ylSuDYg4MgXOGBI-SRi-9Id8GIjIk1oai8auadnuNdTp4gHnfQ4tUtt4Ya1zql8BND3KB2w9-wszH9gM_4w59Mp3Ka3W2JiL6AeajpLZyAngtEL_1rpb8fAhw4r'
$KeyE     = 'AQAB'

if (-not (Test-Path -LiteralPath $TestAppPath)) { throw 'TestAppPath does not exist.' }
if ($UpdateHost -notmatch '^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?$') { throw 'UpdateHost must look like https://host' }
$runnerSrc = Join-Path $PSScriptRoot 'WardsageLab.ps1'
if (-not (Test-Path $runnerSrc)) { throw 'WardsageLab.ps1 must be next to this script.' }

$secure = Read-Host 'Paste the device bundle (input is hidden) and press Enter' -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try { $plain = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
$bundle = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($plain.Trim())) | ConvertFrom-Json
if ($bundle.host -notmatch '^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?$') { throw 'Bad host in the bundle.' }
if ($bundle.device -notmatch '^[a-z0-9-]{1,32}$') { throw 'Bad device name in the bundle.' }
if (-not $bundle.token -or -not $bundle.apikey) { throw 'The bundle has no token.' }

$dir = Join-Path $env:LOCALAPPDATA 'WardsageLab'
New-Item -ItemType Directory -Force -Path $dir | Out-Null
(ConvertTo-SecureString -String $bundle.token -AsPlainText -Force | ConvertFrom-SecureString) | Set-Content -Path (Join-Path $dir 'token.dat')
(ConvertTo-SecureString -String $bundle.apikey -AsPlainText -Force | ConvertFrom-SecureString) | Set-Content -Path (Join-Path $dir 'apikey.dat')

$cfg = [ordered]@{
    host        = $bundle.host
    device      = $bundle.device
    testAppPath = (Resolve-Path -LiteralPath $TestAppPath).Path
    updateHost  = $UpdateHost
    keys        = [ordered]@{ KeyN = $KeyN; TestKeyN = $TestKeyN; KeyE = $KeyE }
}
$cfg | ConvertTo-Json -Depth 4 | Set-Content -Path (Join-Path $dir 'config.json') -Encoding UTF8
Copy-Item -Path $runnerSrc -Destination (Join-Path $dir 'WardsageLab.ps1') -Force
Remove-Item -Path (Join-Path $dir 'STOP') -ErrorAction SilentlyContinue

# desktop shortcut that starts the runner (minimized console; the owner double-clicks it by hand)
$desktop = [Environment]::GetFolderPath('Desktop')
$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut((Join-Path $desktop 'Wardsage Lab.lnk'))
$lnk.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$lnk.Arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $dir 'WardsageLab.ps1') + '"'
$lnk.WorkingDirectory = $dir
$lnk.WindowStyle = 7
$lnk.Description = 'Start the Wardsage test lab runner (after the game is running)'
$lnk.Save()
# "Run as administrator": set bit 0x20 of byte 0x15 in the .lnk (the test app needs admin rights)
$lnkPath = Join-Path $desktop 'Wardsage Lab.lnk'
$lnkBytes = [IO.File]::ReadAllBytes($lnkPath)
$lnkBytes[0x15] = $lnkBytes[0x15] -bor 0x20
[IO.File]::WriteAllBytes($lnkPath, $lnkBytes)
Write-Host 'Done. Start the game yourself, then double-click "Wardsage Lab" on the desktop.'
Write-Host 'To stop the runner at once, create the file:' (Join-Path $dir 'STOP')
