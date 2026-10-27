# Keeps one Chispa server process running.
#
# Task Scheduler's own "restart if the task fails" did not fire for a terminated child process on
# this machine (the task goes to Ready with lastResult 0xFFFFFFFF and stays there - measured, see
# docs/task-04-deployment.md). So the restart is done here, where it can be observed and tested:
# start the launcher, wait for it to exit, log why, wait a few seconds, start it again. The
# scheduled task then only has to keep THIS process alive, which is one process instead of many
# and does nothing but loop.
#
# Stopping is explicit: create the stop flag (tools/uninstall-service.ps1 does) and the loop ends
# after the current child exits. No process is killed, so the server gets its normal shutdown.

param(
    [Parameter(Mandatory = $true)]
    [string]$Launcher,
    [string]$StopFlag = '',
    [int]$RestartDelaySeconds = 5,
    [string]$LogFile = ''
)

$ErrorActionPreference = 'Stop'

function Write-Line([string]$text) {
    $line = '{0} {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $text
    Write-Host $line
    if ($LogFile) {
        try {
            $directory = Split-Path -Parent $LogFile
            if ($directory) { New-Item -ItemType Directory -Force $directory | Out-Null }
            Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8
        } catch { }
    }
}

if (-not (Test-Path -LiteralPath $Launcher)) { throw "launcher not found: $Launcher" }
Write-Line ("watchdog started for {0} (stop flag: {1})" -f $Launcher, $(if ($StopFlag) { $StopFlag } else { 'none' }))

$restarts = 0
while ($true) {
    if ($StopFlag -and (Test-Path -LiteralPath $StopFlag)) { Write-Line 'stop flag present, not starting the server'; break }
    $started = Get-Date
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $Launcher
    $code = $LASTEXITCODE
    $ran = [int]((Get-Date) - $started).TotalSeconds
    Write-Line ("server exited with code {0} after {1}s" -f $code, $ran)
    if ($StopFlag -and (Test-Path -LiteralPath $StopFlag)) { Write-Line 'stop flag present, exiting'; break }
    Start-Sleep -Seconds ([Math]::Max(1, $RestartDelaySeconds))
    $restarts++
    Write-Line ("restarting (attempt {0})" -f $restarts)
}
Write-Line 'watchdog stopped'
exit 0
