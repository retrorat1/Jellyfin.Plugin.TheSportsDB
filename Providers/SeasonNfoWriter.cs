using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TheSportsDB.Providers
{
    /// <summary>
    /// Writes/enriches <c>season.nfo</c> beside the season folder (parallel to series <c>tvshow.nfo</c>).
    /// Jellyfin's built-in NFO saver often writes incomplete season NFOs (empty art, collapsed
    /// IndexNumber like 20252026 for folder 2025-2026). This overwrites with TheSportsDB data.
    /// </summary>
    internal static class SeasonNfoWriter
    {
        private static readonly string[] LocalPrimaryNames =
        {
            "poster.jpg", "poster.png", "poster.webp",
            "folder.jpg", "folder.png",
            "cover.jpg", "cover.png",
            "season.jpg", "season.png"
        };

        private static readonly HashSet<string> PathFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".m2ts", ".wmv", ".mov", ".mpg", ".mpeg",
            ".nfo", ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tbn"
        };

        public static string? GetSeasonFolderPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Prefer extension check so episode file paths work even when the OS path
            // isn't visible to this process (remote/container mounts, etc.).
            var leaf = Path.GetFileName(trimmed);
            if (!string.IsNullOrEmpty(leaf))
            {
                var ext = Path.GetExtension(leaf);
                if (!string.IsNullOrEmpty(ext) && PathFileExtensions.Contains(ext))
                {
                    var parent = Path.GetDirectoryName(trimmed);
                    return string.IsNullOrEmpty(parent) ? null : parent;
                }
            }

            if (Directory.Exists(trimmed))
                return trimmed;

            // If Path points at a file inside the season folder, use its directory
            var dir = Path.GetDirectoryName(trimmed);
            return Directory.Exists(dir) ? dir : trimmed;
        }

        /// <summary>
        /// Parse season identity from a media or season-folder path.
        /// Folder <c>2022</c> → Key=2022, IndexNumber=2022;
        /// Folder <c>2025-2026</c> → Key=2025-2026, IndexNumber=20252026
        /// (Jellyfin's collapsed form for linking; display Name stays hyphenated).
        /// </summary>
        public static bool TryGetSeasonFolderInfo(string? path, out string seasonKey, out int indexNumber)
        {
            seasonKey = string.Empty;
            indexNumber = 0;

            var folderPath = GetSeasonFolderPath(path);
            if (string.IsNullOrEmpty(folderPath))
                return false;

            var folderName = Path.GetFileName(
                folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderName))
                return false;

            return TryParseSeasonFolderName(folderName, out seasonKey, out indexNumber);
        }

        /// <summary>
        /// Parse a season folder name into display key + IndexNumber.
        /// Split seasons use Jellyfin's collapsed IndexNumber (20252026) so episodes link to
        /// the physical season folder; <paramref name="seasonKey"/> stays "2025-2026" for display.
        /// </summary>
        public static bool TryParseSeasonFolderName(string? folderName, out string seasonKey, out int indexNumber)
        {
            seasonKey = string.Empty;
            indexNumber = 0;
            if (string.IsNullOrWhiteSpace(folderName))
                return false;

            var name = folderName.Trim();
            name = Regex.Replace(name, @"^(Season|Series)\s+", "", RegexOptions.IgnoreCase).Trim();
            if (name.Length == 0)
                return false;

            // Split season: 2025-2026 or 2025/2026
            // IndexNumber = collapsed 20252026 (matches Jellyfin folder scan); Key keeps hyphen.
            var split = Regex.Match(
                name,
                @"^((?:19|20)\d{2})\s*[-/]\s*((?:19|20)\d{2})$");
            if (split.Success
                && int.TryParse(split.Groups[1].Value, out var start)
                && int.TryParse(split.Groups[2].Value, out var end)
                && end >= start
                && end <= start + 1)
            {
                seasonKey = $"{start}-{end}";
                indexNumber = start * 10000 + end;
                return true;
            }

            // Calendar year folder: 2022
            var year = Regex.Match(name, @"^((?:19|20)\d{2})$");
            if (year.Success && int.TryParse(year.Groups[1].Value, out var y) && y is >= 1900 and <= 2100)
            {
                seasonKey = year.Groups[1].Value;
                indexNumber = y;
                return true;
            }

            // Collapsed Jellyfin form: 20252026 → Key 2025-2026 / IndexNumber 20252026
            var expanded = TryExpandCollapsedSeason(name);
            if (!string.IsNullOrEmpty(expanded)
                && int.TryParse(name, out var collapsed)
                && collapsed is >= 19001900 and <= 21002100)
            {
                seasonKey = expanded;
                indexNumber = collapsed;
                return true;
            }

            // Season N / bare positive index (non-year)
            if (int.TryParse(name, out var n) && n > 0 && n < 1900)
            {
                seasonKey = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                indexNumber = n;
                return true;
            }

            return false;
        }

        public static bool HasLocalPrimaryImage(string? seasonFolder)
        {
            if (string.IsNullOrEmpty(seasonFolder) || !Directory.Exists(seasonFolder))
                return false;

            return LocalPrimaryNames.Any(name =>
                File.Exists(Path.Combine(seasonFolder, name)));
        }

        public static bool HasNfoPoster(string? seasonFolder)
        {
            var nfoPath = GetNfoPath(seasonFolder);
            if (nfoPath == null || !File.Exists(nfoPath))
                return false;

            try
            {
                var doc = XDocument.Load(nfoPath);
                var poster = doc.Root?
                    .Element("art")?
                    .Element("poster")?
                    .Value?
                    .Trim();
                if (!string.IsNullOrEmpty(poster))
                    return true;

                // Some NFOs use <thumb> instead of <art><poster>
                var thumb = doc.Root?.Elements("thumb")
                    .Select(e => e.Value?.Trim())
                    .FirstOrDefault(v => !string.IsNullOrEmpty(v));
                return !string.IsNullOrEmpty(thumb);
            }
            catch
            {
                return false;
            }
        }

        public static string? GetNfoPath(string? seasonFolder)
        {
            if (string.IsNullOrEmpty(seasonFolder))
                return null;
            return Path.Combine(seasonFolder, "season.nfo");
        }

        /// <summary>
        /// Write or replace season.nfo with title/season string from the folder (e.g. 2025-2026),
        /// provider ids, and poster URL or local poster filename.
        /// </summary>
        public static void Write(
            string seasonFolder,
            string seasonKey,
            string leagueId,
            int? jellyfinSeasonNumber,
            int? year,
            string? posterUrlOrPath,
            ILogger logger)
        {
            try
            {
                if (!Directory.Exists(seasonFolder))
                {
                    logger.LogWarning(
                        "TheSportsDB: Cannot write season.nfo — folder missing: {Folder}",
                        seasonFolder);
                    return;
                }

                var nfoPath = Path.Combine(seasonFolder, "season.nfo");
                var localPoster = LocalPrimaryNames
                    .Select(n => Path.Combine(seasonFolder, n))
                    .FirstOrDefault(File.Exists);
                var posterValue = !string.IsNullOrEmpty(localPoster)
                    ? Path.GetFileName(localPoster)
                    : posterUrlOrPath;

                // Preserve existing NFO poster when rewriting title/ids without a new scrape
                if (string.IsNullOrEmpty(posterValue) && File.Exists(nfoPath))
                {
                    try
                    {
                        var existing = XDocument.Load(nfoPath).Root?
                            .Element("art")?
                            .Element("poster")?
                            .Value?
                            .Trim();
                        if (!string.IsNullOrEmpty(existing))
                            posterValue = existing;
                    }
                    catch
                    {
                        // ignore unreadable prior NFO
                    }
                }

                var sb = new StringBuilder();
                sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>");
                sb.AppendLine("<season>");
                sb.Append("  <title>").Append(XmlEscape(seasonKey)).AppendLine("</title>");
                if (year.HasValue)
                    sb.Append("  <year>").Append(year.Value).AppendLine("</year>");

                // Keep Jellyfin IndexNumber for library linking (may be collapsed 20252026)
                if (jellyfinSeasonNumber.HasValue)
                {
                    sb.Append("  <seasonnumber>")
                        .Append(jellyfinSeasonNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .AppendLine("</seasonnumber>");
                }

                // TheSportsDB season string (hyphenated when applicable)
                sb.Append("  <uniqueid type=\"thesportsdb\" default=\"true\">")
                    .Append(XmlEscape(seasonKey))
                    .AppendLine("</uniqueid>");
                sb.Append("  <uniqueid type=\"thesportsdbseries\">")
                    .Append(XmlEscape(leagueId))
                    .AppendLine("</uniqueid>");
                sb.Append("  <thesportsdbid>")
                    .Append(XmlEscape(seasonKey))
                    .AppendLine("</thesportsdbid>");
                sb.Append("  <thesportsdbseriesid>")
                    .Append(XmlEscape(leagueId))
                    .AppendLine("</thesportsdbseriesid>");

                sb.AppendLine("  <art>");
                if (!string.IsNullOrEmpty(posterValue))
                    sb.Append("    <poster>").Append(XmlEscape(posterValue)).AppendLine("</poster>");
                sb.AppendLine("  </art>");
                sb.AppendLine("  <plot />");
                sb.AppendLine("</season>");

                File.WriteAllText(nfoPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                logger.LogInformation(
                    "TheSportsDB: Wrote season.nfo at {Path} (season=\"{Season}\", poster={Poster})",
                    nfoPath,
                    seasonKey,
                    string.IsNullOrEmpty(posterValue) ? "(none)" : posterValue);
            }
            catch (UnauthorizedAccessException ex)
            {
                logger.LogWarning(
                    ex,
                    "TheSportsDB: Permission denied writing season.nfo in {Folder} — " +
                    "Primary image URL still set in metadata; grant the jellyfin user write access " +
                    "to media folders if you want on-disk season.nfo / poster.jpg",
                    seasonFolder);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "TheSportsDB: Failed writing season.nfo in {Folder}", seasonFolder);
            }
        }

        /// <summary>
        /// Jellyfin collapses folder "2025-2026" to IndexNumber 20252026. Expand back when possible.
        /// </summary>
        public static string? TryExpandCollapsedSeason(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            value = value.Trim();
            value = Regex.Replace(value, @"^(Season|Series)\s+", "", RegexOptions.IgnoreCase).Trim();

            // Already hyphenated / slashed
            if (value.Contains('-') || value.Contains('/'))
                return null;

            var m = Regex.Match(value, @"^(?<a>(?:19|20)\d{2})(?<b>(?:19|20)\d{2})$");
            if (!m.Success)
                return null;

            if (!int.TryParse(m.Groups["a"].Value, out var start)
                || !int.TryParse(m.Groups["b"].Value, out var end))
                return null;

            // Split seasons are almost always same year or start+1 (e.g. 2025-2026)
            if (end < start || end > start + 1)
                return null;

            return $"{start}-{end}";
        }

        private static string XmlEscape(string value)
            => value
                .Replace("&", "&amp;", StringComparison.Ordinal)
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal)
                .Replace("\"", "&quot;", StringComparison.Ordinal)
                .Replace("'", "&apos;", StringComparison.Ordinal);
    }
}
