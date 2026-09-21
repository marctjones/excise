<#
.SYNOPSIS
    Build the Windows installer for excise (Inno Setup .exe).

.DESCRIPTION
    Runs dotnet publish with Native AOT for win-x64 (the same flags the macOS
    and Linux release packages use), then invokes Inno Setup to wrap the
    publish output in an installer. Works locally on Windows and inside the
    .github/workflows/release.yml windows-latest job.

    Native AOT needs the MSVC toolchain (Visual Studio "Desktop development
    with C++"), which hosted windows-latest runners have. It cannot
    cross-compile, so this only works on Windows.

    The publish directory is a native executable plus native libraries
    (libSkiaSharp.dll, libHarfBuzzSharp.dll, av_libglesv2.dll) and NO managed
    assemblies. release.yml proves that with scripts/check-aot-payload.py on the
    publish output, the portable zip and the installed tree.

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

# ── dotnet publish (Native AOT) ───────────────────────────────────────
# Native AOT and PublishSingleFile are mutually exclusive: AOT already emits one
# native executable, so the flags below are the ones build-macos-app.sh and
# build-deb.sh pass for their AOT lanes. EnableScripting and IncludeTessdataInApp
# are already off for a Release build; they are stated here so the recipe does
# not depend on a default.
$publishDir = Join-Path $RepoRoot "artifacts\publish\win-x64\gui"
$symbolsDir = Join-Path $RepoRoot "artifacts\publish\win-x64\symbols"
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
if (Test-Path $symbolsDir) { Remove-Item -Recurse -Force $symbolsDir }
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

$aotArgs = @(
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'true',
    '-p:PublishAot=true',
    '-p:PublishSingleFile=false',
    '-p:PublishReadyToRun=false',
    '-p:EnableScripting=false',
    '-p:IncludeTessdataInApp=false',
    '-p:DebugType=None', '-p:DebugSymbols=false',
    '-o', $publishDir
)

Write-Host "▶ dotnet publish Excise.App (Native AOT) → $publishDir"
& dotnet publish "$RepoRoot\Excise.App\Excise.App.csproj" @aotArgs | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish (GUI) failed"; exit 1 }

# Also publish the CLI, dropped alongside in the same install dir so
# the optional "Add to PATH" task makes `excise.exe` available in cmd.
Write-Host "▶ dotnet publish Excise.Cli (Native AOT) → $publishDir"
& dotnet publish "$RepoRoot\Excise.Cli\Excise.Cli.csproj" @aotArgs | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Error "dotnet publish (CLI) failed"; exit 1 }

# Native symbols stay out of the package. An AOT publish writes the linker's
# .pdb beside the executable (Excise.App.pdb measured 171 MB and libSkiaSharp.pdb
# 84 MB in windows.yml run 35562489812), and the installer below packs everything
# under $publishDir. DebugType=None asks for none; this moves any that appear.
New-Item -ItemType Directory -Force -Path $symbolsDir | Out-Null
foreach ($pdb in @(Get-ChildItem -Path $publishDir -Filter *.pdb -File -Recurse)) {
    Move-Item -Path $pdb.FullName -Destination $symbolsDir -Force
}
Write-Host "  symbols moved aside: $symbolsDir"

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
