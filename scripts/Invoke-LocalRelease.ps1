<#
.SYNOPSIS
    Runs the release pipeline against a local folder instead of GitHub.

.DESCRIPTION
    Same steps as .github/workflows/release.yml, in the same order, with "vpk download github" and
    "vpk upload github" swapped for their "local" equivalents. The folder given as -FeedPath ends up
    holding exactly what a published GitHub release would: releases.win.json, the .nupkg packages and
    the renamed portable zip. It is also a folder APPLOGGD_UPDATE_FEED accepts, so the app can be
    pointed straight at it.

    The repackaging step is not reimplemented here: it calls the same Repack-Portable.ps1 the
    workflow does, which is the whole point of this script existing.

    What it cannot cover, because it never talks to GitHub: "vpk upload github" (draft creation and
    asset upload) and the release-notes step.

.EXAMPLE
    ./scripts/Invoke-LocalRelease.ps1 -Version 1.0.1 -FeedPath C:\Users\me\Desktop\apploggd-feed
#>
[CmdletBinding()]
param(
    # Three components, like the tags the workflow accepts.
    [Parameter(Mandatory = $true)]
    [string] $Version,

    # Stands in for the GitHub releases of the repo.
    [Parameter(Mandatory = $true)]
    [string] $FeedPath,

    [string] $PackId = "Apploggd"
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version '$Version' is not of the form 1.2.3, which is the only shape a release can be built from."
}

$repoRoot    = Split-Path -Parent $PSScriptRoot
$publishDir  = Join-Path $repoRoot "publish"
$releasesDir = Join-Path $repoRoot "Releases"
$tagName     = "v$Version"

Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Force $FeedPath | Out-Null

    # The runner gets a clean checkout every time; this does not.
    foreach ($dir in @($publishDir, $releasesDir)) {
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
    }

    Write-Host "=== Publish (win-x64, self-contained) ===" -ForegroundColor Cyan
    # No PublishSingleFile: vpk packs a folder, and a single-file bundle would leave it nothing to
    # diff for delta updates.
    dotnet publish app/BackloggdMirror/BackloggdMirror.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:Version=$Version `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

    Write-Host "=== Download the previous release from the feed ===" -ForegroundColor Cyan
    # Deltas can only be built against the previous release, so it has to be on disk before pack.
    # Not fatal: the first release has nothing to download, and without it pack emits a full package.
    try {
        vpk download local --path $FeedPath --outputDir $releasesDir
        if ($LASTEXITCODE -ne 0) { Write-Warning "vpk download local returned $LASTEXITCODE; carrying on without a previous release." }
    }
    catch {
        Write-Warning "No previous release could be downloaded: $($_.Exception.Message)"
    }

    Write-Host "=== Pack ===" -ForegroundColor Cyan
    # --noInst: Apploggd ships portable only, so the installer is not built. The .nupkg and
    # releases.win.json still are: they are the feed the in-app updater reads.
    vpk pack `
        --packId $PackId `
        --packVersion $Version `
        --packDir $publishDir `
        --mainExe Apploggd.exe `
        --packTitle Apploggd `
        --packAuthors nik250dev `
        --icon app/BackloggdMirror/Assets/app-logo.ico `
        --outputDir $releasesDir `
        --noInst
    if ($LASTEXITCODE -ne 0) { throw "vpk pack failed." }

    Write-Host "=== Repackage the portable bundle ===" -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "Repack-Portable.ps1") -PackId $PackId -TagName $tagName -ReleasesDir $releasesDir

    Write-Host "=== Upload to the feed ===" -ForegroundColor Cyan
    vpk upload local --path $FeedPath --outputDir $releasesDir
    if ($LASTEXITCODE -ne 0) { throw "vpk upload local failed." }

    Write-Host ""
    Write-Host "Feed now at $FeedPath" -ForegroundColor Green
    Get-ChildItem $FeedPath | Select-Object Name, Length
}
finally {
    Pop-Location
}
