[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$appVersion = '1.2.2'
$publishDirectory = Join-Path $projectRoot "publish\ZZZSwitch-win-x64-v$appVersion"

dotnet publish (Join-Path $projectRoot 'src\ZZZSwitch\ZZZSwitch.csproj') `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Copy-Item (Join-Path $projectRoot 'README.md') (Join-Path $publishDirectory 'README.md') -Force

Write-Host "ZZZSwitch v$appVersion published: $publishDirectory"
