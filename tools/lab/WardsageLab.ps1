# WardsageLab.ps1 - the test-lab runner for the Wardsage TEST app on one Windows laptop (PowerShell 5.1).
#
# What it is: a small whitelist executor. It is started by hand (desktop shortcut made by setup-lab.ps1) after the
# owner switched the laptop on and started the game. Every 60 s it sends a heartbeat to the staging server; every
# few seconds it asks for ONE job, runs it and reports the result. When the server queues the "finish" job, the runner
# exits by itself. Automatic start is opt-in: setup-lab.ps1 -Autostart registers a logon task that starts it with -Service
# (then it does not exit after "finish": it keeps heart-beating and waits for the next run; MaxRunnerHours only restarts its loop).
#
# Self-update: every 30 min (and at start) it reads <updateHost>/downloads/test/lab/WardsageLab.json; when the version is higher
# than $RunnerVersion it downloads WardsageLab.ps1, checks size, sha256 and the RSA signature against the pinned keys (the same
# check as update_app), replaces its own file atomically and starts itself again. The server cannot send code through a job.
#
# What it never does: click or type anywhere, open a shell for the server, run anything the server sends, or let a job
# value reach a command line or a file path. The jobs are only:
#   status | update_app | start_game | restart_app | game_front | screenshot | upload_logs | finish | cancel
# each with a strict argument schema (see $Schemas). The only processes it starts/stops are the configured test app and,
# for start_game, the configured game exe (started directly when the game is not running: no launcher, no clicks).
#
# Local kill switch: create the file  %LOCALAPPDATA%\WardsageLab\STOP  (the runner exits at once, even mid-wait).

param([switch]$Service)
Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$RunnerVersion   = '3'
$Dir             = Join-Path $env:LOCALAPPDATA 'WardsageLab'
$StopFile        = Join-Path $Dir 'STOP'
$LogFile         = Join-Path $Dir 'runner.log'
$HeartbeatSec    = 60
$ClaimSec        = 15
$MaxRunnerHours  = 4          # the runner never lives longer than this
$MaxUpdateBytes  = 64MB       # same limit as app/Updater.cs MaxSize
$GameProcessName = 'Watcher of Realms'   # src/GameInfo.cs ProcessName (the runner never stops it; start_game starts gameExe)
$SelfCheckMin    = 30         # runner self-update check interval
$MaxRunnerBytes  = 512KB
$GameWaitSec     = 180        # start_game: max wait for the game process + main window
$GameStableSec   = 120        # start_game: the game must stay up this long (it may exit when it needs its launcher)

function Write-Log([string]$msg) {
    try {
        if ((Test-Path $LogFile) -and ((Get-Item $LogFile).Length -gt 512KB)) { Remove-Item $LogFile -Force }
        Add-Content -Path $LogFile -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $msg)
    } catch { }
    Write-Host $msg
}

# Server data is read only through Get-Prop: under StrictMode a missing property on a parsed object would throw.
function Get-Prop($obj, [string]$name) {
    if ($obj -isnot [PSCustomObject]) { return $null }
    $p = $obj.PSObject.Properties[$name]
    if ($p -eq $null) { return $null }
    return $p.Value
}
# lab_claim answers null/''/[]/{} when nothing is queued: a claim is a job only with a non-empty id and action
function Get-ClaimJob($d) {
    if ($d -is [array]) {
        if ($d.Count -ne 1) { return $null }
        $d = $d[0]
    }
    if ($d -isnot [PSCustomObject]) { return $null }
    if ([string]::IsNullOrEmpty([string](Get-Prop $d 'id')) -or [string]::IsNullOrEmpty([string](Get-Prop $d 'action'))) { return $null }
    return $d
}

# the test app's manifest is requireAdministrator: the runner must be elevated to start/stop it (checked before the mutex)
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Run the lab as administrator: the test app requires admin rights'
    try {
        $relaunch = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
        if ($Service) { $relaunch += ' -Service' }
        Start-Process -FilePath 'powershell.exe' -ArgumentList $relaunch -Verb RunAs
    } catch { Write-Host 'Elevation was not granted; the runner did not start.' }
    exit 0
}

# ---------------------------------------------------------------- config, secrets
if (-not (Test-Path (Join-Path $Dir 'config.json'))) { Write-Host 'Not set up: run setup-lab.ps1 first.'; exit 2 }
$Cfg = Get-Content -Raw -Path (Join-Path $Dir 'config.json') | ConvertFrom-Json

