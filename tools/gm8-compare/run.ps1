# One measurement: start a server, wait for its port, apply a load scenario,
# and sample the server process's CPU time and memory once a second. Appends
# one row to -Out. Needs a work directory prepared by setup.ps1.
param(
    [Parameter(Mandatory)] [ValidateSet('gm8', 'port')] [string]$Server,
    [Parameter(Mandatory)] [ValidateSet('idle', 'spec1', 'spec16', 'play8')] [string]$Scenario,
    [Parameter(Mandatory)] [string]$WorkDir,
    [int]$Seconds = 90,
    [int]$Rep = 1,
    [string]$Out = (Join-Path $WorkDir 'results.csv')
)
$ErrorActionPreference = 'Stop'
$WorkDir = Resolve-Path $WorkDir
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$gm8Dir = Join-Path $WorkDir 'gm8'
$bin = Join-Path $WorkDir 'bin'
$logs = Join-Path $WorkDir 'logs'
New-Item -ItemType Directory -Force $logs | Out-Null
$port = 8190

function Wait-Port([int]$p, [double]$timeoutS) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $timeoutS) {
        $c = New-Object Net.Sockets.TcpClient
        try { $c.Connect('127.0.0.1', $p); return $sw.Elapsed.TotalMilliseconds } catch { } finally { $c.Dispose() }
        Start-Sleep -Milliseconds 20
    }
    throw "port $p never opened"
}

if ([Net.NetworkInformation.IPGlobalProperties]::GetIPGlobalProperties().GetActiveTcpListeners().Port -contains $port) {
    throw "something is already listening on $port"
}

if ($Server -eq 'gm8') {
    $proc = Start-Process -FilePath (Join-Path $gm8Dir 'Gang Garrison 2.exe') -ArgumentList '-dedicated', '-map', 'ctf_truefort' -WorkingDirectory $gm8Dir -PassThru
} else {
    # --multi-client-limit and --connect-rate exist for the fleet: every client
    # comes from 127.0.0.1 (tools/soak/README.md). --bots 0 because the GM8 arm
    # has none.
    $log = Join-Path $logs "port-$Scenario-$Rep.log"
    $proc = Start-Process -FilePath (Join-Path $bin 'host\gg2-server.exe') -ArgumentList '--port', $port, '--players', '24', '--multi-client-limit', '64', '--connect-rate', '0', '--bots', '0', '--map', 'ctf_truefort' -WorkingDirectory $repo -RedirectStandardOutput $log -RedirectStandardError "$log.err" -PassThru -WindowStyle Hidden
}
$startupMs = Wait-Port $port 60
Start-Sleep -Seconds 5   # map load and the first round

$probe = Join-Path $bin 'probe\loadprobe.exe'
$probeOut = Join-Path $logs "probe-$Server-$Scenario-$Rep.txt"
$loadJobs = @()
switch ($Scenario) {
    'spec1' {
        $loadJobs += Start-Process -FilePath $probe -ArgumentList '--host', '127.0.0.1', '--port', $port, '--clients', '1', '--seconds', $Seconds, '--warmup', '8' -RedirectStandardOutput $probeOut -PassThru -WindowStyle Hidden
    }
    'spec16' {
        $loadJobs += Start-Process -FilePath $probe -ArgumentList '--host', '127.0.0.1', '--port', $port, '--clients', '16', '--seconds', $Seconds, '--warmup', '8', '--stagger-ms', '100' -RedirectStandardOutput $probeOut -PassThru -WindowStyle Hidden
    }
    'play8' {
        $loadJobs += Start-Process -FilePath (Join-Path $bin 'soak\soak-players.exe') -ArgumentList '--port', $port, '--players', '8', '--seconds', ($Seconds + 14), '--seed', '7' -RedirectStandardOutput (Join-Path $logs "soak-$Server-$Rep.txt") -PassThru -WindowStyle Hidden
        Start-Sleep -Seconds 2
        $loadJobs += Start-Process -FilePath $probe -ArgumentList '--host', '127.0.0.1', '--port', $port, '--clients', '1', '--input', '1', '--seconds', $Seconds, '--warmup', '8' -RedirectStandardOutput $probeOut -PassThru -WindowStyle Hidden
    }
}
Start-Sleep -Seconds 10  # joins land inside the probe's own 8 s warmup

# The window: process CPU time over wall time, so it covers every thread and
# needs nothing from inside either server.
$proc.Refresh()
$cpu0 = $proc.TotalProcessorTime.TotalMilliseconds
$t0 = [Diagnostics.Stopwatch]::StartNew()
$prevCpu = $cpu0; $prevT = 0.0
$perSec = New-Object System.Collections.Generic.List[double]
$ws = New-Object System.Collections.Generic.List[double]
$priv = New-Object System.Collections.Generic.List[double]
$threads = 0
while ($t0.Elapsed.TotalSeconds -lt ($Seconds - 12)) {
    Start-Sleep -Milliseconds 1000
    $proc.Refresh()
    $now = $t0.Elapsed.TotalMilliseconds; $c = $proc.TotalProcessorTime.TotalMilliseconds
    $perSec.Add(100.0 * ($c - $prevCpu) / ($now - $prevT))
    $prevCpu = $c; $prevT = $now
    $ws.Add($proc.WorkingSet64 / 1MB); $priv.Add($proc.PrivateMemorySize64 / 1MB)
    $threads = $proc.Threads.Count
}
$wall = $t0.Elapsed.TotalMilliseconds
$cpuPct = 100.0 * ($proc.TotalProcessorTime.TotalMilliseconds - $cpu0) / $wall

foreach ($j in $loadJobs) { $j.WaitForExit(120000) | Out-Null }
Stop-Process -Id $proc.Id -Force
Start-Sleep -Seconds 2

$sorted = $perSec | Sort-Object
$row = [pscustomobject]@{
    server         = $Server; scenario = $Scenario; rep = $Rep
    startup_ms     = [math]::Round($startupMs)
    window_s       = [math]::Round($wall / 1000, 1)
    cpu_pct_core   = [math]::Round($cpuPct, 3)
    cpu_max_1s     = [math]::Round($sorted[-1], 2)
    ws_mb_mean     = [math]::Round(($ws | Measure-Object -Average).Average, 1)
    ws_mb_max      = [math]::Round(($ws | Measure-Object -Maximum).Maximum, 1)
    private_mb_max = [math]::Round(($priv | Measure-Object -Maximum).Maximum, 1)
    threads        = $threads
}
$row | Export-Csv -Path $Out -Append -NoTypeInformation
$row | Format-List
