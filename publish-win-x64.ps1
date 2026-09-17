[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version,

    [string]$OutputRoot = (Join-Path $PSScriptRoot 'publish')
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$projectPath = Join-Path $projectRoot 'src\ZZZSwitch\ZZZSwitch.csproj'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = Get-Content -LiteralPath $projectPath -Raw
    $Version = [string]$project.Project.PropertyGroup.Version
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Invalid application version: $Version"
}

$publishDirectory = Join-Path $OutputRoot "ZZZSwitch-win-x64-v$Version"
if (Test-Path -LiteralPath $publishDirectory) { throw "Preserve existing release: $publishDirectory" }

dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:AssemblyVersion="$Version.0" `
    -p:FileVersion="$Version.0" `
    -p:InformationalVersion=$Version `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

& (Join-Path $projectRoot 'tools\publish-updater.ps1') -OutputDirectory $publishDirectory
Copy-Item (Join-Path $projectRoot 'README.md') (Join-Path $publishDirectory 'README.md') -Force

Write-Host "ZZZSwitch v$Version published: $publishDirectory"
