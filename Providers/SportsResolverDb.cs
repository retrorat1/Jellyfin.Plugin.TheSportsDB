using System;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.TheSportsDB.Providers
{
    public class SportsResolverDb
    {
        private readonly IApplicationPaths _appPaths;
        private string? _resolvedPath;
        private bool? _teamLeaguesAvailable;

        public SportsResolverDb(IApplicationPaths appPaths)
        {
            _appPaths = appPaths;
        }

        private string ResolveDbPath()
        {
            var path = DbPathResolver.Resolve(_appPaths);
            if (!string.Equals(path, _resolvedPath, StringComparison.OrdinalIgnoreCase))
            {
                _resolvedPath = path;
                _teamLeaguesAvailable = null;
            }

            return path;
        }

        private bool HasTeamLeaguesTable(SqliteConnection conn)
        {
            if (_teamLeaguesAvailable.HasValue)
                return _teamLeaguesAvailable.Value;

            using var cmd = new SqliteCommand(
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'team_leagues'", conn);
            _teamLeaguesAvailable = cmd.ExecuteScalar() != null;
            return _teamLeaguesAvailable.Value;
        }

        private static string? ReadTeamName(SqliteCommand cmd)
        {
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? reader.GetString(0) : null;
        }

        public string? GetLeagueSlug(string leagueId)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={ResolveDbPath()};");
                conn.Open();
                using var cmd = new SqliteCommand("SELECT look_up FROM leagues_lookup WHERE id = @id", conn);
                cmd.Parameters.AddWithValue("@id", leagueId);
                using var r = cmd.ExecuteReader();
                return r.Read() ? r.GetString(0) : null;
            }
            catch { return null; }
        }

        public string? GetTeamFullName(string lookupName, string leagueId)
        {
            if (string.IsNullOrWhiteSpace(lookupName)) return null;
            try
            {
                using var conn = new SqliteConnection($"Data Source={ResolveDbPath()};");
                conn.Open();

                string leagueFilter = HasTeamLeaguesTable(conn)
                    ? "(t.league_id = @lid OR EXISTS (SELECT 1 FROM team_leagues tl WHERE tl.team_id = t.id AND tl.league_id = @lid))"
                    : "t.league_id = @lid";

                // Pass 1: strict league (teams.league_id or team_leagues membership)
                using (var cmd = new SqliteCommand($@"
                SELECT t.name FROM teams t
                WHERE {leagueFilter}
                AND (t.name = @l OR t.short_name = @l OR t.stripped_name = @l OR t.alternative_names LIKE @k)
                LIMIT 1", conn))
                {
                    cmd.Parameters.AddWithValue("@lid", leagueId);
                    cmd.Parameters.AddWithValue("@l", lookupName);
                    cmd.Parameters.AddWithValue("@k", $"%{lookupName}%");
                    var name = ReadTeamName(cmd);
                    if (name != null) return name;
                }
                // Pass 2: cross-league
                using (var cmd2 = new SqliteCommand(@"SELECT name FROM teams WHERE (name=@l OR short_name=@l OR stripped_name=@l OR alternative_names LIKE @k) LIMIT 1", conn))
                {
                    cmd2.Parameters.AddWithValue("@l", lookupName);
                    cmd2.Parameters.AddWithValue("@k", $"%{lookupName}%");
                    using var r2 = cmd2.ExecuteReader();
                    if (r2.Read()) return r2.GetString(0);
                }
                return null;
            }
            catch { return null; }
        }

        public string? GetLeagueIdFromAlias(string alias)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={ResolveDbPath()};");
                conn.Open();
                using var cmd = new SqliteCommand("SELECT id FROM leagues_lookup WHERE alias LIKE @a OR name LIKE @a", conn);
                cmd.Parameters.AddWithValue("@a", $"%{alias}%");
                using var r = cmd.ExecuteReader();
                return r.Read() ? r.GetString(0) : null;
            }
            catch { return null; }
        }

        public string? GetLeagueId(string lookup) => GetLeagueIdFromAlias(lookup);
        public string? GetSportName(string lookup) => null;
    }
}
