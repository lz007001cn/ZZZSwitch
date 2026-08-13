[CmdletBinding()]
param(
    [string]$GameVersion = '3.1.0',
    [string]$BasePackageZip,
    [string]$BilibiliPackageZip
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$distRoot = Join-Path $projectRoot 'dist'
$BasePackageZip = if ([string]::IsNullOrWhiteSpace($BasePackageZip)) {
    Join-Path $distRoot "ZZZSwitch-Packages-$GameVersion.zip"
} else {
    [IO.Path]::GetFullPath($BasePackageZip)
}
$BilibiliPackageZip = if ([string]::IsNullOrWhiteSpace($BilibiliPackageZip)) {
    Join-Path $distRoot "ZZZSwitch-Bilibili-Packages-$GameVersion.zip"
} else {
    [IO.Path]::GetFullPath($BilibiliPackageZip)
}
$stagingRoot = Join-Path $projectRoot '_release-staging\unified-package'
$contentRoot = Join-Path $stagingRoot 'content'
$packageZip = Join-Path $stagingRoot "ZZZSwitch-Packages-$GameVersion.zip"
$hashFile = Join-Path $stagingRoot "SHA256SUMS-PACKAGES-$GameVersion.txt"
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

function Expand-SafeZip([string]$ArchivePath, [string]$DestinationRoot) {
    $destinationPrefix = [IO.Path]::GetFullPath($DestinationRoot).TrimEnd('\') + '\'
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName.Replace('\', '/').TrimStart('/')
            while ($relative.StartsWith('./', [StringComparison]::Ordinal)) {
                $relative = $relative.Substring(2)
            }
            if ([string]::IsNullOrWhiteSpace($relative)) {
                continue
            }

            $segments = $relative.Split('/', [StringSplitOptions]::RemoveEmptyEntries)
            if ($segments.Count -eq 0 -or $segments -contains '..' -or $segments -contains '.') {
                throw "Unsafe ZIP entry in $ArchivePath : $($entry.FullName)"
            }

            $destination = [IO.Path]::GetFullPath((Join-Path $DestinationRoot ([IO.Path]::Combine($segments))))
            if (-not $destination.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "ZIP entry escapes destination in $ArchivePath : $($entry.FullName)"
            }

            if ($entry.FullName.EndsWith('/') -or $entry.FullName.EndsWith('\')) {
                New-Item -ItemType Directory -Path $destination -Force | Out-Null
                continue
            }

            if (Test-Path -LiteralPath $destination) {
                throw "Package archives contain a duplicate file: $relative"
            }
            $parent = Split-Path $destination -Parent
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
            $input = $entry.Open()
            $output = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $input.CopyTo($output)
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

foreach ($archivePath in @($BasePackageZip, $BilibiliPackageZip)) {
    if (-not (Test-Path -LiteralPath $archivePath -PathType Leaf)) {
        throw "Required package archive not found: $archivePath"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
Reset-GeneratedDirectory $stagingRoot
New-Item -ItemType Directory -Path $contentRoot -Force | Out-Null

Expand-SafeZip $BasePackageZip $contentRoot
Expand-SafeZip $BilibiliPackageZip $contentRoot

foreach ($oldReadme in @('README-BILIBILI.txt', 'README-BILIBILI-PACKAGES.txt')) {
    $oldReadmePath = Join-Path $contentRoot $oldReadme
    if (Test-Path -LiteralPath $oldReadmePath -PathType Leaf) {
        Remove-Item -LiteralPath $oldReadmePath -Force
    }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs\README-Packages.txt') `
    -Destination (Join-Path $contentRoot 'README-Packages.txt') -Force

$packageVersionRoot = Join-Path $contentRoot ".zzzswitch\packages\$GameVersion"
foreach ($profile in @('global', 'cn_official', 'bilibili')) {
    $profileRoot = Join-Path $packageVersionRoot $profile
    if (-not (Test-Path -LiteralPath $profileRoot -PathType Container) -or
        -not (Get-ChildItem -LiteralPath $profileRoot -Recurse -File | Select-Object -First 1)) {
        throw "Unified package is missing profile content: $profile"
    }
}
if (-not (Test-Path -LiteralPath (Join-Path $packageVersionRoot 'version.ini') -PathType Leaf)) {
    throw "Unified package is missing version.ini for game version $GameVersion"
}

[IO.Compression.ZipFile]::CreateFromDirectory(
    $contentRoot,
    $packageZip,
    [IO.Compression.CompressionLevel]::Optimal,
    $false)
$hash = (Get-FileHash -LiteralPath $packageZip -Algorithm SHA256).Hash
[IO.File]::WriteAllText(
    $hashFile,
    "$hash  $(Split-Path $packageZip -Leaf)$([Environment]::NewLine)",
    $utf8NoBom)

$fileCount = (Get-ChildItem -LiteralPath $packageVersionRoot -Recurse -File).Count
Write-Host "Unified three-profile package staged: $packageZip"
Write-Host "Package files: $fileCount"
Write-Host "SHA-256: $hash"
