"""
P1 — join.py

For each tk event from SOURCE B (Claude Code transcripts), find the matching
entry from SOURCE A (tk own-log) and attach compaction metrics.

Usage:
    python3 Stats/join.py [--from ISO] [--to ISO]

Outputs:
    Stats/model.json  — full joined model for dashboard rendering
    Prints a concise summary to stdout.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

# Allow running from the repo root without installing
sys.path.insert(0, str(Path(__file__).parent))
from ingest import load_sessions, _parse_ts, _parse_cli_dt  # type: ignore[import]


# ---------------------------------------------------------------------------
# Own-log loading
# ---------------------------------------------------------------------------

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


# ---------------------------------------------------------------------------
# Joining logic
# ---------------------------------------------------------------------------

MATCH_WINDOW = timedelta(seconds=30)


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
    """Classify: WIN / NET_NEGATIVE / UNKNOWN."""
    raw_chars = event.get("raw_chars")
    if raw_chars is None:
        return "UNKNOWN"
    shown = event.get("shown_chars")
    if shown is None:
        return "UNKNOWN"
    if shown < raw_chars:
        return "WIN"
    return "NET_NEGATIVE"


# ---------------------------------------------------------------------------
# Main join
# ---------------------------------------------------------------------------

def run_join(
    from_dt: datetime | None = None,
    to_dt: datetime | None = None,
) -> dict:
    """
    Load sessions + own-log, join, classify outcomes, return the full model dict.
    """
    sessions = load_sessions(from_dt, to_dt)
    own_log = load_own_log()
    index = _build_own_log_index(own_log)

    total_tk = 0
    matched = 0
    outcome_counts: dict[str, int] = {"WIN": 0, "NET_NEGATIVE": 0, "UNKNOWN": 0}
    agg_raw_win = 0
    agg_shown_win = 0
    agg_raw_neg = 0
    agg_shown_neg = 0

    # Work out the date range from events
    all_ts: list[str] = []
    for session in sessions:
        for event in session["events"]:
            if event.get("ts"):
                all_ts.append(event["ts"])

    # Fix 2: greedy 1:1 assignment — process tk events in timestamp order,
    # mark each matched own-log entry consumed so it cannot be reused.
    used: set[int] = set()

    # Collect all tk events across sessions (with back-reference to their session)
    tk_events_ordered: list[tuple[dict, dict]] = []  # (session, event)
    for session in sessions:
        for event in session["events"]:
            if event.get("is_tk"):
                tk_events_ordered.append((session, event))

    # Sort by timestamp ascending (oldest first)
    tk_events_ordered.sort(key=lambda x: x[1].get("ts") or "")

    # Process tk events in timestamp order for 1:1 matching, then reconstruct
    # session event lists in their original order.
    tk_enriched: dict[int, dict] = {}  # id(original event) -> enriched ev
    for session, event in tk_events_ordered:
        total_tk += 1
        match = _find_match(event, index, used)

        ev = {k: v for k, v in event.items() if k != "tool_use_id"}  # Fix 6
        if match:
            matched += 1
            used.add(id(match))  # Fix 2: mark consumed
            ev["raw_chars"] = match.get("rawChars")
            ev["raw_lines"] = match.get("rawLines")
            ev["shown_chars_ownlog"] = match.get("shownChars")
            ev["duration_ms"] = match.get("durationMs")
            ev["exit"] = match.get("exit")
            ev["detail"] = match.get("detail")
        else:
            ev["raw_chars"] = None
            ev["raw_lines"] = None
            ev["shown_chars_ownlog"] = None
            ev["duration_ms"] = None
            ev["exit"] = None
            ev["detail"] = None

        outcome = _outcome(ev)
        ev["outcome"] = outcome
        outcome_counts[outcome] += 1

        if outcome == "WIN":
            rc = ev["raw_chars"] or 0
            sc = ev["shown_chars"] or 0
            agg_raw_win += rc
            agg_shown_win += sc
        elif outcome == "NET_NEGATIVE":
            rc = ev["raw_chars"] or 0
            sc = ev["shown_chars"] or 0
            agg_raw_neg += rc
            agg_shown_neg += sc

        tk_enriched[id(event)] = ev

    # Reconstruct session event lists in original order
    for session in sessions:
        enriched_events: list[dict] = []
        for event in session["events"]:
            if event.get("is_tk"):
                enriched_events.append(tk_enriched[id(event)])
            else:
                # Fix 6: drop tool_use_id from non-tk events too
                ev = {k: v for k, v in event.items() if k != "tool_use_id"}
                enriched_events.append(ev)
        session["events"] = enriched_events

    # Build session models (Fix 6: tool_use_id already stripped above)
    session_models = []
    for session in sessions:
        sm = {k: v for k, v in session.items() if k != "events"}
        sm["ts_start"] = session.get("ts_start")
        sm["ts_end"] = session.get("ts_end")
        sm["events"] = session["events"]
        session_models.append(sm)

    match_rate = matched / total_tk if total_tk else 0.0

    model = {
        "generated_for_range": {
            "from": from_dt.isoformat() if from_dt else None,
            "to": to_dt.isoformat() if to_dt else None,
            "actual_from": min(all_ts) if all_ts else None,
            "actual_to": max(all_ts) if all_ts else None,
        },
        "match_stats": {
            "total_tk_events": total_tk,
            "matched": matched,
            "unmatched": total_tk - matched,
            "match_rate": round(match_rate, 4),
        },
        "outcome_counts": outcome_counts,
        "aggregates": {
            "win": {
                "raw_chars": agg_raw_win,
                "shown_chars": agg_shown_win,
                "saved_chars": agg_raw_win - agg_shown_win,
            },
            "net_negative": {
                "raw_chars": agg_raw_neg,
                "shown_chars": agg_shown_neg,
                "extra_chars": agg_shown_neg - agg_raw_neg,
            },
        },
        "sessions": session_models,
    }
    return model


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main() -> None:
    parser = argparse.ArgumentParser(description="Join tk events with own-log")
    parser.add_argument("--from", dest="from_dt", metavar="ISO")
    parser.add_argument("--to", dest="to_dt", metavar="ISO")
    args = parser.parse_args()

    from_dt = _parse_cli_dt(args.from_dt) if args.from_dt else None
    to_dt = _parse_cli_dt(args.to_dt, end_of_day=True) if args.to_dt else None

    model = run_join(from_dt, to_dt)

    # Write model.json
    result_dir = Path(__file__).parent / "result"
    result_dir.mkdir(exist_ok=True)
    model_path = result_dir / "model.json"
    with open(model_path, "w", encoding="utf-8") as f:
        json.dump(model, f, indent=2)

    ms = model["match_stats"]
    oc = model["outcome_counts"]
    agg = model["aggregates"]
    rng = model["generated_for_range"]
    sessions = model["sessions"]
    n_sessions = len(sessions)
    n_tk = ms["total_tk_events"]

    print(f"Sessions:      {n_sessions}")
    print(f"tk events:     {n_tk}")
    print(f"Matched:       {ms['matched']} / {n_tk}  ({ms['match_rate']*100:.1f}%)")
    print()
    print("Outcomes:")
    print(f"  WIN:          {oc['WIN']}")
    print(f"  NET_NEGATIVE: {oc['NET_NEGATIVE']}")
    print(f"  UNKNOWN:      {oc['UNKNOWN']}")
    print()
    print("Aggregates (matched WIN/NET_NEGATIVE events):")
    w = agg["win"]
    n = agg["net_negative"]
    print(f"  WIN:          raw={w['raw_chars']:,}  shown={w['shown_chars']:,}  saved={w['saved_chars']:,} chars")
    print(f"  NET_NEGATIVE: raw={n['raw_chars']:,}  shown={n['shown_chars']:,}  extra={n['extra_chars']:,} chars")
    print()
    print(f"Date range:    {rng['actual_from'] and rng['actual_from'][:10]} → {rng['actual_to'] and rng['actual_to'][:10]}")
    print(f"model.json written to {model_path}")


if __name__ == "__main__":
    main()
