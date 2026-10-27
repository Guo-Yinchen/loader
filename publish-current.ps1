param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$Version,
    [string]$SourceDirectory,
    [ValidateSet('production', 'local')][string]$Profile = 'production',
    # The signing key lives in the release environment, never in the server's data directory and
    # never in source control. This default is the local development key produced by
    #   ./tools/Chispa.Sign keygen --out .local/signing --id release-dev-2026-10
    [string]$SigningKey = '',
    [string]$KeyId = 'release-dev-2026-10',
    [string]$PasswordFile = '',
    # One-time: copy the public half into the server's trust file so the local server can verify
    # what this script publishes. Without it, publishing fails with a "trust keys not configured".
    [switch]$InstallTrustKey
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

# The published version and the version compiled into the EXE have to be the same string: the
# launch lease the program receives names one version, and the program compares it with its own
# stamp. Publishing 1.2.3 from a build stamped 1.2.2 produces a package that downloads, installs
# and then refuses to start - so it is caught here instead of by a user.
$identity = & (Join-Path $source 'Wardogs.exe') --auth-build
if ($LASTEXITCODE -ne 0 -or -not $identity) {
    throw "Wardogs.exe did not answer --auth-build, so its authorization stamp cannot be checked. Rebuild it with ./build.ps1."
}
$parts = ($identity | Select-Object -First 1).Trim() -split '\s+'
if ($parts.Count -lt 2 -or $parts[0] -ne 'chispa') {
    throw "Unexpected --auth-build answer: '$identity'"
}
if ($parts[1] -ne $Version) {
    throw ("Wardogs.exe is stamped {0}, but you are publishing {1}. Rebuild with: ./build.ps1 -Version {1}  (in the Wardogs repository)" -f $parts[1], $Version)
}
if ($Version -eq '0.0.0') { throw 'An unstamped 0.0.0 build cannot be published.' }
$profiles = [IO.File]::ReadAllText((Join-Path $root 'release/release.json')) | ConvertFrom-Json
$expectedServer = ([string]$profiles.$Profile.serverUrl).TrimEnd('/')
if ($parts.Count -ne 4 -or $parts[2] -ne '->' -or
    -not [string]::Equals($parts[3].TrimEnd('/'), $expectedServer, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The program server address differs from the $Profile release profile. Rebuild the program with -ServerUrl '$expectedServer'."
}
Write-Output "Authorization stamp verified: $identity"

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

# ---- packaging and signing ------------------------------------------------------------------
# The signature covers the exact bytes of the ZIP, so the tool that produces those bytes is the
# tool that signs them. The server never repacks: it verifies this signature and stores the file
# as it is. That is why a compromised download server cannot issue a release the loader accepts.
$localDotnet = Join-Path $root '.tooling/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$signer = Join-Path $root 'tools/Chispa.Sign/bin/Release/net10.0/Chispa.Sign.dll'
if (-not (Test-Path -LiteralPath $signer)) { throw 'The signing tool is not built. Run ./build.ps1 first (it builds tools/Chispa.Sign).' }
if (-not $SigningKey) { $SigningKey = Join-Path $root '.local/signing/release-private.pem' }
if (-not (Test-Path -LiteralPath $SigningKey)) {
    throw ("Signing key not found: {0}. Generate one on the release machine:`n    {1} {2} keygen --out .local/signing --id {3}`nthen paste the printed public key into client/ReleaseTrust.cs and rebuild the client." -f $SigningKey, $dotnet, $signer, $KeyId)
}
$releaseDir = Join-Path $root ".local/releases/$Version"
New-Item -ItemType Directory -Force $releaseDir | Out-Null
$package = Join-Path $releaseDir 'package.zip'
$signature = Join-Path $releaseDir 'signature.json'

& $dotnet $signer pack --source $stage --out $package --executable Wardogs.exe
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$signArguments = @('sign', '--package', $package, '--program', 'chispa', '--version', $Version,
    '--executable', 'Wardogs.exe', '--key', $SigningKey, '--key-id', $KeyId, '--out', $signature)
if ($PasswordFile) { $signArguments += @('--password-file', $PasswordFile) }
& $dotnet $signer @signArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if ($InstallTrustKey) {
    $publicKey = Join-Path (Split-Path -Parent $SigningKey) 'release-public.json'
    if (-not (Test-Path -LiteralPath $publicKey)) { throw "Public key file not found: $publicKey" }
    New-Item -ItemType Directory -Force (Join-Path $root '.local/server') | Out-Null
    Copy-Item -LiteralPath $publicKey -Destination (Join-Path $root '.local/server/release-trust.json') -Force
    Write-Output "Installed the trust key into .local/server/release-trust.json"
}

& (Join-Path $root 'run-local.ps1') program-add chispa Chispa Wardogs.exe
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $root 'run-local.ps1') publish chispa $Version $package $signature
exit $LASTEXITCODE