function Read-Secret([string]$file) {
    $ss = Get-Content -Path (Join-Path $Dir $file) | ConvertTo-SecureString      # DPAPI, this user on this PC only
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($ss)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
$Token  = Read-Secret 'token.dat'
$ApiKey = Read-Secret 'apikey.dat'

if ($Cfg.host -notmatch '^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?$') { Write-Host 'Bad host in config.'; exit 2 }
if ($Cfg.updateHost -notmatch '^https://[A-Za-z0-9.-]+(:[0-9]{1,5})?$') { Write-Host 'Bad updateHost in config.'; exit 2 }
if (-not (Test-Path -LiteralPath $Cfg.testAppPath)) { Write-Host 'Test app not found at the configured path.'; exit 2 }

# one runner at a time
$createdNew = $false
$Mutex = New-Object System.Threading.Mutex($true, 'Wardsage.Lab.Runner', [ref]$createdNew)
if (-not $createdNew) { Write-Host 'The runner is already running.'; exit 0 }

# ---------------------------------------------------------------- server calls (the four RPCs only)
function Invoke-Rpc([string]$name, [string]$json) {
    if (@('lab_heartbeat', 'lab_claim', 'lab_finish', 'lab_put_file') -notcontains $name) { throw 'rpc not allowed' }
    $uri = $Cfg.host + '/rest/v1/rpc/' + $name
    $headers = @{ apikey = $ApiKey; Authorization = ('Bearer ' + $Token) }
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    try {
        $r = Invoke-RestMethod -Uri $uri -Method Post -Headers $headers -ContentType 'application/json' -Body $bytes -TimeoutSec 60
        return @{ ok = $true; data = $r; code = 200; msg = '' }
    } catch {
        $code = 0; $msg = $_.Exception.Message
        try {
            $resp = $_.Exception.Response
            if ($resp -ne $null) {
                $code = [int]$resp.StatusCode
                $sr = New-Object IO.StreamReader($resp.GetResponseStream())
                $body = $sr.ReadToEnd(); $sr.Close()
                $o = $body | ConvertFrom-Json
                $em = Get-Prop $o 'message'
                if ($em -ne $null) { $msg = [string]$em }
            }
        } catch { }
        return @{ ok = $false; data = $null; code = $code; msg = $msg }
    }
}

# ---------------------------------------------------------------- local facts
$AppExe  = $Cfg.testAppPath
$AppName = [IO.Path]::GetFileNameWithoutExtension($AppExe)

function Get-AppProcesses {
    $res = @()
    foreach ($p in @(Get-Process -Name $AppName -ErrorAction SilentlyContinue)) {
        try { if ($p.Path -eq $AppExe) { $res += $p } } catch { }
    }
    return $res
}
function Get-AppVersion {
    try {
        $v = ([string](Get-Item -LiteralPath $AppExe).VersionInfo.FileVersion).Trim()
        $parts = ConvertTo-VersionParts $v
        if ($parts -ne $null) { return ($parts -join '.') }
        return $v
    } catch { return '' }
}
function Get-Status {
    $game = @(Get-Process -Name $GameProcessName -ErrorAction SilentlyContinue).Count -gt 0
    $apps = @(Get-AppProcesses)
    return [ordered]@{
        runner = $RunnerVersion; game_running = $game; app_running = ($apps.Count -gt 0)
        app_version = (Get-AppVersion); busy = $script:Busy; up_min = [int]((Get-Date) - $script:Started).TotalMinutes
    }
}
function ConvertTo-StatusJson($st) {
    return '{"p_status":' + ($st | ConvertTo-Json -Compress) + '}'
}

# ---------------------------------------------------------------- argument schemas (strict whitelist)
# action -> allowed keys with their validators. A key not listed, a wrong type or a value out of range rejects the job.
$Schemas = @{
    status      = @{}
    update_app  = @{}
    start_game  = @{}
    restart_app = @{}
    game_front  = @{}
    screenshot  = @{ target = { param($v) $v -is [string] -and $v -ceq 'app' } }
    upload_logs = @{ lines  = { param($v) ($v -is [int] -or $v -is [long]) -and $v -ge 50 -and $v -le 2000 } }
    finish      = @{}
    cancel      = @{}
}
function Test-JobArgs([string]$action, $args_) {
    if (-not $Schemas.ContainsKey($action)) { return $false }
    $schema = $Schemas[$action]
    if ($args_ -eq $null) { return $true }
    if ($args_ -isnot [PSCustomObject]) { return $false }
    foreach ($prop in $args_.PSObject.Properties) {
        if (-not $schema.ContainsKey($prop.Name)) { return $false }
        if (-not (& $schema[$prop.Name] $prop.Value)) { return $false }
    }
    return $true
}

# ---------------------------------------------------------------- update_app (same checks as app/Updater.cs)
function ConvertFrom-Base64Url([string]$s) {
    $s = $s.Replace('-', '+').Replace('_', '/')
    while ($s.Length % 4 -ne 0) { $s += '=' }
    return [Convert]::FromBase64String($s)
}
function Test-VersionText([string]$v) { return ($v -match '^[0-9]{1,9}(\.[0-9]{1,9}){0,3}$') -and $v.Length -le 20 }
function ConvertTo-VersionParts([string]$v) {
    if (-not (Test-VersionText $v)) { return $null }
    $r = @(0, 0, 0, 0); $i = 0
    foreach ($p in $v.Split('.')) { $r[$i] = [int64]$p; $i++ }
    return $r
}
function Test-Newer([string]$cand, [string]$cur) {
    $a = ConvertTo-VersionParts $cand; $b = ConvertTo-VersionParts $cur
    if ($a -eq $null -or $b -eq $null) { return $false }
    for ($i = 0; $i -lt 4; $i++) { if ($a[$i] -ne $b[$i]) { return ($a[$i] -gt $b[$i]) } }
    return $false
}
function Get-Bytes([Uri]$uri, [int64]$max) {
    $req = [Net.HttpWebRequest]::Create($uri)
    $req.Method = 'GET'; $req.UserAgent = 'WardsageLab/1'; $req.Timeout = 30000; $req.ReadWriteTimeout = 60000
    $req.AllowAutoRedirect = $false
    $req.Headers['Cache-Control'] = 'no-cache'
    $resp = $req.GetResponse()
    try {
        $s = $resp.GetResponseStream(); $ms = New-Object IO.MemoryStream
        $buf = New-Object byte[] 65536
        while (($n = $s.Read($buf, 0, $buf.Length)) -gt 0) {
            $ms.Write($buf, 0, $n)
            if ($ms.Length -gt $max) { throw 'too large' }
        }
        return , $ms.ToArray()
    } finally { $resp.Close() }
}
# RSA PKCS#1 v1.5 / SHA-256 against any of the pinned moduli (src/UpdateKeys.cs VerifyAny)
function Test-Signature([byte[]]$data, [byte[]]$sig) {
    foreach ($n in @($Cfg.keys.KeyN, $Cfg.keys.TestKeyN)) {
        try {
            $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
            $p = New-Object Security.Cryptography.RSAParameters
            $p.Modulus = ConvertFrom-Base64Url $n
            $p.Exponent = ConvertFrom-Base64Url $Cfg.keys.KeyE
            $rsa.ImportParameters($p)
            if ($rsa.VerifyData($data, 'SHA256', $sig)) { return $true }
        } catch { }
    }
    return $false
}
function Invoke-UpdateApp {
    $base = [Uri]$Cfg.updateHost
    $feed = Get-Bytes ([Uri]($Cfg.updateHost.TrimEnd('/') + '/downloads/test/latest.json')) 65536
    $m = [Text.Encoding]::UTF8.GetString($feed) | ConvertFrom-Json
    $ver = [string](Get-Prop $m 'version')
    if (-not (Test-VersionText $ver)) { return @{ ok = $false; res = @{ error = 'bad_version' } } }
    $mUrl = [string](Get-Prop $m 'url')
    $mSig = [string](Get-Prop $m 'sig')
    $mSha = [string](Get-Prop $m 'sha256')
    $size = [int64](Get-Prop $m 'size')
    if ($size -le 0 -or $size -gt $MaxUpdateBytes -or [string]::IsNullOrEmpty($mUrl) -or [string]::IsNullOrEmpty($mSig)) {
        return @{ ok = $false; res = @{ error = 'bad_manifest' } }
    }
    $installed = Get-AppVersion
    if (-not (Test-Newer $ver $installed)) {
        return @{ ok = $true; res = @{ updated = $false; version = $ver; installed = $installed } }
    }
    # the build comes from the configured host only
    $file = $null
    if (-not [Uri]::TryCreate($base, $mUrl, [ref]$file) -or $file.Host -ne $base.Host -or $file.Scheme -ne $base.Scheme) {
        return @{ ok = $false; res = @{ error = 'foreign_url' } }
    }
    $bytes = Get-Bytes $file $MaxUpdateBytes
    $hex = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-', '')).ToLowerInvariant()
    if ($bytes.LongLength -ne $size -or $hex -ne $mSha.ToLowerInvariant()) { return @{ ok = $false; res = @{ error = 'hash_mismatch' } } }
    if (-not (Test-Signature $bytes ([Convert]::FromBase64String($mSig)))) { return @{ ok = $false; res = @{ error = 'bad_signature' } } }
    # drop where Updater.ApplyAtStart looks: %LOCALAPPDATA%\WardsageTest\update\Wardsage-Test-<ver>.exe + .json
    $upd = Join-Path $env:LOCALAPPDATA 'WardsageTest\update'
    New-Item -ItemType Directory -Force -Path $upd | Out-Null
    $exe = Join-Path $upd ('Wardsage-Test-' + $ver + '.exe')
    [IO.File]::WriteAllBytes($exe + '.tmp', $bytes)
    if (Test-Path $exe) { Remove-Item $exe -Force }
    Move-Item -Path ($exe + '.tmp') -Destination $exe
    $notes = '{"ru":"","en":""}'
    $meta = '{"version":"' + $ver + '","url":' + ($mUrl | ConvertTo-Json -Compress) + ',"size":' + $size + ',"sha256":"' + $hex + '","sig":"' + $mSig + '","notes":' + $notes + '}'
    [IO.File]::WriteAllText((Join-Path $upd ('Wardsage-Test-' + $ver + '.json')), $meta, (New-Object Text.UTF8Encoding($false)))
    return @{ ok = $true; res = @{ updated = $true; version = $ver; installed = $installed } }
}

# ---------------------------------------------------------------- start_game (gameExe from the config, directly: no launcher, no clicks)
# ---------------------------------------------------------------- game window: maximize + bring to front (window calls only, no input)
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class LabWin {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
}
"@
function Set-GameFront {
    $h = [IntPtr]::Zero
    foreach ($p in @(Get-Process -Name $GameProcessName -ErrorAction SilentlyContinue)) {
        try { $p.Refresh(); if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $h = $p.MainWindowHandle } } catch { }
    }
    if ($h -eq [IntPtr]::Zero) { Write-Log 'game_front: no game window'; return @{ ok = $false; res = @{ error = 'no_game_window' } } }
    $maximized = $false
    try {
        $zoomed = [LabWin]::IsZoomed($h); $iconic = [LabWin]::IsIconic($h)
        if ($iconic -or -not $zoomed) { [void][LabWin]::ShowWindow($h, 3); $maximized = $true }      # SW_MAXIMIZE
        $fg = [LabWin]::SetForegroundWindow($h)
        Write-Log ('game_front: maximized=' + $maximized + ' foreground=' + $fg)
        return @{ ok = $true; res = @{ maximized = $maximized; foreground = $fg } }
    } catch {
        Write-Log ('game_front failed: ' + (Get-ShortError $_))
        return @{ ok = $false; res = @{ error = 'game_front_failed' } }
    }
}
function Invoke-GameFront { return (Set-GameFront) }

