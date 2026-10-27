# Shared path and ACL checks. Keep ASCII for Windows PowerShell 5.1.
function Assert-PlainBackupPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if (Test-Path -LiteralPath $current) {
            $item = Get-Item -LiteralPath $current -Force
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Links/reparse points are not allowed in backup paths: $current"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($current.TrimEnd('\', '/'))
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Get-SafeBackupPath([string]$Base, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative)) {
        throw 'Backup paths must be non-empty relative paths.'
    }
    foreach ($part in ($Relative -split '[\\/]')) {
        if (-not $part -or $part -eq '.' -or $part -eq '..' -or
            $part -match '[<>:"|?*\x00-\x1f]' -or $part -match '[ .]$' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw "Unsafe backup path: $Relative"
        }
    }
    $boundary = [IO.Path]::GetFullPath($Base).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $full = [IO.Path]::GetFullPath((Join-Path $Base $Relative))
    if (-not $full.StartsWith($boundary, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Backup path escaped its root: $Relative"
    }
    Assert-PlainBackupPath $full
    return $full
}

function New-PrivateBackupDirectory([string]$Path) {
    Assert-PlainBackupPath $Path
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    $sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    & icacls.exe $Path /inheritance:r /grant:r "*$sid`:(OI)(CI)F" '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Cannot restrict access to backup directory: $Path" }
}
