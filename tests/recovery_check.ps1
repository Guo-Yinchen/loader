# Isolated verification of the recovery configuration: it drives the real installer, kills the real
# server process, and records what actually happens. Nothing here touches a production instance -
# it uses its own task name, its own data directory, its own port and its own service directory,
# and it unregisters itself at the end.
#
#   .\tests\recovery_check.ps1                 # 约 1-2 分钟
#   .\tests\recovery_check.ps1 -KeepTask       # 保留任务与数据目录以便手工观察
#
# What it proves:
#   1. tools/install-service.ps1 registers a definition that starts the service unattended;
#   2. when the server process dies, the registered watchdog starts it again (crash recovery);
#   3. tools/uninstall-service.ps1 stops the service through its stop flag and removes the task;
#   4. installing and starting again brings the service back healthy (not a real reboot test).

param(
    [string]$ServerDll = '',
    [int]$StartTimeoutSeconds = 90,
    [int]$RestartTimeoutSeconds = 60,
    [switch]$KeepTask
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$taskName = 'Chispa.RecoveryCheck.' + [guid]::NewGuid().ToString('N')
$installer = Join-Path $root 'tools/install-service.ps1'
$uninstaller = Join-Path $root 'tools/uninstall-service.ps1'
$pointer = Join-Path $root '.local/current-server-build.txt'
$serverDll = if ($ServerDll) { [IO.Path]::GetFullPath($ServerDll) } else { Join-Path $root ('.local/server-builds/' + ([IO.File]::ReadAllText($pointer).Trim()) + '/Chispa.Server.dll') }
if (-not (Test-Path $serverDll)) { throw "Server build not found: $serverDll (run ./build.ps1)" }

$work = Join-Path $root ('.local/qa-recovery-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force $work | Out-Null
$data = Join-Path $work 'data'
$logs = Join-Path $work 'logs'
$service = Join-Path $work 'service'
New-Item -ItemType Directory -Force $data, $logs, $service | Out-Null
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start(); $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
$base = "http://127.0.0.1:$port"

$failures = 0
$results = New-Object System.Collections.ArrayList
function Record([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Output ("PASS {0}{1}" -f $name, $(if ($detail) { "  ($detail)" } else { '' })) }
    else { Write-Output ("FAIL {0}  [{1}]" -f $name, $detail); $script:failures++ }
    [void]$results.Add(@{ name = $name; ok = $ok; detail = $detail })
}

function Test-Ready {
    try { (Invoke-WebRequest -Uri "$base/ready" -TimeoutSec 3 -UseBasicParsing -ErrorAction Stop).StatusCode -eq 200 }
    catch { $false }
}
function Wait-Ready([int]$seconds, [string]$label) {
    $started = Get-Date
    while (((Get-Date) - $started).TotalSeconds -lt $seconds) {
        if (Test-Ready) { return [int]((Get-Date) - $started).TotalSeconds }
        Start-Sleep -Milliseconds 400
    }
    throw "$label：服务在 $seconds 秒内没有恢复 /ready"
}
function Get-ServicePid {
    (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
        Select-Object -First 1).OwningProcess
}
function Install-Chispa {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installer -TaskName $taskName `
        -DataRoot $data -ServerUrl $base -LogDirectory $logs -TrustedProxies '127.0.0.1' `
        -Trigger AtLogOn -ServiceDirectory $service -ServerDll $serverDll -Force | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "install-service.ps1 failed with $LASTEXITCODE" }
}
function Uninstall-Chispa {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $uninstaller -TaskName $taskName `
        -ServiceDirectory $service | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "uninstall-service.ps1 failed with $LASTEXITCODE" }
}

try {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
    Install-Chispa
    Record '安装脚本注册了任务' ($null -ne (Get-ScheduledTask -TaskName $taskName)) ''

    Start-ScheduledTask -TaskName $taskName
    $seconds = Wait-Ready $StartTimeoutSeconds '首次启动'
    Record '注册的定义能无人值守启动服务' (Test-Ready) ("用时 $seconds 秒")
    $first = Get-ServicePid
    Record '服务进程确实由该任务拉起' ([bool]$first) ("PID $first")

    # Crash: end the server process the way a hard fault would.
    Stop-Process -Id $first -Force
    Start-Sleep -Milliseconds 500
    Record '结束进程后服务确实下线' (-not (Test-Ready)) ''
    $seconds = Wait-Ready $RestartTimeoutSeconds '崩溃重启'
    $second = Get-ServicePid
    Record '监视进程自动把服务重新拉起' ([bool]$second -and $second -ne $first) ("用时 $seconds 秒，新 PID $second")
    $watchdog = Join-Path $logs 'watchdog.log'
    $watchdogText = if (Test-Path $watchdog) { Get-Content $watchdog -Raw } else { '' }
    Record '监视日志记录了这次崩溃与重启' ($watchdogText -match 'server exited with code' -and $watchdogText -match 'restarting') ''

    # Uninstall: stops through the stop flag, removes the task.
    Uninstall-Chispa
    Start-Sleep -Seconds 2
    Record '卸载脚本注销了任务' ($null -eq (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) ''
    Record '卸载后服务已停止' (-not (Test-Ready)) ''

    # Install and start again - the path a reboot takes.
    Install-Chispa
    Start-ScheduledTask -TaskName $taskName
    $seconds = Wait-Ready $StartTimeoutSeconds '重新启动'
    Record '重新安装并启动后服务恢复' (Test-Ready) ("用时 $seconds 秒")

    $logFiles = Get-ChildItem $logs -Filter 'chispa-*.log' -ErrorAction SilentlyContinue
    Record '运行的实例写出了滚动日志文件' ($logFiles.Count -ge 1) (($logFiles | ForEach-Object { $_.Name }) -join ', ')
    $logText = if ($logFiles) { Get-Content $logFiles[0].FullName -Raw } else { '' }
    Record '日志记录了部署配置（代理信任与生产开关）' ($logText -match 'deployment: production=') ''
}
finally {
    if (-not $KeepTask) {
        try { Uninstall-Chispa } catch { Write-Output ("cleanup: {0}" -f $_.Exception.Message) }
        $leftover = Get-ServicePid
        if ($leftover) { Stop-Process -Id $leftover -Force -ErrorAction SilentlyContinue }
    }
}

$summary = @{ kind = 'recovery'; port = $port; checks = $results; failures = $failures;
    work = $work; at = (Get-Date).ToString('o') } | ConvertTo-Json -Depth 5
$summaryPath = Join-Path $root '.local/qa-recovery-last.json'
Set-Content -LiteralPath $summaryPath -Value $summary -Encoding UTF8
Write-Output "结果已写入 $summaryPath"
if ($failures -gt 0) { exit 1 }
exit 0
