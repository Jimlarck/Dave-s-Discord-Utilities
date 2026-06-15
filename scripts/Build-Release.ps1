[CmdletBinding()]
param(
    [string]$VintageStoryPath = $env:VINTAGE_STORY_PRE,
    [string]$Configuration = "Release",
    [string]$OutputZip = "",
    [switch]$Install,
    [string]$InstallTo = $env:VINTAGE_STORY_MODS
)

$ErrorActionPreference = "Stop"

function Resolve-VintageStoryPath {
    param([string]$RequestedPath)

    $candidates = @()
    if ($RequestedPath) { $candidates += $RequestedPath }
    if ($env:VINTAGE_STORY_PRE) { $candidates += $env:VINTAGE_STORY_PRE }
    if ($env:APPDATA) { $candidates += (Join-Path $env:APPDATA "Vintagestory") }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles "Vintagestory") }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} "Vintagestory") }

    foreach ($candidate in ($candidates | Where-Object { $_ } | Select-Object -Unique)) {
        $resolved = Resolve-Path -LiteralPath $candidate -ErrorAction SilentlyContinue
        if (-not $resolved) { continue }

        $path = $resolved.Path
        if ((Test-Path -LiteralPath (Join-Path $path "VintagestoryAPI.dll")) -and
            (Test-Path -LiteralPath (Join-Path $path "VintagestoryLib.dll"))) {
            return $path
        }
    }

    throw "Could not find Vintage Story assemblies. Pass -VintageStoryPath or set VINTAGE_STORY_PRE."
}

function Resolve-ModsFolder {
    param([string]$RequestedPath)

    if ($RequestedPath) { return $RequestedPath }
    if ($env:VINTAGE_STORY_MODS) { return $env:VINTAGE_STORY_MODS }
    if ($env:APPDATA) { return (Join-Path $env:APPDATA "VintagestoryData\Mods") }

    throw "Could not choose a Mods folder. Pass -InstallTo when using -Install."
}

$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$projectDir = Join-Path $repoRoot "DavesDiscordUtilities"
$solutionPath = Join-Path $repoRoot "DavesDiscordUtilities.slnx"
$modInfoPath = Join-Path $projectDir "modinfo.json"
$modInfo = Get-Content -LiteralPath $modInfoPath -Raw | ConvertFrom-Json

$resolvedVintageStoryPath = Resolve-VintageStoryPath $VintageStoryPath
$env:VINTAGE_STORY_PRE = $resolvedVintageStoryPath

Write-Host "Vintage Story references: $resolvedVintageStoryPath"
Write-Host "Building $solutionPath ($Configuration)"
& dotnet build $solutionPath -c $Configuration
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

$modOutput = Join-Path $projectDir "bin\$Configuration\Mods\mod"
foreach ($requiredFile in @("DavesDiscordUtilities.dll", "modinfo.json")) {
    $requiredPath = Join-Path $modOutput $requiredFile
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Build output is missing $requiredFile at $requiredPath."
    }
}

$releasesDir = Join-Path $repoRoot "Releases"
New-Item -ItemType Directory -Path $releasesDir -Force | Out-Null

if (-not $OutputZip) {
    $OutputZip = Join-Path $releasesDir "$($modInfo.modid)_$($modInfo.version).zip"
}

Write-Host "Packaging $OutputZip"
Compress-Archive -Path (Join-Path $modOutput "*") -DestinationPath $OutputZip -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$sharedDependencyPatterns = @(
    "Discord.Net*.dll",
    "Newtonsoft.Json.dll",
    "Microsoft.Bcl.AsyncInterfaces.dll",
    "System.Interactive.Async.dll",
    "System.Linq.Async.dll"
)

$zip = [IO.Compression.ZipFile]::OpenRead($OutputZip)
try {
    foreach ($entry in $zip.Entries) {
        $name = [IO.Path]::GetFileName($entry.FullName)
        foreach ($pattern in $sharedDependencyPatterns) {
            if ($name -like $pattern) {
                throw "Package includes shared dependency $name. Th3Essentials should provide this at runtime."
            }
        }
    }
}
finally {
    $zip.Dispose()
}

$installedTo = $null
if ($Install) {
    $modsFolder = Resolve-ModsFolder $InstallTo
    New-Item -ItemType Directory -Path $modsFolder -Force | Out-Null
    $installedTo = Join-Path $modsFolder (Split-Path -Path $OutputZip -Leaf)
    Copy-Item -LiteralPath $OutputZip -Destination $installedTo -Force
    Write-Host "Installed $installedTo"
}

[pscustomobject]@{
    VintageStoryPath = $resolvedVintageStoryPath
    OutputZip = (Resolve-Path -LiteralPath $OutputZip).Path
    InstalledTo = $installedTo
}
