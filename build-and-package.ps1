# Build and Package Script for Jellyfin.Plugin.TheSportsDB
$ErrorActionPreference = "Stop"

$ProjectDir = $PSScriptRoot
$ProjectFile = Join-Path $ProjectDir "Jellyfin.Plugin.TheSportsDB.csproj"
$BuildDir = Join-Path $ProjectDir "bin\Release\net8.0"
$DllName = "Jellyfin.Plugin.TheSportsDB.dll"
$ZipName = "Jellyfin.Plugin.TheSportsDB.zip"
$ZipPath = Join-Path $ProjectDir $ZipName

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Building Jellyfin.Plugin.TheSportsDB" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Clean previous build
if (Test-Path $BuildDir) {
    Remove-Item -Path $BuildDir -Recurse -Force
}

# Build the project in Release mode
Write-Host "`nBuilding project in Release mode..." -ForegroundColor Yellow
dotnet build $ProjectFile --configuration Release

# Check if DLL exists
$DllPath = Join-Path $BuildDir $DllName
if (-not (Test-Path $DllPath)) {
    Write-Error "DLL not found at: $DllPath"
    exit 1
}

Write-Host "`nBuild successful!" -ForegroundColor Green

# Create the zip package
Write-Host "`nCreating zip package..." -ForegroundColor Yellow

# Remove old zip if exists
if (Test-Path $ZipPath) {
    Remove-Item -Path $ZipPath -Force
}

# Create zip from the build directory
Compress-Archive -Path "$BuildDir\*" -DestinationPath $ZipPath -Force

if (Test-Path $ZipPath) {
    $ZipItem = Get-Item $ZipPath
    $ZipSize = $ZipItem.Length / 1KB
    Write-Host "`nPackage created successfully!" -ForegroundColor Green
    Write-Host "  Location: $ZipPath" -ForegroundColor White
    Write-Host "  Size: $([math]::Round($ZipSize, 2)) KB" -ForegroundColor White
}
else {
    Write-Error "Failed to create zip package!"
    exit 1
}

Write-Host "`n========================================" -ForegroundColor Cyan
Write-Host "Build and Package Complete!" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
