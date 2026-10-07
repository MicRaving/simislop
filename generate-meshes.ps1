<#
.SYNOPSIS
    Generates Simitone's 3D object meshes from a The Sims 1 install.

.DESCRIPTION
    One-command wrapper around Simitone.GenerateMeshes (see
    docs/MESH_GENERATION_RESEARCH.md). It builds the generator, runs it over the
    object FAR archives of a The Sims: Complete Collection install, and writes the
    meshes as flat "<iff>_iff_<drawgroup>.fsom" files - the names and folder shape
    the runtime mesh loader already looks for.

    Meshes are rebuilt with the image-based generator (DGRP3DLegacy): every sprite
    pixel becomes a vertex carrying its own UV, displaced along the view ray by its
    depth, so the colours match the artist's sprite exactly. Dynamic/animation parts
    parked away from the object are dropped, and the depth is smoothed before it is
    displaced so depth discontinuities do not throw spikes. Animated objects are
    merged into a single container.

    Only drawing groups with no hand-authored remesh are generated: if the
    community remesh pack is checked out, its aliases and priorities are used to
    skip anything it already covers, so nothing hand-made is ever overwritten.
    A second list - Content\MeshReplace\cleaned-meshes.txt, maintained by the
    manual cleanup pass (docs/MESH_MANUAL_CLEANUP.md) - protects meshes an agent
    has cleaned by hand the same way; those are neither generated nor overwritten.

    With -InstallOutput the meshes are synced into the pack the game actually
    ships - the client's committed Content\MeshReplace (and the built game's copy).
    Generated meshes are overwritten and stale ones removed; hand-authored
    community meshes are never touched. Regenerating without -InstallOutput only
    fills the scratch folder and changes nothing the game loads.

.PARAMETER SimsPath
    Path to the The Sims install (the folder containing GameData\Behavior.iff).
    Defaults to a short list of common locations.

.PARAMETER Output
    Where to write the generated .fsom files. Defaults to <repo>\GeneratedMeshes.

.PARAMETER Pack
    Remesh pack root (the folder with metadata.json and the Author\Group tree).
    Defaults to <repo>\FSO.Remeshes-main\remeshes when present; pass "" to disable.

.PARAMETER Filter
    Only process IFF files whose name contains this substring (e.g. "tables").

.PARAMETER FarFilter
    Only scan FAR archives whose file name contains this substring (e.g.
    "ExpansionPack7", or "Objects" for the base game). Useful for scoping a run.

.PARAMETER Max
    Stop after this many generated meshes. 0 (default) means no limit.

.PARAMETER Clean
    Delete existing .fsom files in the output folder before generating. Off by
    default so a stopped run can be resumed (existing files are kept and skipped).

.PARAMETER Quiet
    Do not print a line per generated mesh.

.PARAMETER InstallOutput
    Sync the generated meshes into the pack the game actually ships - the client's
    committed Content\MeshReplace plus the built game's copy. Generated meshes are
    overwritten and stale ones deleted; community meshes are never touched.

.PARAMETER Configuration
    Build configuration for the generator and the install target. Default Release.

.PARAMETER NoBuild
    Do not rebuild; use the generator already in bin\<config>.

.PARAMETER Diagnostics
    Pass per-stage vertex/triangle counts through to the generator's diagnostics.

.EXAMPLE
    .\generate-meshes.ps1 -SimsPath "D:\Program Files\Sims"

.EXAMPLE
    .\generate-meshes.ps1 -Filter Sculptures -Max 20 -Diagnostics

.EXAMPLE
    .\generate-meshes.ps1 -InstallOutput
