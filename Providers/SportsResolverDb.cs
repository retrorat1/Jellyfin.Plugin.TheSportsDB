using System;
using Microsoft.Data.Sqlite;

public class SportsResolverDb
{
    private readonly string _dbPath;

    public SportsResolverDb(string dbPath) 
    { 
        _dbPath = dbPath; 
    }

    // New Schema: leagues_lookup (id, name, look_up, sport_id, alias)
    // New Schema: teams (id, name, sport_id, stripped_name, country, short_name, alternative_names, league_id)

    /// <summary>
    /// Gets the API slug (e.g. 'english_premier_league') using the League ID (e.g. '4328').
    /// </summary>
    public string? GetLeagueSlug(string leagueId)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath};");
            conn.Open();
            // 'look_up' is the API slug column in the new schema
            using var cmd = new SqliteCommand("SELECT look_up FROM leagues_lookup WHERE id = @id", conn);
            cmd.Parameters.AddWithValue("@id", leagueId);
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? reader.GetString(0) : null;
        }
        catch 
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the full team name given a short name/abbreviation and league ID.
    /// Pass 1: strict league match (for abbreviations like NJD → New Jersey Devils).
    /// Pass 2: cross-league fallback (e.g. India Cricket stored under ODI but queried for T20 WC).
    /// Pass 3: return null — caller uses the raw string as-is.
    /// </summary>
    public string? GetTeamFullName(string lookupName, string leagueId)
    {
        if (string.IsNullOrWhiteSpace(lookupName)) return null;
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath};");
            conn.Open();

            // Pass 1: strict league match — for abbreviations (NJD, BUF, MTL etc.)
            using (var cmd = new SqliteCommand(@"
                SELECT name FROM teams
                WHERE league_id = @lid
                  AND (
                    name             = @lookup
                    OR short_name    = @lookup
                    OR stripped_name = @lookup
                    OR alternative_names LIKE @like
                  )
                LIMIT 1", conn))
            {
                cmd.Parameters.AddWithValue("@lid",    leagueId);
                cmd.Parameters.AddWithValue("@lookup", lookupName);
                cmd.Parameters.AddWithValue("@like",   $"%{lookupName}%");
                using var r = cmd.ExecuteReader();
                if (r.Read()) return r.GetString(0);
            }

            // Pass 2: cross-league fallback
            // e.g. "India Cricket" stored under ODI (4801) but queried for T20 WC (5103)
            using (var cmd2 = new SqliteCommand(@"
                SELECT name FROM teams
                WHERE (
                    name             = @lookup
                    OR short_name    = @lookup
                    OR stripped_name = @lookup
                    OR alternative_names LIKE @like
                )
                LIMIT 1", conn))
            {
                cmd2.Parameters.AddWithValue("@lookup", lookupName);
                cmd2.Parameters.AddWithValue("@like",   $"%{lookupName}%");
                using var r2 = cmd2.ExecuteReader();
                if (r2.Read()) return r2.GetString(0);
            }

            return null; // caller uses raw string as-is
        }
        catch { return null; }
    }
    
    /// <summary>
    /// Resolve League ID from an Alias (e.g. "EPL" or "Premier League")
    /// </summary>
    public string? GetLeagueIdFromAlias(string alias)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={_dbPath};");
            conn.Open();
            // alias column is "Premier League|EPL"
            using var cmd = new SqliteCommand("SELECT id FROM leagues_lookup WHERE alias LIKE @alias OR name LIKE @alias", conn);
            cmd.Parameters.AddWithValue("@alias", $"%{alias}%");
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? reader.GetString(0) : null;
        }
        catch { return null; }
    }

    // --- Legacy methods for compatibility if needed, but updated to use new schema where possible or return null ---

    public string? GetSportName(string lookup)
    {
        // New schema doesn't have sport_name text directly in leagues_lookup, 
        // but we might not need it if we rely on GetLeagueSlug.
        // Return null to force usage of League Slug flow.
        return null; 
    }
    
    public string? GetLeagueId(string lookup)
    {
        // Wrapper for alias lookup
        return GetLeagueIdFromAlias(lookup);
    }
}