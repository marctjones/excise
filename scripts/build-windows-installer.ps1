<#
.SYNOPSIS
    Build the Windows installer for excise (Inno Setup .exe).

.DESCRIPTION
    Runs dotnet publish in self-contained single-file mode for win-x64,
    then invokes Inno Setup to wrap the publish output in an installer.
    Works locally on Windows and inside the .github/workflows/release.yml
    windows-latest job.

.PARAMETER Version
    The version string baked into the installer ("2.1.0-rc8"). When
    omitted, derives from `git describe --tags --abbrev=0`.

.PARAMETER OutputDir
    Where to copy the final .exe. Defaults to dist\.

.EXAMPLE
    pwsh scripts/build-windows-installer.ps1

.EXAMPLE
    pwsh scripts/build-windows-installer.ps1 -Version 2.1.0-rc8 -OutputDir dist
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir = "dist"
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── Locate project root (repo root = parent of scripts\) ──────────────
$RepoRoot = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $RepoRoot

# ── Resolve version ───────────────────────────────────────────────────
if (-not $Version) {
    try {
        $tag = (& git describe --tags --abbrev=0 2>$null).Trim()
        if ($tag) { $Version = $tag.TrimStart('v') }
    } catch { }
    if (-not $Version) { $Version = "0.0.0" }
}
Write-Host "▶ Building Windows installer for excise $Version"

# ── Locate Inno Setup compiler ────────────────────────────────────────
# Order: PATH, Program Files, choco-installed location.
$iscc = $null
$candidates = @(
    "iscc.exe",
    "${env:ProgramFiles}\Inno Setup 6\iscc.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\iscc.exe",
    "C:\Program Files (x86)\Inno Setup 6\iscc.exe"
)
foreach ($c in $candidates) {
    $cmd = Get-Command $c -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source; break }
    if (Test-Path $c) { $iscc = $c; break }
}
if (-not $iscc) {
    Write-Error "Inno Setup compiler (iscc.exe) not found. Install via 'choco install innosetup' or download from https://jrsoftware.org/isdl.php"
    exit 1
}
Write-Host "  iscc        : $iscc"

# ── dotnet publish (self-contained single-file) ───────────────────────
$publishDir = Join-Path $RepoRoot "artifacts\publish\win-x64\gui"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

Write-Host "▶ dotnet publish Excise.App → $publishDir"
& dotnet publish "$RepoRoot\Excise.App\Excise.App.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $publishDir | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish (GUI) failed"; exit 1 }

# Also publish the CLI, dropped alongside in the same install dir so
# the optional "Add to PATH" task makes `excise.exe` available in cmd.
Write-Host "▶ dotnet publish Excise.Cli → $publishDir"
& dotnet publish "$RepoRoot\Excise.Cli\Excise.Cli.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o $publishDir | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish (CLI) failed"; exit 1 }

if (-not (Test-Path "$publishDir\Excise.App.exe")) { Write-Error "Excise.App.exe missing from publish output"; exit 1 }
if (-not (Test-Path "$publishDir\excise.exe"))      { Write-Error "excise.exe missing from publish output"; exit 1 }

# ── Run Inno Setup ────────────────────────────────────────────────────
$issPath = Join-Path $RepoRoot "packaging\windows\excise.iss"
$issOut  = Join-Path $RepoRoot "packaging\windows\Output"
if (Test-Path $issOut) { Remove-Item -Recurse -Force $issOut }

# Inno Setup version field can't have a `-` in legacy AppVerName
# matching, but we still want "2.1.0-rc8" displayed. iscc accepts the
# raw string in /DMyAppVersion — Inno's internal version comparison is
# alphanumeric so 2.1.0-rc8 < 2.1.0 < 2.1.0a etc. — close enough for an
# installer's "is this an upgrade" check.
& $iscc /Qp /DMyAppVersion="$Version" /DPublishDir="$publishDir" /DRepoRoot="$RepoRoot" $issPath | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Error "iscc failed"; exit 1 }

# ── Copy result to dist\ + checksum ───────────────────────────────────
$expected = "excise-$Version-win-x64-setup.exe"
$builtPath = Join-Path $issOut $expected
if (-not (Test-Path $builtPath)) { Write-Error "Inno Setup output not found at $builtPath"; exit 1 }

if (-not (Test-Path $OutputDir)) { New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null }
$destPath = Join-Path $OutputDir $expected
Copy-Item $builtPath $destPath -Force

$sha = (Get-FileHash $destPath -Algorithm SHA256).Hash.ToLower()
"$sha  $expected" | Out-File -Encoding ascii "$destPath.sha256"

$size = "{0:N1} MB" -f ((Get-Item $destPath).Length / 1MB)
Write-Host ""
Write-Host "✓ Built $destPath ($size)"
Write-Host "  sha256: $sha"
Write-Host ""
Write-Host "Install on a target machine with:"
Write-Host "  $expected"
Write-Host ""
Write-Host "Or silently:"
Write-Host "  $expected /VERYSILENT /SUPPRESSMSGBOXES /NORESTART"
