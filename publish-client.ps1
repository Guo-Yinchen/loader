<#
.SYNOPSIS
    Package the Chispa launcher as one self-contained EXE.

.DESCRIPTION
    production (default) reads release/release.json, requires a public HTTPS origin, builds into
    a staging directory, verifies the finished EXE by running it, then swaps it into the
    distribution folder. The previous artifact is kept next to it as "<output>.previous".

    local builds a loopback-only client into .local/distribution/client-local for local testing.

    Keep this file ASCII-only: Windows PowerShell 5.1 reads BOM-less scripts as ANSI, and a
    single non-ASCII character would break parsing for the double-click entry point.

.PARAMETER Profile
    production (default) or local.

.PARAMETER ServerUrl
    Override the configured address. It is still validated against the chosen profile, so it
    cannot be used to smuggle a loopback or plain-HTTP address into a production build.

.PARAMETER OutputDirectory
    Build target. Relative paths resolve against the repository root.
    Defaults: .local/distribution/client (production), .local/distribution/client-local (local).

.PARAMETER ConfigPath
    Release configuration file. Defaults to release/release.json.

.PARAMETER VerifyArtifact
    Do not build; verify an existing EXE against the resolved address and exit.

.EXAMPLE
    ./publish-client.ps1

.EXAMPLE
    ./publish-client.ps1 -Profile local

.EXAMPLE
    ./publish-client.ps1 -VerifyArtifact .local/distribution/client/Chispa.Launcher.exe
#>
[CmdletBinding()]
param(
    [ValidateSet('production', 'local')]
    [string]$Profile = 'production',
    [string]$ServerUrl,
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string]$Version,
    [string]$OutputDirectory,
    [string]$ConfigPath,
    [string]$VerifyArtifact
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$artifactName = 'Chispa.Launcher.exe'
. (Join-Path $root 'tools/BackupPaths.ps1')

function Fail {
    param([Parameter(Mandatory = $true)][string]$Message)
    throw $Message
}

function Resolve-PathFromRoot {
    param([Parameter(Mandatory = $true)][string]$Path)
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $root $Path))
}

function Get-ReleaseProfiles {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { Fail "Release configuration not found: $Path" }
    try {
        $json = Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json
    }
    catch {
        Fail "Release configuration is not valid JSON: $Path ($($_.Exception.Message))"
    }
    $profiles = @{}
    foreach ($name in @('production', 'local')) {
        $entry = $json.$name
        if ($null -eq $entry) { continue }
        $value = [string]$entry.serverUrl
        if ([string]::IsNullOrWhiteSpace($value)) {
            Fail "Release configuration is missing $name.serverUrl: $Path"
        }
        $profiles[$name] = $value
    }
    if ($profiles.Count -eq 0) { Fail "Release configuration has no usable profile: $Path" }
    return $profiles
}

function Resolve-ServerUrl {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$ProfileName
    )
    $text = $Value.Trim()
    if ([string]::IsNullOrEmpty($text)) { Fail 'Server URL is empty.' }

    $uri = $null
    if (-not [Uri]::TryCreate($text, [UriKind]::Absolute, [ref]$uri)) {
        # Give a dedicated message for an out-of-range port instead of a generic parse error.
        if ($text -match '^[a-zA-Z][a-zA-Z0-9+.-]*://[^/:]+:(\d+)') {
            $port = [int]$Matches[1]
            if ($port -lt 1 -or $port -gt 65535) {
                Fail "Invalid port: $($Matches[1]) (expected 1-65535). URL: $text"
            }
        }
        Fail "Invalid server URL: $text (example: https://example.com:24154)"
    }

    if ($uri.UserInfo) { Fail 'Server URL must not contain a username or password.' }
    if ($uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment) {
        Fail "Server URL must be the site origin without a path, query or fragment: $text"
    }
    if ($uri.Port -lt 1 -or $uri.Port -gt 65535) { Fail "Invalid port: $($uri.Port). URL: $text" }

    $hostName = $uri.DnsSafeHost.ToLowerInvariant()
    $loopback = $uri.IsLoopback -or (@('localhost', '127.0.0.1', '::1', '[::1]', '0.0.0.0', '[::]') -contains $hostName)

    if ($ProfileName -eq 'production') {
        if ($uri.Scheme -ne 'https') {
            Fail "Production builds require HTTPS: $text (the client rejects remote plain HTTP)."
        }
        if ($loopback) {
            Fail "Production builds cannot use a loopback address: $text. Use -Profile local for local testing."
        }
        if ($hostName.StartsWith('169.254.')) {
            Fail "Production builds cannot use a link-local address: $text"
        }
        if ($hostName -match '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)') {
            Write-Warning "Production address is a private LAN IP ($hostName): internet users will not reach it. Continue only if that is intended."
        }
    }
    else {
        if (-not $loopback) {
            Fail "Local builds require a loopback address: $text. Use -Profile production for a release build."
        }
        if ($uri.IsDefaultPort) {
            Fail "Local address must name its port, e.g. http://127.0.0.1:5115"
        }
    }

    return $uri.GetLeftPart([UriPartial]::Authority).TrimEnd('/')
}