#>
[CmdletBinding()]
param(
    [string]$SimsPath,
    [string]$Output,
    [string]$Pack,
    [string]$Filter,
    [string]$FarFilter,
    [int]$Max = 0,
    [switch]$Clean,
    [switch]$Quiet,
    [switch]$InstallOutput,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$NoBuild,
    [switch]$Diagnostics
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = $PSScriptRoot
$ToolProject = Join-Path $RepoRoot 'Client\Simitone\Simitone.GenerateMeshes\Simitone.GenerateMeshes.csproj'
$GameProject = Join-Path $RepoRoot 'Client\Simitone\Simitone.Windows\Simitone.Windows.csproj'

# The two target frameworks are single-sourced in Client/Simitone/Directory.Build.props.
# They used to be string literals here, duplicated from the two csproj files with nothing
# checking them, so a TFM bump broke this authoring tool with "Generator not found at ..."
# - an error that points at the wrong file and names a path that never existed. Asking
# MSBuild is supported from SDK 8 onwards, and it is the project file's own answer rather
# than a copy of it. Falls back to the pre-10.0 values only if the query is unavailable,
# so a very old SDK still produces a comprehensible error instead of an empty path.
function Get-ProjectProperty([string]$Project, [string]$Property) {
    $raw = (& dotnet msbuild $Project "-getProperty:$Property" -nologo 2>$null | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not $raw) { return $null }
    # -getProperty prints the bare value for a single property, and a JSON document when
    # several are asked for. Accept either so this does not silently start falling back
    # the day someone widens the query.
    if ($raw.StartsWith('{')) {
        $m = [regex]::Match($raw, '"' + [regex]::Escape($Property) + '"\s*:\s*"([^"]*)"')
        if ($m.Success) { return $m.Groups[1].Value }
        return $null
    }
    return $raw
}

$ToolTfm = Get-ProjectProperty -Project $ToolProject -Property 'TargetFramework'
if (-not $ToolTfm) {
    $ToolTfm = 'net10.0'
    Write-Warning "Could not read TargetFramework from $ToolProject; assuming '$ToolTfm'. If the generator is not found, check the TFM in the .csproj."
}
$GameTfm = Get-ProjectProperty -Project $GameProject -Property 'TargetFramework'
if (-not $GameTfm) {
    $GameTfm = 'net10.0-windows'
    Write-Warning "Could not read TargetFramework from $GameProject; assuming '$GameTfm'. If -InstallOutput finds no built game, check the TFM in the .csproj."
}

$ToolDll = Join-Path $RepoRoot "Client\Simitone\Simitone.GenerateMeshes\bin\$Configuration\$ToolTfm\Simitone.GenerateMeshes.dll"

function Write-Step([string]$Message) {
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Resolve-SimsPath {
    param([string]$Requested)

    if ($Requested) {
        if (Test-Path $Requested) {
            if (-not (Test-Path (Join-Path $Requested 'GameData\Behavior.iff'))) {
                Write-Warning "'$Requested' has no GameData\Behavior.iff; assuming it is a folder of FAR archives."
            }
            return (Resolve-Path $Requested).Path
        }
        throw "Path not found: '$Requested'."
    }

    # No D:\ entry: the audit flagged it as one developer's personal machine layout
    # hardcoded ahead of the portable derivations below, which made every other machine
    # pay a pointless stat on every run. Pass -SimsPath if the install is somewhere else.
    $candidates = @(
        'C:\Program Files\Sims',
        'C:\Program Files (x86)\Sims'
    )
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'Sims') }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Sims') }
    $candidates += (Join-Path $RepoRoot 'The Sims')

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path (Join-Path $candidate 'GameData\Behavior.iff'))) {
            return (Resolve-Path $candidate).Path
        }
    }

    throw "Could not find a The Sims install. Pass -SimsPath ""C:\Path\To\The Sims""."
}

# --- 0. prerequisites ------------------------------------------------------

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) { throw 'dotnet was not found on PATH. Install the .NET SDK (see global.json) and retry.' }
$dotnetVersion = (& dotnet --version).Trim()
Write-Host "dotnet $dotnetVersion"

# --- 1. resolve inputs -----------------------------------------------------

if (-not $Output) { $Output = Join-Path $RepoRoot 'GeneratedMeshes' }
$Output = [System.IO.Path]::GetFullPath($Output)

$SimsPath = Resolve-SimsPath -Requested $SimsPath
Write-Host "Sims install: $SimsPath"
Write-Host "Output:       $Output"

$packRoot = $null
if ($PSBoundParameters.ContainsKey('Pack')) {
    if ($Pack) { $packRoot = $Pack }
}
else {
    $defaultPack = Join-Path $RepoRoot 'FSO.Remeshes-main\remeshes'
    if (Test-Path (Join-Path $defaultPack 'metadata.json')) { $packRoot = $defaultPack }
}

