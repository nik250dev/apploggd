<#
.SYNOPSIS
    Reshapes the portable bundle vpk produces into the zip the download links point at.

.DESCRIPTION
    vpk always names the portable bundle "<PackId>-win-Portable.zip" and puts no containing folder
    inside it, so extracting it drops its four entries wherever the user happens to be. This gives it
    a versioned name and a containing folder, then points assets.win.json at the new name so the
    upload step still knows what to attach.

    Safe to reshape: the portable bundle is only ever downloaded by a person. The updater reads
    releases.win.json, which lists the .nupkg files and never mentions this one.

    Lives in a script rather than inline in the workflow so that a local test run
    (scripts/Invoke-LocalRelease.ps1) exercises the same code that release.yml does.

    Windows PowerShell 5.1 and PowerShell 7 both run this unchanged.
#>
[CmdletBinding()]
param(
    # Velopack package id, which is also the prefix of every file vpk writes.
    [Parameter(Mandatory = $true)]
    [string] $PackId,

    # Release tag, e.g. "v1.2.3". Only used to name the zip.
    [Parameter(Mandatory = $true)]
    [string] $TagName,

    [string] $ReleasesDir = "Releases",

    [string] $StageDir = "portable-stage"
)

$ErrorActionPreference = "Stop"

$newName  = "$PackId-$TagName-win-x64.zip"
$original = Join-Path $ReleasesDir "$PackId-win-Portable.zip"

if (-not (Test-Path $original)) {
    throw "No portable bundle at '$original'. Did vpk pack run, and was it run without --noPortable?"
}

# The runner starts clean, a local test run does not: leftovers from the previous version would be
# zipped straight into this one.
if (Test-Path $StageDir) { Remove-Item -Recurse -Force $StageDir }

New-Item -ItemType Directory -Force (Join-Path $StageDir $PackId) | Out-Null
Expand-Archive -Path $original -DestinationPath (Join-Path $StageDir $PackId) -Force
Remove-Item $original

# The entries are written by hand, with "/" forced as the separator. Neither Compress-Archive nor
# ZipFile.CreateFromDirectory can be trusted with this: on Windows PowerShell 5.1 (.NET Framework)
# both write "\", and unzip -- so macOS, Linux and several Windows extractors -- will not rebuild the
# folder tree from that, leaving a flat pile of files named "Apploggd\current\...". They only agree
# on "/" under PowerShell 7, which is what the runner has and a local run does not.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$target = Join-Path (Resolve-Path $ReleasesDir) $newName
if (Test-Path $target) { Remove-Item $target }

$sourceRoot = (Resolve-Path $StageDir).Path.TrimEnd('\')
$archive = [System.IO.Compression.ZipFile]::Open($target, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($item in Get-ChildItem -Path $sourceRoot -Recurse -Force) {
        $relative = $item.FullName.Substring($sourceRoot.Length).TrimStart('\', '/') -replace '\\', '/'

        if ($item.PSIsContainer) {
            # Only empty ones need an entry of their own; the rest come in with their files. Kept so
            # the extracted tree matches the packed one exactly.
            if (-not (Get-ChildItem -Path $item.FullName -Force)) {
                [void]$archive.CreateEntry("$relative/")
            }
            continue
        }

        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $item.FullName, $relative, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
}

Remove-Item -Recurse -Force $StageDir

$manifestPath = Join-Path $ReleasesDir "assets.win.json"
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

$patched = $false
foreach ($asset in $manifest) {
    if ($asset.Type -eq "Portable") {
        $asset.RelativeFileName = $newName
        $patched = $true
    }
}

if (-not $patched) {
    throw "assets.win.json has no Portable entry, so the renamed bundle would never be uploaded."
}

# @() so a manifest that ever holds a single entry still serialises as an array, and WriteAllText
# because Set-Content's UTF-8 means BOM or no BOM depending on the shell.
$json = ConvertTo-Json @($manifest) -Depth 5 -Compress
[System.IO.File]::WriteAllText((Resolve-Path $manifestPath), $json)

Write-Host "Portable bundle repackaged as $newName"
Get-ChildItem $ReleasesDir | Select-Object Name, Length
