[CmdletBinding()]
param(
    [switch]$Apply,
    [string]$SoftwareRoot = (Join-Path (Split-Path $PSScriptRoot -Parent) '../wardogs chispa')
)
$ErrorActionPreference = 'Stop'
$loaderRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$softwareRootPath = [IO.Path]::GetFullPath($SoftwareRoot)
. (Join-Path $PSScriptRoot 'BackupPaths.ps1')
$targets = [Collections.Generic.List[string]]::new()
function Add-Target([string]$Base, [string]$Relative) {
    $path = Get-SafeBackupPath -Base $Base -Relative $Relative
    if (Test-Path -LiteralPath $path) { $targets.Add($path) }
}

# Runtime data, signing keys, SDKs and dist are deliberately outside this allowlist.
foreach ($relative in @('client/bin','client/obj','server/bin','server/obj','tests/bin','tests/obj',
    'tools/Chispa.Sign/bin','tools/Chispa.Sign/obj','.local/client-publish-bin',
    '.local/server-publish-temp','.local/staging','.local/acceptance','.local/archive',
    '.local/client-build-check','.local/readiness-check','.local/ui-qa')) {
    Add-Target $loaderRoot $relative
}
foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $loaderRoot '.local') -Directory) {
    if ($directory.Name -match '^(qa-|capture-|icon-|launcher-|startup-failure|ui-scale-)') {
        Add-Target $loaderRoot ('.local/' + $directory.Name)
    }
}
$current = ([IO.File]::ReadAllText((Join-Path $loaderRoot '.local/current-server-build.txt'))).Trim()
if ($current -notmatch '^[0-9a-f]{32}$') { throw 'Invalid active server pointer; cleanup refused.' }
$activeCommands = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object Name -eq 'dotnet.exe' | Select-Object -ExpandProperty CommandLine)
foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $loaderRoot '.local/server-builds') -Directory) {
    if ($directory.Name -eq $current) { continue }
    if ($activeCommands | Where-Object { $_ -and $_.IndexOf($directory.FullName, [StringComparison]::OrdinalIgnoreCase) -ge 0 }) { continue }
    Add-Target $loaderRoot ('.local/server-builds/' + $directory.Name)
}
# The latest outputs must be copied into dist before deleting their historical copies.
$client = Join-Path $loaderRoot 'dist/client/Chispa.Launcher.exe'
$software = Join-Path $loaderRoot 'dist/software/Wardogs-1.2.4.zip'
if ((Test-Path -LiteralPath $client) -and (Test-Path -LiteralPath $software)) {
    Add-Target $loaderRoot '.local/distribution'
    Add-Target $loaderRoot '.local/releases'
}
foreach ($relative in @('bin/x64/Wardogs/intermediate','bin/x64/Wardogs/ui-test-output',
    'bin/x64/Wardogs/Wardogs.ilk','bin/x64/Wardogs/Wardogs.pdb','Usermode/intermediate',
    'tests/auth/bin','tests/auth/obj','tests/probe-tmp','tools/_logtest/build')) {
    Add-Target $softwareRootPath $relative
}
foreach ($directory in Get-ChildItem -LiteralPath $softwareRootPath -Directory) {
    if ($directory.Name -match '^ui-test-(output|weapon-recorder)') { Add-Target $softwareRootPath $directory.Name }
}
foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $softwareRootPath 'tests') -Directory) {
    if ($directory.Name -match '^(build-|ui-check-)') { Add-Target $softwareRootPath ('tests/' + $directory.Name) }
}
foreach ($file in Get-ChildItem -LiteralPath $softwareRootPath -File -Filter '*.obj') { Add-Target $softwareRootPath $file.Name }

$before = 0L
$removed = 0L
$skipped = [Collections.Generic.List[string]]::new()
foreach ($target in $targets) {
    # Revalidate the resolved absolute path immediately before every deletion.
    $base = if ($target.StartsWith($loaderRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { $loaderRoot } else { $softwareRootPath }
    $relative = $target.Substring($base.TrimEnd('\', '/').Length + 1)
    $safe = Get-SafeBackupPath -Base $base -Relative $relative
    if ((Get-Item -LiteralPath $safe).PSIsContainer -and
        (Get-ChildItem -LiteralPath $safe -Recurse -Force -ErrorAction SilentlyContinue |
            Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } | Select-Object -First 1)) {
        $skipped.Add($safe); Write-Warning ('Kept path containing links: ' + $safe); continue
    }
    $size = (Get-ChildItem -LiteralPath $safe -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    if (Test-Path -LiteralPath $safe -PathType Leaf) { $size = (Get-Item -LiteralPath $safe).Length }
    $before += $size
    if (-not $Apply) { Write-Output ('Would remove: ' + $relative); continue }
    try {
        Remove-Item -LiteralPath $safe -Recurse -Force -ErrorAction Stop
        $removed += $size
    }
    catch {
        $remaining = (Get-ChildItem -LiteralPath $safe -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
        $removed += [math]::Max(0, $size - $remaining)
        $skipped.Add($safe); Write-Warning ('Kept in-use/inaccessible path: ' + $safe)
    }
}
[pscustomobject]@{ Applied = [bool]$Apply; TargetCount = $targets.Count;
    CandidateMiB = [math]::Round($before / 1MB, 1); RemovedMiB = [math]::Round($removed / 1MB, 1);
    Skipped = $skipped.ToArray(); ActiveServerBuild = $current } | ConvertTo-Json -Depth 4
