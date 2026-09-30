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
$Commit = '7f69d99e25d395e301a2e0c143b3b40d3b071573'
$Version = '0.54.5-safepatch.5'
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
    # A package of the same version packed earlier would otherwise be served from NuGet's cache.
    $cached = Join-Path $env:USERPROFILE ".nuget\packages\$($project.ToLowerInvariant())\$Version"
    if (Test-Path $cached) { Remove-Item $cached -Recurse -Force }
}
Set-Content $stamp "$Commit $Version"
Write-Step "packed $Version into $Feed"
