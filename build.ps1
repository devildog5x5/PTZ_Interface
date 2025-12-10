# CameraControll Build Script
# Author: Robert Foster

param(
    [switch]$Release,
    [switch]$CreateInstaller,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$ProjectPath = $PSScriptRoot
$ProjectFile = Join-Path $ProjectPath "CameraControll.csproj"
$Configuration = if ($Release) { "Release" } else { "Debug" }

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  CameraControll Build Script" -ForegroundColor Cyan
Write-Host "  Configuration: $Configuration" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($Clean) {
    Write-Host "`nCleaning previous builds..." -ForegroundColor Yellow
    $binPath = Join-Path $ProjectPath "bin"
    $objPath = Join-Path $ProjectPath "obj"
    if (Test-Path $binPath) { Remove-Item -Path $binPath -Recurse -Force }
    if (Test-Path $objPath) { Remove-Item -Path $objPath -Recurse -Force }
    Write-Host "Clean complete!" -ForegroundColor Green
}

Write-Host "`nRestoring NuGet packages..." -ForegroundColor Yellow
dotnet restore $ProjectFile
if ($LASTEXITCODE -ne 0) {
    Write-Host "Package restore failed!" -ForegroundColor Red
    exit 1
}
Write-Host "Packages restored successfully!" -ForegroundColor Green

Write-Host "`nBuilding project ($Configuration)..." -ForegroundColor Yellow
dotnet build $ProjectFile -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}
Write-Host "Build successful!" -ForegroundColor Green

if ($CreateInstaller) {
    Write-Host "`nCreating installer..." -ForegroundColor Yellow
    
    $InnoSetupPath = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    
    if ($InnoSetupPath) {
        $IssFile = Join-Path $ProjectPath "Installer\CameraControllInstaller.iss"
        & $InnoSetupPath $IssFile
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Installer created successfully!" -ForegroundColor Green
            $OutputDir = Join-Path $ProjectPath "Installer\Output"
            Write-Host "Installer location: $OutputDir" -ForegroundColor Cyan
        } else {
            Write-Host "Installer creation failed!" -ForegroundColor Red
        }
    } else {
        Write-Host "Inno Setup not found." -ForegroundColor Yellow
    }
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "  Build Complete!" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$OutputPath = Join-Path $ProjectPath "bin\$Configuration\net8.0-windows"
Write-Host "`nOutput: $OutputPath" -ForegroundColor Gray

