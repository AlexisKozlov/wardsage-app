# WardsageLab.ps1 - the test-lab runner for the Wardsage TEST app on one Windows laptop (PowerShell 5.1).
#
# What it is: a small whitelist executor. It is started by hand (desktop shortcut made by setup-lab.ps1) after the
# owner switched the laptop on and started the game. Every 60 s it sends a heartbeat to the staging server; every
# few seconds it asks for ONE job, runs it and reports the result. When the server queues the "finish" job, the runner
# exits by itself. Nothing is installed to run it automatically: no scheduled task, no autostart, no service.
#
# What it never does: click or type anywhere, start or stop the game or its launcher, open a shell for the server,
# run anything the server sends, or let a job value reach a command line or a file path. The jobs are only:
#   status | update_app | restart_app | screenshot | upload_logs | finish | cancel
# each with a strict argument schema (see $Schemas). The only process it starts/stops is the configured test app.
#
# Local kill switch: create the file  %LOCALAPPDATA%\WardsageLab\STOP  (the runner exits at once, even mid-wait).

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$RunnerVersion   = '1'
$Dir             = Join-Path $env:LOCALAPPDATA 'WardsageLab'
$StopFile        = Join-Path $Dir 'STOP'
$LogFile         = Join-Path $Dir 'runner.log'
$HeartbeatSec    = 60
$ClaimSec        = 15
$MaxRunnerHours  = 4          # the runner never lives longer than this
$MaxUpdateBytes  = 64MB       # same limit as app/Updater.cs MaxSize
$GameProcessName = 'Watcher of Realms'   # src/GameInfo.cs ProcessName (read only: the runner never starts/stops it)

function Write-Log([string]$msg) {
    try {
        if ((Test-Path $LogFile) -and ((Get-Item $LogFile).Length -gt 512KB)) { Remove-Item $LogFile -Force }
        Add-Content -Path $LogFile -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $msg)
    } catch { }
    Write-Host $msg
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
                if ($o.PSObject.Properties['message']) { $msg = [string]$o.message }
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
    try { return ([string](Get-Item -LiteralPath $AppExe).VersionInfo.FileVersion).Trim() } catch { return '' }
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
    restart_app = @{}
    screenshot  = @{ target = { param($v) $v -is [string] -and $v -ceq 'app' } }
    upload_logs = @{ lines  = { param($v) ($v -is [int] -or $v -is [long]) -and $v -ge 50 -and $v -le 2000 } }
    finish      = @{}
    cancel      = @{}
}
function Test-JobArgs([string]$action, $args_) {
    if (-not $Schemas.ContainsKey($action)) { return $false }
    $schema = $Schemas[$action]
    if ($args_ -eq $null) { return $true }
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
    $ver = [string]$m.version
    if (-not (Test-VersionText $ver)) { return @{ ok = $false; res = @{ error = 'bad_version' } } }
    $size = [int64]$m.size
    if ($size -le 0 -or $size -gt $MaxUpdateBytes -or [string]::IsNullOrEmpty([string]$m.url) -or [string]::IsNullOrEmpty([string]$m.sig)) {
        return @{ ok = $false; res = @{ error = 'bad_manifest' } }
    }
    $installed = Get-AppVersion
    if (-not (Test-Newer $ver $installed)) {
        return @{ ok = $true; res = @{ updated = $false; version = $ver; installed = $installed } }
    }
    # the build comes from the configured host only
    $file = $null
    if (-not [Uri]::TryCreate($base, [string]$m.url, [ref]$file) -or $file.Host -ne $base.Host -or $file.Scheme -ne $base.Scheme) {
        return @{ ok = $false; res = @{ error = 'foreign_url' } }
    }
    $bytes = Get-Bytes $file $MaxUpdateBytes
    $hex = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-', '')).ToLowerInvariant()
    if ($bytes.LongLength -ne $size -or $hex -ne ([string]$m.sha256).ToLowerInvariant()) { return @{ ok = $false; res = @{ error = 'hash_mismatch' } } }
    if (-not (Test-Signature $bytes ([Convert]::FromBase64String([string]$m.sig)))) { return @{ ok = $false; res = @{ error = 'bad_signature' } } }
    # drop where Updater.ApplyAtStart looks: %LOCALAPPDATA%\WardsageTest\update\Wardsage-Test-<ver>.exe + .json
    $upd = Join-Path $env:LOCALAPPDATA 'WardsageTest\update'
    New-Item -ItemType Directory -Force -Path $upd | Out-Null
    $exe = Join-Path $upd ('Wardsage-Test-' + $ver + '.exe')
    [IO.File]::WriteAllBytes($exe + '.tmp', $bytes)
    if (Test-Path $exe) { Remove-Item $exe -Force }
    Move-Item -Path ($exe + '.tmp') -Destination $exe
    $notes = '{"ru":"","en":""}'
    $meta = '{"version":"' + $ver + '","url":' + ([string]$m.url | ConvertTo-Json -Compress) + ',"size":' + $size + ',"sha256":"' + $hex + '","sig":"' + ([string]$m.sig) + '","notes":' + $notes + '}'
    [IO.File]::WriteAllText((Join-Path $upd ('Wardsage-Test-' + $ver + '.json')), $meta, (New-Object Text.UTF8Encoding($false)))
    return @{ ok = $true; res = @{ updated = $true; version = $ver; installed = $installed } }
}

