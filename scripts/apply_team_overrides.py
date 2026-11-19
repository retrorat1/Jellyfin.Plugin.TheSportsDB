#!/usr/bin/env python3
"""
Apply durable team short_name / alternative_names overrides to sports_resolver.db.

Overrides live in scripts/team_overrides.json so monthly rebuilds from TheSportsDB
cannot wipe hand-maintained aliases (e.g. NHL Utah Mammoth short_name UTA while
the API still returns UHC).

Usage:
  python scripts/apply_team_overrides.py
  python scripts/apply_team_overrides.py --db /path/to/sports_resolver.db --dry-run
"""

from __future__ import annotations

import argparse
import json
import sqlite3
import sys
from pathlib import Path

DEFAULT_OVERRIDES = Path(__file__).resolve().parent / "team_overrides.json"


def load_overrides(path: Path) -> list[dict]:
    data = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(data, list):
        raise ValueError(f"Overrides file must be a JSON list: {path}")
    return data


def merge_ensure_alternates(
    name: str | None,
    short_name: str | None,
    existing: str | None,
    ensure: list[str] | None,
) -> str | None:
    merged: list[str] = []
    seen: set[str] = set()

    def add(part: str) -> None:
        part = part.strip()
        if not part:
            return
        if name and part == name:
            return
        if short_name and part == short_name:
            return
        key = part.casefold()
        if key in seen:
            return
        seen.add(key)
        merged.append(part)

    for part in (p.strip() for p in (existing or "").split(",") if p.strip()):
        add(part)
    for part in ensure or []:
        add(part)

    return ", ".join(merged) if merged else None


def apply_overrides(
    conn: sqlite3.Connection, overrides: list[dict]
) -> list[tuple[str, str]]:
    """Apply overrides. Returns list of (team_id, summary) for changed rows."""
    changes: list[tuple[str, str]] = []

    for entry in overrides:
        team_id = str(entry["id"])
        row = conn.execute(
            "SELECT name, short_name, alternative_names FROM teams WHERE id = ?",
            (team_id,),
        ).fetchone()
        if row is None:
            print(f"WARNING: override team id {team_id} not found in DB", file=sys.stderr)
            continue

        name, short_name, alternative_names = row
        new_short = entry.get("short_name", short_name)
        if new_short is not None:
            new_short = str(new_short).strip() or short_name

        ensure = entry.get("ensure_alternative_names") or []
        if not isinstance(ensure, list):
            raise ValueError(
                f"ensure_alternative_names must be a list for team {team_id}"
            )

        # If short_name is changing, keep the previous code as an alias.
        ensure_list = [str(x) for x in ensure]
        if short_name and new_short and short_name != new_short:
            ensure_list = [short_name, *ensure_list]

        new_alts = merge_ensure_alternates(name, new_short, alternative_names, ensure_list)

        if new_short == short_name and new_alts == alternative_names:
            continue

        conn.execute(
            "UPDATE teams SET short_name = ?, alternative_names = ? WHERE id = ?",
            (new_short, new_alts, team_id),
        )
        summary = (
            f"{name}: short_name {short_name!r}->{new_short!r}; "
            f"alts {alternative_names!r}->{new_alts!r}"
        )
        changes.append((team_id, summary))

    return changes


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--db",
        type=Path,
        default=Path(__file__).resolve().parent.parent / "sports_resolver.db",
        help="Path to sports_resolver.db (default: repo root)",
    )
    parser.add_argument(
        "--overrides",
        type=Path,
        default=DEFAULT_OVERRIDES,
        help=f"Overrides JSON (default: {DEFAULT_OVERRIDES.name})",
    )
    parser.add_argument("--dry-run", action="store_true", help="Report without writing")
    args = parser.parse_args()

    db_path = args.db.resolve()
    overrides_path = args.overrides.resolve()
    if not db_path.exists():
        print(f"Database not found: {db_path}", file=sys.stderr)
        return 1
    if not overrides_path.exists():
        print(f"Overrides file not found: {overrides_path}", file=sys.stderr)
        return 1

    overrides = load_overrides(overrides_path)
    conn = sqlite3.connect(db_path)
    try:
        changes = apply_overrides(conn, overrides)
        if args.dry_run:
            conn.rollback()
            print(f"Dry run — {len(changes)} team(s) would change:")
        else:
            conn.commit()
            print(f"Updated {len(changes)} team(s) in {db_path}:")
        for team_id, summary in changes:
            print(f"  {team_id}: {summary}")
        if not changes:
            print("No changes needed.")
    finally:
        conn.close()

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
