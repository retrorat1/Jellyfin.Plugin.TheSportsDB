# Jellyfin.Plugin.TheSportsDB

A Jellyfin metadata provider plugin for [TheSportsDB](https://www.thesportsdb.com/).

## Features

- Fetches League metadata (Name, Overview, Year, Images)
- Maps Leagues to "Series" in Jellyfin
- Fetches Event/Match metadata (Name, Date, Thumbnails, Fanart)
- Maps Events to "Episodes" in Jellyfin
- Supports user API Key configuration

## Installation

### From Release
1. Download the latest release zip from the [Releases page](https://github.com/retrorat1/Jellyfin.Plugin.TheSportsDB/releases).
2. Extract the DLL to your Jellyfin plugins folder.
3. Restart Jellyfin.

### Build from Source
1. Clone the repository
2. Run `build-and-package.ps1` (Windows) to build the plugin
3. The plugin zip will be created in the project root directory
4. Extract the contents to your Jellyfin plugins folder

## Configuration

Go to Dashboard > Plugins > TheSportsDB and enter your API Key.

## Naming Conventions & Troubleshooting

### File Naming
For best results, name your files with the date and teams:
- Format: `YYYY-MM-DD-HomeTeam-AwayTeam.mp4` (e.g., `2026-01-25-TOR-COL.mp4`)
- Format: `YYYY-MM-DD Home vs Away.mp4`
- Team abbreviations (e.g., TOR, COL, VAN, MTL, WSH) are supported. The plugin resolves these to the correct team (e.g., MTL -> Montreal Canadiens).

### Troubleshooting
- **"Season Unknown" or No Metadata**: Ensure your Series folder (e.g., "NHL") is correctly identified by Jellyfin. If using abbreviation filenames, ensure the date is correct.
- **ffprobe failed**: This usually indicates Jellyfin cannot read the file. Check file permissions (ensure the `jellyfin` user has read access).