# ---------------------------------------------------------------- restart_app (the configured test app only)
function Invoke-RestartApp {
    foreach ($p in @(Get-AppProcesses)) { try { [void]$p.CloseMainWindow() } catch { } }
    $t = 0
    while (@(Get-AppProcesses).Count -gt 0 -and $t -lt 10) { Start-Sleep -Seconds 1; $t++ }
    foreach ($p in @(Get-AppProcesses)) { try { $p.Kill() } catch { } }
    Start-Sleep -Seconds 2
    # when a staged update is waiting, the app swaps its own exe at start (Updater.ApplyAtStart) and starts again
    $psi = New-Object Diagnostics.ProcessStartInfo($AppExe)
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = [IO.Path]::GetDirectoryName($AppExe)
    [void][Diagnostics.Process]::Start($psi)
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
    if ($a -ne $null -and ($a.PSObject.Properties.Name -contains 'lines')) { $n = [int]$a.lines }
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
    $id = [string]$job.id
    $action = [string]$job.action
    if ($id -notmatch '^[0-9a-fA-F-]{36}$') { Write-Log 'job with a bad id ignored'; return }
    $script:Busy = $true
    $out = @{ ok = $false; res = @{ error = 'unknown' } }
    try {
        $expires = [DateTime]::Parse([string]$job.expires_at).ToUniversalTime()
        if ($expires -lt (Get-Date).ToUniversalTime()) {
            $out = @{ ok = $false; res = @{ error = 'expired' } }
        } elseif (-not (Test-JobArgs $action $job.args)) {
            $out = @{ ok = $false; res = @{ error = 'bad_args' } }
        } else {
            Write-Log ('job: ' + $action)
            switch ($action) {
                'status'      { $out = @{ ok = $true; res = (Get-Status) } }
                'update_app'  { $out = Invoke-UpdateApp }
                'restart_app' { $out = Invoke-RestartApp }
                'screenshot'  { $out = Invoke-Screenshot $id }
                'upload_logs' { $out = Invoke-UploadLogs $id $job.args }
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
Write-Log ('runner started (device ' + $Cfg.device + '). To stop it at once create ' + $StopFile)
$lastBeat = [DateTime]::MinValue
$backoffUntil = [DateTime]::MinValue
try {
    while (-not $script:Exit) {
        if (Test-Path $StopFile) { Write-Log 'STOP file found, exiting'; break }
        if (((Get-Date) - $script:Started).TotalHours -gt $MaxRunnerHours) { Write-Log 'max lifetime reached, exiting'; break }
        if ((Get-Date) -ge $backoffUntil) {
            $err = $null
            if (((Get-Date) - $lastBeat).TotalSeconds -ge $HeartbeatSec) {
                $r = Invoke-Rpc 'lab_heartbeat' (ConvertTo-StatusJson (Get-Status))
                $lastBeat = Get-Date
                if (-not $r.ok) { $err = $r }
            }
            if ($err -eq $null) {
                $c = Invoke-Rpc 'lab_claim' '{}'
                if (-not $c.ok) { $err = $c }
                elseif ($c.data -ne $null) {
                    Invoke-Job $c.data
                    $lastBeat = [DateTime]::MinValue      # heartbeat right after a job
                }
            }
            if ($err -ne $null) {
                Write-Log ('server said ' + $err.code + ' ' + $err.msg)
                if ($err.msg -in @('lab_bad_gen', 'lab_disabled', 'lab_unknown_device', 'lab_forbidden')) { Write-Log 'this token is no longer valid, exiting'; break }
                $wait = 300; if ($err.msg -ne 'lab_killed' -and $err.msg -ne 'lab_rate') { $wait = 30 }
                $backoffUntil = (Get-Date).AddSeconds($wait)
            }
        }
        $t = 0
        while ($t -lt $ClaimSec -and -not $script:Exit -and -not (Test-Path $StopFile)) { Start-Sleep -Seconds 1; $t++ }
    }
} finally {
    $Mutex.ReleaseMutex()
    Write-Log 'runner stopped'
}
