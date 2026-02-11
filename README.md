# Jellyfin.Plugin.TheSportsDB

A Jellyfin metadata provider plugin for [TheSportsDB](https://www.thesportsdb.com/).

## Features

- Fetches League metadata (Name, Overview, Year, Images)
- Maps Leagues to "Series" in Jellyfin
- Fetches Event/Match metadata (Name, Date, Thumbnails, Fanart)
- Maps Events to "Episodes" in Jellyfin
- Supports user API Key configuration

## Installation

1. Download the latest release.
2. Extract the DLL to your Jellyfin plugins folder.
3. Restart Jellyfin.

## Configuration

Go to Dashboard > Plugins > TheSportsDB and enter your API Key.


### League Mapping

The plugin supports automatic league detection (e.g. "NHL", "EPL"). If you use a custom or abbreviated folder name that isn't automatically detected (e.g. "Süper Lig" or "My Soccer Folder"), you can map it manually:

1.  Go to **Dashboard > Plugins > TheSportsDB**.
2.  Scroll to **League Mappings**.
3.  Click **Add Mapping**.
4.  Enter your **Folder Name** (e.g. "Süper Lig").
5.  Enter the **TheSportsDB League ID** (e.g. "4339"). You can find these IDs in the URL of the league page on TheSportsDB website.
6.  Click **Save**.
7.  Rescan your library.

## Naming Conventions &amp; Troubleshooting

### File Naming
For best results, name your files with the date and teams:
- Format: `YYYY-MM-DD-HomeTeam-AwayTeam.mp4` (e.g., `2026-01-25-TOR-COL.mp4`)
- Format: `YYYY-MM-DD Home vs Away.mp4`
- Team abbreviations (e.g., TOR, COL, VAN, MTL, WSH) are supported. The plugin resolves these to the correct team (e.g., MTL -> Montreal Canadiens).

### Troubleshooting
- **"Season Unknown" or No Metadata**: Ensure your Series folder (e.g., "NHL") is correctly identified by Jellyfin. If your folder name is unique (e.g. "Footy"), add it to the **League Mappings** in plugin settings.
- **ffprobe failed**: This usually indicates Jellyfin cannot read the file. Check file permissions (ensure the `jellyfin` user has read access).

