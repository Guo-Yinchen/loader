# 备份 Chispa 服务端数据目录。
#
#   .\tools\backup-server.ps1 -Target D:\ChispaBackups
#   .\tools\backup-server.ps1 -Target E:\Backups\chispa -Keep 30
#
# 目标必须是本机磁盘上的目录：本脚本不会上传到任何外部服务，也不会替你决定异地副本——那是运维的
# 责任（移动硬盘、另一台机器、离线介质都行），脚本只负责产出可校验的一份副本。
#
# 一致性：先给源目录做一次快照（大小+修改时间），复制并逐文件校验哈希，复制结束后再快照一次。
# 两次快照不一致说明备份期间有人在写（发布、改账号），整份作废重试；重试仍不稳定就失败退出，
# 而不是交付一批"看起来齐了"的文件。服务端本身的写入都是"写临时文件再改名"或"整目录移动"，
# 所以单文件不会是半截，剩下的风险就是不同文件之间的时间差，这正是两次快照要挡住的。
#
# 不在备份里的东西（有意排除）：uploads/（上传中转）、logs/（轮转日志）、.stage-*（发布中间目录）、
# .readiness-probe。它们要么是瞬态的，要么是恢复后会自动重建的。

param(
    [string]$DataRoot = '',
    [Parameter(Mandatory = $true)]
    [string]$Target,
    [ValidateRange(1, 10000)][int]$Keep = 14,
    # 额外要一起备份的目录（例如发布签名私钥 .local/signing）。它们不属于服务端数据目录，
    # 但丢了同样无法恢复：签名私钥丢失意味着不能再签新版本。放进备份的 extra/<目录名>/。
    [string[]]$IncludePath = @(),
    [ValidateRange(1, 10)][int]$Retries = 3,
    [switch]$AllowRepositoryTarget,
    [switch]$NoAcl
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'BackupPaths.ps1')
if (-not $DataRoot) { $DataRoot = Join-Path $root '.local/server' }
$DataRoot = [IO.Path]::GetFullPath($DataRoot)
$Target = [IO.Path]::GetFullPath($Target).TrimEnd('\', '/')
$DataRoot = $DataRoot.TrimEnd('\', '/')
Assert-PlainBackupPath $DataRoot
Assert-PlainBackupPath $Target

function Say([string]$text) { Write-Output ('[{0}] {1}' -f (Get-Date).ToString('HH:mm:ss'), $text) }

# Windows PowerShell 5.1 的 Get-Content 默认按 ANSI 解码 BOM-less UTF-8；数据里的中文（程序显示名、
# 清单里的提示）会被解成乱码，甚至让 ConvertFrom-Json 直接抛异常。读 JSON 一律走 UTF-8。
function Read-Json([string]$path) {
    return [IO.File]::ReadAllText($path, [Text.Encoding]::UTF8) | ConvertFrom-Json
}

# ---- 路径检查：不做任何"猜你想要"的覆盖 ------------------------------------------------------
if (-not (Test-Path -LiteralPath $DataRoot)) { throw "数据目录不存在：$DataRoot" }
$looksLikeData = (Test-Path (Join-Path $DataRoot 'users.json')) -or
                 (Test-Path (Join-Path $DataRoot 'programs')) -or
                 (Test-Path (Join-Path $DataRoot 'keys'))
if (-not $looksLikeData) { throw "这个目录不像 Chispa 数据目录（没有 users.json / programs / keys）：$DataRoot" }

if ($Target -eq $DataRoot) { throw '备份目标不能等于数据目录本身。' }
if ($Target.StartsWith($DataRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw '备份目标不能放在数据目录里（恢复时会互相干扰）。'
}
$insideRepository = $Target.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
if ($insideRepository -and -not $AllowRepositoryTarget) {
    throw (("备份目标在仓库目录里（{0}）。备份含账号哈希、DPAPI 密文与发布包，默认不写进工作区。" -f $Target) +
            " 换一个仓库外的目录，或明确加 -AllowRepositoryTarget（仅限测试）。")
}
foreach ($include in $IncludePath) {
    $includeRoot = [IO.Path]::GetFullPath($include).TrimEnd('\', '/') + '\'
    Assert-PlainBackupPath $includeRoot
    if (-not (Test-Path -LiteralPath $includeRoot -PathType Container)) { throw 'Extra backup source must be a directory.' }
    if (($Target.TrimEnd('\', '/') + '\').StartsWith($includeRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Backup target cannot be inside an extra source directory.'
    }
}
New-Item -ItemType Directory -Force $Target | Out-Null

# ---- 复制 ------------------------------------------------------------------------------------
$excludedDirectoryNames = @('uploads', 'logs')
$stamp = (Get-Date -Format 'yyyyMMdd-HHmmss-fffffff') + '-' + [guid]::NewGuid().ToString('N')

function Test-Excluded([string]$relative) {
    $parts = $relative -split '[\\/]'
    foreach ($part in $parts) {
        if ($excludedDirectoryNames -contains $part) { return $true }
        if ($part -like '.stage-*') { return $true }
    }
    if ($parts[-1] -like '*.tmp') { return $true }
    if ($parts[-1] -eq '.readiness-probe') { return $true }
    return $false
}

function Get-Snapshot([string]$base) {
    $snapshot = @{}
    Get-ChildItem -LiteralPath $base -Recurse -File -Force | ForEach-Object {
        Assert-PlainBackupPath $_.FullName
        $relative = $_.FullName.Substring($base.Length + 1)
        if (Test-Excluded $relative) { return }
        $snapshot[$relative] = '{0}|{1}' -f $_.Length, $_.LastWriteTimeUtc.Ticks
    }
    return $snapshot
}

function Compare-Snapshot($left, $right) {
    if ($left.Count -ne $right.Count) { return $false }
    foreach ($key in $left.Keys) {
        if (-not $right.ContainsKey($key)) { return $false }
        if ($left[$key] -ne $right[$key]) { return $false }
    }
    return $true
}

function Copy-Tree([string]$source, [string]$destination) {
    # 复制每个文件时同时算源哈希，写完再算一次目标哈希：两者相等才算复制成功。
    $records = New-Object System.Collections.ArrayList
    $totalBytes = 0L
    Get-ChildItem -LiteralPath $source -Recurse -File -Force | ForEach-Object {
        Assert-PlainBackupPath $_.FullName
        $relative = $_.FullName.Substring($source.Length + 1)
        if (Test-Excluded $relative) { return }
        $targetPath = Join-Path $destination $relative
        $targetDirectory = Split-Path -Parent $targetPath
        if (-not (Test-Path -LiteralPath $targetDirectory)) {
            New-Item -ItemType Directory -Force $targetDirectory | Out-Null
        }
        $sourceHash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        Copy-Item -LiteralPath $_.FullName -Destination $targetPath -Force
        [IO.File]::SetLastWriteTimeUtc($targetPath, $_.LastWriteTimeUtc)
        $targetHash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash
        if ($sourceHash -ne $targetHash) { throw "复制校验失败：$relative" }
        $totalBytes += $_.Length
        [void]$records.Add([ordered]@{
            path   = $relative.Replace('\', '/')
            size   = $_.Length
            sha256 = $sourceHash.ToLowerInvariant()
            atUtc  = $_.LastWriteTimeUtc.ToString('o')
        })
    }
    return @{ Records = $records; Bytes = $totalBytes }
}

function Test-BackupIntegrity([string]$dataDirectory) {
    # 恢复前能发现的问题，备份时就发现：JSON 能解析、发布目录自洽、包大小与哈希对得上。
    $problems = New-Object System.Collections.ArrayList
    $warnings = New-Object System.Collections.ArrayList
    foreach ($json in Get-ChildItem -LiteralPath $dataDirectory -Recurse -File -Filter '*.json' -Force) {
        try { Read-Json $json.FullName | Out-Null }
        catch { [void]$problems.Add("JSON 无法解析：$($json.FullName.Substring($dataDirectory.Length + 1))") }
    }
    $usersFile = Join-Path $dataDirectory 'users.json'
    $ciphertextUsers = 0
    if (Test-Path $usersFile) {
        $users = @(Read-Json $usersFile)
        if ($users.Count -eq 0) { [void]$warnings.Add('users.json 里还没有账号') }
        $withCiphertext = 0
        foreach ($user in $users) {
            if (-not $user.username) { [void]$problems.Add('users.json 里有账号缺少 username') }
            if (-not $user.hash) { [void]$problems.Add("账号 $($user.username) 缺少口令哈希") }
            if ($user.passwordCiphertext) { $withCiphertext++ }
        }
        $ciphertextUsers = $withCiphertext
        if ($withCiphertext -gt 0) {
            [void]$warnings.Add("$withCiphertext 个账号的密码密文依赖 DPAPI：同机同账号可恢复，换机器/账号不可读")
        }
    } else {
        [void]$warnings.Add('还没有 users.json（服务会在第一个账号创建时生成）')
    }
    $programsDirectory = Join-Path $dataDirectory 'programs'
    if (Test-Path $programsDirectory) {
        foreach ($programDirectory in Get-ChildItem -LiteralPath $programsDirectory -Directory -Force) {
            if (-not (Test-Path (Join-Path $programDirectory.FullName 'program.json'))) {
                [void]$problems.Add("程序缺少 program.json：$($programDirectory.Name)")
            }
            $releases = Join-Path $programDirectory.FullName 'releases'
            if (-not (Test-Path $releases)) { continue }
            foreach ($releaseDirectory in Get-ChildItem -LiteralPath $releases -Directory -Force) {
                if ($releaseDirectory.Name -like '.stage-*') { continue }
                $manifestPath = Join-Path $releaseDirectory.FullName 'manifest.json'
                $packagePath = Join-Path $releaseDirectory.FullName 'package.zip'
                $signaturePath = Join-Path $releaseDirectory.FullName 'signature.json'
                $label = "$($programDirectory.Name)/$($releaseDirectory.Name)"
                if (-not (Test-Path $manifestPath)) { [void]$problems.Add("发布缺少 manifest.json：$label"); continue }
                if (-not (Test-Path $packagePath)) { [void]$problems.Add("发布缺少 package.zip：$label"); continue }
                $releaseManifest = Read-Json $manifestPath
                $packageHash = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant()
                if ($releaseManifest.sha256 -and ($packageHash -ne $releaseManifest.sha256)) {
                    [void]$problems.Add("包哈希与清单不符：$label")
                }
                $packageSize = (Get-Item -LiteralPath $packagePath).Length
                if ($releaseManifest.size -and ($packageSize -ne $releaseManifest.size)) {
                    [void]$problems.Add("包大小与清单不符：$label")
                }
                if (-not (Test-Path $signaturePath)) { [void]$warnings.Add("发布没有签名（旧包）：$label") }
            }
        }
    }
    $keys = Join-Path $dataDirectory 'keys'
    $keyCount = if (Test-Path $keys) { @(Get-ChildItem -LiteralPath $keys -File -Force).Count } else { 0 }
    if ($keyCount -lt 1) {
        # 没有密钥环本身不代表损坏（服务启动后会生成），但已有密码密文却丢了密钥环就真的取不回来了。
        if ($ciphertextUsers -gt 0) {
            [void]$problems.Add('有账号密码密文，但 Data Protection 密钥环缺失——密文将无法解密')
        } else {
            [void]$warnings.Add('备份里没有 Data Protection 密钥环（服务下次启动会生成）')
        }
    }
    if (-not (Test-Path (Join-Path $dataDirectory 'admin.json'))) {
        [void]$problems.Add('缺少管理员记录 admin.json')
    }
    return @{ Problems = $problems; Warnings = $warnings }
}

# ---- 两遍快照之间完成复制，源在变就重来 ------------------------------------------------------
$backupDirectory = ''
$copyResult = $null
$integrity = $null
$attempt = 0
for ($attempt = 1; $attempt -le $Retries; $attempt++) {
    $stampAttempt = if ($attempt -eq 1) { $stamp } else { '{0}-r{1}' -f $stamp, $attempt }
    $candidate = Join-Path $Target ('chispa-' + $stampAttempt)
    $before = Get-Snapshot $DataRoot
    $candidate = Get-SafeBackupPath $Target (Split-Path -Leaf $candidate)
    if (Test-Path -LiteralPath $candidate) { throw 'Backup destination already exists.' }
    if ($NoAcl) { New-Item -ItemType Directory -Path $candidate | Out-Null }
    else { New-PrivateBackupDirectory $candidate }
    $dataDirectory = Join-Path $candidate 'data'
    New-Item -ItemType Directory -Force $dataDirectory | Out-Null
    $copyResult = Copy-Tree -source $DataRoot -destination $dataDirectory
    $after = Get-Snapshot $DataRoot
    if (Compare-Snapshot $before $after) {
        $integrity = Test-BackupIntegrity $dataDirectory
        if ($integrity.Problems.Count -gt 0) {
            Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue
            throw ("备份内容自检失败，已删除这份副本：" + (($integrity.Problems | Select-Object -First 5) -join '；'))
        }
        $backupDirectory = $candidate
        break
    }
    Remove-Item -LiteralPath $candidate -Recurse -Force -ErrorAction SilentlyContinue
    Say ("第 {0} 次复制期间源目录发生变化，重试" -f $attempt)
    Start-Sleep -Seconds 2
}
if (-not $backupDirectory) {
    throw (("连续 {0} 次备份期间源目录都在变化。先在维护窗口停止服务，或稍后再试；" -f $Retries) +
            '不要在写操作进行中强行交付一份副本。')
}

# ---- 额外目录（签名私钥等）------------------------------------------------------------------
$extras = New-Object System.Collections.ArrayList
$extraNames = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($includePath in $IncludePath) {
    $source = [IO.Path]::GetFullPath($includePath)
    if (-not (Test-Path -LiteralPath $source)) { throw "要一起备份的目录不存在：$source" }
    Assert-PlainBackupPath $source
    $leaf = Split-Path -Leaf $source
    if (-not $extraNames.Add($leaf)) { throw 'Extra directories must have unique names.' }
    $destination = Join-Path (Join-Path $backupDirectory 'extra') $leaf
    New-Item -ItemType Directory -Force $destination | Out-Null
    $extraRecords = New-Object System.Collections.ArrayList
    Get-ChildItem -LiteralPath $source -Recurse -File -Force | ForEach-Object {
        Assert-PlainBackupPath $_.FullName
        $relative = $_.FullName.Substring($source.Length + 1)
        $targetPath = Join-Path $destination $relative
        $targetDirectory = Split-Path -Parent $targetPath
        if (-not (Test-Path -LiteralPath $targetDirectory)) { New-Item -ItemType Directory -Force $targetDirectory | Out-Null }
        Copy-Item -LiteralPath $_.FullName -Destination $targetPath -Force
        $hash = (Get-FileHash -LiteralPath $targetPath -Algorithm SHA256).Hash.ToLowerInvariant()
        [void]$extraRecords.Add([ordered]@{ path = $relative.Replace('\', '/'); size = $_.Length; sha256 = $hash })
    }
    [void]$extras.Add([ordered]@{ name = $leaf; source = $source; files = $extraRecords })
    Say ("一并备份额外目录：{0}（{1} 个文件）" -f $source, $extraRecords.Count)
}
# ---- 清单 ------------------------------------------------------------------------------------
$buildPointer = Join-Path $root '.local/current-server-build.txt'
$buildId = if (Test-Path $buildPointer) { ([IO.File]::ReadAllText($buildPointer)).Trim() } else { 'unknown' }
$manifest = [ordered]@{
    schema      = 'chispa-backup-v1'
    createdAt   = (Get-Date).ToString('o')
    source      = $DataRoot
    serverBuild = $buildId
    host        = $env:COMPUTERNAME
    user        = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    files       = $copyResult.Records.Count
    bytes       = $copyResult.Bytes
    excluded    = @('uploads/', 'logs/', '.stage-*', '.readiness-probe', '*.tmp')
    filesIndex  = $copyResult.Records
    extras      = $extras
    warnings    = @($integrity.Warnings | Select-Object -First 20)
}
$manifestPath = Join-Path $backupDirectory 'backup-manifest.json'
# BOM-less UTF-8: the manifest is machine-read (restore script, monitoring, any JSON tool). A BOM
# makes most JSON parsers fail at byte one, which is not a failure mode worth debugging later.
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6), $utf8NoBom)

$restoreNote = @"
Chispa 备份（schema chispa-backup-v1）
创建：$($manifest.createdAt)
来源：$DataRoot
主机：$($env:COMPUTERNAME)  账号：$($manifest.user)
文件：$($copyResult.Records.Count) 个，$('{0:N1}' -f ($copyResult.Bytes / 1MB)) MB

恢复方法（不要直接覆盖正在使用的数据目录）：
  1. .\tools\restore-backup.ps1 -Backup "$backupDirectory" -Target <新目录> -AdminUser <管理员账号>
  2. 脚本会在新目录上起一个临时实例做验证（登录、权限、下载校验），通过后才提示切换
  3. 切换：停服务 → 改名旧目录 → 把新目录放到 CHISPA_SERVER_DATA 位置 → 起服务
  额外目录（extra/，例如签名私钥）会被恢复到 -ExtraTarget 指定的目录，需要人工放回原位

同机同账号：可直接恢复，含账号密码密文与 Data Protection 密钥环。
换机器 / 换 Windows 账号：账号登录、权限、发布包仍然可用；密码查看（DPAPI 密文）与已签发的
后台登录 Cookie 不可用，需要用管理员重置密码重建密文。详见 docs/task-05-backup.md。
"@
[IO.File]::WriteAllText((Join-Path $backupDirectory 'RESTORE.txt'), $restoreNote, $utf8NoBom)

# ---- 限制访问 --------------------------------------------------------------------------------
if (-not $NoAcl) {
    try {
        $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        $sid = $identity.User.Value
        # 只给当前账号、SYSTEM、管理员组；SID 写法避免中英文系统上的组名差异。
        & icacls.exe $backupDirectory /inheritance:r /grant:r "*$sid`:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
        if ($LASTEXITCODE -ne 0) { Say '警告：未能设置备份目录 ACL，请手工确认访问权限。' }
    } catch {
        Say ('警告：设置 ACL 失败：{0}' -f $_.Exception.Message)
    }
}

# ---- 保留策略 --------------------------------------------------------------------------------
$all = Get-ChildItem -LiteralPath $Target -Directory -Force |
    Where-Object { $_.Name -like 'chispa-*' } |
    Where-Object { Test-Path (Join-Path $_.FullName 'backup-manifest.json') } |
    Sort-Object LastWriteTime -Descending
$pruned = 0
foreach ($stale in ($all | Select-Object -Skip $Keep)) {
    $safeStale = Get-SafeBackupPath $Target $stale.Name
    $staleManifest = Read-Json (Join-Path $safeStale 'backup-manifest.json')
    if ($staleManifest.schema -ne 'chispa-backup-v1') { continue }
    Remove-Item -LiteralPath $safeStale -Recurse -Force -ErrorAction SilentlyContinue
    $pruned++
    Say ("清理超出保留份数的旧备份：{0}" -f $stale.Name)
}

Say ("备份完成：{0}" -f $backupDirectory)
Say ("文件 {0} 个，{1:N1} MB；保留策略 keep={2}，本次清理 {3} 份" -f $copyResult.Records.Count, ($copyResult.Bytes / 1MB), $Keep, $pruned)
if ($integrity.Warnings.Count -gt 0) {
    foreach ($warning in ($integrity.Warnings | Select-Object -First 5)) { Say ('提示：' + $warning) }
}
Write-Output ("BACKUP_DIR=" + $backupDirectory)
