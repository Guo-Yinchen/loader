# Removes the Chispa scheduled task and its generated launcher.
#
#   .\tools\uninstall-service.ps1            # 停止并注销任务（不动数据目录）
#
# Data (.local/server) and logs are deliberately left alone: they are the account database and the
# audit trail, not part of the unit definition.

param(
    [string]$TaskName = 'Chispa.Server',
    [string]$ServiceDirectory = '',
    [switch]$KeepLauncher
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# The flag prevents restarts. Stopping the task can interrupt in-flight requests;
# run this in a maintenance window, after uploads finish.
$serviceDirectory = if ($ServiceDirectory) { $ServiceDirectory } else { Join-Path $root '.local/service' }
$stopFlag = Join-Path $serviceDirectory 'stop.flag'
$serviceDirectory = Split-Path -Parent $stopFlag
if (Test-Path -LiteralPath $serviceDirectory) {
    New-Item -ItemType File -Force $stopFlag | Out-Null
    Start-Sleep -Seconds 3
}
$existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $existing) {
    Write-Output "任务 '$TaskName' 不存在，无需卸载。"
} else {
    if ($existing.State -eq 'Running') {
        Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
    }
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    Write-Output "已注销任务 '$TaskName'。"
}
if (Test-Path -LiteralPath $stopFlag) { Remove-Item -LiteralPath $stopFlag -Force }
$launcher = Join-Path $serviceDirectory 'start-chispa-server.ps1'
if (-not $KeepLauncher -and (Test-Path -LiteralPath $launcher)) {
    Remove-Item -LiteralPath $launcher -Force
    Write-Output "已删除生成的启动脚本：$launcher"
}
Write-Output '数据目录与日志未改动。'
