#!/usr/bin/env python3
"""
Rebuild sports_resolver.db team data from TheSportsDB (one API call per team).

For each team this script:
  1. Strips bogus FC/AFC/SC/Football Club aliases
  2. Sets alternative_names from TSDB strTeamAlternate (merged with kept locals)
  3. Upserts team_leagues from idLeague..idLeague7
  4. Sets teams.league_id to the TSDB primary league

Rate limited to 95 requests/minute by default (under the usual 100/min cap).

Usage:
  set THESPORTSDB_API_KEY=your_key
  python scripts/rebuild_db_from_api.py --backup

  python scripts/rebuild_db_from_api.py --backup --api-key YOUR_KEY --max-rpm 95
"""

from __future__ import annotations

import argparse
import shutil
import sqlite3
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from tsdb_api import (
    DEFAULT_MAX_REQUESTS_PER_MINUTE,
    RateLimiter,
    lookup_team,
    parse_league_memberships,
    resolve_api_key,
)
from fix_team_aliases import strip_bogus_aliases


def ensure_team_leagues_schema(conn: sqlite3.Connection) -> None:
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


def merge_alternates(
    name: str,
    short_name: str | None,
    existing: str | None,
    api_alternate: str | None,
) -> str | None:
    cleaned = strip_bogus_aliases(name, short_name, existing) or ""
    merged: list[str] = []
    seen: set[str] = set()

    for source in (cleaned, api_alternate or ""):
        for part in (p.strip() for p in source.split(",") if p.strip()):
            if part == name:
                continue
            if short_name and part == short_name:
                continue
            key = part.casefold()
            if key in seen:
                continue
            seen.add(key)
            merged.append(part)

    return ", ".join(merged) if merged else None


def format_eta(seconds: float) -> str:
    seconds = max(0, int(seconds))
    hours, rem = divmod(seconds, 3600)
    minutes, secs = divmod(rem, 60)
    if hours:
        return f"{hours}h {minutes}m {secs}s"
    if minutes:
        return f"{minutes}m {secs}s"
    return f"{secs}s"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--db",
        type=Path,
        default=Path(__file__).resolve().parent.parent / "sports_resolver.db",
    )
    parser.add_argument("--api-key", default=None, help="TheSportsDB API key (or THESPORTSDB_API_KEY)")
    parser.add_argument(
        "--max-rpm",
        type=int,
        default=DEFAULT_MAX_REQUESTS_PER_MINUTE,
        help=f"Max API requests per 60s window (default: {DEFAULT_MAX_REQUESTS_PER_MINUTE})",
    )
    parser.add_argument("--timeout", type=float, default=20.0, help="HTTP timeout per API call")
    parser.add_argument("--backup", action="store_true", help="Create sports_resolver.db.bak first")
    parser.add_argument("--limit", type=int, default=0, help="Only process N teams (0 = all)")
    parser.add_argument(
        "--skip",
        type=int,
        default=0,
        help="Skip first N teams (resume after an interrupted run)",
    )
    parser.add_argument("--commit-every", type=int, default=250, help="Commit every N teams")
    args = parser.parse_args()

    api_key = resolve_api_key(args.api_key)
    db_path = args.db.resolve()
    if not db_path.exists():
        print(f"Database not found: {db_path}", file=sys.stderr)
        return 1

    if args.backup:
        backup_path = db_path.with_suffix(db_path.suffix + ".bak")
        shutil.copy2(db_path, backup_path)
        print(f"Backup written to {backup_path}")

    limiter = RateLimiter(max_requests=args.max_rpm)
    conn = sqlite3.connect(db_path)
    ensure_team_leagues_schema(conn)

    rows = list(
        conn.execute(
            "SELECT id, name, short_name, alternative_names FROM teams ORDER BY id"
        )
    )
    if args.skip:
        rows = rows[args.skip :]
    if args.limit:
        rows = rows[: args.limit]

    total = len(rows)
    if args.skip:
        print(f"Resuming: skipped first {args.skip} teams")
    estimated_seconds = total * limiter.min_interval_seconds
    print(f"Teams to process: {total}")
    print(f"Rate limit:        {args.max_rpm} requests / 60s (~{limiter.min_interval_seconds:.2f}s between calls)")
    print(f"Estimated runtime: {format_eta(estimated_seconds)}")
    print()

    updated_aliases = 0
    updated_leagues = 0
    api_failed = 0
    started = time.monotonic()

    for index, (team_id, name, short_name, alternative_names) in enumerate(rows, start=1):
        try:
            team = lookup_team(team_id, api_key, args.timeout, limiter)
        except Exception as exc:
            api_failed += 1
            print(f"API failed for {team_id} ({name}): {exc}", file=sys.stderr)
            continue

        if not team or not isinstance(team, dict):
            api_failed += 1
            print(f"API returned no team data for {team_id} ({name})", file=sys.stderr)
            continue

        api_alternate = (team.get("strTeamAlternate") or "").strip()
        new_alternate = merge_alternates(name, short_name, alternative_names, api_alternate)
        if new_alternate != alternative_names:
            conn.execute(
                "UPDATE teams SET alternative_names = ? WHERE id = ?",
                (new_alternate, team_id),
            )
            updated_aliases += 1

        memberships = parse_league_memberships(team)
        if memberships:
            for league_id, is_primary in memberships:
                conn.execute(
                    """
                    INSERT INTO team_leagues (team_id, league_id, is_primary)
                    VALUES (?, ?, ?)
                    ON CONFLICT(team_id, league_id) DO UPDATE SET is_primary = excluded.is_primary
                    """,
                    (team_id, league_id, 1 if is_primary else 0),
                )
            primary_league = next(lid for lid, is_primary in memberships if is_primary)
            conn.execute("UPDATE teams SET league_id = ? WHERE id = ?", (primary_league, team_id))
            updated_leagues += 1

        if index % args.commit_every == 0:
            conn.commit()
            elapsed = time.monotonic() - started
            rate = index / elapsed if elapsed > 0 else 0
            remaining = (total - index) / rate if rate > 0 else 0
            print(
                f"[{index}/{total}] aliases={updated_aliases} leagues={updated_leagues} "
                f"failed={api_failed} ETA={format_eta(remaining)}"
            )

    conn.commit()

    league_rows = conn.execute("SELECT COUNT(*) FROM team_leagues").fetchone()[0]
    multi_league = conn.execute(
        """
        SELECT COUNT(*) FROM (
            SELECT team_id FROM team_leagues GROUP BY team_id HAVING COUNT(*) > 1
        )
        """
    ).fetchone()[0]

    elapsed = time.monotonic() - started
    print()
    print(f"Finished in {format_eta(elapsed)}")
    print(f"Alias rows updated:   {updated_aliases}")
    print(f"League rows updated:  {updated_leagues}")
    print(f"API failed/empty:     {api_failed}")
    print(f"team_leagues rows:    {league_rows}")
    print(f"Teams in 2+ leagues:  {multi_league}")

    conn.close()
    return 0 if api_failed == 0 else 2


if __name__ == "__main__":
    raise SystemExit(main())
