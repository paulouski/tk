"""
Shared ISO-8601 timestamp parsing and date-range filtering.

Used by every ingestion adapter (claude, codex, opencode, hooklog), the
matching pipeline, and tkstats/bashanalytics/readcost.py.
"""

from __future__ import annotations

import re as _re
from datetime import datetime, timezone


def _parse_ts(ts_str: str | None) -> datetime | None:
    """Parse an ISO-8601 timestamp to a UTC-aware datetime.

    Handles:
    - Trailing 'Z' (replace with +00:00)
    - Fractional seconds with more than 6 digits (.NET emits 7, Python fromisoformat
      only accepts up to 6) — truncated to 6.
    """
    if not ts_str:
        return None
    s = ts_str.strip()
    if s.endswith("Z"):
        s = s[:-1] + "+00:00"
    # Truncate fractional seconds to at most 6 digits (Python fromisoformat limit).
    # Match the decimal point and digits before the timezone part.
    s = _re.sub(r"(\.\d{6})\d+", r"\1", s)
    try:
        dt = datetime.fromisoformat(s)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except ValueError:
        return None


def _parse_cli_dt(s: str, end_of_day: bool = False) -> datetime:
    """Parse a CLI --from/--to value (date or datetime).

    When end_of_day=True and a bare date (no time) is given, the returned
    datetime is 23:59:59 UTC on that date so the entire day is included.
    """
    bare_date_fmt = "%Y-%m-%d"
    for fmt in ("%Y-%m-%dT%H:%M:%S", "%Y-%m-%dT%H:%M", bare_date_fmt):
        try:
            dt = datetime.strptime(s, fmt)
            if end_of_day and fmt == bare_date_fmt:
                dt = dt.replace(hour=23, minute=59, second=59)
            return dt.replace(tzinfo=timezone.utc)
        except ValueError:
            continue
    raise ValueError(f"Cannot parse datetime: {s!r}")


def _in_range(ts_str: str | None, from_dt: datetime | None, to_dt: datetime | None) -> bool:
    if from_dt is None and to_dt is None:
        return True
    dt = _parse_ts(ts_str)
    if dt is None:
        return True  # include events without timestamps
    if from_dt and dt < from_dt:
        return False
    if to_dt and dt > to_dt:
        return False
    return True