function Send-Beat {
    try { [void](Invoke-Rpc 'lab_heartbeat' (ConvertTo-StatusJson (Get-Status))) } catch { }
}
function Get-GameWindow {
    foreach ($p in @(Get-Process -Name $GameProcessName -ErrorAction SilentlyContinue)) {
        try { $p.Refresh(); if ($p.MainWindowHandle -ne [IntPtr]::Zero) { return $true } } catch { }
    }
    return $false
}
function Invoke-StartGame {
    if (@(Get-Process -Name $GameProcessName -ErrorAction SilentlyContinue).Count -gt 0) {
        return @{ ok = $true; res = @{ started = $false; game_running = $true } }
    }
    $exe = [string](Get-Prop $Cfg 'gameExe')
    if ([string]::IsNullOrEmpty($exe) -or -not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        return @{ ok = $false; res = @{ error = 'no_game_exe' } }
    }
    try {
        $psi = New-Object Diagnostics.ProcessStartInfo($exe)
        $psi.UseShellExecute = $false
        $psi.WorkingDirectory = [IO.Path]::GetDirectoryName($exe)
        [void][Diagnostics.Process]::Start($psi)
    } catch {
        $m = Get-ShortError $_
        Write-Log ('start_game: start failed: ' + $m)
        return @{ ok = $false; res = @{ error = 'game_needs_launcher'; message = $m } }
    }
    $t0 = Get-Date
    $seenWindow = $false
    $lastBeat = $t0
    while ($true) {
        Start-Sleep -Seconds 2
        if (Test-Path $StopFile) { return @{ ok = $false; res = @{ error = 'stopped' } } }
        $el = ((Get-Date) - $t0).TotalSeconds
        $up = @(Get-Process -Name $GameProcessName -ErrorAction SilentlyContinue).Count -gt 0
        if ($seenWindow -and -not $up) { Write-Log 'start_game: the game exited'; return @{ ok = $false; res = @{ error = 'game_needs_launcher'; message = 'exited' } } }
        if ($up -and -not $seenWindow -and (Get-GameWindow)) { $seenWindow = $true }
        if ($seenWindow -and $el -ge $GameStableSec) { [void](Set-GameFront); return @{ ok = $true; res = @{ started = $true; game_running = $true } } }
        if (-not $up -and $el -ge 20 -and -not $seenWindow) { Write-Log 'start_game: no game process'; return @{ ok = $false; res = @{ error = 'game_needs_launcher'; message = 'no process' } } }
        if (-not $seenWindow -and $el -ge $GameWaitSec) { Write-Log 'start_game: no game window'; return @{ ok = $false; res = @{ error = 'game_needs_launcher'; message = 'no window' } } }
        if (((Get-Date) - $lastBeat).TotalSeconds -ge 50) { Send-Beat; $lastBeat = Get-Date }      # the server must still see the laptop online
    }
}

