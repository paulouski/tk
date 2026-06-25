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
from pricing import cost_tokens_by_type, get_unknown_models  # type: ignore[import]


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


# ---------------------------------------------------------------------------
# Group rollup (cost economics of delegation)
# ---------------------------------------------------------------------------

def _build_groups(session_models: list[dict]) -> list[dict]:
    """
    Group sessions by group_id and compute delegation economics per group.
    """
    from collections import defaultdict

    # Collect by group_id
    by_group: dict[str, list[dict]] = defaultdict(list)
    for sm in session_models:
        gid = sm.get("group_id") or sm.get("session_id", "")
        by_group[gid].append(sm)

    groups: list[dict] = []
    for gid, members in by_group.items():
        mains = [m for m in members if not m.get("is_subagent")]
        subs  = [m for m in members if m.get("is_subagent")]

        main_sm = mains[0] if mains else None
        main_session_id = main_sm.get("session_id") if main_sm else None

        def _cost(sm: dict) -> dict:
            return sm.get("cost") or {}

        def _tok(sm: dict) -> dict:
            return _cost(sm).get("tokens") or {}

        def _usd_by(sm: dict) -> dict:
            return _cost(sm).get("usd_by_type") or {}

        main_usd = _cost(main_sm).get("usd", 0.0) if main_sm else 0.0
        subs_usd = sum(_cost(s).get("usd", 0.0) for s in subs)
        total_usd = main_usd + subs_usd

        main_output_usd = _usd_by(main_sm).get("output", 0.0) if main_sm else 0.0
        main_read_usd = (
            (_usd_by(main_sm).get("input", 0.0) or 0.0)
            + (_usd_by(main_sm).get("cache_read", 0.0) or 0.0)
            + (_usd_by(main_sm).get("cache_write", 0.0) or 0.0)
        ) if main_sm else 0.0

        subs_output_usd = sum(_usd_by(s).get("output", 0.0) for s in subs)
        subs_read_usd = sum(
            (_usd_by(s).get("input", 0.0) or 0.0)
            + (_usd_by(s).get("cache_read", 0.0) or 0.0)
            + (_usd_by(s).get("cache_write", 0.0) or 0.0)
            for s in subs
        )

        # Counterfactual inline estimate (upper-bound heuristic)
        inline_estimate_usd: float | None = None
        delegation_delta_usd: float | None = None
        if main_sm and subs:
            main_model = (_cost(main_sm).get("model") or "").strip()
            if main_model:
                # Sub output tokens, priced at main's output rate
                sub_out_tok = sum(_tok(s).get("output", 0) for s in subs)
                sub_in_tok  = sum(_tok(s).get("input", 0) for s in subs)
                sub_cr_tok  = sum(_tok(s).get("cache_read", 0) for s in subs)
                inline_sub_cost = (
                    cost_tokens_by_type(sub_out_tok, "output", main_model)
                    + cost_tokens_by_type(sub_in_tok + sub_cr_tok, "input", main_model)
                )
                inline_estimate_usd = round(main_usd + inline_sub_cost, 6)
                delegation_delta_usd = round(inline_estimate_usd - total_usd, 6)

        groups.append({
            "group_id": gid,
            "main_session_id": main_session_id,
            "main_usd": round(main_usd, 6),
            "subs_usd": round(subs_usd, 6),
            "total_usd": round(total_usd, 6),
            "n_subs": len(subs),
            "main_output_usd": round(main_output_usd, 6),
            "main_read_usd": round(main_read_usd, 6),
            "subs_output_usd": round(subs_output_usd, 6),
            "subs_read_usd": round(subs_read_usd, 6),
            "inline_estimate_usd": inline_estimate_usd,
            "delegation_delta_usd": delegation_delta_usd,
            "assumptions": (
                "inline_estimate is an UPPER-BOUND heuristic: it prices sub OUTPUT at main's "
                "output rate and sub INPUT/cache_read at main's input rate (assumes main would "
                "re-read the same context). It does NOT model that some sub context was already "
                "in main's window, nor the review-read of sub output (which already happened "
                "and is in main_usd). Treat delegation_delta as indicative, not authoritative."
            ) if subs else None,
        })

    # Sort by total_usd descending
    groups.sort(key=lambda g: g["total_usd"], reverse=True)
    return groups


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
    outcome_counts: dict[str, int] = {"WIN": 0, "NEUTRAL": 0, "NET_NEGATIVE": 0, "UNKNOWN": 0}
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
            ev["tk_version"] = match.get("tkVersion")
            ev["result_count"] = match.get("resultCount")
        else:
            ev["raw_chars"] = None
            ev["raw_lines"] = None
            ev["shown_chars_ownlog"] = None
            ev["duration_ms"] = None
            ev["exit"] = None
            ev["detail"] = None
            ev["tk_version"] = None
            ev["result_count"] = None

        outcome = _outcome(ev)
        ev["outcome"] = outcome
        outcome_counts[outcome] += 1

        if outcome == "WIN":
            rc = ev["raw_chars"] or 0
            sc = ev["shown_chars_ownlog"] or 0
            agg_raw_win += rc
            agg_shown_win += sc
        elif outcome == "NET_NEGATIVE":
            rc = ev["raw_chars"] or 0
            sc = ev["shown_chars_ownlog"] or 0
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
        # Ensure new grouping/cost fields are present even if ingest didn't emit them
        sm.setdefault("parent_session_id", None)
        sm.setdefault("group_id", sm.get("session_id", ""))
        sm.setdefault("cost", {"model": "", "usd": 0.0, "tokens": {}, "usd_by_type": {}})
        sm["events"] = session["events"]
        session_models.append(sm)

    # Build group rollup
    groups = _build_groups(session_models)

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
        "groups": groups,
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
    print(f"  NEUTRAL:      {oc['NEUTRAL']}")
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
