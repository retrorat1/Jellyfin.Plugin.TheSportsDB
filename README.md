# Jellyfin.Plugin.TheSportsDB

A comprehensive Jellyfin metadata provider plugin for [TheSportsDB](https://www.thesportsdb.com/). This plugin fetches rich metadata for sports content across **ANY sport** available on TheSportsDB, including Soccer ⚽, Ice Hockey 🏒, American Football 🏈, Basketball 🏀, Baseball ⚾, MMA/UFC 🥊, and many more!

## 🎯 Plugin Overview

This plugin integrates TheSportsDB's extensive sports database with Jellyfin, providing:

- **League Metadata**: Name, Overview, Year, Logo Images, and Fanart
- **Event/Match Metadata**: Event Name, Date, Teams, Thumbnails, and Fanart
- **Smart Mapping**: Leagues map to "Series" and Events map to "Episodes" in Jellyfin
- **Flexible Configuration**: Custom API Keys and League Mappings support
- **Multi-Sport Support**: Works with ANY sport available on TheSportsDB

## 📦 Installation

### Option 1: Install via Jellyfin Plugin Repository (Recommended)

1. **Open Jellyfin Web Interface**: Log in to your Jellyfin server.
2. **Go to Plugins**: Navigate to the manage repositories section.
3. **Click New Repository**
4. **Enter The Name TheSportsDB**
5. **Enter `https://raw.githubusercontent.com/retrorat1/Jellyfin.Plugin.TheSportsDB/main/manifest.json`**
6. **Browse or Search**: Locate **TheSportsDB** plugin in the repository.
7. **Install**: Click to install directly from the Jellyfin plugin repository.
8. **Restart Jellyfin**: Restart Jellyfin if prompted to load the plugin.

### Option 2: Manual Installation

1. **Download the Plugin**: Get the latest release from the [GitHub Releases page](https://github.com/retrorat1/Jellyfin.Plugin.TheSportsDB/releases)
2. **Extract the ZIP**: Unzip the archive to your Jellyfin plugins directory:
   - **Linux**: `/var/lib/jellyfin/plugins/TheSportsDB/`
   - **Windows**: `%ProgramData%\Jellyfin\Server\plugins\TheSportsDB\`
   - **Docker**: Map to `/config/plugins/TheSportsDB/`
3. **Ensure Database File**: The `sports_resolver.db` file must be in the plugin directory (it's included in the release)
4. **Restart Jellyfin**: Restart Jellyfin to load the plugin

---

## ⚡️ **CRUCIAL: Folder/Mapping Alignment**

**To ensure accurate metadata and art, your league folder names and TheSportsDB league mappings MUST match (case-insensitive)!**

> **You MUST configure your league mappings _before_ scanning your library with Jellyfin.**  
> If you add new leagues or rename league folders, update the mapping and rescan.

- The **immediate parent folder** of your event files should exactly match a mapping entry (e.g., `EPL`, `ICC T20 WC`, `NFL`, etc.).
- If the folder name does not match a league mapping, the plugin will not be able to resolve the correct league, causing lookups to fail and no metadata/posters to appear.
- Any league or competition available on TheSportsDB can be mapped; simply match your folder name and set the correct League ID.

#### **Example Table**

| Folder Name        | League ID | Example Path                                   |
|--------------------|-----------|------------------------------------------------|
| EPL                | 4328      | `/media/Sports/EPL/2025-2026/2026-02-08 Liverpool vs Manchester City.mkv`     |
| ICC T20 WC         | 5103      | `/media/Sports/ICC T20 WC/2026/ICC Mens T20 World Cup 2026-02-15 India Cricket vs Pakistan Cricket.mkv` |
| NFL                | 4391      | `/media/Sports/NFL/2025/2026-01-15 Buffalo Bills vs Baltimore Ravens.mkv`   |

> **Note:** If you rename a folder (e.g., `ICC T20 WC` → `ICC T20 Mens WC`), you must create a matching mapping in the plugin for the new folder name and rescan the library.

---

## ⚙️ Configuration

### Setting up your API Key

1. Get a free API key from [TheSportsDB.com](https://www.thesportsdb.com/)
2. Go to **Dashboard > Plugins > TheSportsDB**
3. Enter your **API Key**
4. Click **Save**

> **Note:** Jellyfin will restart automatically after saving configuration changes.

### League Mappings

The plugin includes built-in support for popular leagues (see [Known Leagues](#-known-leagues-built-in) below). **For custom or abbreviated folder names, you must create manual mappings before scanning your library:**

1. Go to **Dashboard > Plugins > TheSportsDB**
2. Scroll to **League Mappings**
3. Click **Add Mapping**
4. Enter your **Folder Name** — just the parent folder name, NOT the full path (e.g., `EPL`, `Süper Lig`)
5. Enter the **TheSportsDB League ID** (you can find these on TheSportsDB.com league pages)
6. Click **Save**

> **Reminder**: Mapping changes require a **full library rescan** for new/changed folders.

---

## 📝 Recommended File Naming Guide

**File and directory naming matters most for accurate matching!** Clean, consistent filenames and correct folder mapping produce the best results.

### Best Formats (by Match Rate)

| Format                   | Example                                                     | Match Rate |
|--------------------------|-------------------------------------------------------------|------------|
| `YYYY-MM-DD Team1 vs Team2` | `2026-02-08 Liverpool vs Manchester City.mkv`               | ⭐⭐⭐⭐⭐      |
| `YYYY-MM-DD_Away@Home`      | `2026-03-16_BOS@NJD.mkv`                                    | ⭐⭐⭐⭐⭐      |
| `YYYY-MM-DD-ABBR-ABBR`      | `2026-02-05-NJD-NYI.mkv`                                    | ⭐⭐         |
| `Team1 vs Team2`            | `Liverpool vs Manchester City.mkv`                          | ⭐⭐⭐⭐       |
| `Full Event Name`           | `UFC 315 Jones vs Aspinall.mkv`                             | ⭐⭐⭐⭐       |
| `YYYY Team1 vs Team2 DD MM` | `2026 Liverpool vs Manchester City 08 02.mkv`               | ⭐⭐⭐        |
| `League YYYY Team1 vs Team2 ...` | `EPL 2026 Liverpool vs Manchester City 08 02 720p.mkv` | ⭐⭐         |
| `YYYY-MM-DD Event Name`    | `2026-03-08 Australian Grand Prix.mkv`                       | ⭐⭐⭐⭐⭐      |

### Team Separator Formats

The plugin supports two formats for team sports:

| Format | Convention | Example |
|--------|------------|---------|
| `Home vs Away` | Home team first | `2026-03-16 New Jersey Devils vs Boston Bruins.mkv` |
| `Away@Home` | Away team first (away **@** home) | `2026-03-16_BOS@NJD.mkv` |

> **`@` means away @ home** — this matches how TheSportsDB stores alternate event names (e.g. `strEventAlternate: "Boston Bruins @ New Jersey Devils"`). This convention is universal across all sports on TheSportsDB, not just NHL.

So for the same game, both of these are valid:
```
NHL-2026-03-16_NJD vs BOS.mkv          ← home vs away (vs format)
NHL-2026-03-16_BOS@NJD.mkv             ← away @ home (@ format)
```

And for Premier League:
```
EPL-2026-03-16_Brentford vs Wolverhampton Wanderers.mkv
EPL-2026-03-16_WOL@BRE.mkv
```

### Key Naming Rules

- **✅ Always use ISO dates (`YYYY-MM-DD`)** if possible
- **✅ Use full team names** where possible (less ambiguous)
- **✅ Use `vs` or `@` as the separator** between team names (`vs` = home first, `@` = away first)
- **✅ Make sure the folder name is mapped** in plugin settings ("League Mappings")
- **✅ Keep filenames clean** — avoid broadcaster/network tags (e.g. `_ESPN`, `_MSG`, `_FDSNDET`). The plugin strips common ones but cleaner is always better
- **✅ Use season/year subfolders for organization**
- **✅ 3-letter team abbreviations are supported for NHL and other North American leagues**
- **✅ Soccer/football: Use full club names**

### Recommended Folder Structure (Universal Format)

Use POSIX-style forward slashes for cross-platform compatibility.

```plaintext
/media/Sports/
├── EPL/
│   └── 2025-2026/
│       ├── 2026-02-08 Liverpool vs Manchester City.mkv
│       └── 2026-01-15 Arsenal vs Chelsea.mkv
├── NHL/
│   └── 2025-2026/
│       ├── 2026-03-16_BOS@NJD.mkv
│       ├── 2026-02-05-NJD-NYI.mkv
│       └── 2026-01-22 Edmonton Oilers vs Pittsburgh Penguins.mkv
├── NBA/
│   └── 2025-2026/
│       ├── 2026-02-10 Los Angeles Lakers vs Boston Celtics.mkv
│       └── 2026-01-28 Golden State Warriors vs Miami Heat.mkv
├── NFL/
│   └── 2025-2026/
│       ├── 2026-02-01 Kansas City Chiefs vs Philadelphia Eagles.mkv
│       └── 2026-01-20 Buffalo Bills vs Baltimore Ravens.mkv
├── Formula 1/
│   └── 2026/
│       ├── 2026-03-08 Australian Grand Prix.mkv
│       └── 2026-03-15 Chinese Grand Prix.mkv
└── UFC/
    └── 2026/
        └── 2026-03-15 UFC 315 Jones vs Aspinall.mkv
```

---

## 🔧 How the Plugin Works

The plugin uses a sophisticated matching pipeline to find the correct metadata for your sports files:

1. **Derive Series/League Name**: Extracts the league name from your folder structure (parent folder is used for mapping).
2. **Resolve League ID**: Searches for the league ID in this order:
   - User-configured League Mappings (requires mapping before scanning the library!)
   - Local `sports_resolver.db` database (name/alias lookup)
3. **Clean the Filename**: Removes dates, broadcaster tags, and scene tags from the filename
4. **Expand Team Abbreviations**: Converts common abbreviations to full team names (e.g., `BOS` → Boston Bruins, `NJD` → New Jersey Devils)
5. **Parse Team Separator**: Detects `vs` (home vs away) or `@` (away @ home) and resolves both team names
6. **Search TheSportsDB API**: Searches for matching events using the cleaned team names, mapped league ID, and date if available
7. **Date-Based Fallback**: If no match found, falls back to date-based lookup with ±2 day tolerance (helps with timezone slippage)
8. **Flexible Filename Matching**: Filenames with or without the league prefix are supported (e.g., `2026-03-08 Australian Grand Prix.mkv` works just as well as `Formula 1 2026-03-08 Australian Grand Prix.mkv`).

> **API note:** for TheSportsDB `eventsday.php`, the `l` parameter should be a numeric league ID (for example `4370` for Formula 1), not a slug like `formula_1`.

---

## 🏆 Common League IDs (Quick Reference)

The following are commonly used league IDs:

| League | League ID | Sport           |
|--------|-----------|-----------------|
| NHL    | 4380      | Ice Hockey 🏒    |
| EPL    | 4328      | Soccer ⚽        |
| NFL    | 4391      | American Football 🏈 |
| NBA    | 4387      | Basketball 🏀    |
| MLB    | 4424      | Baseball ⚾      |
| UFC    | 4463      | Mixed Martial Arts 🥊 |

**Note:** Any league on TheSportsDB can be used via League Mappings.

---

## 🛠️ Troubleshooting

### Common Issues and Solutions

**"No metadata found" or Empty Results**
- Check your filename format — use ISO date and clean team names
- Verify the event exists on TheSportsDB.com
- Check your API key is correctly configured
- Make sure your **league folder is mapped in plugin settings before library scan**
- Try using full team names instead of abbreviations

**"Season Unknown"**
- Ensure your season folder contains the year (e.g., `2025-2026`)
- Check that the parent folder matches a known league or has a mapping configured
- **Do not prefix with "Season"** — use `2026` or `2025-2026`, not `Season 2026`. TheSportsDB stores seasons as bare years only.

**"Logs stop after saving configuration"**
- Normal: Jellyfin always restarts after plugin config is saved
- Wait a minute for the server to reload, then check your library again

**"Wrong match found" or Incorrect Metadata**
- Use more specific filenames with ISO dates (`YYYY-MM-DD`)
- Include full team names
- Verify your names match TheSportsDB.com events

**Plugin doesn't recognize my league folder (e.g., "Bundesliga", "La Liga")**
- Add a League Mapping in the plugin configuration before your first scan
- Find the League ID on TheSportsDB.com (`https://www.thesportsdb.com/league/4331` → ID is `4331`)

**NFO files not being written on Linux**
- The Jellyfin service user (typically `jellyfin`) must have write permission to your media folders
- Check with: `ls -la /path/to/your/media/`
- If permission is denied, the NFO write is skipped silently and a warning is logged — metadata is still returned normally
- **Note:** If Jellyfin's built-in NFO metadata saver is also enabled (Dashboard > Libraries > Manage Library > Metadata Savers > Nfo), it may overwrite the plugin-generated NFO on library refresh

---

## ❤️ Supporting TheSportsDB

This plugin relies entirely on the amazing work done by the team at [TheSportsDB](https://www.thesportsdb.com/). They provide a comprehensive API that makes plugins like this possible.

### Free API Key

Get started with a **free API key** for testing and personal use. Register at [TheSportsDB.com](https://www.thesportsdb.com/).

### Premium API (Recommended!)

Please consider a [TheSportsDB Premium Key](https://www.thesportsdb.com/pricing) if you use this plugin regularly:
- ✅ Higher rate limits
- ✅ Extra endpoints and more data
- ✅ Live scores and advanced info
- ✅ Support ongoing maintenance and improvements

**Note:** This plugin is unaffiliated with TheSportsDB—please support their data service if you benefit from it!

---

## 🤝 Contributing

We welcome contributions! Here's how you can help:

### Building the Plugin

1. Clone the repository:
   ```bash
   git clone https://github.com/retrorat1/Jellyfin.Plugin.TheSportsDB.git
   cd Jellyfin.Plugin.TheSportsDB
   ```

2. Build the plugin:
   ```bash
   dotnet build
   ```

3. Package the plugin:
   ```bash
   # On Windows
   .\build-and-package.ps1
   ```

### Reporting Issues

Found a bug or have a feature request? Please [open an issue](https://github.com/retrorat1/Jellyfin.Plugin.TheSportsDB/issues) on GitHub.

When reporting issues, please include:
- Jellyfin version
- Plugin version
- Example filename that isn't working
- Relevant log entries from Jellyfin

### Contributing Code

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Make your changes
4. Test thoroughly
5. Commit your changes (`git commit -m 'Add amazing feature'`)
6. Push to your fork (`git push origin feature/amazing-feature`)
7. Open a Pull Request

---

## 📄 License

This project is licensed under the [MIT License](./LICENSE).

---

## 🙏 Acknowledgments

- **TheSportsDB Team** — For providing the incredible sports database API
- **Jellyfin Team** — For the amazing media server platform
- **Contributors** — Thank you to everyone who has contributed!
