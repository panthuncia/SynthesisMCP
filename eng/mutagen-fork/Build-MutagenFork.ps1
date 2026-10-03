<#
.SYNOPSIS
  Packs the Mutagen fork SafePatch builds against into a local NuGet feed: Mutagen.Bethesda.Kernel, .Core and .Skyrim
  from panthuncia/Mutagen at a pinned commit (branch safepatch-perf-experiment: the fixes and performance changes
  proposed upstream, merged). Mutagen.Bethesda.Json and Synthesis still come from nuget.org; they accept any Mutagen
  from 0.54.4 up.

  Idempotent: the feed is stamped with the commit it was packed from. Run it before the first build, and again when
  the pin changes; NuGet.config at the repository root reads the feed. Remove once the changes are in a Mutagen release.
#>
param(
    [string]$Feed = (Join-Path $env:LOCALAPPDATA 'SafePatch\mutagen-fork')
)

$ErrorActionPreference = 'Stop'

# ---- Pins. Changing any of these is a reviewed change; bump Version with the commit. ----
$Repo = 'https://github.com/panthuncia/Mutagen.git'
$Commit = 'aec0e1890ff92121802e8f208013d64904f315c7'
$Version = '0.54.5-safepatch.19'
$Projects = 'Mutagen.Bethesda.Kernel', 'Mutagen.Bethesda.Core', 'Mutagen.Bethesda.Skyrim'

function Write-Step([string]$message) { Write-Host "[mutagen-fork] $message" }
function Invoke-Checked([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') failed ($LASTEXITCODE)" }
}

$stamp = Join-Path $Feed 'packed-from.txt'
if ((Test-Path $stamp) -and (Get-Content $stamp -Raw).Trim() -eq "$Commit $Version") {
    Write-Step "feed is current ($Version from $($Commit.Substring(0, 7)))"
    return
}

# NuGet's cache is shared with Oculory, which packs the same pin. A version is packed from one commit only (the
# version is bumped with the commit), so packages already in the cache are reused rather than packed again: packing
# isn't byte-for-byte reproducible, and two packs of one version would disagree with each other's lock files.
$globalPackages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
$cachedPackages = foreach ($project in $Projects) {
    $id = $project.ToLowerInvariant()
    Join-Path $globalPackages "$id\$Version\$id.$Version.nupkg"
}
if (-not ($cachedPackages | Where-Object { -not (Test-Path $_) })) {
    Get-ChildItem $Feed -Filter '*.nupkg' -ErrorAction SilentlyContinue | Remove-Item -Force
    foreach ($package in $cachedPackages) { Copy-Item $package $Feed }
    Set-Content $stamp "$Commit $Version"
    Write-Step "reused $Version from NuGet's cache"
    return
}

$source = Join-Path $Feed 'src'
if (-not (Test-Path (Join-Path $source '.git'))) {
    Write-Step "cloning $Repo"
    Invoke-Checked git @('clone', '--quiet', '--no-checkout', $Repo, $source)
}
Invoke-Checked git @('-C', $source, 'fetch', '--quiet', 'origin', $Commit)
Invoke-Checked git @('-C', $source, 'checkout', '--quiet', '--force', $Commit)
if ((git -C $source rev-parse HEAD).Trim() -ne $Commit) { throw "Checked out the wrong commit." }

Get-ChildItem $Feed -Filter '*.nupkg' -ErrorAction SilentlyContinue | Remove-Item -Force
foreach ($project in $Projects) {
    Write-Step "packing $project $Version"
    Invoke-Checked dotnet @('pack', (Join-Path $source "$project\$project.csproj"), '-c', 'Release', '-o', $Feed,
        "-p:Version=$Version", '-p:DisableGitVersionTask=true', '-p:GeneratePackageOnBuild=false', '-nologo', '-v', 'q')
    # A partial set in the cache (an interrupted restore) would otherwise be served instead of the new packages.
    $cached = Join-Path $globalPackages "$($project.ToLowerInvariant())\$Version"
    if (Test-Path $cached) { Remove-Item $cached -Recurse -Force }
}
Set-Content $stamp "$Commit $Version"
Write-Step "packed $Version into $Feed"
