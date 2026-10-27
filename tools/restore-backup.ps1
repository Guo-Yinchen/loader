# 从备份恢复到一个**新目录**，并在那里验证。
#
#   .\tools\restore-backup.ps1 -Backup D:\ChispaBackups\chispa-20261026-130000 -Target D:\restore-1 -AdminUser manager
#
# 三条硬规则：
#   1. 只写新目录。目标已存在且非空时直接拒绝——恢复不是覆盖操作，误指到生产目录也不会有事。
#   2. 先校验再复制。manifest 里每个文件的 SHA-256 都要对上，任何不符立即失败（损坏的备份不会被
#      恢复成"半个能用的服务"）。
#   3. 复制完在目标目录上起一个临时实例做真实验证（登录、权限、下载校验），通过后才提示切换。
#
# 验证需要的口令从环境变量读取（-AdminPasswordEnv / -UserPasswordEnv 给出变量名），或交互式询问；
# 不写在命令行，也不写进任何文件。

param(
    [Parameter(Mandatory = $true)]
    [string]$Backup,
    [Parameter(Mandatory = $true)]
    [string]$Target,
    [string]$AdminUser = '',
    [string]$AdminPasswordEnv = 'CHISPA_RESTORE_ADMIN_PASSWORD',
    [string]$UserAccount = '',
    [string]$UserPasswordEnv = 'CHISPA_RESTORE_USER_PASSWORD',
    # 额外目录（备份里的 extra/，例如发布签名私钥）恢复到哪个目录。默认在恢复目标旁边，
    # 绝不放进恢复出来的数据目录里——私钥不该跟着数据目录一起被分发或打包。
    [string]$ExtraTarget = '',
    [string]$ServerDll = '',
    [switch]$SkipVerify
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'BackupPaths.ps1')
$Backup = [IO.Path]::GetFullPath($Backup)
$Target = [IO.Path]::GetFullPath($Target)

$failures = 0
$results = New-Object System.Collections.ArrayList
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Output ("VERIFY PASS {0}{1}" -f $name, $(if ($detail) { "  ($detail)" } else { '' })) }
    else { Write-Output ("VERIFY FAIL {0}  [{1}]" -f $name, $detail); $script:failures++ }
    [void]$results.Add(@{ name = $name; ok = $ok; detail = $detail })
}

function Say([string]$text) { Write-Output ('[{0}] {1}' -f (Get-Date).ToString('HH:mm:ss'), $text) }

