$ProjectDir = "$PSScriptRoot"
$BuildDir = "$ProjectDir\bin\Debug\net9.0"
$DllName = "Jellyfin.Plugin.TheSportsDB.dll"
$DllPath = "$BuildDir\$DllName"

# Common Jellyfin plugin paths on Windows
$PossiblePaths = @(
    "D:\JellyfinServer\programdata\plugins",
    "D:\JellyfinServer\plugins",
    "$env:ProgramData\Jellyfin\Server\plugins",
    "$env:LOCALAPPDATA\jellyfin\plugins"
)

Write-Host "Build path: $DllPath"

if (-not (Test-Path $DllPath)) {
    Write-Error "DLL not found! Please run 'dotnet build' first."
    exit 1
}

$TargetFound = $false

foreach ($Path in $PossiblePaths) {
    if (Test-Path $Path) {
        $PluginDir = "$Path\TheSportsDB"
        if (-not (Test-Path $PluginDir)) {
            New-Item -ItemType Directory -Force -Path $PluginDir | Out-Null
        }
        
        Copy-Item -Path $DllPath -Destination "$PluginDir\$DllName" -Force
        Write-Host "Success! Plugin copied to: $PluginDir\$DllName"
        Write-Host "Please restart your Jellyfin server to load the plugin."
        $TargetFound = $true
        break
    }
}

if (-not $TargetFound) {
    Write-Warning "Could not find Jellyfin plugins directory automatically."
    Write-Host "Please manually copy:" 
    Write-Host "  Source: $DllPath"
    Write-Host "  Destination: <Your Jellyfin Install Path>\plugins\TheSportsDB\$DllName"
    Write-Host "Then restart Jellyfin."
}
