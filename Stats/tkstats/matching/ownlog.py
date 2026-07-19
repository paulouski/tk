"""
tk own-log loading and own-log matching.

For each tk event from a transcript source, find the matching entry from
tk's own log (~/.local/state/tk/analytics/*.jsonl) and attach its compaction
metrics (raw_chars vs shown_chars), then classify the outcome.
"""

from __future__ import annotations

import hashlib
import json
import os
from pathlib import Path

from tkstats.config import MATCH_WINDOW
from tkstats.timeparse import _parse_ts


def _state_dir() -> Path:
    xdg = os.environ.get("XDG_STATE_HOME")
    if xdg:
        return Path(xdg)
    return Path.home() / ".local" / "state"


def load_own_log() -> list[dict]:
    """Load all own-log JSONL entries from ~/.local/state/tk/analytics/."""
    analytics_dir = _state_dir() / "tk" / "analytics"
    entries: list[dict] = []
    if not analytics_dir.exists():
        return entries
    for path in sorted(analytics_dir.glob("*.jsonl")):
        try:
            with open(path, encoding="utf-8", errors="replace") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        entries.append(json.loads(line))
                    except json.JSONDecodeError:
                        continue
        except OSError:
            continue
    return entries


def _ws_for_cwd(cwd: str) -> str:
    """Compute workspace hash: first 16 lowercase hex chars of SHA256(utf8(cwd))."""
    return hashlib.sha256(cwd.encode("utf-8")).hexdigest()[:16]


def _build_own_log_index(own_log: list[dict]) -> dict[str, list[dict]]:
    """
    Build an index: (ws, command, sub_or_None) -> sorted list of entries.
    sub is normalized to None when absent/null.
    """
    index: dict[str, list[dict]] = {}
    for entry in own_log:
        ws = entry.get("ws", "")
        cmd = entry.get("command", "")
        sub = entry.get("sub") or None
        key = f"{ws}\x00{cmd}\x00{sub}"
        if key not in index:
            index[key] = []
        index[key].append(entry)
    return index


def _find_match(
    event: dict,
    index: dict[str, list[dict]],
    used: set[int],
) -> dict | None:
    """
    Find the nearest own-log entry matching ws+command+sub within MATCH_WINDOW.
    Skips entries already consumed (tracked by id() in `used`).
    Returns the matching entry or None.
    """
    # Fix 3: use effective_cwd when available (set by ingest for cd-prefix commands)
    cwd = event.get("effective_cwd") or event.get("cwd") or ""
    if not cwd:
        return None
    ws = _ws_for_cwd(cwd)

    cmd = event.get("tk_command") or ""
    sub = event.get("tk_sub") or None
    key = f"{ws}\x00{cmd}\x00{sub}"

    candidates = index.get(key)
    if not candidates:
        return None

    event_dt = _parse_ts(event.get("ts"))
    if event_dt is None:
        return None

    # Find nearest unused entry within window
    best: dict | None = None
    best_delta = MATCH_WINDOW

    for entry in candidates:
        if id(entry) in used:
            continue
        entry_dt = _parse_ts(entry.get("ts"))
        if entry_dt is None:
            continue
        delta = abs(event_dt - entry_dt)
        if delta <= best_delta:
            best_delta = delta
            best = entry

    return best


def _outcome(event: dict) -> str:
    """Classify: WIN / NEUTRAL / NET_NEGATIVE / UNKNOWN."""
    raw_chars = event.get("raw_chars")
    if raw_chars is None:
        return "UNKNOWN"
    shown = event.get("shown_chars_ownlog")
    if shown is None:
        return "UNKNOWN"
    if shown < raw_chars:
        return "WIN"
    if shown == raw_chars:
        return "NEUTRAL"
    return "NET_NEGATIVE"