# ---- 备份自身校验 ----------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $Backup)) { throw "备份目录不存在：$Backup" }
$manifestPath = Join-Path $Backup 'backup-manifest.json'
if (-not (Test-Path $manifestPath)) { throw "不是 Chispa 备份（缺少 backup-manifest.json）：$Backup" }
# 显式 UTF-8：Windows PowerShell 5.1 的 Get-Content 默认按 ANSI 解码，BOM-less UTF-8 里的
# 中文会把 JSON 解坏（甚至让 ConvertFrom-Json 抛 ArgumentException）。
$manifest = [IO.File]::ReadAllText($manifestPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
if ($manifest.schema -ne 'chispa-backup-v1') { throw "不认识的备份格式：$($manifest.schema)" }
$dataDirectory = Join-Path $Backup 'data'
if (-not (Test-Path $dataDirectory)) { throw "备份里没有 data 目录：$Backup" }

# Validate the complete restore plan before creating any output, including extra/private files.
Assert-PlainBackupPath $Backup
Assert-PlainBackupPath $Target
$backupRoot = $Backup.TrimEnd('\', '/') + '\'
$targetRoot = $Target.TrimEnd('\', '/') + '\'
if ($targetRoot.StartsWith($backupRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $backupRoot.StartsWith($targetRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Backup source and restore target must not overlap.'
}
$extras = @($manifest.extras | Where-Object { $_ })
if ($extras.Count -gt 0) {
    if (-not $ExtraTarget) { $ExtraTarget = $Target.TrimEnd('\', '/') + '-extras' }
    $ExtraTarget = [IO.Path]::GetFullPath($ExtraTarget)
    Assert-PlainBackupPath $ExtraTarget
    $extraRoot = $ExtraTarget.TrimEnd('\', '/') + '\'
    foreach ($other in @($backupRoot, $targetRoot)) {
        if ($extraRoot.StartsWith($other, [StringComparison]::OrdinalIgnoreCase) -or
            $other.StartsWith($extraRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Extra restore target must be separate from data and backup directories.'
        }
    }
    if ((Test-Path -LiteralPath $ExtraTarget) -and @(Get-ChildItem -LiteralPath $ExtraTarget -Force).Count -gt 0) {
        throw 'Extra restore target must be empty.'
    }
}
$allDestinations = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
function Assert-RestoreEntry($entry, [string]$sourceRoot, [string]$destinationRoot) {
    $source = Get-SafeBackupPath $sourceRoot $entry.path
    $destination = Get-SafeBackupPath $destinationRoot $entry.path
    if (-not $allDestinations.Add($destination)) { throw 'Duplicate backup destination.' }
    if ($entry.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or $null -eq $entry.size -or [long]$entry.size -lt 0) {
        throw 'Invalid backup file metadata.'
    }
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Backup file missing: $($entry.path)" }
    if ((Get-Item -LiteralPath $source).Length -ne [long]$entry.size -or
        (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $entry.sha256) {
        throw "备份文件大小或哈希不符：$($entry.path)"
    }
}
if (@($manifest.filesIndex).Count -ne $manifest.files -or $manifest.files -lt 1) {
    throw 'Backup file index is empty or inconsistent.'
}
foreach ($entry in $manifest.filesIndex) { Assert-RestoreEntry $entry $dataDirectory $Target }
$groupNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($group in $extras) {
    if ($group.name -match '[\\/]' -or -not $groupNames.Add($group.name)) { throw 'Invalid extra group name.' }
    $sourceGroup = Get-SafeBackupPath (Join-Path $Backup 'extra') $group.name
    $destinationGroup = Get-SafeBackupPath $ExtraTarget $group.name
    foreach ($entry in $group.files) { Assert-RestoreEntry $entry $sourceGroup $destinationGroup }
}

Say ("校验 {0}（{1} 个文件，创建于 {2}）" -f $Backup, $manifest.files, $manifest.createdAt)
$bad = New-Object System.Collections.ArrayList
foreach ($entry in $manifest.filesIndex) {
    $path = Join-Path $dataDirectory ($entry.path -replace '/', '\')
    if (-not (Test-Path -LiteralPath $path)) { [void]$bad.Add("缺失：$($entry.path)"); continue }
    $item = Get-Item -LiteralPath $path
    if ($item.Length -ne $entry.size) { [void]$bad.Add("大小不符：$($entry.path)"); continue }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -ne $entry.sha256) { [void]$bad.Add("哈希不符：$($entry.path)") }
}
Check '备份文件完整（逐个 SHA-256 校验）' ($bad.Count -eq 0) (($bad | Select-Object -First 5) -join '；')
if ($bad.Count -gt 0) {
    Write-Output '备份已损坏或不完整，未写入任何目标目录。'
    exit 1
}

# ---- 目标目录：必须是新的 --------------------------------------------------------------------
if (Test-Path -LiteralPath $Target) {
    $existing = @(Get-ChildItem -LiteralPath $Target -Force)
    if ($existing.Count -gt 0) {
        throw ("目标目录已存在且非空：{0}（{1} 项）。恢复只写新目录；请换一个新的路径，" -f $Target, $existing.Count) +
              '或先自行确认并移走旧目录——本脚本不会删除任何现有数据。'
    }
} else {
    New-PrivateBackupDirectory $Target
}
New-PrivateBackupDirectory $Target
Check '目标目录是新的（未覆盖任何现有数据）' $true $Target

# ---- 复制 ------------------------------------------------------------------------------------
Say ('复制到 {0}' -f $Target)
$copied = 0
foreach ($entry in $manifest.filesIndex) {
    $relative = ($entry.path -replace '/', '\')
    $source = Join-Path $dataDirectory $relative
    $destination = Join-Path $Target $relative
    $destinationDirectory = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $destinationDirectory)) { New-Item -ItemType Directory -Force $destinationDirectory | Out-Null }
    Copy-Item -LiteralPath $source -Destination $destination -Force
    $copied++
}
Check '复制完成' ($copied -eq $manifest.filesIndex.Count) ("$copied 个文件")

# ---- 额外目录（签名私钥等）：恢复到独立目录，不放进数据目录 ----------------------------------
$extras = @($manifest.extras | Where-Object { $_ })
if ($extras.Count -gt 0) {
    if (-not $ExtraTarget) { $ExtraTarget = $Target.TrimEnd('\') + '-extras' }
    $ExtraTarget = [IO.Path]::GetFullPath($ExtraTarget)
    if (Test-Path -LiteralPath $ExtraTarget) {
        if (@(Get-ChildItem -LiteralPath $ExtraTarget -Force).Count -gt 0) {
            throw ("额外目录的目标已存在且非空：{0}。请用 -ExtraTarget 指定一个新的目录。" -f $ExtraTarget)
        }
    }
    New-PrivateBackupDirectory $ExtraTarget
    $extraCount = 0
    $extraBad = New-Object System.Collections.ArrayList
    foreach ($group in $extras) {
        foreach ($entry in $group.files) {
            $source = Join-Path (Join-Path (Join-Path $Backup 'extra') $group.name) ($entry.path -replace '/', '\')
            if (-not (Test-Path -LiteralPath $source)) { [void]$extraBad.Add("缺失：$($group.name)/$($entry.path)"); continue }
            $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($hash -ne $entry.sha256) { [void]$extraBad.Add("哈希不符：$($group.name)/$($entry.path)"); continue }
            $destination = Join-Path (Join-Path $ExtraTarget $group.name) ($entry.path -replace '/', '\')
            $destinationDirectory = Split-Path -Parent $destination
            if (-not (Test-Path -LiteralPath $destinationDirectory)) { New-Item -ItemType Directory -Force $destinationDirectory | Out-Null }
            Copy-Item -LiteralPath $source -Destination $destination -Force
            $extraCount++
        }
    }
    Check '额外目录（签名私钥等）完整且已恢复' ($extraBad.Count -eq 0) (($extraBad | Select-Object -First 3) -join '；')
    if ($extraBad.Count -eq 0) { Write-Output ("EXTRA_TARGET=" + $ExtraTarget + "  (" + $extraCount + " 个文件)") }
}
# ---- 在恢复出来的目录上做真实验证 ------------------------------------------------------------
if ($SkipVerify) {
    Say '按要求跳过运行时验证（-SkipVerify）：数据已复制并逐文件校验哈希，但实例未启动验证。'
} else {
    $pointer = Join-Path $root '.local/current-server-build.txt'
    $serverDll = if ($ServerDll) { [IO.Path]::GetFullPath($ServerDll) } elseif (Test-Path $pointer) {
        Join-Path $root ('.local/server-builds/' + ([IO.File]::ReadAllText($pointer)).Trim() + '/Chispa.Server.dll')
    } else { Join-Path $root 'server/bin/Release/net10.0/Chispa.Server.dll' }
    $dotnet = if (Test-Path (Join-Path $root '.tooling/dotnet/dotnet.exe')) {
        Join-Path $root '.tooling/dotnet/dotnet.exe'
    } else { 'dotnet' }
    if (-not (Test-Path $serverDll)) { throw "找不到服务端构建：$serverDll（先运行 ./build.ps1）" }

    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
    $base = "http://127.0.0.1:$port"
    $verificationLog = Join-Path $Target '_verification-server.log'
    $env:CHISPA_SERVER_DATA = $Target
    $env:CHISPA_SERVER_URL = $base
    $env:CHISPA_TRUSTED_PROXIES = '127.0.0.1'
    $env:CHISPA_PRODUCTION = '0'
    $env:CHISPA_RELEASE_TRUST_KEYS = if (Test-Path -LiteralPath (Join-Path $Target 'release-trust.json')) { Join-Path $Target 'release-trust.json' } else { $null }
    $env:CHISPA_LOG_LEVEL = 'Warning'
    $env:DOTNET_CLI_HOME = Join-Path $root '.tooling/home'
    $server = Start-Process -FilePath $dotnet -ArgumentList @('"' + $serverDll + '"') -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $verificationLog -RedirectStandardError ($verificationLog + '.err')
    try {
        $ready = $false
        for ($i = 0; $i -lt 100; $i++) {
            try {
                $response = Invoke-WebRequest -Uri "$base/ready" -TimeoutSec 2 -UseBasicParsing -ErrorAction Stop
                if ($response.StatusCode -eq 200) { $ready = $true; break }
            } catch { }
            Start-Sleep -Milliseconds 300
        }
        Check '恢复出来的数据能启动服务（/ready 200）' $ready ''
        if ($ready) {
            # 管理员登录 = Data Protection 密钥环可用（Cookie 票据是用它加密的）
            $jar = New-Object Microsoft.PowerShell.Commands.WebRequestSession
            $loginPage = Invoke-WebRequest -Uri "$base/admin/login" -WebSession $jar -UseBasicParsing -TimeoutSec 10
            $tokenMatch = [regex]::Match($loginPage.Content, "name='__RequestVerificationToken' value='([^']+)'")
            if (-not $tokenMatch.Success) { throw '恢复实例的登录页没有 CSRF 令牌' }
            $token = $tokenMatch.Groups[1].Value

            $adminPassword = ''
            if ($AdminUser) {
                $adminPassword = [Environment]::GetEnvironmentVariable($AdminPasswordEnv)
                if (-not $adminPassword) {
                    $secure = Read-Host -AsSecureString ("管理员 {0} 的密码（仅用于本次验证）" -f $AdminUser)
                    $adminPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
                        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
                }
                $form = @{ __RequestVerificationToken = $token; username = $AdminUser; password = $adminPassword }
                try {
                    $login = Invoke-WebRequest -Uri "$base/admin/login" -Method Post -Body $form -WebSession $jar `
                        -UseBasicParsing -TimeoutSec 10 -MaximumRedirection 0 -ErrorAction SilentlyContinue
                } catch {
                    # 302 在 Windows PowerShell 里会作为异常抛出，302 就是成功
                    $login = $_.Exception.Response
                }
                $adminPage = Invoke-WebRequest -Uri "$base/admin" -WebSession $jar -UseBasicParsing -TimeoutSec 10
                $adminOk = $adminPage.Content -match '创建账号'
                Check '管理员能登录（Data Protection 密钥环可用）' $adminOk ''
                if ($adminOk) {
                    $adminToken = [regex]::Match($adminPage.Content, "name='__RequestVerificationToken' value='([^']+)'").Groups[1].Value
                    if ($UserAccount) {
                        # 查看账号密码走的是 users.json 里的 DPAPI 密文：能解出来说明密文与密钥环配套
                        try {
                            # 查看密码需要先重新验证管理员身份（任务 06），所以这里走真实的两步。
                            $stepup = Invoke-WebRequest -Uri "$base/admin/users/$UserAccount/password/stepup" -Method Post `
                                -Body @{ __RequestVerificationToken = $adminToken; adminPassword = $adminPassword } -WebSession $jar `
                                -UseBasicParsing -TimeoutSec 10
                            $grantMatch = [regex]::Match($stepup.Content, "name='grant' value='([^']+)'")
                            if (-not $grantMatch.Success) { throw "恢复实例没有签发查看授权" }
                            $grant = $grantMatch.Groups[1].Value
                            $view = Invoke-WebRequest -Uri "$base/admin/users/$UserAccount/password/view" -Method Post `
                                -Body @{ __RequestVerificationToken = $adminToken; grant = $grant } -WebSession $jar `
                                -UseBasicParsing -TimeoutSec 10
                            $passwordVisible = $view.Content -match "value='[^']+'" -and $view.Content -notmatch '无法还原'
                            Check ("账号 {0} 的密码密文可解密（DPAPI 同机同账号）" -f $UserAccount) $passwordVisible ''
                        } catch {
                            Check ("账号 {0} 的密码密文可解密（DPAPI 同机同账号）" -f $UserAccount) $false $_.Exception.Message
                        }
                    }
                }
            } else {
                Say '未提供 -AdminUser，跳过管理员相关检查'
            }

            # 普通账号：登录、权限、下载与哈希
            if ($UserAccount) {
                $userPassword = [Environment]::GetEnvironmentVariable($UserPasswordEnv)
                if (-not $userPassword) {
                    $secure = Read-Host -AsSecureString ("账号 {0} 的密码（仅用于本次验证）" -f $UserAccount)
                    $userPassword = [Runtime.InteropServices.Marshal]::PtrToStringAuto(
                        [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
                }
                try {
                    $session = Invoke-RestMethod -Uri "$base/api/v1/session" -Method Post `
                        -ContentType 'application/json' -Body (@{ username = $UserAccount; password = $userPassword } | ConvertTo-Json) `
                        -TimeoutSec 10
                    Check ("账号 {0} 能登录（PBKDF2 哈希随备份完整）" -f $UserAccount) ([bool]$session.token) ''
                    $headers = @{ Authorization = 'Bearer ' + $session.token }
                    $programs = Invoke-RestMethod -Uri "$base/api/v1/programs" -Headers $headers -TimeoutSec 10
                    Check '账号能读到自己的授权程序列表' ($programs.Count -ge 0) ("{0} 个" -f @($programs).Count)
                    $downloaded = 0
                    foreach ($program in @($programs)) {
                        if ($program.hasRelease -ne $true) { continue }
                        $release = Invoke-RestMethod -Uri ("$base/api/v1/programs/{0}/releases/latest" -f $program.id) `
                            -Headers $headers -TimeoutSec 10
                        $temp = Join-Path ([IO.Path]::GetTempPath()) ('chispa-restore-' + [guid]::NewGuid().ToString('N') + '.zip')
                        try {
                            Invoke-WebRequest -Uri ("$base/api/v1/programs/{0}/releases/{1}/package" -f $program.id, $release.version) `
                                -Headers $headers -OutFile $temp -UseBasicParsing -TimeoutSec 120
                            $hash = (Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash.ToLowerInvariant()
                            $downloadOk = ($hash -eq $release.sha256) -and ((Get-Item -LiteralPath $temp).Length -eq $release.size)
                            Check ("下载 {0} {1} 并通过大小与哈希校验" -f $program.id, $release.version) $downloadOk $hash
                            $downloaded++
                        } finally {
                            if (Test-Path $temp) { Remove-Item $temp -Force }
                        }
                    }
                    Check '至少完整校验过一个发布包' ($downloaded -ge 1) ("{0} 个" -f $downloaded)
                } catch {
                    Check ("账号 {0} 的恢复后验证" -f $UserAccount) $false $_.Exception.Message
                }
            }
        }
    } finally {
        if ($server -and -not $server.HasExited) {
            Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
            $server.WaitForExit(10000) | Out-Null
        }
        Remove-Item Env:\CHISPA_SERVER_DATA, Env:\CHISPA_SERVER_URL, Env:\CHISPA_TRUSTED_PROXIES, Env:\CHISPA_PRODUCTION, Env:\CHISPA_LOG_LEVEL -ErrorAction SilentlyContinue
    }
}

if ($failures -gt 0) {
    Write-Output ("恢复验证失败：{0} 项未通过。目标目录保留在 {1} 以便排查，生产数据未被触碰。" -f $failures, $Target)
    exit 1
}

Write-Output ("RESTORE_TARGET=" + $Target)
if ($SkipVerify) { Write-Output '恢复已复制完成（未做运行时验证）。切换方法（需要停服务，属于人工操作）：' }
else { Write-Output '恢复完成并通过验证。切换方法（需要停服务，属于人工操作）：' }
Write-Output '  1. 停服务：Stop-ScheduledTask -TaskName Chispa.Server（或 ./tools/uninstall-service.ps1）'
Write-Output ("  2. 把现有数据目录改名保留（不要删除）：{0} -> {0}.before-restore" -f '<当前 CHISPA_SERVER_DATA>')
Write-Output ("  3. 把 {0} 移动到 CHISPA_SERVER_DATA 的位置" -f $Target)
Write-Output '  4. 起服务：Start-ScheduledTask -TaskName Chispa.Server，确认 /ready 与后台登录正常'
Write-Output '  5. 保留旧目录至少一个备份周期后再删除'
exit 0
