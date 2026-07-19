"""
Matching pipeline orchestrator.

For each tk event from a transcript source, find the matching own-log entry
and attach compaction metrics, classify the outcome, infer tk_version, and
roll up sub-agent delegation economics. This is the P1 join step of the
Stats pipeline: ingest -> join (this module) -> detect -> report.

Usage:
    python3 Stats/join.py [--from ISO] [--to ISO] [--source ...]

Outputs:
    Stats/result/model.json  — full joined model for dashboard rendering
    Prints a concise summary to stdout.
"""

from __future__ import annotations

import argparse
import json
from datetime import datetime, timezone
from pathlib import Path

from tkstats.ingestion.claude import load_sessions
from tkstats.ingestion.codex import load_sessions as _load_codex_sessions
from tkstats.ingestion.hooklog import load_interventions
from tkstats.ingestion.opencode import load_sessions as _load_oc_sessions
from tkstats.matching.groups import _build_groups
from tkstats.matching.ownlog import _build_own_log_index, _find_match, _outcome, load_own_log
from tkstats.matching.versions import _infer_version
from tkstats.timeparse import _parse_cli_dt


def run_join(
    from_dt: datetime | None = None,
    to_dt: datetime | None = None,
    source: str = "claude",
) -> dict:
    """
    Load sessions + own-log, join, classify outcomes, return the full model dict.

    source: "claude" (default), "opencode", "codex", "both", or "all".
    """
    if source == "claude":
        sessions = load_sessions(from_dt, to_dt)
    elif source == "opencode":
        sessions = _load_oc_sessions(from_dt, to_dt)
    elif source == "codex":
        sessions = _load_codex_sessions(from_dt, to_dt)
    elif source == "both":
        sessions = load_sessions(from_dt, to_dt) + _load_oc_sessions(from_dt, to_dt)
    elif source == "all":
        sessions = (
            load_sessions(from_dt, to_dt)
            + _load_oc_sessions(from_dt, to_dt)
            + _load_codex_sessions(from_dt, to_dt)
        )
    else:
        raise ValueError(
            f"Unknown source: {source!r} (expected claude|opencode|codex|both|all)"
        )
    own_log = load_own_log()
    index = _build_own_log_index(own_log)

    # S4: load hook-log interventions (cap-read/nudge/route-tk/route-rtk) as a
    # flat, model-level list (each record already carries session_id). Kept
    # flat rather than attached per session dict: a resumed session can span
    # multiple transcript files that all share the same session_id (one
    # session dict per file), so per-session attachment would duplicate each
    # intervention once per file. detect.py's adoption pass groups these by
    # session_id itself, against that session_id's events merged across every
    # file that shares it. Sessions from a non-Claude source (opencode)
    # simply get no matches (different session_id namespace).
    interventions = (
        load_interventions(from_dt, to_dt)
        if source in ("claude", "both", "all")
        else []
    )
    for intervention in interventions:
        ts_epoch = intervention.get("ts_epoch")
        if ts_epoch is not None:
            ts = datetime.fromtimestamp(ts_epoch, tz=timezone.utc).isoformat()
            intervention["tk_version"] = _infer_version(ts)

    total_tk = 0
    matched = 0
    raw_baseline_events = 0
    total_tk_invocations = 0
    matched_invocations = 0
    raw_baseline_invocations = 0
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
        ev = {k: v for k, v in event.items() if k != "tool_use_id"}  # Fix 6
        invocations = event.get("tk_invocations") or [{
            key: event.get(key)
            for key in (
                "tk_command", "tk_sub", "tk_flags", "tk_operands",
                "tk_operand_values", "tk_detail", "effective_cwd",
            )
        }]
        enriched_invocations: list[dict] = []
        for invocation in invocations:
            total_tk_invocations += 1
            match_event = {**event, **invocation}
            match = _find_match(match_event, index, used)
            child = dict(invocation)
            child["own_log_matched"] = match is not None
            if match:
                matched_invocations += 1
                used.add(id(match))
                child["raw_chars"] = match.get("rawChars")
                child["raw_lines"] = match.get("rawLines")
                child["shown_chars_ownlog"] = match.get("shownChars")
                child["duration_ms"] = match.get("durationMs")
                child["exit"] = match.get("exit")
                child["detail"] = match.get("detail")
                child["tk_version"] = match.get("tkVersion") or _infer_version(event.get("ts"))
                child["result_count"] = match.get("resultCount")
            else:
                child["raw_chars"] = None
                child["raw_lines"] = None
                child["shown_chars_ownlog"] = None
                child["duration_ms"] = None
                child["exit"] = None
                child["detail"] = None
                child["tk_version"] = _infer_version(event.get("ts"))
                child["result_count"] = None
            child["raw_baseline_available"] = child["raw_chars"] is not None
            if child["raw_baseline_available"]:
                raw_baseline_invocations += 1
            child["outcome"] = _outcome(child)
            enriched_invocations.append(child)

        primary = enriched_invocations[0]
        ev["tk_invocations"] = enriched_invocations
        ev["tk_invocation_count"] = len(enriched_invocations)
        for key in (
            "raw_chars", "raw_lines", "shown_chars_ownlog", "duration_ms", "exit",
            "detail", "tk_version", "result_count", "own_log_matched",
            "raw_baseline_available",
        ):
            ev[key] = primary.get(key)
        if ev["own_log_matched"]:
            matched += 1
        if ev["raw_baseline_available"]:
            raw_baseline_events += 1

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
        # Session-level version: most frequent non-None tk_version among tk events
        from collections import Counter
        tk_vers = [e["tk_version"] for e in session["events"] if e.get("is_tk") and e.get("tk_version")]
        if tk_vers:
            sm["tk_version"] = Counter(tk_vers).most_common(1)[0][0]
        else:
            sm["tk_version"] = _infer_version(sm.get("ts_start"))
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
            "own_log_matched_events": matched,
            "own_log_unmatched_events": total_tk - matched,
            "own_log_event_match_rate": round(match_rate, 4),
            "raw_baseline_events": raw_baseline_events,
            "raw_baseline_event_rate": round(raw_baseline_events / total_tk, 4) if total_tk else 0.0,
            "total_tk_invocations": total_tk_invocations,
            "own_log_matched_invocations": matched_invocations,
            "own_log_invocation_match_rate": round(matched_invocations / total_tk_invocations, 4)
            if total_tk_invocations else 0.0,
            "raw_baseline_invocations": raw_baseline_invocations,
            "raw_baseline_invocation_rate": round(raw_baseline_invocations / total_tk_invocations, 4)
            if total_tk_invocations else 0.0,
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
        "interventions": interventions,
    }
    return model


def main() -> None:
    parser = argparse.ArgumentParser(description="Join tk events with own-log")
    parser.add_argument("--from", dest="from_dt", metavar="ISO")
    parser.add_argument("--to", dest="to_dt", metavar="ISO")
    parser.add_argument(
        "--source",
        choices=("claude", "opencode", "codex", "both", "all"),
        default="claude",
    )
    args = parser.parse_args()

    from_dt = _parse_cli_dt(args.from_dt) if args.from_dt else None
    to_dt = _parse_cli_dt(args.to_dt, end_of_day=True) if args.to_dt else None

    model = run_join(from_dt, to_dt, source=args.source)

    # Write model.json
    result_dir = Path(__file__).parent.parent.parent / "result"
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
    print(
        f"Own-log:      {ms['own_log_matched_events']} / {n_tk}  "
        f"({ms['own_log_event_match_rate']*100:.1f}%)"
    )
    print(
        f"Raw baseline: {ms['raw_baseline_events']} / {n_tk}  "
        f"({ms['raw_baseline_event_rate']*100:.1f}%)"
    )
    print(f"Invocations:  {ms['total_tk_invocations']}")
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
