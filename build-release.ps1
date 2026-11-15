# Rebuilds dist\BebirdScope.exe (single file, self-contained, no install needed to run).
# Usage:  powershell -ExecutionPolicy Bypass -File build-release.ps1
# It uses a private .NET SDK in .tools\ (downloaded automatically if missing). Nothing is installed system-wide.

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$tools = Join-Path $root '.tools'
$dotnetDir = Join-Path $tools 'dotnet'

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_ROOT = $dotnetDir
$env:NUGET_PACKAGES = Join-Path $tools 'nuget'
$env:PATH = "$dotnetDir;$env:PATH"

if (-not (Test-Path (Join-Path $dotnetDir 'dotnet.exe'))) {
    Write-Host 'Downloading .NET 8 SDK into .tools\ (one time, ~290 MB)...'
    New-Item -ItemType Directory -Force -Path $tools | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $installer = Join-Path $tools 'dotnet-install.ps1'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer
    & $installer -Channel 8.0 -InstallDir $dotnetDir -NoPath
}

Set-Location (Join-Path $root 'BebirdScope')
dotnet publish BebirdScope.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false `
    -nologo -v minimal -o (Join-Path $root 'dist')

Write-Host ''
Get-Item (Join-Path $root 'dist\BebirdScope.exe') | Select-Object FullName, LastWriteTime, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
