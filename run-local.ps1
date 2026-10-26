$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$localDotnet = Join-Path $root '.tooling/dotnet/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { (Get-Command dotnet -ErrorAction Stop).Source }
$env:CHISPA_SERVER_DATA = Join-Path $root '.local/server'
$env:CHISPA_SERVER_URL = 'http://127.0.0.1:5115'
$env:DOTNET_CLI_HOME = Join-Path $root '.tooling/home'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$pointer = Join-Path $root '.local/current-server-build.txt'
$serverDll = if (Test-Path -LiteralPath $pointer) {
    $buildId = [System.IO.File]::ReadAllText($pointer).Trim()
    if ($buildId -notmatch '^[0-9a-f]{32}$') { throw 'Invalid server build pointer.' }
    Join-Path $root ".local/server-builds/$buildId/Chispa.Server.dll"
} else {
    Join-Path $root 'server/bin/Release/net10.0/Chispa.Server.dll'
}
& $dotnet $serverDll @args
exit $LASTEXITCODE
