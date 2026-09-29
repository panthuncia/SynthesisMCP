<#
.SYNOPSIS
  Fetches the pinned third-party tools the end-to-end tests launch: Mod Organizer 2 (release
  binaries) and the Synthesis CLI (built from source at a pinned tag). Idempotent: each tool is
  stamped once installed. Every download is SHA-256 verified; the Synthesis clone is commit-verified.

  Run automatically by tests/SafePatch.EndToEnd.Tests before build. Writes <ToolsRoot>\tools.json.
#>
param(
    [Parameter(Mandatory = $true)][string]$ToolsRoot
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# ---- Pins. Changing any of these is a reviewed change. ----
$SevenZip = @{
    Url    = 'https://github.com/ip7z/7zip/releases/download/26.03/7zr.exe'
    Sha256 = 'ad4c82fadcbdf93c03b4fc440f300509c7d60c5c2f4d183e35d9d70d6957037d'
}
$Mo2 = @{
    Version = '2.5.2'
    Url     = 'https://github.com/ModOrganizer2/modorganizer/releases/download/v2.5.2/Mod.Organizer-2.5.2.7z'
    Sha256  = 'e6376efd87fd5ddd95aee959405e8f067afa526ea6c2c0c5aa03c5108bf4a815'
}
$Synthesis = @{
    Version = '0.36.6'
    Repo    = 'https://github.com/Mutagen-Modding/Synthesis.git'
    Commit  = 'd8150fb29d544216081e37e2704e56e3b48460f7'
}

function Write-Step([string]$message) { Write-Host "[e2e-tools] $message" }

function Get-VerifiedFile([string]$url, [string]$sha256, [string]$path) {
    if (Test-Path $path) {
        if ((Get-FileHash $path -Algorithm SHA256).Hash -eq $sha256) { return }
        Remove-Item $path -Force
    }
    Write-Step "downloading $url"
    $partial = "$path.partial"
    Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing
    $actual = (Get-FileHash $partial -Algorithm SHA256).Hash
    if ($actual -ne $sha256) {
        Remove-Item $partial -Force
        throw "SHA-256 mismatch for $url`n  expected $sha256`n  actual   $actual"
    }
    Move-Item $partial $path -Force
}

function Invoke-Checked([string]$exe, [string[]]$arguments, [string]$workingDirectory) {
    Push-Location $workingDirectory
    try {
        & $exe @arguments
        if ($LASTEXITCODE -ne 0) { throw "$exe $($arguments -join ' ') exited with $LASTEXITCODE" }
    }
    finally { Pop-Location }
}

New-Item -ItemType Directory -Force -Path (Join-Path $ToolsRoot 'downloads') | Out-Null
$downloads = Join-Path $ToolsRoot 'downloads'

# ---- 7zr: extracts the MO2 archive without requiring 7-Zip to be installed. ----
$sevenZr = Join-Path $downloads '7zr-26.03.exe'
Get-VerifiedFile $SevenZip.Url $SevenZip.Sha256 $sevenZr

# ---- Mod Organizer 2 ----
$mo2Dir = Join-Path $ToolsRoot "MO2-$($Mo2.Version)"
$mo2Stamp = Join-Path $mo2Dir '.installed'
if (-not (Test-Path $mo2Stamp)) {
    $archive = Join-Path $downloads "Mod.Organizer-$($Mo2.Version).7z"
    Get-VerifiedFile $Mo2.Url $Mo2.Sha256 $archive
    if (Test-Path $mo2Dir) { Remove-Item $mo2Dir -Recurse -Force }
    Write-Step "extracting Mod Organizer $($Mo2.Version)"
    Invoke-Checked $sevenZr @('x', '-y', "-o$mo2Dir", $archive) $ToolsRoot | Out-Null
    Set-Content $mo2Stamp $Mo2.Sha256
}

# ---- Synthesis CLI (the release ships only the GUI) ----
$sourceDir = Join-Path $ToolsRoot "Synthesis-src-$($Synthesis.Version)"
$cliDir = Join-Path $ToolsRoot "SynthesisCli-$($Synthesis.Version)"
$cliStamp = Join-Path $cliDir '.installed'
if (-not (Test-Path $cliStamp)) {
    if (-not (Test-Path (Join-Path $sourceDir '.git'))) {
        Write-Step "cloning Synthesis $($Synthesis.Version)"
        Invoke-Checked 'git' @('clone', '-q', '--depth', '1', '--branch', $Synthesis.Version, $Synthesis.Repo, $sourceDir) $ToolsRoot
    }
    $head = (& git -C $sourceDir rev-parse HEAD).Trim()
    if ($head -ne $Synthesis.Commit) { throw "Synthesis tag $($Synthesis.Version) is at $head, expected $($Synthesis.Commit)" }

    Write-Step "building Synthesis CLI $($Synthesis.Version)"
    # GitVersion cannot run on a shallow clone; it only stamps version numbers.
    Invoke-Checked 'dotnet' @('publish', 'Synthesis.Bethesda.CLI/Synthesis.Bethesda.CLI.csproj', '-c', 'Release',
        '-o', $cliDir, '-nologo', '-v', 'q', '-clp:ErrorsOnly', '-p:DisableGitVersionTask=true', '-p:NuGetAudit=false') $sourceDir
    Set-Content $cliStamp $Synthesis.Commit
}

$tools = [ordered]@{
    modOrganizer = (Join-Path $mo2Dir 'ModOrganizer.exe')
    synthesisCli = (Join-Path $cliDir 'Synthesis.Bethesda.CLI.exe')
}
foreach ($path in $tools.Values) { if (-not (Test-Path $path)) { throw "Expected tool missing: $path" } }
$tools | ConvertTo-Json | Set-Content (Join-Path $ToolsRoot 'tools.json') -Encoding UTF8
Write-Step "ready in $ToolsRoot"
