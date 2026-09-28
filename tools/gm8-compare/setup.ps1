# Prepares a work directory for run.ps1: publishes this server and the two load
# tools in Release, and copies the stock GM8 build with a test gg2.ini.
#
# The GM8 build is COPIED, never edited in place: its gg2.ini is the owner's
# config, and a test that flips UseLobby on it and forgets to flip it back
# announces a benchmark server to the public lobby.
param(
    [Parameter(Mandatory)] [string]$Gm8Build,   # directory holding "Gang Garrison 2.exe", Maps\, gg2.ini
    [Parameter(Mandatory)] [string]$WorkDir
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
New-Item -ItemType Directory -Force $WorkDir | Out-Null
$WorkDir = Resolve-Path $WorkDir

foreach ($p in @(
        @('src\Gg2.Host\Gg2.Host.csproj', 'host'),
        @('tools\loadprobe\loadprobe.csproj', 'probe'),
        @('tools\soak\soak-players.csproj', 'soak'))) {
    dotnet publish -c Release (Join-Path $repo $p[0]) -o (Join-Path $WorkDir "bin\$($p[1])") | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish failed: $($p[0])" }
}

$gm8 = Join-Path $WorkDir 'gm8'
New-Item -ItemType Directory -Force $gm8 | Out-Null
Copy-Item (Join-Path $Gm8Build 'Gang Garrison 2.exe') $gm8
foreach ($d in 'Maps', 'Plugins') {
    $src = Join-Path $Gm8Build $d
    if (Test-Path $src) { Copy-Item $src $gm8 -Recurse -Force }
}

# UseLobby=0 keeps the benchmark off the public lobby; PlayerLimit=24 seats the
# 16-spectator scenario. The loopback address is exempt from MultiClientLimit
# in the reference (acceptJoiningPlayer.gml), so that needs no change.
$ini = Get-Content (Join-Path $Gm8Build 'gg2.ini')
$ini = @($ini | Where-Object { $_ -notmatch '^(UseLobby|PlayerLimit)=' })
$at = [array]::IndexOf($ini, '[Settings]')
if ($at -lt 0) { $ini = @('[Settings]') + $ini; $at = 0 }
$ini = $ini[0..$at] + @('UseLobby=0', 'PlayerLimit=24') + $(if ($at + 1 -lt $ini.Count) { $ini[($at + 1)..($ini.Count - 1)] } else { @() })
Set-Content -Path (Join-Path $gm8 'gg2.ini') -Value $ini -Encoding ascii
Write-Output "ready: $WorkDir"