if ($packRoot) {
    if (-not (Test-Path (Join-Path $packRoot 'metadata.json'))) {
        throw "No metadata.json under '$packRoot' - that is not a remesh pack. Pass -Pack """" to disable."
    }
    Write-Host "Remesh pack:  $packRoot (covered objects will be skipped)"
}
else {
    Write-Host 'Remesh pack:  none (every drawing group will be generated)'
}

# File names the community pack owns: never overwrite these when installing, so a
# hand-authored mesh can never be shadowed by a generated one.
$protectedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
if ($packRoot) {
    Get-ChildItem $packRoot -Recurse -Filter *.fsom -File -ErrorAction SilentlyContinue |
        ForEach-Object { [void]$protectedNames.Add($_.Name) }
}

# Hand-cleaned meshes (the manual cleanup pass, docs/MESH_MANUAL_CLEANUP.md) are protected the
# same way. The manifest lives beside the shipped meshes, one file name per line; a cleaned mesh
# must be listed there or the next regeneration would replace it.
$cleanedManifest = Join-Path $RepoRoot 'Client\Simitone\Simitone.Client\Content\MeshReplace\cleaned-meshes.txt'
if (Test-Path $cleanedManifest) {
    $listed = 0
    foreach ($line in Get-Content $cleanedManifest) {
        $name = ($line -split '#')[0].Trim()
        if (-not $name) { continue }
        if ($protectedNames.Add($name)) { $listed++ }
    }
    Write-Host "Cleaned-mesh list: $listed name(s) protected (cleaned-meshes.txt)"
}

# --- 2. build the generator ------------------------------------------------

if (-not (Test-Path $ToolProject)) { throw "Generator project not found: $ToolProject" }

if (-not $NoBuild) {
    Write-Step "Building Simitone.GenerateMeshes ($Configuration)"
    & dotnet build $ToolProject -c $Configuration -v minimal --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)." }
}

if (-not (Test-Path $ToolDll)) {
    throw "Generator not found at '$ToolDll'. Run without -NoBuild, or build it first."
}

# --- 3. generate -----------------------------------------------------------

if ($Clean) {
    if (Test-Path $Output) {
        Write-Host "Cleaning previous output in $Output (-Clean)"
        Get-ChildItem $Output -Filter *.fsom -File -ErrorAction SilentlyContinue | Remove-Item -Force
    }
}
if (-not (Test-Path $Output)) {
    New-Item -ItemType Directory -Path $Output -Force | Out-Null
}
elseif (-not $Clean) {
    $existing = @(Get-ChildItem $Output -Filter *.fsom -File -ErrorAction SilentlyContinue).Count
    if ($existing -gt 0) { Write-Host "Resuming: $existing existing .fsom file(s) will be kept and skipped." }
}

$toolArgs = @($ToolDll, $SimsPath, $Output, '--verify')
if ($packRoot) { $toolArgs += @('--pack', $packRoot) }
if (Test-Path $cleanedManifest) { $toolArgs += @('--skip-list', $cleanedManifest) }
if ($Filter) { $toolArgs += @('--filter', $Filter) }
if ($FarFilter) { $toolArgs += @('--far-filter', $FarFilter) }
if ($Max -gt 0) { $toolArgs += @('--max', $Max) }
if ($Quiet) { $toolArgs += '--quiet' }
if ($Diagnostics) { $toolArgs += '--verbose' }

Write-Step 'Generating meshes'
$started = Get-Date
& dotnet @toolArgs
$exit = $LASTEXITCODE
$elapsed = (Get-Date) - $started

if ($exit -ne 0) { throw "Mesh generation failed (exit $exit) after $([int]$elapsed.TotalSeconds)s." }

$files = @(Get-ChildItem $Output -Filter *.fsom -File -ErrorAction SilentlyContinue)
Write-Host ("Generated {0} mesh file(s) in {1}s" -f $files.Count, [int]$elapsed.TotalSeconds) -ForegroundColor Green

# --- 4. optionally install into the shipped pack and the built game ---------

if ($InstallOutput) {
    # The meshes the game loads live in the client's committed Content\MeshReplace
    # (copied into the build output by Simitone.Client.csproj, then overlaid with the
    # community pack by Simitone.PackageRemeshes). Regenerating into a scratch folder
    # is not enough - the old meshes stay in the shipped pack - so sync them:
    # overwrite generated meshes, delete stale ones, never touch a community mesh.
    $shippedDir = Join-Path $RepoRoot 'Client\Simitone\Simitone.Client\Content\MeshReplace'
    $binDir = Join-Path $RepoRoot "Client\Simitone\Simitone.Windows\bin\$Configuration\$GameTfm\Content\MeshReplace"

    $generatedNames = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) { [void]$generatedNames.Add($file.Name) }

    $targets = @($shippedDir)
    if (Test-Path (Split-Path $binDir -Parent)) { $targets += $binDir }

    foreach ($target in $targets) {
        New-Item -ItemType Directory -Path $target -Force | Out-Null
        Write-Step "Installing into $target"

        $removed = 0
        foreach ($existing in @(Get-ChildItem $target -Filter *.fsom -File -ErrorAction SilentlyContinue)) {
            if ($protectedNames.Contains($existing.Name)) { continue }  # community mesh
            if ($generatedNames.Contains($existing.Name)) { continue }  # about to be overwritten
            Remove-Item $existing.FullName -Force
            $removed++
        }

        $copied = 0; $kept = 0
        foreach ($file in $files) {
            if ($protectedNames.Contains($file.Name)) { $kept++; continue }
            Copy-Item $file.FullName (Join-Path $target $file.Name) -Force
            $copied++
        }

        Write-Host ("Installed {0} mesh file(s); removed {1} stale; kept {2} community file(s)." -f $copied, $removed, $kept) -ForegroundColor Green
    }
}

# --- 5. next steps ---------------------------------------------------------

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
if (-not $InstallOutput) {
    Write-Host "Meshes are in: $Output"
    Write-Host 'Nothing the game loads has changed yet. Re-run with -InstallOutput to sync them'
    Write-Host 'into the shipped Content\MeshReplace pack (or copy them there yourself).'
}
