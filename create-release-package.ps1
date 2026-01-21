# PTZ Camera Operator - Release Package Creator
# Creates ZIP files for GitHub Releases

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  PTZ Camera Operator Release Packager" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if release folder exists
$releaseFolder = "publish\release"
if (-not (Test-Path $releaseFolder)) {
    Write-Host "ERROR: Release folder not found: $releaseFolder" -ForegroundColor Red
    Write-Host "Please build the release first using:" -ForegroundColor Yellow
    Write-Host "  dotnet publish PTZCameraOperator\PTZCameraOperator.csproj -c Release -r win-x64 --self-contained true -o publish\release" -ForegroundColor Gray
    exit 1
}

# Check if installer exists
$installerPath = "Installer\Output\PTZCameraOperatorSetup-1.0.0.exe"
$hasInstaller = Test-Path $installerPath

Write-Host "Creating release packages..." -ForegroundColor Yellow
Write-Host ""

# Create portable build ZIP
$portableZip = "PTZCameraOperator-Portable-v1.0.0.zip"
Write-Host "Creating portable build ZIP: $portableZip" -ForegroundColor Cyan

if (Test-Path $portableZip) {
    Remove-Item $portableZip -Force
    Write-Host "  Removed existing $portableZip" -ForegroundColor Gray
}

Compress-Archive -Path "$releaseFolder\*" -DestinationPath $portableZip -Force
$portableSize = [math]::Round((Get-Item $portableZip).Length / 1MB, 2)
Write-Host "  ✓ Created: $portableZip ($portableSize MB)" -ForegroundColor Green

# Copy installer if it exists
if ($hasInstaller) {
    $installerSize = [math]::Round((Get-Item $installerPath).Length / 1MB, 2)
    Write-Host ""
    Write-Host "Installer found: $installerPath ($installerSize MB)" -ForegroundColor Cyan
    Write-Host "  ✓ Ready for upload: Installer\Output\PTZCameraOperatorSetup-1.0.0.exe" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "WARNING: Installer not found: $installerPath" -ForegroundColor Yellow
    Write-Host "  Build the installer using Inno Setup if needed" -ForegroundColor Gray
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  Release Packages Ready!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Files ready for GitHub Release upload:" -ForegroundColor Yellow
Write-Host "  1. $portableZip ($portableSize MB)" -ForegroundColor White
if ($hasInstaller) {
    Write-Host "  2. Installer\Output\PTZCameraOperatorSetup-1.0.0.exe ($installerSize MB)" -ForegroundColor White
}
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Cyan
Write-Host "  1. Go to: https://github.com/devildog5x5/PTZ_Interface/releases/new" -ForegroundColor White
Write-Host "  2. Create a new release (e.g., 'v1.0.0')" -ForegroundColor White
Write-Host "  3. Upload the files listed above as release assets" -ForegroundColor White
Write-Host ""