function Get-ExpectedServerUrl {
    $profiles = Get-ReleaseProfiles -Path $configPath
    $value = $ServerUrl
    if ([string]::IsNullOrWhiteSpace($value)) {
        if (-not $profiles.ContainsKey($Profile)) {
            Fail "Release configuration has no '$Profile' profile (available: $($profiles.Keys -join ', ')): $configPath"
        }
        $value = $profiles[$Profile]
    }
    return Resolve-ServerUrl -Value $value -ProfileName $Profile
}

function Invoke-PackagedExe {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )
    # Start-Process -PassThru does not expose ExitCode reliably for a GUI-subsystem binary
    # under Windows PowerShell 5.1, so drive the process API directly and read both streams.
    $info = New-Object System.Diagnostics.ProcessStartInfo
    $info.FileName = $Exe
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Arguments = ($Arguments | ForEach-Object { '"' + ($_ -replace '"', '\"') + '"' }) -join ' '

    $process = [System.Diagnostics.Process]::Start($info)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(120000)) {
            try { $process.Kill() } catch { }
            Fail "Artifact check timed out: $Exe $($Arguments -join ' ')"
        }
        return [pscustomobject]@{
            Code   = $process.ExitCode
            Stdout = $stdout.Result
            Stderr = $stderr.Result
        }
    }
    finally {
        $process.Dispose()
    }
}

function Assert-ArtifactLayout {
    param([Parameter(Mandatory = $true)][string]$Directory)
    $files = @(Get-ChildItem -LiteralPath $Directory -File)
    $others = @(Get-ChildItem -LiteralPath $Directory -Directory)
    if ($files.Count -ne 1 -or $others.Count -ne 0 -or $files[0].Name -ne $artifactName) {
        Fail "Distribution folder must contain only $artifactName, found: $((@($files.Name) + @($others.Name)) -join ', ')"
    }
    return $files[0].FullName
}

function Test-ArtifactServerUrl {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string]$Expected
    )
    $report = [IO.Path]::GetTempFileName()
    try {
        $printed = Invoke-PackagedExe -Exe $Exe -Arguments @('--print-server-url', $report)
        if ($printed.Code -ne 0) { Fail "Artifact could not report its server URL (exit $($printed.Code)): $Exe" }
        $actual = ([IO.File]::ReadAllText($report)).Trim()
        if (-not [string]::Equals($actual, $Expected, [StringComparison]::OrdinalIgnoreCase)) {
            Fail "Artifact server URL mismatch: actual '$actual', expected '$Expected'. Check for a stale build cache."
        }

        $verified = Invoke-PackagedExe -Exe $Exe -Arguments @('--verify-server-url', $Expected)
        if ($verified.Code -ne 0) { Fail "Artifact self-check failed (exit $($verified.Code)): $Exe" }

        # Negative control: the same check must fail for a different address, otherwise the
        # verification would be meaningless.
        $mismatch = Invoke-PackagedExe -Exe $Exe -Arguments @('--verify-server-url', ($Expected + '.invalid'))
        if ($mismatch.Code -eq 0) { Fail "Artifact self-check is not trustworthy: a wrong address also returned success ($Exe)" }

        Write-Output "    Artifact self-check OK: embedded $actual (read by running the EXE)"
    }
    catch {
        $cause = $_.Exception.GetBaseException()
        if ($cause -is [System.ComponentModel.Win32Exception] -and $cause.NativeErrorCode -eq 740) {
            Test-AdministratorArtifact -Exe $Exe -Expected $Expected
            return
        }
        throw
    }
    finally {
        if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report -Force -ErrorAction SilentlyContinue }
    }
}

