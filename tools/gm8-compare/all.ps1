# The full comparison: every scenario on both servers, alternating server by
# server so a slow drift in the machine's state lands on both arms equally.
param(
    [Parameter(Mandatory)] [string]$WorkDir,
    [int]$Seconds = 90,
    [int]$Reps = 2
)
$ErrorActionPreference = 'Stop'
$out = Join-Path $WorkDir 'results.csv'
if (Test-Path $out) { Remove-Item $out }
foreach ($rep in 1..$Reps) {
    foreach ($scenario in 'idle', 'spec1', 'spec16', 'play8') {
        foreach ($server in 'gm8', 'port') {
            Write-Output "== $server $scenario $rep"
            & (Join-Path $PSScriptRoot 'run.ps1') -Server $server -Scenario $scenario -WorkDir $WorkDir -Seconds $Seconds -Rep $rep -Out $out | Out-Null
        }
    }
}
Import-Csv $out | Format-Table -AutoSize
