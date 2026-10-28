[CmdletBinding()]
param([string]$Version = '1.0.5', [string]$OutputDirectory = 'dist/server')
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid server version.' }
$root = $PSScriptRoot
. (Join-Path $root 'tools/BackupPaths.ps1')
$output = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory)) }
if ($output.TrimEnd('\','/') -eq $root.TrimEnd('\','/') -or $output.TrimEnd('\','/') -eq [IO.Path]::GetPathRoot($output).TrimEnd('\','/')) { throw 'Unsafe output directory.' }
Assert-PlainBackupPath $output
if (Test-Path -LiteralPath (Join-Path $output 'data')) { throw 'Output contains runtime data; use a separate directory.' }
$stage = Get-SafeBackupPath -Base $root -Relative '.local/server-publish-temp'
$dotnet = Join-Path $root '.tooling/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_HOME = Join-Path $root '.tooling/home'
$env:NUGET_PACKAGES = Join-Path $root '.tooling/nuget'
$env:NUGET_HTTP_CACHE_PATH = Join-Path $root '.tooling/nuget-http-cache'
$env:TEMP = Join-Path $root '.tooling/temp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP,$env:NUGET_HTTP_CACHE_PATH,$stage | Out-Null
& $dotnet publish (Join-Path $root 'server/Chispa.Server.csproj') -c Release -r win-x64 --self-contained true "-p:Version=$Version" -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o $stage
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }
$content = Get-SafeBackupPath -Base $root -Relative '.local/server-publish-temp/package'
New-Item -ItemType Directory -Force $content | Out-Null
Copy-Item -LiteralPath (Join-Path $stage 'Chispa.Server.exe') -Destination $content -Force
foreach ($name in @('START-SERVER.cmd','README.md','release-trust.json')) {
    Copy-Item -LiteralPath (Join-Path $root ('release/server/' + $name)) -Destination $content -Force
}
$trust = [IO.File]::ReadAllText((Join-Path $content 'release-trust.json')) | ConvertFrom-Json
foreach ($key in $trust.keys) {
    if ($key.publicKey -match 'PRIVATE' -or @($key.PSObject.Properties.Name | Where-Object { $_ -notin @('keyId','publicKey') }).Count) { throw 'Trust file must contain public keys only.' }
}
$zipName = "Chispa.Server-$Version-win-x64.zip"
if (@(Get-ChildItem -LiteralPath $content -Force).Count -ne 4 -or
    @(Get-ChildItem -LiteralPath $content -Directory).Count) { throw 'Unexpected file in server package staging.' }
$zip = Join-Path $stage $zipName
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($content, $zip)
New-Item -ItemType Directory -Force $output | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $content -File) { Copy-Item -LiteralPath $file.FullName -Destination $output -Force }
Copy-Item -LiteralPath $zip -Destination $output -Force
$hash = (Get-FileHash -LiteralPath (Join-Path $output $zipName) -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $zipName`n", [Text.Encoding]::ASCII)
Write-Output "Server package: $(Join-Path $output $zipName)"
Write-Output "SHA256: $hash"