function Test-AdministratorArtifact {
    param([string]$Exe, [string]$Expected)
    if (-not $script:inspectorBuilt) {
        & $dotnet build (Join-Path $root 'tools/Chispa.Sign/Chispa.Sign.csproj') -c Release | Out-Host
        if ($LASTEXITCODE -ne 0) { Fail 'Could not build the launcher artifact inspector.' }
        $script:inspectorBuilt = $true
    }
    $inspector = Join-Path $root 'tools/Chispa.Sign/bin/Release/net10.0/Chispa.Sign.dll'
    $checked = & $dotnet $inspector inspect-launcher $Exe $Expected
    if ($LASTEXITCODE -ne 0) { Fail 'Administrator launcher artifact inspection failed.' }
    $details = $checked | ConvertFrom-Json
    if ($details.executionLevel -ne 'requireAdministrator' -or $details.serverUrl -ne $Expected) {
        Fail 'Administrator launcher manifest or embedded server URL did not match.'
    }
    $rejected = & $dotnet $inspector inspect-launcher $Exe ($Expected + '.invalid') 2>$null
    if ($LASTEXITCODE -eq 0) { Fail 'Artifact inspector accepted an incorrect server URL.' }
    $script:artifactVerifiedBy = 'Native requireAdministrator manifest and actual bundled assembly metadata inspection (no elevation)'
    Write-Output "    Artifact inspection OK: requireAdministrator; bundled URL $($details.serverUrl)"
}

function Get-StaticUrlHint {
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string]$Expected
    )
    # Informational only: a compressed single-file build usually stores the assembly
    # compressed, so a miss here proves nothing. The runtime check above is authoritative.
    try {
        $bytes = [IO.File]::ReadAllBytes($Exe)
        $text = [Text.Encoding]::GetEncoding(28591).GetString($bytes)
        $hostName = ([Uri]$Expected).DnsSafeHost
        if ($text.IndexOf($hostName, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return "found in plain text ($hostName)"
        }
        return 'not found in plain text (normal for a compressed single-file build; informational only)'
    }
    catch {
        return "could not scan bytes ($($_.Exception.Message)); informational only"
    }
}

function Write-Manifest {
    param(
        [Parameter(Mandatory = $true)][string]$Output,
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string]$Expected,
        [string]$PreviousExe
    )
    $file = Get-Item -LiteralPath $Exe
    $manifestPath = "$Output.manifest.json"
    $record = [ordered]@{
        profile    = $Profile
        serverUrl  = $Expected
        artifact   = $Exe
        sha256     = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
        size       = $file.Length
        builtAt    = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
        verifiedBy = if ($script:artifactVerifiedBy) { $script:artifactVerifiedBy } else { 'Chispa.Launcher.exe --verify-server-url (runs the artifact and reads the embedded address)' }
        previous   = $PreviousExe
    }
    $json = $record | ConvertTo-Json -Depth 4
    [IO.File]::WriteAllText($manifestPath, $json + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
    return $manifestPath
}

