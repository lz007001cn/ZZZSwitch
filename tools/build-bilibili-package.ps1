[CmdletBinding()]
param(
    [string]$LauncherRoot = 'E:\BaiduNetdiskDownload\BLPlatform64',
    [string]$SdkPath,
    [string]$GameVersion = '3.1.0'
)

$ErrorActionPreference = 'Stop'
$SdkPath = if ([string]::IsNullOrWhiteSpace($SdkPath)) {
    Join-Path $PSScriptRoot 'bilibili-package-assets\PCGameSDK-5.0.4.0.dll'
} else {
    $SdkPath
}
$projectRoot = Split-Path $PSScriptRoot -Parent
$stableConfigRoot = Join-Path $projectRoot 'config'
$stagingRoot = Join-Path $projectRoot '_release-staging\bilibili-package'
$packageArchiveRoot = Join-Path $stagingRoot 'package-archive'
$packageDirectory = Join-Path $packageArchiveRoot ".zzzswitch\packages\$GameVersion\bilibili"
$packageZip = Join-Path $stagingRoot "ZZZSwitch-Bilibili-Packages-$GameVersion.zip"
$hashFile = Join-Path $stagingRoot "SHA256SUMS-BILIBILI-$GameVersion.txt"
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Assert-PathUnderProject([string]$Path) {
    $project = [IO.Path]::GetFullPath($projectRoot).TrimEnd('\') + '\'
    $candidate = [IO.Path]::GetFullPath($Path).TrimEnd('\') + '\'
    if (-not $candidate.StartsWith($project, [StringComparison]::OrdinalIgnoreCase) -or $candidate -eq $project) {
        throw "Refusing generated-directory operation outside project: $Path"
    }
}

function Reset-GeneratedDirectory([string]$Path) {
    Assert-PathUnderProject $Path
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Write-Json([string]$Path, $Value) {
    $json = $Value | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($Path, $json + [Environment]::NewLine, $utf8NoBom)
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function New-ReplaceEntry([string]$Source, [string]$Target, [string]$PhysicalPath, [string]$SourcePackage = $null) {
    $entry = [ordered]@{
        source = $Source
        target = $Target
    }
    if ($SourcePackage) {
        $entry.sourcePackageDirectoryName = $SourcePackage
    }
    $entry.length = [long](Get-Item -LiteralPath $PhysicalPath).Length
    $entry.sha256 = Get-Sha256 $PhysicalPath
    [pscustomobject]$entry
}

function Copy-ReplaceEntry($Entry, [string]$SourcePackage = $null) {
    $copy = [ordered]@{
        source = [string]$Entry.source
        target = [string]$Entry.target
    }
    if ($SourcePackage) {
        $copy.sourcePackageDirectoryName = $SourcePackage
    }
    $copy.length = [long]$Entry.length
    $copy.sha256 = [string]$Entry.sha256
    [pscustomobject]$copy
}

function New-IniPatch([string]$Cps, [string]$Channel, [string]$SubChannel, [string]$Uapc) {
    [pscustomobject][ordered]@{
        target = 'config.ini'
        section = 'General'
        values = [ordered]@{
            cps = $Cps
            channel = $Channel
            sub_channel = $SubChannel
            uapc = $Uapc
        }
    }
}

function New-Transition(
    [string]$Source,
    [string]$Target,
    [object[]]$Replace,
    [object[]]$IniPatches,
    [object[]]$OptionalDelete,
    [string]$Notes) {
    [pscustomobject][ordered]@{
        sourceProfile = $Source
        targetProfile = $Target
        gameVersion = $GameVersion
        enabled = $true
        expectedReplaceCount = [int]($Replace.Count + $IniPatches.Count)
        expectedDeleteCount = 0
        replaceFiles = $Replace
        iniPatches = $IniPatches
        deleteFiles = @()
        optionalDeleteFiles = $OptionalDelete
        notes = $Notes
    }
}

if (-not (Test-Path -LiteralPath $LauncherRoot -PathType Container)) {
    throw "Bilibili launcher directory not found: $LauncherRoot"
}
if (-not (Test-Path -LiteralPath $SdkPath -PathType Leaf)) {
    throw "PCGameSDK not found: $SdkPath"
}

$platformExe = Join-Path $LauncherRoot 'PCGamePlatform.exe'
$protectionExe = Join-Path $LauncherRoot 'game_security_protection.exe'
foreach ($signedFile in @($platformExe, $protectionExe, $SdkPath)) {
    if (-not (Test-Path -LiteralPath $signedFile -PathType Leaf)) {
        throw "Required Bilibili file not found: $signedFile"
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $signedFile
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notlike '*SERIALNUMBER=91310110779301025N*') {
        throw "Bilibili signature validation failed: $signedFile"
    }
    $version = (Get-Item -LiteralPath $signedFile).VersionInfo.FileVersion
    if ($version -notlike '5.0.4.0*') {
        throw "This package manifest was analyzed for Bilibili SDK 5.0.4.0, found $version in $signedFile"
    }
}

New-Item -ItemType Directory -Path (Join-Path $stableConfigRoot 'profiles') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $stableConfigRoot 'transitions') -Force | Out-Null
Reset-GeneratedDirectory $stagingRoot
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

# The B package contains only the overlay. CN core files are read from the existing
# cn_official package through sourcePackageDirectoryName and are never duplicated.
Copy-Item -LiteralPath $LauncherRoot -Destination (Join-Path $packageDirectory 'BLPlatform64') -Recurse -Force
Copy-Item -LiteralPath $SdkPath -Destination (Join-Path $packageDirectory 'PCGameSDK.dll') -Force
$sdkTarget = Join-Path $packageDirectory 'PCGameSDK.dll'
$sdkMd5 = (Get-FileHash -LiteralPath $sdkTarget -Algorithm MD5).Hash.ToLowerInvariant()
$sdkRecord = [ordered]@{
    fileSize = [long](Get-Item -LiteralPath $sdkTarget).Length
    remoteName = 'ZenlessZoneZero_Data/Plugins/x86_64/PCGameSDK.dll'
    md5 = $sdkMd5
} | ConvertTo-Json -Compress
[IO.File]::WriteAllText((Join-Path $packageDirectory 'sdk_pkg_version'), $sdkRecord, $utf8NoBom)

$globalProfile = Get-Content -Raw -Encoding UTF8 (Join-Path $stableConfigRoot 'profiles\global.json') | ConvertFrom-Json
$cnProfile = Get-Content -Raw -Encoding UTF8 (Join-Path $stableConfigRoot 'profiles\cn_official.json') | ConvertFrom-Json
$globalToCn = Get-Content -Raw -Encoding UTF8 (Join-Path $stableConfigRoot 'transitions\global-to-cn-official.json') | ConvertFrom-Json
$cnToGlobal = Get-Content -Raw -Encoding UTF8 (Join-Path $stableConfigRoot 'transitions\cn-official-to-global.json') | ConvertFrom-Json

$sdkEntry = New-ReplaceEntry 'PCGameSDK.dll' 'ZenlessZoneZero_Data\Plugins\x86_64\PCGameSDK.dll' $sdkTarget
$sdkVersionEntry = New-ReplaceEntry 'sdk_pkg_version' 'sdk_pkg_version' (Join-Path $packageDirectory 'sdk_pkg_version')
$launcherEntries = @(
    Get-ChildItem -LiteralPath (Join-Path $packageDirectory 'BLPlatform64') -Recurse -File |
        Sort-Object FullName |
        ForEach-Object {
            $relative = $_.FullName.Substring((Join-Path $packageDirectory 'BLPlatform64').Length + 1)
            New-ReplaceEntry ('BLPlatform64\' + $relative) ('ZenlessZoneZero_Data\Plugins\x86_64\BLPlatform64\' + $relative) $_.FullName
        }
)
$overlayEntries = @($sdkEntry, $sdkVersionEntry) + $launcherEntries
$optionalDelete = @($overlayEntries | ForEach-Object { [pscustomobject][ordered]@{ target = $_.target } })

$bProfile = [pscustomobject][ordered]@{
    id = 'bilibili'
    displayName = "$([char]0x7EDD)$([char]0x533A)$([char]0x96F6)B$([char]0x670D)"
    packageDirectoryName = 'bilibili'
    enabled = $true
    keyFiles = @($cnProfile.keyFiles) + @(
        [pscustomobject][ordered]@{
            path = 'ZenlessZoneZero_Data\Plugins\x86_64\PCGameSDK.dll'
            length = [long]$sdkEntry.length
            sha256 = [string]$sdkEntry.sha256
        },
        [pscustomobject][ordered]@{
            path = 'ZenlessZoneZero_Data\Plugins\x86_64\BLPlatform64\PCGamePlatform.exe'
            length = [long](Get-Item -LiteralPath (Join-Path $packageDirectory 'BLPlatform64\PCGamePlatform.exe')).Length
            sha256 = (Get-Sha256 (Join-Path $packageDirectory 'BLPlatform64\PCGamePlatform.exe'))
        },
        [pscustomobject][ordered]@{
            path = 'ZenlessZoneZero_Data\Plugins\x86_64\BLPlatform64\game_security_protection.exe'
            length = [long](Get-Item -LiteralPath (Join-Path $packageDirectory 'BLPlatform64\game_security_protection.exe')).Length
            sha256 = (Get-Sha256 (Join-Path $packageDirectory 'BLPlatform64\game_security_protection.exe'))
        }
    )
}
Write-Json (Join-Path $stableConfigRoot 'profiles\bilibili.json') $bProfile

$bPatch = @(New-IniPatch 'zzz_bilibili_pc' '14' '0' '{"hyp":{"uapc":""},"nap_cn":{"uapc":""}}')
$cnPatch = @(New-IniPatch 'zzz_mktbackup2_pc' '1' '2' '{"hyp":{"uapc":""},"nap_cn":{"uapc":""}}')
$globalPatch = @(New-IniPatch 'zzz_oversea_gw_pc' '1' '0' '{"hyp":{"uapc":""},"nap_global":{"uapc":""}}')
$cnCoreFromSharedPackage = @($globalToCn.replaceFiles | ForEach-Object { Copy-ReplaceEntry $_ 'cn_official' })
$globalCore = @($cnToGlobal.replaceFiles | ForEach-Object { Copy-ReplaceEntry $_ })

Write-Json (Join-Path $stableConfigRoot 'transitions\cn-official-to-bilibili.json') (
    New-Transition 'cn_official' 'bilibili' $overlayEntries $bPatch @() 'Keep CN core; add the signed Bilibili SDK/login window and patch config.ini keys.')
Write-Json (Join-Path $stableConfigRoot 'transitions\global-to-bilibili.json') (
    New-Transition 'global' 'bilibili' ($cnCoreFromSharedPackage + $overlayEntries) $bPatch @() 'Reuse cn_official package files, then add the Bilibili SDK/login window.')
Write-Json (Join-Path $stableConfigRoot 'transitions\bilibili-to-cn-official.json') (
    New-Transition 'bilibili' 'cn_official' @() $cnPatch $optionalDelete 'Keep CN core, remove Bilibili overlay files when present, and restore CN config values.')
Write-Json (Join-Path $stableConfigRoot 'transitions\bilibili-to-global.json') (
    New-Transition 'bilibili' 'global' $globalCore $globalPatch $optionalDelete 'Restore global core, remove Bilibili overlay files when present, and restore global config values.')

$packageReadme = @"
ZZZSwitch Bilibili package $GameVersion

Extract beside the game directory so the files are located at:
.zzzswitch\packages\$GameVersion\bilibili

This archive contains only the Bilibili overlay. The stable global and cn_official packages are also required.
Do not copy BLPlatform64 into the game root manually; the app installs it after creating a transaction backup.
"@
[IO.File]::WriteAllText((Join-Path $packageArchiveRoot 'README-BILIBILI.txt'), $packageReadme, $utf8NoBom)

dotnet run --project (Join-Path $projectRoot 'tests\ZZZSwitch.Core.Tests\ZZZSwitch.Core.Tests.csproj') -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Core test suite failed with exit code $LASTEXITCODE"
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($packageArchiveRoot, $packageZip, [IO.Compression.CompressionLevel]::Optimal, $false)

$hashLine = "$(Get-Sha256 $packageZip)  $(Split-Path $packageZip -Leaf)"
[IO.File]::WriteAllText($hashFile, $hashLine + [Environment]::NewLine, $utf8NoBom)
Write-Host "Bilibili package staged: $packageZip"
Write-Host "SHA-256 file: $hashFile"
