param(
    [string]$ServerUrl = 'http://127.0.0.1:5115'
)

$ErrorActionPreference = 'Stop'
$uri = [Uri]$ServerUrl
if (-not $uri.IsAbsoluteUri -or
    ($uri.Scheme -ne 'https' -and -not ($uri.Scheme -eq 'http' -and $uri.IsLoopback))) {
    throw 'ServerUrl must be HTTPS, except for a local loopback server.'
}
if ($uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment) {
    throw 'ServerUrl must be the server origin without a path, query or fragment.'
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
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

$output = Join-Path $root '.local/distribution/client'
New-Item -ItemType Directory -Force $output | Out-Null
Get-ChildItem -LiteralPath $output -File | Remove-Item -Force
$publishArgs = @(
    'publish', (Join-Path $root 'client/Chispa.Launcher.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:PublishTrimmed=false',
    '-p:EnableCompressionInSingleFile=true',
    '-p:DebugType=none',
    '-p:DebugSymbols=false',
    "-p:ChispaServerUrl=$ServerUrl",
    "-p:OutputPath=$root/.local/client-publish-bin/",
    '-o', $output
)
& $dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$files = @(Get-ChildItem -LiteralPath $output -File)
if ($files.Count -ne 1 -or $files[0].Name -ne 'Chispa.Launcher.exe') {
    throw "Expected exactly one EXE in $output; found: $($files.Name -join ', ')"
}
Write-Output "Single-file launcher: $($files[0].FullName)"
Write-Output "Server: $ServerUrl"
