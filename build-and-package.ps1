# Build and Package Script for Jellyfin.Plugin.TheSportsDB
# This script builds the plugin in Release mode and creates a distributable zip file

$ProjectDir = "$PSScriptRoot"
$ProjectFile = "$ProjectDir\Jellyfin.Plugin.TheSportsDB.csproj"
$BuildDir = "$ProjectDir\bin\Release\net8.0"
$DllName = "Jellyfin.Plugin.TheSportsDB.dll"
$ZipName = "Jellyfin.Plugin.TheSportsDB.zip"
$ZipPath = "$ProjectDir\$ZipName"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Building Jellyfin.Plugin.TheSportsDB" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Clean previous build
Write-Host "`nCleaning previous builds..." -ForegroundColor Yellow
if (Test-Path $BuildDir) {
    Remove-Item -Path $BuildDir -Recurse -Force
}

# Build the project in Release mode
Write-Host "`nBuilding project in Release mode..." -ForegroundColor Yellow
dotnet build $ProjectFile --configuration Release

if ($LASTEXITCODE -ne 0) {
    Write-Error "Build failed!"
    exit 1
}

# Check if DLL exists
$DllPath = "$BuildDir\$DllName"
if (-not (Test-Path $DllPath)) {
    Write-Error "DLL not found at: $DllPath"
    exit 1
}

Write-Host "`n✓ Build successful!" -ForegroundColor Green

# Create the zip package
Write-Host "`nCreating zip package..." -ForegroundColor Yellow

# Remove old zip if exists
if (Test-Path $ZipPath) {
    Remove-Item -Path $ZipPath -Force
    Write-Host "  Removed old zip file" -ForegroundColor Gray
}

# Create zip from the build directory
Compress-Archive -Path "$BuildDir\*" -DestinationPath $ZipPath -Force

if (Test-Path $ZipPath) {
    $ZipSize = (Get-Item $ZipPath).Length / 1KB
    Write-Host "`n✓ Package created successfully!" -ForegroundColor Green
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
Write-Host "`nNext steps:" -ForegroundColor Yellow
Write-Host "  1. Test the plugin locally with deploy.ps1" -ForegroundColor White
Write-Host "  2. Commit and push the updated zip to GitHub:" -ForegroundColor White
Write-Host "     git add $ZipName" -ForegroundColor Gray
Write-Host "     git commit -m 'Update plugin package'" -ForegroundColor Gray
Write-Host "     git push origin main" -ForegroundColor Gray
