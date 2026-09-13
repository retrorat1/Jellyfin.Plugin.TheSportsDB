#!/usr/bin/env python3
"""
Add team_leagues junction table so teams can belong to multiple competitions.

Usage:
  python scripts/migrate_team_leagues.py --backup
  python scripts/migrate_team_leagues.py --backup --fetch-from-api --api-key YOUR_KEY
"""

from __future__ import annotations

import argparse
import json
import shutil
import sqlite3
import sys
import urllib.error
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from tsdb_api import (
    DEFAULT_MAX_REQUESTS_PER_MINUTE,
    RateLimiter,
    lookup_team,
    parse_league_memberships,
    resolve_api_key,
)


def ensure_schema(conn: sqlite3.Connection) -> None:
    conn.execute(
        """
        CREATE TABLE IF NOT EXISTS team_leagues (
            team_id TEXT NOT NULL,
            league_id TEXT NOT NULL,
            is_primary INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (team_id, league_id),
            FOREIGN KEY (team_id) REFERENCES teams(id),
            FOREIGN KEY (league_id) REFERENCES leagues_lookup(id)
        )
        """
    )
    conn.execute(
        "CREATE INDEX IF NOT EXISTS idx_team_leagues_league_id ON team_leagues(league_id)"
    )


def seed_from_existing(conn: sqlite3.Connection) -> int:
    rows = conn.execute(
        """
        INSERT OR IGNORE INTO team_leagues (team_id, league_id, is_primary)
        SELECT id, league_id, 1 FROM teams
        WHERE league_id IS NOT NULL AND league_id != ''
        """
    )
    return rows.rowcount


def fetch_team_leagues(
    team_id: str, api_key: str, timeout: float, limiter: RateLimiter
) -> list[tuple[str, bool]]:
    team = lookup_team(team_id, api_key, timeout, limiter)
    if not team:
        return []
    return parse_league_memberships(team)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--db",
        type=Path,
        default=Path(__file__).resolve().parent.parent / "sports_resolver.db",
        help="Path to sports_resolver.db",
    )
    parser.add_argument(
        "--api-key",
        default=None,
        help="TheSportsDB API key (or THESPORTSDB_API_KEY)",
    )
    parser.add_argument(
        "--max-rpm",
        type=int,
        default=DEFAULT_MAX_REQUESTS_PER_MINUTE,
        help=f"Max API requests per 60s window (default: {DEFAULT_MAX_REQUESTS_PER_MINUTE})",
    )
    parser.add_argument("--timeout", type=float, default=20.0, help="HTTP timeout per API call")
    parser.add_argument("--backup", action="store_true", help="Create a .bak copy before writing")
    parser.add_argument("--dry-run", action="store_true", help="Report only; do not write")
    parser.add_argument(
        "--fetch-from-api",
        action="store_true",
        help="Fetch idLeague..idLeague7 from TheSportsDB for every team",
    )
    parser.add_argument(
        "--limit",
        type=int,
        default=0,
        help="Only process this many teams when fetching from API (0 = all)",
    )
    args = parser.parse_args()

    db_path = args.db.resolve()
    if not db_path.exists():
        print(f"Database not found: {db_path}", file=sys.stderr)
        return 1

    if args.backup and not args.dry_run:
        backup_path = db_path.with_suffix(db_path.suffix + ".bak")
        shutil.copy2(db_path, backup_path)
        print(f"Backup written to {backup_path}")

    conn = sqlite3.connect(db_path)
    ensure_schema(conn)

    if args.dry_run:
        existing = conn.execute("SELECT COUNT(*) FROM teams").fetchone()[0]
        print(
            f"Would ensure team_leagues table and seed up to {existing} rows from teams.league_id"
        )
        if args.fetch_from_api:
            limit = args.limit or existing
            print(f"Would fetch league memberships from API for {limit} teams")
        conn.close()
        return 0

    with conn:
        seeded = seed_from_existing(conn)
        print(f"Seeded {seeded} team_leagues rows from existing teams.league_id")

        if not args.fetch_from_api:
            total = conn.execute("SELECT COUNT(*) FROM team_leagues").fetchone()[0]
            print(
                f"team_leagues now has {total} rows. "
                "Run again with --fetch-from-api to add TSDB leagues 2-7."
            )
            conn.commit()
            return 0

        api_key = resolve_api_key(args.api_key)
        limiter = RateLimiter(max_requests=args.max_rpm)
        team_ids = [row[0] for row in conn.execute("SELECT id FROM teams ORDER BY id")]
        if args.limit:
            team_ids = team_ids[: args.limit]

        inserted = 0
        primary_updates = 0
        api_failed = 0

        for index, team_id in enumerate(team_ids, start=1):
            try:
                memberships = fetch_team_leagues(
                    team_id, api_key, args.timeout, limiter
                )
            except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
                api_failed += 1
                print(f"API failed for team {team_id}: {exc}", file=sys.stderr)
                continue

            if not memberships:
                api_failed += 1
                continue

            for league_id, is_primary in memberships:
                conn.execute(
                    """
                    INSERT INTO team_leagues (team_id, league_id, is_primary)
                    VALUES (?, ?, ?)
                    ON CONFLICT(team_id, league_id) DO UPDATE SET is_primary = excluded.is_primary
                    """,
                    (team_id, league_id, 1 if is_primary else 0),
                )
                inserted += 1

            primary_league = next(
                league_id for league_id, is_primary in memberships if is_primary
            )
            conn.execute(
                "UPDATE teams SET league_id = ? WHERE id = ?",
                (primary_league, team_id),
            )
            primary_updates += 1

            if index % 250 == 0:
                print(f"Processed {index}/{len(team_ids)} teams...")
                conn.commit()

    total = conn.execute("SELECT COUNT(*) FROM team_leagues").fetchone()[0]
    multi = conn.execute(
        """
        SELECT COUNT(*) FROM (
            SELECT team_id FROM team_leagues GROUP BY team_id HAVING COUNT(*) > 1
        )
        """
    ).fetchone()[0]

    print()
    print(f"API memberships upserted: {inserted}")
    print(f"Primary league_id updated: {primary_updates}")
    print(f"API failed/empty:         {api_failed}")
    print(f"team_leagues rows:        {total}")
    print(f"Teams in 2+ leagues:      {multi}")

    conn.close()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
