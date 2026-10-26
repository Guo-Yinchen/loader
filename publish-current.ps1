param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$Version,
    [string]$SourceDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$candidates = @(
    (Join-Path $root '../wardogs chispa/bin/x64/Wardogs'),
    (Join-Path $root '../bin/x64/Wardogs')
)
$source = if ($SourceDirectory) { $SourceDirectory } else { $candidates | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Wardogs.exe') } | Select-Object -First 1 }
if (-not $source) { throw 'Cannot find Wardogs runtime. Supply -SourceDirectory.' }
$stage = Join-Path $root ".local/staging/$Version"
if (Test-Path -LiteralPath $stage) { throw "Staging directory already exists: $stage" }
if (-not (Test-Path -LiteralPath (Join-Path $source 'Wardogs.exe'))) { throw 'Build Wardogs.exe first.' }
New-Item -ItemType Directory -Force $stage | Out-Null
$files = @('Wardogs.exe', 'info.db', 'README.txt', 'LICENSE', 'FTDI-source-licenses.txt', 'FTDI-SOURCE.txt', 'CHECK-RUNTIME.cmd')
foreach ($name in $files) {
    $path = Join-Path $source $name
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $stage }
}
Get-ChildItem -LiteralPath $source -File -Filter '*.dll' | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $stage }
foreach ($name in @('assets', 'terrain-packs')) {
    $path = Join-Path $source $name
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $stage -Recurse }
}
foreach ($name in @('vmm.dll', 'leechcore.dll', 'FTD3XX.dll', 'info.db')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stage $name))) { throw "Runtime is missing $name" }
}
if (-not (Test-Path -LiteralPath (Join-Path $stage 'assets'))) { throw 'Runtime is missing assets.' }
Write-Output "Prepared $stage"
& (Join-Path $root 'run-local.ps1') program-add chispa Chispa Wardogs.exe
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $root 'run-local.ps1') publish chispa $Version $stage
exit $LASTEXITCODE
