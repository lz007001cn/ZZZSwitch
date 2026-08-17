[CmdletBinding()]
param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '_verification\runnable-test')
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '_verification\runnable-test'))
$resolvedOutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$allowedPrefix = $allowedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar

if ($resolvedOutputRoot -ne $allowedRoot -and
    -not $resolvedOutputRoot.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Runnable test output must stay under $allowedRoot"
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

$projectPath = Join-Path $projectRoot 'src\ZZZSwitch\ZZZSwitch.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Invalid application version: $version"
}

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$informationalVersion = "${version}-test.$timestamp"
$currentDirectory = Join-Path $resolvedOutputRoot 'current'
$stagingDirectory = Join-Path $resolvedOutputRoot ("staging-" + [Guid]::NewGuid().ToString('N'))

Write-Host 'Validating solution...'
Invoke-DotNet @('build', 'ZZZSwitch.sln', '-c', 'Release', '--nologo')
Invoke-DotNet @('run', '--project', 'tests\ZZZSwitch.Core.Tests\ZZZSwitch.Core.Tests.csproj', '-c', 'Release')
Invoke-DotNet @('run', '--project', 'tests\ZZZSwitch.ManifestTool.Tests\ZZZSwitch.ManifestTool.Tests.csproj', '-c', 'Release')
Invoke-DotNet @('run', '--project', 'tests\ZZZSwitch.Ui.Smoke\ZZZSwitch.Ui.Smoke.csproj', '-c', 'Release')

New-Item -ItemType Directory -Path $resolvedOutputRoot -Force | Out-Null
try {
    Write-Host "Publishing runnable test $informationalVersion..."
    Invoke-DotNet @(
        'publish', $projectPath,
        '-c', 'Release',
        '-r', 'win-x64',
        '--self-contained', 'true',
        "-p:Version=$version",
        "-p:AssemblyVersion=$version.0",
        "-p:FileVersion=$version.0",
        "-p:InformationalVersion=$informationalVersion",
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:EnableCompressionInSingleFile=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        '-o', $stagingDirectory)

    Copy-Item (Join-Path $projectRoot 'README.md') (Join-Path $stagingDirectory 'README.md') -Force
    $commit = (& git -C $projectRoot rev-parse --short HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($commit)) {
        $commit = 'unavailable'
    }

    $workingChanges = @(& git -C $projectRoot status --porcelain 2>$null)
    $buildInfo = [ordered]@{
        product = 'ZZZSwitch'
        purpose = 'runnable-test'
        version = $version
        informationalVersion = $informationalVersion
        builtAt = (Get-Date).ToString('o')
        sourceRoot = $projectRoot
        gitCommit = $commit.Trim()
        workingTreeChangeCount = $workingChanges.Count
        validation = [ordered]@{
            solutionBuild = 'passed'
            coreTests = 'passed'
            manifestToolTests = 'passed'
            uiSmoke = 'passed'
        }
    }
    $buildInfo | ConvertTo-Json -Depth 4 | Set-Content `
        -LiteralPath (Join-Path $stagingDirectory 'BUILD-INFO.json') -Encoding utf8

    $executable = Join-Path $stagingDirectory 'ZZZSwitch.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Runnable executable was not produced: $executable"
    }

    $hash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    "$hash  ZZZSwitch.exe" | Set-Content `
        -LiteralPath (Join-Path $stagingDirectory 'SHA256SUMS-TEST.txt') -Encoding ascii

    if (Test-Path -LiteralPath $currentDirectory) {
        $resolvedCurrent = [IO.Path]::GetFullPath($currentDirectory)
        if (-not $resolvedCurrent.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to replace unsafe runnable test path: $resolvedCurrent"
        }

        Remove-Item -LiteralPath $resolvedCurrent -Recurse -Force
    }

    Move-Item -LiteralPath $stagingDirectory -Destination $currentDirectory
    Write-Host "Runnable test ready: $(Join-Path $currentDirectory 'ZZZSwitch.exe')"
    Write-Host "SHA-256: $hash"
}
finally {
    if (Test-Path -LiteralPath $stagingDirectory) {
        Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
    }
}
