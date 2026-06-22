"""Shared TheSportsDB API helpers with sliding-window rate limiting."""

from __future__ import annotations

import json
import os
import time
import urllib.error
import urllib.request
from collections import deque

DEFAULT_MAX_REQUESTS_PER_MINUTE = 95  # stay under the usual 100/min cap
LOOKUP_TEAM_URL = "https://www.thesportsdb.com/api/v1/json/{api_key}/lookupteam.php?id={team_id}"
LEAGUE_SLOTS = ("idLeague", "idLeague2", "idLeague3", "idLeague4", "idLeague5", "idLeague6", "idLeague7")


class RateLimiter:
    """Sliding-window limiter: at most N requests per 60-second window."""

    def __init__(self, max_requests: int = DEFAULT_MAX_REQUESTS_PER_MINUTE, window_seconds: float = 60.0):
        self.max_requests = max_requests
        self.window_seconds = window_seconds
        self._timestamps: deque[float] = deque()

    def wait(self) -> None:
        now = time.monotonic()

        while self._timestamps and self._timestamps[0] <= now - self.window_seconds:
            self._timestamps.popleft()

        if len(self._timestamps) >= self.max_requests:
            sleep_for = self.window_seconds - (now - self._timestamps[0]) + 0.05
            if sleep_for > 0:
                time.sleep(sleep_for)

        self._timestamps.append(time.monotonic())

    @property
    def min_interval_seconds(self) -> float:
        return self.window_seconds / self.max_requests


def resolve_api_key(cli_value: str | None) -> str:
    key = (cli_value or os.environ.get("THESPORTSDB_API_KEY") or "").strip()
    if not key:
        raise SystemExit(
            "TheSportsDB API key required. Pass --api-key or set THESPORTSDB_API_KEY."
        )
    return key


def lookup_team(team_id: str, api_key: str, timeout: float, limiter: RateLimiter) -> dict | None:
    url = LOOKUP_TEAM_URL.format(api_key=api_key, team_id=team_id)
    attempts = 0

    while attempts < 5:
        limiter.wait()
        attempts += 1
        try:
            with urllib.request.urlopen(url, timeout=timeout) as response:
                payload = json.load(response)
        except urllib.error.HTTPError as exc:
            if exc.code == 429:
                retry_after = exc.headers.get("Retry-After")
                sleep_for = float(retry_after) if retry_after else min(30.0, 2.0 ** attempts)
                time.sleep(sleep_for)
                continue
            raise

        teams = payload.get("teams") or []
        if not teams:
            return None

        team = teams[0]
        if not isinstance(team, dict):
            return None

        return team

    return None


def parse_league_memberships(team: dict) -> list[tuple[str, bool]]:
    memberships: list[tuple[str, bool]] = []
    seen: set[str] = set()

    for index, slot in enumerate(LEAGUE_SLOTS):
        league_id = (team.get(slot) or "").strip()
        if not league_id or league_id in seen:
            continue
        seen.add(league_id)
        memberships.append((league_id, index == 0))

    return memberships
