#!/usr/bin/env python3
"""
Strip bulk-generated FC/AFC/SC/Football Club aliases from sports_resolver.db
and optionally refresh alternative_names from TheSportsDB lookupteam API.
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
from tsdb_api import DEFAULT_MAX_REQUESTS_PER_MINUTE, RateLimiter, lookup_team, resolve_api_key

BOGUS_SUFFIXES = (" FC", " AFC", " SC", " Football Club")


def strip_bogus_aliases(
    name: str, short_name: str | None, alternative_names: str | None
) -> str | None:
    if not alternative_names:
        return alternative_names

    bogus = tuple(f"{name}{suffix}" for suffix in BOGUS_SUFFIXES)
    cleaned: list[str] = []
    seen: set[str] = set()

    for part in (p.strip() for p in alternative_names.split(",") if p.strip()):
        if part in bogus:
            continue
        if short_name and part == short_name:
            continue
        if part == name:
            continue
        key = part.casefold()
        if key in seen:
            continue
        seen.add(key)
        cleaned.append(part)

    return ", ".join(cleaned) if cleaned else None


def fetch_thesportsdb_alternate(
    team_id: str, api_key: str, timeout: float, limiter: RateLimiter
) -> str | None:
    team = lookup_team(team_id, api_key, timeout, limiter)
    if not team:
        return None
    alternate = (team.get("strTeamAlternate") or "").strip()
    return alternate or None


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--db",
        type=Path,
        default=Path(__file__).resolve().parent.parent / "sports_resolver.db",
        help="Path to sports_resolver.db (default: repo root)",
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
    parser.add_argument("--dry-run", action="store_true", help="Report changes without writing")
    parser.add_argument(
        "--strip-only",
        action="store_true",
        help="Only remove bogus aliases; do not call TheSportsDB for empty rows",
    )
    args = parser.parse_args()
    api_key = resolve_api_key(args.api_key) if not args.strip_only else (args.api_key or "3")
    limiter = RateLimiter(max_requests=args.max_rpm)

    db_path = args.db.resolve()
    if not db_path.exists():
        print(f"Database not found: {db_path}", file=sys.stderr)
        return 1

    if args.backup and not args.dry_run:
        backup_path = db_path.with_suffix(db_path.suffix + ".bak")
        shutil.copy2(db_path, backup_path)
        print(f"Backup written to {backup_path}")

    conn = sqlite3.connect(db_path)
    rows = list(
        conn.execute(
            "SELECT id, name, short_name, alternative_names, sport_id FROM teams ORDER BY id"
        )
    )

    updates: list[tuple[str | None, str]] = []
    stripped_empty = 0
    api_refreshed = 0
    api_failed = 0
    unchanged = 0

    for index, (team_id, name, short_name, alternative_names, sport_id) in enumerate(
        rows, start=1
    ):
        cleaned = strip_bogus_aliases(name, short_name, alternative_names)
        final = cleaned

        if not final and not args.strip_only:
            try:
                fetched = fetch_thesportsdb_alternate(
                    team_id, api_key, args.timeout, limiter
                )
                if fetched:
                    final = fetched
                    api_refreshed += 1
                else:
                    api_failed += 1
            except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as exc:
                api_failed += 1
                print(f"API failed for {team_id} ({name}): {exc}", file=sys.stderr)

        if not final and cleaned != alternative_names:
            stripped_empty += 1

        if final == alternative_names:
            unchanged += 1
            continue

        updates.append((final, team_id))

        if index % 500 == 0:
            print(f"Processed {index}/{len(rows)} teams...")

    print()
    print(f"Teams scanned:     {len(rows)}")
    print(f"Unchanged:         {unchanged}")
    print(f"To update:         {len(updates)}")
    print(f"Empty after strip: {stripped_empty}")
    if not args.strip_only:
        print(f"API refreshed:     {api_refreshed}")
        print(f"API failed/empty:  {api_failed}")

    if args.dry_run:
        print("\nDry run — no changes written.")
        conn.close()
        return 0

    with conn:
        conn.executemany(
            "UPDATE teams SET alternative_names = ? WHERE id = ?", updates
        )

    conn.close()
    print(f"\nUpdated {len(updates)} teams in {db_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