# ---------------------------------------------------------------- runner self-update (signed, like update_app)
$script:Relaunch = $false
function Invoke-RunnerSelfUpdate {
    $base = $Cfg.updateHost.TrimEnd('/') + '/downloads/test/lab/'
    $m = [Text.Encoding]::UTF8.GetString((Get-Bytes ([Uri]($base + 'WardsageLab.json')) 65536)) | ConvertFrom-Json
    $ver = [string](Get-Prop $m 'version')
    if (-not (Test-VersionText $ver)) { Write-Log 'self-update: bad version in the manifest'; return }
    if (-not (Test-Newer $ver $RunnerVersion)) { return }
    $size = [int64](Get-Prop $m 'size')
    $mSha = [string](Get-Prop $m 'sha256')
    $mSig = [string](Get-Prop $m 'sig')
    if ($size -le 0 -or $size -gt $MaxRunnerBytes -or [string]::IsNullOrEmpty($mSha) -or [string]::IsNullOrEmpty($mSig)) { Write-Log 'self-update: bad manifest'; return }
    $bytes = Get-Bytes ([Uri]($base + 'WardsageLab.ps1')) $MaxRunnerBytes
    $hex = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-', '')).ToLowerInvariant()
    if ($bytes.LongLength -ne $size -or $hex -ne $mSha.ToLowerInvariant()) { Write-Log 'self-update: hash mismatch, ignored'; return }
    if (-not (Test-Signature $bytes ([Convert]::FromBase64String($mSig)))) { Write-Log 'self-update: bad signature, ignored'; return }
    # the signed script must carry the version the (unsigned) manifest claims: no replay of an old script under a new number
    $text = [Text.Encoding]::UTF8.GetString($bytes)
    $pat = '(?m)^\$RunnerVersion\s*=\s*''' + [regex]::Escape($ver) + ''''
    if ($text -notmatch $pat) { Write-Log 'self-update: version inside the script differs, ignored'; return }
    $self = $PSCommandPath
    [IO.File]::WriteAllBytes($self + '.new', $bytes)
    Move-Item -LiteralPath ($self + '.new') -Destination $self -Force
    Write-Log ('self-update: runner ' + $RunnerVersion + ' -> ' + $ver + ', restarting')
    $script:Relaunch = $true
    $script:Exit = $true
}
function Test-RunnerUpdateDue {
    if (((Get-Date) - $script:LastSelfCheck).TotalMinutes -lt $SelfCheckMin) { return }
    $script:LastSelfCheck = Get-Date
    try { Invoke-RunnerSelfUpdate } catch { Write-Log ('self-update check failed: ' + (Get-ShortError $_)) }
}

# ---------------------------------------------------------------- restart_app (the configured test app only)
# innermost exception message, one line, at most 200 chars
function Get-ShortError($err) {
    $e = $err.Exception
    while ($e.InnerException -ne $null) { $e = $e.InnerException }
    $m = ([string]$e.Message) -replace '\s+', ' '
    if ($m.Length -gt 200) { $m = $m.Substring(0, 200) }
    return $m
}

function Invoke-RestartApp {
    foreach ($p in @(Get-AppProcesses)) {
        try { [void]$p.CloseMainWindow() } catch { Write-Log ('restart_app: close failed: ' + (Get-ShortError $_)) }
    }
    $t = 0
    while (@(Get-AppProcesses).Count -gt 0 -and $t -lt 10) { Start-Sleep -Seconds 1; $t++ }
    foreach ($p in @(Get-AppProcesses)) {
        try { $p.Kill() } catch { Write-Log ('restart_app: kill failed: ' + (Get-ShortError $_)) }
    }
    Start-Sleep -Seconds 2
    # when a staged update is waiting, the app swaps its own exe at start (Updater.ApplyAtStart) and starts again
    try {
        $psi = New-Object Diagnostics.ProcessStartInfo($AppExe)
        $psi.UseShellExecute = $false
        $psi.WorkingDirectory = [IO.Path]::GetDirectoryName($AppExe)
        [void][Diagnostics.Process]::Start($psi)
    } catch {
        $m = Get-ShortError $_
        Write-Log ('restart_app: start failed: ' + $m)
        return @{ ok = $false; res = @{ error = 'start_failed'; message = $m } }
    }
    Start-Sleep -Seconds 5
    [void](Set-GameFront)      # the test app took the focus: the game goes back to the front
    return @{ ok = $true; res = @{ started = $true } }
}

# ---------------------------------------------------------------- screenshot: the app window only (PrintWindow)
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @"
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
public static class LabShot {
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
    // JPEG of the window (longest side <= maxSide, <= maxBytes), or null when it cannot be printed
    public static byte[] Jpeg(IntPtr h, int maxSide, int maxBytes) {
        try { SetProcessDPIAware(); } catch (Exception) { }
        if (h == IntPtr.Zero || IsIconic(h)) return null;
        RECT r; if (!GetWindowRect(h, out r)) return null;
        int w = r.R - r.L, ht = r.B - r.T;
        if (w < 50 || ht < 50) return null;
        using (Bitmap full = new Bitmap(w, ht, PixelFormat.Format24bppRgb)) {
            using (Graphics g = Graphics.FromImage(full)) {
                IntPtr dc = g.GetHdc();
                bool ok = PrintWindow(h, dc, 2);   // 2 = PW_RENDERFULLCONTENT (WebView2 content)
                g.ReleaseHdc(dc);
                if (!ok) return null;
            }
            double k = Math.Min(1.0, (double)maxSide / Math.Max(w, ht));
            int nw = Math.Max(1, (int)(w * k)), nh = Math.Max(1, (int)(ht * k));
            using (Bitmap small = new Bitmap(nw, nh, PixelFormat.Format24bppRgb)) {
                using (Graphics g2 = Graphics.FromImage(small)) {
                    g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g2.DrawImage(full, 0, 0, nw, nh);
                }
                ImageCodecInfo codec = null;
                foreach (ImageCodecInfo c in ImageCodecInfo.GetImageEncoders()) if (c.MimeType == "image/jpeg") codec = c;
                if (codec == null) return null;
                foreach (long q in new long[] { 75, 55, 35 }) {
                    using (EncoderParameters ep = new EncoderParameters(1))
                    using (MemoryStream ms = new MemoryStream()) {
                        ep.Param[0] = new EncoderParameter(Encoder.Quality, q);
                        small.Save(ms, codec, ep);
                        if (ms.Length <= maxBytes) return ms.ToArray();
                    }
                }
                return null;
            }
        }
    }
}
"@

function Send-File([string]$job, [string]$name, [string]$mime, [byte[]]$bytes) {
    $json = '{"p_job":"' + $job + '","p_name":"' + $name + '","p_mime":"' + $mime + '","p_b64":"' + [Convert]::ToBase64String($bytes) + '"}'
    return Invoke-Rpc 'lab_put_file' $json
}
function Invoke-Screenshot([string]$job) {
    $procs = @(Get-AppProcesses)
    if ($procs.Count -eq 0) { return @{ ok = $false; res = @{ error = 'app_not_running' } } }
    $h = [IntPtr]::Zero
    foreach ($p in $procs) { $p.Refresh(); if ($p.MainWindowHandle -ne [IntPtr]::Zero) { $h = $p.MainWindowHandle } }
    $jpg = [LabShot]::Jpeg($h, 1280, 1400000)
    if ($jpg -eq $null) { return @{ ok = $false; res = @{ error = 'no_printable_window' } } }
    $r = Send-File $job 'app-window.jpg' 'image/jpeg' $jpg
    if (-not $r.ok) { return @{ ok = $false; res = @{ error = 'upload_' + $r.code } } }
    return @{ ok = $true; res = @{ bytes = $jpg.Length } }
}

# ---------------------------------------------------------------- upload_logs: tail of the test app's own log
function Invoke-UploadLogs([string]$job, $a) {
    $n = 1000
    $al = Get-Prop $a 'lines'
    if ($al -ne $null) { $n = [int]$al }
    $log = Join-Path $env:APPDATA 'WardsageTest\wardsage.log'      # src/Config.cs DefaultDir + Log.Path
    if (-not (Test-Path -LiteralPath $log)) { return @{ ok = $false; res = @{ error = 'no_log' } } }
    $fs = New-Object IO.FileStream($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try { $sr = New-Object IO.StreamReader($fs, [Text.Encoding]::UTF8); $text = $sr.ReadToEnd() } finally { $fs.Close() }
    $lines = $text -split "`r?`n"
    if ($lines.Count -gt $n) { $lines = $lines[($lines.Count - $n)..($lines.Count - 1)] }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))
    if ($bytes.Length -gt 1100000) { $bytes = $bytes[($bytes.Length - 1100000)..($bytes.Length - 1)] }
    if ($bytes.Length -eq 0) { return @{ ok = $false; res = @{ error = 'empty_log' } } }
    $r = Send-File $job 'wardsage-log.txt' 'text/plain' $bytes
    if (-not $r.ok) { return @{ ok = $false; res = @{ error = 'upload_' + $r.code } } }
    return @{ ok = $true; res = @{ bytes = $bytes.Length; lines = $lines.Count } }
}

# ---------------------------------------------------------------- one job
$script:Exit = $false
function Invoke-Job($job) {
    $id = ''
    $action = ''
    $out = @{ ok = $false; res = @{ error = 'unknown' } }
    try {
        $id = [string](Get-Prop $job 'id')
        $action = [string](Get-Prop $job 'action')
        if ($id -notmatch '^[0-9a-fA-F-]{36}$') { Write-Log 'job with a bad id ignored'; return }
        $script:Busy = $true
        $jobArgs = Get-Prop $job 'args'
        $expires = [DateTime]::Parse([string](Get-Prop $job 'expires_at')).ToUniversalTime()
        if ($expires -lt (Get-Date).ToUniversalTime()) {
            $out = @{ ok = $false; res = @{ error = 'expired' } }
        } elseif (-not (Test-JobArgs $action $jobArgs)) {
            $out = @{ ok = $false; res = @{ error = 'bad_args' } }
        } else {
            Write-Log ('job: ' + $action)
            switch ($action) {
                'status'      { $out = @{ ok = $true; res = (Get-Status) } }
                'update_app'  { $out = Invoke-UpdateApp }
                'start_game'  { $out = Invoke-StartGame }
                'restart_app' { $out = Invoke-RestartApp }
                'game_front'  { $out = Invoke-GameFront }
                'screenshot'  { $out = Invoke-Screenshot $id }
                'upload_logs' { $out = Invoke-UploadLogs $id $jobArgs }
                'finish'      { $out = @{ ok = $true; res = @{ bye = $true } }; $script:Exit = $true }
                'cancel'      { $out = @{ ok = $true; res = @{ cancelled = $true } } }
            }
        }
    } catch {
        Write-Log ('job ' + $action + ' failed: ' + $_.Exception.GetType().Name)
        $out = @{ ok = $false; res = @{ error = 'exception' } }
    }
    $script:Busy = $false
    $json = '{"p_job":"' + $id + '","p_ok":' + ([string]$out.ok).ToLower() + ',"p_result":' + ($out.res | ConvertTo-Json -Compress -Depth 4) + '}'
    $r = Invoke-Rpc 'lab_finish' $json
    if (-not $r.ok) { Write-Log ('finish report failed: ' + $r.code + ' ' + $r.msg) }
    Write-Log ('job ' + $action + ' -> ' + $(if ($out.ok) { 'ok' } else { 'failed' }))
}

# ---------------------------------------------------------------- main loop
$script:Busy = $false
$script:Started = Get-Date
$script:LastSelfCheck = [DateTime]::MinValue
$mode = 'one run'; if ($Service) { $mode = 'service' }
Write-Log ('runner ' + $RunnerVersion + ' started (device ' + $Cfg.device + ', ' + $mode + '). To stop it at once create ' + $StopFile)
$lastBeat = [DateTime]::MinValue
$backoffUntil = [DateTime]::MinValue
try {
    while (-not $script:Exit) {
        if (Test-Path $StopFile) { Write-Log 'STOP file found, exiting'; break }
        if (((Get-Date) - $script:Started).TotalHours -gt $MaxRunnerHours) {
            if (-not $Service) { Write-Log 'max lifetime reached, exiting'; break }
            Write-Log 'max lifetime reached, service mode: loop restarted'
            $script:Started = Get-Date
        }
        Test-RunnerUpdateDue
        if ($script:Exit) { break }
        if ((Get-Date) -ge $backoffUntil) {
            try {
                $err = $null
                if (((Get-Date) - $lastBeat).TotalSeconds -ge $HeartbeatSec) {
                    $r = Invoke-Rpc 'lab_heartbeat' (ConvertTo-StatusJson (Get-Status))
                    $lastBeat = Get-Date
                    if (-not $r.ok) { $err = $r }
                }
                if ($err -eq $null) {
                    $c = Invoke-Rpc 'lab_claim' '{}'
                    if (-not $c.ok) { $err = $c }
                    else {
                        $job = Get-ClaimJob $c.data
                        if ($job -ne $null) {
                            Invoke-Job $job
                            if ($Service -and -not $script:Relaunch) { $script:Exit = $false }      # service mode: "finish" ends the run, not the runner
                            $lastBeat = [DateTime]::MinValue      # heartbeat right after a job
                        }
                    }
                }
                if ($err -ne $null) {
                    Write-Log ('server said ' + $err.code + ' ' + $err.msg)
                    if ($err.msg -in @('lab_bad_gen', 'lab_disabled', 'lab_unknown_device', 'lab_forbidden')) { Write-Log 'this token is no longer valid, exiting'; break }
                    $wait = 300; if ($err.msg -ne 'lab_killed' -and $err.msg -ne 'lab_rate') { $wait = 30 }
                    $backoffUntil = (Get-Date).AddSeconds($wait)
                }
            } catch {
                $script:Busy = $false
                $em = [string]$_.Exception.Message
                if ($em.Length -gt 200) { $em = $em.Substring(0, 200) }
                Write-Log ('loop error: ' + $_.Exception.GetType().Name + ': ' + $em)
                $backoffUntil = (Get-Date).AddSeconds(30)
            }
        }
        $t = 0
        while ($t -lt $ClaimSec -and -not $script:Exit -and -not (Test-Path $StopFile)) { Start-Sleep -Seconds 1; $t++ }
    }
} finally {
    $Mutex.ReleaseMutex()
    Write-Log 'runner stopped'
}
if ($script:Relaunch) {
    # the mutex is free now: start the new copy of this file (the runner is already elevated, so no prompt)
    try {
        $args2 = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
        if ($Service) { $args2 += ' -Service' }
        Start-Process -FilePath 'powershell.exe' -ArgumentList $args2
    } catch { Write-Log ('relaunch failed: ' + (Get-ShortError $_)) }
}
