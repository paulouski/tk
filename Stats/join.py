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
import subprocess
import sys
from datetime import datetime, timezone
from pathlib import Path

# Allow running from the repo root without installing
sys.path.insert(0, str(Path(__file__).parent))
from config import MATCH_WINDOW
from ingest import load_sessions, _parse_ts, _parse_cli_dt  # type: ignore[import]
from ingest_codex import load_sessions as _load_codex_sessions  # type: ignore[import]
from ingest_hooklog import load_interventions  # type: ignore[import]
from ingest_opencode import load_sessions as _load_oc_sessions  # type: ignore[import]
from pricing import cost_tokens_by_type, get_unknown_models, is_known_model  # type: ignore[import]


# ---------------------------------------------------------------------------
# Release version inference from git tags
# ---------------------------------------------------------------------------

_REPO_ROOT = Path(__file__).parent.parent

# Module-level cache; None means "not yet loaded", [] means "loaded but empty"
_RELEASE_DATES: list[tuple[str, datetime]] | None = None


def _load_release_dates() -> list[tuple[str, datetime]]:
    """
    Return a sorted list of (version_str, dt) from git tags matching 'v*'.
    Result is cached after first call. Any error → empty list.
    """
    global _RELEASE_DATES
    if _RELEASE_DATES is not None:
        return _RELEASE_DATES

    result: list[tuple[str, datetime]] = []
    try:
        tags_out = subprocess.check_output(
            ["git", "-C", str(_REPO_ROOT), "tag", "--list", "v*"],
            stderr=subprocess.DEVNULL,
            text=True,
        )
        for tag in tags_out.splitlines():
            tag = tag.strip()
            if not tag:
                continue
            try:
                date_out = subprocess.check_output(
                    ["git", "-C", str(_REPO_ROOT), "log", "-1", "--format=%cI", tag],
                    stderr=subprocess.DEVNULL,
                    text=True,
                ).strip()
                dt = _parse_ts(date_out)
                if dt is None:
                    continue
                version = tag.lstrip("v")
                result.append((version, dt))
            except subprocess.SubprocessError:
                continue
    except Exception:
        pass

    result.sort(key=lambda x: x[1])
    _RELEASE_DATES = result
    return result


def _infer_version(ts_str: str | None) -> str | None:
    """
    Infer the tk version active at the time of ts_str by comparing against
    release tag dates. Returns None if the timestamp predates all known tags
    or if release data is unavailable.
    """
    if not ts_str:
        return None
    dt = _parse_ts(ts_str)
    if dt is None:
        return None
    dates = _load_release_dates()
    if not dates:
        return None
    # Event predates first known release → don't fabricate a version
    if dt < dates[0][1]:
        return None
    # Walk backwards to find the last release whose date <= dt
    best: str | None = None
    for version, rel_dt in dates:
        if rel_dt <= dt:
            best = version
        else:
            break
    return best


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