function Assert-SafeOutput {
    param([Parameter(Mandatory = $true)][string]$Path)
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if ($full -eq ([IO.Path]::GetFullPath($root).TrimEnd('\', '/'))) { Fail "Output directory cannot be the repository root: $full" }
    if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\', '/')) { Fail 'Output cannot be a drive root.' }
    Assert-PlainBackupPath $full
    if (Test-Path -LiteralPath $full -PathType Leaf) { Fail "Output path is a file, not a directory: $full" }
    if (Test-Path -LiteralPath $full) { Assert-ArtifactLayout $full | Out-Null }
    $backup = $full + '.previous'
    Assert-PlainBackupPath $backup
    if (Test-Path -LiteralPath $backup) { Assert-ArtifactLayout $backup | Out-Null }
    return $full
}

# ---------------------------------------------------------------- resolve inputs
$configPath = if ($ConfigPath) { Resolve-PathFromRoot $ConfigPath } else { Join-Path $root 'release/release.json' }
$expected = Get-ExpectedServerUrl

$localDotnet = Join-Path $root '.tooling/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $root '.tooling/home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:TEMP = Join-Path $root '.tooling/temp'
$env:TMP = $env:TEMP
$env:NUGET_PACKAGES = Join-Path $root '.tooling/nuget'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $root '.tooling/nuget-http-cache'
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
New-Item -ItemType Directory -Force $env:NUGET_HTTP_CACHE_PATH | Out-Null

if ($VerifyArtifact) {
    $artifact = Resolve-PathFromRoot $VerifyArtifact
    if (-not (Test-Path -LiteralPath $artifact -PathType Leaf)) { Fail "Artifact to verify not found: $artifact" }
    Write-Output "Artifact: $artifact"
    Write-Output "Expected URL: $expected (profile $Profile)"
    Test-ArtifactServerUrl -Exe $artifact -Expected $expected
    Write-Output "SHA-256: $((Get-FileHash -LiteralPath $artifact -Algorithm SHA256).Hash.ToLowerInvariant())"
    return
}

if ($OutputDirectory) { $output = Assert-SafeOutput (Resolve-PathFromRoot $OutputDirectory) }
elseif ($Profile -eq 'production') { $output = Assert-SafeOutput (Join-Path $root '.local/distribution/client') }
else { $output = Assert-SafeOutput (Join-Path $root '.local/distribution/client-local') }

# ---------------------------------------------------------------- build to staging
$parent = Split-Path -Parent $output
if ($parent) { New-Item -ItemType Directory -Force $parent | Out-Null }
$staging = "$output.staging-" + [Guid]::NewGuid().ToString('N')
New-Item -ItemType Directory -Force $staging | Out-Null

Write-Output "Profile: $Profile"
Write-Output "URL: $expected"
Write-Output "Staging: $staging"

try {
    $publishArgs = @(
        'publish', (Join-Path $root 'client/Chispa.Launcher.csproj'),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:PublishTrimmed=false',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=none',
        '-p:DebugSymbols=false',
        "-p:ChispaServerUrl=$expected",
        "-p:OutputPath=$root/.local/client-publish-bin/",
        '-o', $staging
    )
    if ($Version) { $publishArgs += "-p:Version=$Version" }
    & $dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed (exit $LASTEXITCODE)" }

    $stagedExe = Assert-ArtifactLayout -Directory $staging
    Write-Output "    Built: $([math]::Round((Get-Item -LiteralPath $stagedExe).Length / 1MB, 1)) MB"

    Test-ArtifactServerUrl -Exe $stagedExe -Expected $expected
    Write-Output "    Plain-text scan: $(Get-StaticUrlHint -Exe $stagedExe -Expected $expected)"
}
catch {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
    $kept = if (Test-Path -LiteralPath (Join-Path $output $artifactName)) { "previous artifact left untouched in $output" } else { "$output had no artifact before" }
    Write-Error "Publish failed: $($_.Exception.Message) ($kept)" -ErrorAction Continue
    exit 1
}

# ---------------------------------------------------------------- swap in, keep previous
$previous = "$output.previous"
$previousExe = $null
$hadPrevious = $false
try {
    Assert-SafeOutput $output | Out-Null
    if (Test-Path -LiteralPath $output) {
        if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Recurse -Force }
        Move-Item -LiteralPath $output -Destination $previous
        $hadPrevious = $true
        $previousExe = Join-Path $previous $artifactName
    }
    Move-Item -LiteralPath $staging -Destination $output
}
catch {
    if (-not (Test-Path -LiteralPath $output) -and (Test-Path -LiteralPath $previous)) {
        Move-Item -LiteralPath $previous -Destination $output
        Write-Error "Could not replace the distribution artifact; restored the previous version: $($_.Exception.Message)" -ErrorAction Continue
    }
    else {
        Write-Error "Could not replace the distribution artifact: $($_.Exception.Message)" -ErrorAction Continue
    }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue }
    exit 1
}

$finalExe = Assert-ArtifactLayout -Directory $output
Test-ArtifactServerUrl -Exe $finalExe -Expected $expected
$manifest = Write-Manifest -Output $output -Exe $finalExe -Expected $expected -PreviousExe $previousExe
$hash = (Get-FileHash -LiteralPath $finalExe -Algorithm SHA256).Hash.ToLowerInvariant()
$size = [math]::Round((Get-Item -LiteralPath $finalExe).Length / 1MB, 1)

Write-Output ''
Write-Output "Single-file artifact: $finalExe ($size MB)"
Write-Output "Embedded URL: $expected (confirmed by the artifact itself)"
Write-Output "SHA-256: $hash"
Write-Output "Manifest: $manifest"
if ($hadPrevious) { Write-Output "Previous build kept: $previousExe" }
Write-Output ''
if ($Profile -eq 'production') {
    Write-Output 'Production build: ship this single EXE; the folder must not contain anything else.'
}
else {
    Write-Output 'Local build: loopback address, for local testing only - do not ship it.'
}
