"""
tk release-version inference and version filtering.

Infers which tk version was active at a given timestamp by comparing against
git release-tag dates, and filters an already-joined model down to a single
version (used by stats.sh before injecting the model into the dashboard).
"""

from __future__ import annotations

import subprocess
from datetime import datetime, timezone
from pathlib import Path

from tkstats.timeparse import _parse_ts

_REPO_ROOT = Path(__file__).parent.parent.parent.parent

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


def _ver_key(version: str) -> tuple[int, ...]:
    if version == "unknown":
        return (-1,)
    try:
        return tuple(int(part) for part in version.split("."))
    except ValueError:
        return (-1,)


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

    from tkstats.detect import annotate
    from tkstats.detect.adoption import _summarize_adoption
    annotate(filtered)
    filtered["adoption"] = _summarize_adoption(filtered)

    return filtered