# MATCH_WINDOW is imported from config.py (single source of truth).


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

        # A group with any un-priced (unknown/empty) model has an understated
        # cost, which would silently inflate the delegation delta in the optimistic
        # direction. Flag it and skip the delta so it can't anchor the headline.
        has_unknown_model = any(
            not is_known_model((_cost(m).get("model") or "")) for m in members
        )

        # B5: parent-sub tk operand overlap. When the main session and a sub
        # both touch the same symbols, the sub is reinforcing the main's work
        # rather than exploring something new. Count overlapping operands and
        # keep a short examples list for debugging/dashboard use.
        def _collect_ops(sm: dict | None) -> set[str]:
            if not sm:
                return set()
            ops: set[str] = set()
            for ev in sm.get("events") or []:
                for invocation in (ev.get("tk_invocations") or [ev]):
                    for op in (invocation.get("tk_operand_values") or []):
                        if op:
                            ops.add(op)
            return ops

        if main_sm and subs:
            parent_ops = _collect_ops(main_sm)
            overlap_count = 0
            overlap_examples: list[str] = []
            for sub in subs:
                sub_ops = _collect_ops(sub)
                common = sub_ops & parent_ops
                overlap_count += len(common)
                for op in common:
                    if op not in overlap_examples and len(overlap_examples) < 10:
                        overlap_examples.append(op)
        else:
            overlap_count = 0
            overlap_examples = []

        # Counterfactual inline estimate (upper-bound heuristic)
        inline_estimate_usd: float | None = None
        delegation_delta_usd: float | None = None
        inline_estimate_lower_usd: float | None = None
        delegation_delta_lower_usd: float | None = None
        if main_sm and subs and not has_unknown_model:
            main_model = (_cost(main_sm).get("model") or "").strip()
            if main_model:
                # Sub output tokens, priced at main's output rate
                sub_out_tok = sum(_tok(s).get("output", 0) for s in subs)
                sub_in_tok  = sum(_tok(s).get("input", 0) for s in subs)
                # Unique informational footprint of the subs: tokens written to
                # cache the first time they were seen. cache_read is the same
                # context re-read every turn — an artifact of turn count, not
                # new information — so it is excluded.
                sub_cw_tok  = sum(
                    _tok(s).get("cache_write_5m", 0) + _tok(s).get("cache_write_1h", 0)
                    for s in subs
                )
                sub_unique_tok = sub_in_tok + sub_cw_tok
                out_cost = cost_tokens_by_type(sub_out_tok, "output", main_model)
                # Upper bound: main re-reads every unique token at its full input rate
                inline_sub_cost = out_cost + cost_tokens_by_type(sub_unique_tok, "input", main_model)
                inline_estimate_usd = round(main_usd + inline_sub_cost, 6)
                delegation_delta_usd = round(inline_estimate_usd - total_usd, 6)
                # Lower bound: that same unique material mostly overlaps main's
                # context and is read at the cheap cache_read rate
                inline_sub_cost_lower = out_cost + cost_tokens_by_type(sub_unique_tok, "cache_read", main_model)
                inline_estimate_lower_usd = round(main_usd + inline_sub_cost_lower, 6)
                delegation_delta_lower_usd = round(inline_estimate_lower_usd - total_usd, 6)

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
            "inline_estimate_lower_usd": inline_estimate_lower_usd,
            "delegation_delta_lower_usd": delegation_delta_lower_usd,
            "has_unknown_model": has_unknown_model,
            "parent_sub_operand_overlap": overlap_count,
            "parent_sub_operand_examples": overlap_examples,
            "assumptions": (
                "Range estimate over the subs' unique token footprint (input + cache_write; "
                "cache_read is excluded as a re-read artifact of turn count). Lower bound: "
                "main ingests that material at the cheap cache_read rate (mostly overlaps its "
                "context). Upper bound: main re-reads all of it fresh at its full input rate. "
                "Both add the subs' output tokens at main's output rate."
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


# ---------------------------------------------------------------------------
# Version filter (used by Stats/stats.sh before injecting into the HTML template)
# ---------------------------------------------------------------------------

def apply_version_filter(model: dict, target: str) -> dict:
    """Filter a joined model to a single tk version, recomputing aggregates.

    target: 'all' returns the model unchanged; 'latest' resolves to the
    highest available non-unknown version; any other value filters sessions
    and events to that tk_version. Raises ValueError on unknown version so the
    caller can decide the exit code (stats.sh maps it to SystemExit(2)).
    """
    avail = [
        row.get("version", "unknown")
        for row in model.get("by_version", [])
        if row.get("version")
    ]
    avail = [v for v in avail if v]

    if target == "latest":
        candidates = [v for v in avail if v != "unknown"]
        target = max(candidates, key=_ver_key) if candidates else "unknown"

    if target != "all" and target not in avail:
        raise ValueError(
            f"tk version {target!r} not found. Available: {', '.join(avail) or '(none)'}"
        )

    if target == "all":
        return model

    sessions = []
    for session in model.get("sessions", []):
        copy = dict(session)
        copy["tk_version"] = target
        copy["events"] = []
        for event in session.get("events", []):
            if not event.get("is_tk"):
                # Native events without an attributable release are excluded:
                # retaining them would leak adoption/read stats across versions.
                event_version = event.get("tk_version") or _infer_version(event.get("ts"))
                if event_version == target:
                    copy["events"].append(dict(event))
                continue
            invocations = [
                dict(invocation)
                for invocation in (event.get("tk_invocations") or [event])
                if (invocation.get("tk_version") or event.get("tk_version")) == target
            ]
            if not invocations:
                continue
            event_copy = dict(event)
            event_copy["tk_invocations"] = invocations
            event_copy["tk_invocation_count"] = len(invocations)
            primary = invocations[0]
            for key in (
                "tk_command", "tk_sub", "tk_flags", "tk_operands", "tk_operand_values",
                "tk_detail", "effective_cwd", "raw_chars", "raw_lines",
                "shown_chars_ownlog", "duration_ms", "exit", "detail", "tk_version",
                "result_count", "own_log_matched", "raw_baseline_available", "outcome",
            ):
                if key in primary:
                    event_copy[key] = primary[key]
            copy["events"].append(event_copy)
        if not copy["events"]:
            continue
        event_ts = [event["ts"] for event in copy["events"] if event.get("ts")]
        copy["ts_start"] = min(event_ts) if event_ts else None
        copy["ts_end"] = max(event_ts) if event_ts else None
        copy["n_events"] = len(copy["events"])
        copy["n_tk_events"] = sum(1 for ev in copy["events"] if ev.get("is_tk"))
        copy["n_tk_invocations"] = sum(
            len(ev.get("tk_invocations") or [ev])
            for ev in copy["events"] if ev.get("is_tk")
        )
        sessions.append(copy)

    tk_events = [
        ev
        for session in sessions
        for ev in session.get("events", [])
        if ev.get("is_tk")
    ]
    matched = sum(
        1 for ev in tk_events
        if ev.get("own_log_matched", ev.get("raw_chars") is not None)
    )
    raw_baseline_events = sum(1 for ev in tk_events if ev.get("raw_chars") is not None)
    tk_invocations = [
        invocation
        for ev in tk_events
        for invocation in (ev.get("tk_invocations") or [ev])
    ]
    matched_invocations = sum(
        1 for invocation in tk_invocations
        if invocation.get("own_log_matched", invocation.get("raw_chars") is not None)
    )
    raw_baseline_invocations = sum(
        1 for invocation in tk_invocations if invocation.get("raw_chars") is not None
    )
    outcomes = {"WIN": 0, "NEUTRAL": 0, "NET_NEGATIVE": 0, "UNKNOWN": 0}
    win_raw = win_shown = neg_raw = neg_shown = 0
    by_version: dict[str, dict] = {}

    for ev in tk_events:
        outcome = ev.get("outcome") or "UNKNOWN"
        outcomes[outcome] = outcomes.get(outcome, 0) + 1
        version = ev.get("tk_version") or "unknown"
        row = by_version.setdefault(
            version,
            {
                "version": version,
                "n": 0,
                "WIN": 0,
                "NEUTRAL": 0,
                "NET_NEGATIVE": 0,
                "UNKNOWN": 0,
                "saved_chars": 0,
            },
        )
        row["n"] += 1
        row[outcome] = row.get(outcome, 0) + 1
        if outcome == "WIN":
            raw = ev.get("raw_chars") or 0
            shown = ev.get("shown_chars_ownlog") or 0
            row["saved_chars"] += raw - shown
            win_raw += raw
            win_shown += shown
        elif outcome == "NET_NEGATIVE":
            neg_raw += ev.get("raw_chars") or 0
            neg_shown += ev.get("shown_chars_ownlog") or 0

    all_ts = [
        ev.get("ts")
        for session in sessions
        for ev in session.get("events", [])
        if ev.get("ts")
    ]
    group_ids = {session.get("group_id") for session in sessions}
    session_ids = {session.get("session_id") for session in sessions}

    interventions = []
    for intervention in model.get("interventions", []):
        if intervention.get("session_id") not in session_ids:
            continue
        intervention_version = intervention.get("tk_version")
        if intervention_version is None and intervention.get("ts_epoch") is not None:
            ts = datetime.fromtimestamp(
                intervention["ts_epoch"], tz=timezone.utc
            ).isoformat()
            intervention_version = _infer_version(ts)
        if intervention_version != target:
            continue
        intervention_copy = dict(intervention)
        intervention_copy["tk_version"] = intervention_version
        interventions.append(intervention_copy)

    filtered = dict(model)
    filtered["version_filter"] = target
    filtered["sessions"] = sessions
    filtered["groups"] = [
        group for group in model.get("groups", [])
        if group.get("group_id") in group_ids
    ]
    filtered["match_stats"] = {
        "total_tk_events": len(tk_events),
        "matched": matched,
        "unmatched": len(tk_events) - matched,
        "match_rate": round(matched / len(tk_events), 4) if tk_events else 0.0,
        "own_log_matched_events": matched,
        "own_log_unmatched_events": len(tk_events) - matched,
        "own_log_event_match_rate": round(matched / len(tk_events), 4) if tk_events else 0.0,
        "raw_baseline_events": raw_baseline_events,
        "raw_baseline_event_rate": round(raw_baseline_events / len(tk_events), 4) if tk_events else 0.0,
        "total_tk_invocations": len(tk_invocations),
        "own_log_matched_invocations": matched_invocations,
        "own_log_invocation_match_rate": round(matched_invocations / len(tk_invocations), 4)
        if tk_invocations else 0.0,
        "raw_baseline_invocations": raw_baseline_invocations,
        "raw_baseline_invocation_rate": round(raw_baseline_invocations / len(tk_invocations), 4)
        if tk_invocations else 0.0,
    }
    filtered["outcome_counts"] = outcomes
    filtered["aggregates"] = {
        "win": {
            "raw_chars": win_raw,
            "shown_chars": win_shown,
            "saved_chars": win_raw - win_shown,
        },
        "net_negative": {
            "raw_chars": neg_raw,
            "shown_chars": neg_shown,
            "extra_chars": neg_shown - neg_raw,
        },
    }
    filtered["generated_for_range"] = dict(model.get("generated_for_range", {}))
    filtered["generated_for_range"]["actual_from"] = min(all_ts) if all_ts else None
    filtered["generated_for_range"]["actual_to"] = max(all_ts) if all_ts else None
    filtered["by_version"] = sorted(
        by_version.values(),
        key=lambda row: _ver_key(row["version"]),
        reverse=True,
    )
    filtered["interventions"] = interventions

    from detect import annotate, _summarize_adoption  # type: ignore[import]
    annotate(filtered)
    filtered["adoption"] = _summarize_adoption(filtered)

    return filtered


def _ver_key(version: str) -> tuple[int, ...]:
    if version == "unknown":
        return (-1,)
    try:
        return tuple(int(part) for part in version.split("."))
    except ValueError:
        return (-1,)


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

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
