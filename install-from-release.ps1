#Requires -Version 5.1
<#
.SYNOPSIS
    Download and install Excise.App from GitHub releases.

.DESCRIPTION
    Downloads the latest release of Excise.App from GitHub,
    extracts it, and creates shortcuts. No .NET SDK required.

.PARAMETER Version
    Specific version to install (e.g., "v1.0.0"). Default: latest.

.EXAMPLE
    .\install-from-release.ps1

.EXAMPLE
    .\install-from-release.ps1 -Version "v1.0.0"
#>

param(
    [string]$Version = "latest"
)

$ErrorActionPreference = "Stop"

$AppName = "Excise.App"
$AppDisplayName = "PDF Editor"
$RepoOwner = "marctjones"
$RepoName = "excise"
$InstallDir = Join-Path $env:LOCALAPPDATA $AppName
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$TempDir = Join-Path $env:TEMP "Excise.App-Install"

Write-Host "=== Excise.App Installation from GitHub ===" -ForegroundColor Cyan
Write-Host ""

# Create temp directory
if (Test-Path $TempDir) {
    Remove-Item $TempDir -Recurse -Force
}
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

# Get release info
Write-Host "Step 1: Finding release..." -ForegroundColor Yellow

if ($Version -eq "latest") {
    $ApiUrl = "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest"
} else {
    $ApiUrl = "https://api.github.com/repos/$RepoOwner/$RepoName/releases/tags/$Version"
}

try {
    $Release = Invoke-RestMethod -Uri $ApiUrl -Headers @{ "User-Agent" = "Excise.App-Installer" }
    $TagName = $Release.tag_name
    Write-Host "  Found release: $TagName" -ForegroundColor Green
} catch {
    Write-Host "Error: Could not find release. Check https://github.com/$RepoOwner/$RepoName/releases" -ForegroundColor Red
    exit 1
}

# Find Windows asset
$WindowsAsset = $Release.assets | Where-Object { $_.name -like "*windows-x64*.zip" } | Select-Object -First 1

if (-not $WindowsAsset) {
    Write-Host "Error: No Windows release found for $TagName" -ForegroundColor Red
    Write-Host "Available assets:" -ForegroundColor Yellow
    $Release.assets | ForEach-Object { Write-Host "  - $($_.name)" }
    exit 1
}

$DownloadUrl = $WindowsAsset.browser_download_url
$ZipFile = Join-Path $TempDir $WindowsAsset.name

Write-Host "  Asset: $($WindowsAsset.name)" -ForegroundColor Gray

# Download
Write-Host ""
Write-Host "Step 2: Downloading ($([math]::Round($WindowsAsset.size / 1MB, 1)) MB)..." -ForegroundColor Yellow

try {
    $ProgressPreference = 'SilentlyContinue'  # Faster download
    Invoke-WebRequest -Uri $DownloadUrl -OutFile $ZipFile -UseBasicParsing
    $ProgressPreference = 'Continue'
    Write-Host "  Downloaded to: $ZipFile" -ForegroundColor Green
} catch {
    Write-Host "Error: Download failed - $_" -ForegroundColor Red
    exit 1
}

# Extract
Write-Host ""
Write-Host "Step 3: Extracting..." -ForegroundColor Yellow

# Remove old installation
if (Test-Path $InstallDir) {
    Write-Host "  Removing previous installation..." -ForegroundColor Gray
    Remove-Item $InstallDir -Recurse -Force
}

Expand-Archive -Path $ZipFile -DestinationPath $TempDir -Force

# Find extracted folder and move contents
$ExtractedFolder = Get-ChildItem -Path $TempDir -Directory | Where-Object { $_.Name -like "Excise.App-*" } | Select-Object -First 1
if ($ExtractedFolder) {
    Move-Item -Path $ExtractedFolder.FullName -Destination $InstallDir -Force
} else {
    # Files might be directly in temp dir
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Get-ChildItem -Path $TempDir -Exclude "*.zip" | Move-Item -Destination $InstallDir -Force
}

Write-Host "  Installed to: $InstallDir" -ForegroundColor Green

# Verify executable exists
$ExePath = Join-Path $InstallDir "Excise.App.exe"
if (-not (Test-Path $ExePath)) {
    # Try to find it in subdirectory
    $ExePath = Get-ChildItem -Path $InstallDir -Recurse -Filter "Excise.App.exe" | Select-Object -First 1 -ExpandProperty FullName
}

if (-not $ExePath -or -not (Test-Path $ExePath)) {
    Write-Host "Error: Could not find Excise.App.exe after extraction" -ForegroundColor Red
    exit 1
}

# Create shortcuts
Write-Host ""
Write-Host "Step 4: Creating shortcuts..." -ForegroundColor Yellow

$WshShell = New-Object -ComObject WScript.Shell

# Start Menu shortcut
$StartMenuShortcut = Join-Path $StartMenuDir "$AppDisplayName.lnk"
$Shortcut = $WshShell.CreateShortcut($StartMenuShortcut)
$Shortcut.TargetPath = $ExePath
$Shortcut.WorkingDirectory = Split-Path $ExePath
$Shortcut.Description = "PDF viewer and editor with TRUE redaction capabilities"
$Shortcut.Save()
Write-Host "  Start Menu: $StartMenuShortcut" -ForegroundColor Green

# Desktop shortcut
$DesktopShortcut = Join-Path ([Environment]::GetFolderPath("Desktop")) "$AppDisplayName.lnk"
$Shortcut2 = $WshShell.CreateShortcut($DesktopShortcut)
$Shortcut2.TargetPath = $ExePath
$Shortcut2.WorkingDirectory = Split-Path $ExePath
$Shortcut2.Description = "PDF viewer and editor with TRUE redaction capabilities"
$Shortcut2.Save()
Write-Host "  Desktop: $DesktopShortcut" -ForegroundColor Green

# Cleanup
Write-Host ""
Write-Host "Step 5: Cleaning up..." -ForegroundColor Yellow
Remove-Item $TempDir -Recurse -Force
Write-Host "  Removed temporary files" -ForegroundColor Green

# Done
Write-Host ""
Write-Host "=== Installation Complete ===" -ForegroundColor Green
Write-Host ""
Write-Host "Version: $TagName" -ForegroundColor White
Write-Host "Location: $InstallDir" -ForegroundColor White
Write-Host ""
Write-Host "You can now:" -ForegroundColor Cyan
Write-Host "  1. Find 'PDF Editor' in your Start Menu"
Write-Host "  2. Use the Desktop shortcut"
Write-Host "  3. Double-click any PDF file and choose 'Open with' -> PDF Editor"
Write-Host ""
Write-Host "To uninstall:" -ForegroundColor Gray
Write-Host "  1. Delete the shortcuts"
Write-Host "  2. Delete folder: $InstallDir"
