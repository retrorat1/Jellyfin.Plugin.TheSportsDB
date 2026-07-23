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

        public static string? GetSeasonFolderPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Directory.Exists(trimmed))
                return trimmed;

            // If Path points at a file inside the season folder, use its directory
            var dir = Path.GetDirectoryName(trimmed);
            return Directory.Exists(dir) ? dir : trimmed;
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
                    "ensure the Jellyfin service user can write to media folders",
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
