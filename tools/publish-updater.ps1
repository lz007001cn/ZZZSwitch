[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\ZZZSwitch.Updater\ZZZSwitch.Updater.csproj'
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Updater publish failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $OutputDirectory 'ZZZSwitch.Updater.exe'))) { throw 'Missing standalone updater.' }
