$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$localDotnet = Join-Path $root '.tooling/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $root '.tooling/home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:TEMP = Join-Path $root '.tooling/temp'
$env:TMP = $env:TEMP
$env:NUGET_PACKAGES = Join-Path $root '.tooling/nuget'
New-Item -ItemType Directory -Force $env:TEMP | Out-Null
$buildId = [guid]::NewGuid().ToString('N')
$serverOutput = Join-Path $root ".local/server-builds/$buildId"
New-Item -ItemType Directory -Force $serverOutput | Out-Null
& $dotnet build (Join-Path $root 'server/Chispa.Server.csproj') -c Release -o $serverOutput
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$pointer = Join-Path $root '.local/current-server-build.txt'
$temporary = $pointer + '.tmp'
[System.IO.File]::WriteAllText($temporary, $buildId)
Move-Item -LiteralPath $temporary -Destination $pointer -Force
& $dotnet build (Join-Path $root 'client/Chispa.Launcher.csproj') -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
# Packaging and signing happen in the release environment, but the tool is built here so that
# publish-current.ps1 always runs the version of the format this checkout signs and verifies.
& $dotnet build (Join-Path $root 'tools/Chispa.Sign/Chispa.Sign.csproj') -c Release
exit $LASTEXITCODE
