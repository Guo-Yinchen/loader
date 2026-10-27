# Availability check for the Chispa service.
#
#   .\health-check.ps1                       # 本机 /ready，一次检查
#   .\health-check.ps1 -Loop                 # 每 60 秒检查一次（计划任务也可以用 -Once 定时跑）
#   .\health-check.ps1 -PublicUrl https://teamviewer.rm7lwjwtg.nyat.app:24154/ready
#   .\health-check.ps1 -AlertWebhook https://example.invalid/hooks/chispa
#
# 告警目的地是配置项：不传 -AlertWebhook 就不发送任何消息，只在本机留下记录。这是有意的——
# 这个脚本不知道谁的告警系统应该接收事件，也不应该替运维决定往哪里发。
#
# 退出码：0 = 健康；1 = 不健康（-Loop 模式下只在启动时失败才返回非零，之后靠记录与告警）。

param(
    [string]$Url = 'http://127.0.0.1:5115/ready',
    [string]$PublicUrl = '',
    [int]$TimeoutSeconds = 10,
    [int]$Attempts = 3,
    [int]$RetryDelaySeconds = 5,
    [int]$IntervalSeconds = 60,
    [string]$AlertWebhook = '',
    [string]$AlertFile = '',
    [string]$LogFile = '',
    [switch]$Loop,
    [switch]$Once
)

$ErrorActionPreference = 'Stop'

function Write-Line([string]$text) {
    $line = '{0} {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $text
    # Write-Host, not Write-Output: the lines belong to the operator, and anything a function
    # emits through the pipeline becomes part of its return value - a string array is always
    # truthy, which would turn "unhealthy" into "healthy".
    Write-Host $line
    if ($LogFile) {
        try {
            $directory = Split-Path -Parent $LogFile
            if ($directory) { New-Item -ItemType Directory -Force $directory | Out-Null }
            Add-Content -LiteralPath $LogFile -Value $line -Encoding UTF8
        } catch { }
    }
}

function Test-Target([string]$target) {
    # Only the scheme/host/port are ever logged or sent: the path may carry a private endpoint.
    $uri = [Uri]$target
    $label = '{0}://{1}:{2}{3}' -f $uri.Scheme, $uri.Host, $uri.Port, $uri.AbsolutePath
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri $target -TimeoutSec $TimeoutSeconds -UseBasicParsing -ErrorAction Stop
            $body = ''
            if ($response.Content) {
                $body = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { [string]$response.Content }
            }
            if ($response.StatusCode -eq 200) {
                Write-Line ("OK   {0} {1}" -f $label, $body.Trim())
                return @{ Ok = $true; Target = $label; Detail = $body.Trim() }
            }
            $detail = 'HTTP {0}' -f $response.StatusCode
        } catch {
            $detail = if ($_.Exception.Response) { 'HTTP {0}' -f [int]$_.Exception.Response.StatusCode } else { $_.Exception.Message }
        }
        Write-Line ("FAIL {0} attempt {1}/{2}: {3}" -f $label, $attempt, $Attempts, $detail)
        if ($attempt -lt $Attempts) { Start-Sleep -Seconds $RetryDelaySeconds }
    }
    return @{ Ok = $false; Target = $label; Detail = $detail }
}

function Send-Alert([string]$target, [string]$detail) {
    # Payload carries the host and a status word, nothing about the inside of the machine.
    $payload = [ordered]@{
        service = 'chispa'
        status  = 'down'
        target  = $target
        detail  = $detail
        at      = (Get-Date).ToString('o')
    } | ConvertTo-Json -Compress
    if ($AlertFile) {
        try {
            $directory = Split-Path -Parent $AlertFile
            if ($directory) { New-Item -ItemType Directory -Force $directory | Out-Null }
            Add-Content -LiteralPath $AlertFile -Value ((Get-Date).ToString('o') + ' ' + $payload) -Encoding UTF8
            Write-Line ("alert recorded in {0}" -f $AlertFile)
        } catch { Write-Line ("could not write the alert file: {0}" -f $_.Exception.Message) }
    }
    if (-not $AlertWebhook) {
        Write-Line 'no alert destination configured (AlertWebhook is empty), nothing was sent'
        return
    }
    try {
        Invoke-RestMethod -Uri $AlertWebhook -Method Post -ContentType 'application/json' -Body $payload -TimeoutSec $TimeoutSeconds | Out-Null
        Write-Line ("alert sent to {0}" -f ([Uri]$AlertWebhook).Host)
    } catch {
        Write-Line ("alert could not be sent: {0}" -f $_.Exception.Message)
    }
}

function Invoke-Round {
    $targets = @($Url)
    if ($PublicUrl) { $targets += $PublicUrl }
    $failed = @()
    foreach ($target in $targets) {
        $result = Test-Target $target
        if (-not $result.Ok) { $failed += $result }
    }
    foreach ($result in $failed) { Send-Alert $result.Target $result.Detail }
    return ($failed.Count -eq 0)
}

if ($Loop -and -not $Once) {
    Write-Line ("watching {0} every {1}s" -f $Url, $IntervalSeconds)
    while ($true) {
        Invoke-Round | Out-Null
        Start-Sleep -Seconds $IntervalSeconds
    }
}

if (Invoke-Round) { exit 0 } else { exit 1 }
