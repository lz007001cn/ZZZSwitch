[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runRoot = Join-Path $repository ('_verification\online-update-e2e-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
if (Test-Path -LiteralPath $runRoot) { throw 'Use a fresh verification directory.' }
New-Item -ItemType Directory -Path $runRoot | Out-Null
function Invoke-CheckedDotnet([string[]]$Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($Arguments -join ' ')" }
}
Push-Location $repository
try {
    foreach ($item in @(@('source','1.3.7','1.3.7'), @('target','1.3.8','1.3.8-test'))) {
        $out = Join-Path $runRoot $item[0]
        Invoke-CheckedDotnet @('publish', 'src\ZZZSwitch\ZZZSwitch.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
            "-p:Version=$($item[1])", "-p:AssemblyVersion=$($item[1]).0", "-p:FileVersion=$($item[1]).0", "-p:InformationalVersion=$($item[2])",
            '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true', '-p:DebugType=None', '-p:DebugSymbols=false', '-o', $out)
        & (Join-Path $PSScriptRoot 'publish-updater.ps1') -OutputDirectory $out
    }
    Invoke-CheckedDotnet @('run', '--project', 'tests\ZZZSwitch.Update.Tests\ZZZSwitch.Update.Tests.csproj', '-c', 'Release', '--',
        '--e2e', (Join-Path $runRoot 'source'), (Join-Path $runRoot 'target'), (Join-Path $runRoot 'run'))
    Write-Host "Verified real updater and isolated WPF runtime restart: $runRoot"
} finally { Pop-Location }
