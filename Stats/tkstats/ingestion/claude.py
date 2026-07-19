"""
Claude Code transcript adapter.

Walk all Claude Code transcripts under ~/.claude/projects/**/*.jsonl,
parse tool_use / tool_result blocks, detect tk invocations, and emit a
normalized event stream.

Importable API
--------------
  load_sessions(from_dt=None, to_dt=None) -> list[dict]   # session summaries
  load_events(from_dt=None, to_dt=None)   -> list[dict]   # all events

CLI
---
  python3 Stats/ingest.py [--from ISO] [--to ISO]
"""

from __future__ import annotations

import argparse
import json
from datetime import datetime
from pathlib import Path

from tkstats.timeparse import _parse_cli_dt, _in_range
from tkstats.ingestion.common import (
    _aggregate_cost,
    _compute_empty_marker,
    _extract_tk_info,
    _flatten_content,
    _tool_input_summary,
)
from tkstats.pricing import cost_for_usage


# ---------------------------------------------------------------------------
# Path helpers
# ---------------------------------------------------------------------------

def _projects_dir() -> Path:
    return Path.home() / ".claude" / "projects"


# ---------------------------------------------------------------------------
# Agent type resolution
# ---------------------------------------------------------------------------

def _agent_type_for_subagent(jsonl_path: Path) -> str:
    """
    Read the sibling .meta.json for agentType.
    Falls back to 'unknown' if missing/unreadable.
    """
    meta_path = jsonl_path.with_suffix(".meta.json")
    if meta_path.exists():
        try:
            with open(meta_path) as f:
                meta = json.load(f)
            return meta.get("agentType") or "unknown"
        except Exception:
            pass
    return "unknown"


# ---------------------------------------------------------------------------
# Session parsing
# ---------------------------------------------------------------------------

def _parse_session(jsonl_path: Path, projects_dir: Path) -> dict | None:
    """
    Parse a single .jsonl file as one independent session.
    Returns a dict with session metadata and a list of events.
    """
    rel_path = str(jsonl_path.relative_to(projects_dir))

    # Determine if this is a subagent file
    # Main files: <proj>/<uuid>.jsonl
    # Subagent files: <proj>/<uuid>/subagents/agent-<id>.jsonl
    parts = jsonl_path.parts
    is_subagent = "subagents" in parts

    if is_subagent:
        agent_type = _agent_type_for_subagent(jsonl_path)
        session_id = jsonl_path.stem  # agent-<id>
        # Parent is the folder containing "subagents"
        sub_idx = list(parts).index("subagents")
        parent_session_id: str | None = parts[sub_idx - 1]
        group_id = parent_session_id
    else:
        agent_type = "main"
        session_id = jsonl_path.stem  # uuid
        parent_session_id = None
        group_id = jsonl_path.stem

    # Session-level metadata (collected from lines)
    cwd: str | None = None
    git_branch: str | None = None
    cc_version: str | None = None
    session_id_from_file: str | None = None

    # Two-pass: first collect tool_uses, then join tool_results
    tool_uses: dict[str, dict] = {}   # tool_use_id -> event dict (partial)
    tool_results: dict[str, dict] = {}  # tool_use_id -> result info

    # Cost accumulation: per-turn records for aggregation
    _turn_costs: list[dict] = []  # each: {model, usd, input, output, cache_read, cw5m, cw1h}

    try:
        with open(jsonl_path, encoding="utf-8", errors="replace") as f:
            for raw_line in f:
                raw_line = raw_line.strip()
                if not raw_line:
                    continue
                try:
                    line = json.loads(raw_line)
                except json.JSONDecodeError:
                    continue

                # Harvest session-level fields from any line
                if cwd is None and line.get("cwd"):
                    cwd = line["cwd"]
                if git_branch is None and line.get("gitBranch"):
                    git_branch = line["gitBranch"]
                if cc_version is None and line.get("version"):
                    cc_version = line["version"]
                if session_id_from_file is None and line.get("sessionId"):
                    session_id_from_file = line["sessionId"]

                # Collect assistant-turn usage for cost calculation
                msg = line.get("message", {})
                if isinstance(msg, dict) and msg.get("role") == "assistant":
                    usage = msg.get("usage")
                    turn_model = msg.get("model") or ""
                    if usage and isinstance(usage, dict):
                        cache_obj = usage.get("cache_creation") or {}
                        cw_5m = cache_obj.get("ephemeral_5m_input_tokens", 0) or 0
                        cw_1h = cache_obj.get("ephemeral_1h_input_tokens", 0) or 0
                        if cw_5m == 0 and cw_1h == 0:
                            cw_5m = usage.get("cache_creation_input_tokens", 0) or 0
                        turn_usd = cost_for_usage(usage, turn_model)
                        # total token weight for dominant model detection
                        tok_total = (
                            (usage.get("input_tokens") or 0)
                            + (usage.get("output_tokens") or 0)
                            + (usage.get("cache_read_input_tokens") or 0)
                            + cw_5m + cw_1h
                        )
                        _turn_costs.append({
                            "model": turn_model,
                            "usd": turn_usd,
                            "tok_total": tok_total,
                            "input": usage.get("input_tokens") or 0,
                            "output": usage.get("output_tokens") or 0,
                            "cache_read": usage.get("cache_read_input_tokens") or 0,
                            "cw5m": cw_5m,
                            "cw1h": cw_1h,
                        })

                ts_str = line.get("timestamp")
                content = msg.get("content", []) if isinstance(msg, dict) else line.get("message", {}).get("content", [])
                if not isinstance(content, list):
                    continue

                for block in content:
                    if not isinstance(block, dict):
                        continue

                    btype = block.get("type")

                    if btype == "tool_use":
                        tool_id = block.get("id", "")
                        tool_name = block.get("name", "")
                        tool_input = block.get("input", {})
                        event: dict = {
                            "session_id": session_id_from_file or session_id,
                            "session_file": rel_path,
                            "agent_type": agent_type,
                            "is_subagent": is_subagent,
                            "cwd": cwd,
                            "git_branch": git_branch,
                            "cc_version": cc_version,
                            "ts": ts_str,
                            "tool": tool_name,
                            "tool_use_id": tool_id,
                            "is_tk": False,
                            "tool_input_summary": _tool_input_summary(tool_name, tool_input),
                        }
                        if tool_name == "Bash":
                            cmd = tool_input.get("command", "")
                            tk_info = _extract_tk_info(cmd, session_cwd=cwd)
                            if tk_info:
                                event.update(tk_info)
                        elif tool_name == "Read":
                            # S4 adoption detection needs to tell a sliced Read
                            # (offset/limit given — a nudge/cap-read was heeded)
                            # from a whole-file Read.
                            event["read_offset"] = tool_input.get("offset")
                            event["read_limit"] = tool_input.get("limit")
                        tool_uses[tool_id] = event

                    elif btype == "tool_result":
                        tool_id = block.get("tool_use_id", "")
                        raw_content = block.get("content")
                        is_error = bool(block.get("is_error", False))
                        flat_text = _flatten_content(raw_content)
                        tool_results[tool_id] = {
                            "shown_chars": len(flat_text),
                            "result_is_error": is_error,
                            "_text": flat_text,  # kept for empty_marker; not stored on event
                        }

    except OSError:
        return None

    if not tool_uses:
        return None

    # Join results into events
    events: list[dict] = []
    for tool_id, event in tool_uses.items():
        if tool_id in tool_results:
            event["shown_chars"] = tool_results[tool_id]["shown_chars"]
            event["result_is_error"] = tool_results[tool_id]["result_is_error"]
            # Compute empty_marker for tk events from the result text
            if event.get("is_tk"):
                result_text = tool_results[tool_id].get("_text", "")
                event["empty_marker"] = _compute_empty_marker(
                    result_text, event.get("tk_command"))
        else:
            event["shown_chars"] = None
            event["result_is_error"] = None
        events.append(event)

    # Sort by timestamp
    events.sort(key=lambda e: e.get("ts") or "")

    # Backfill session-level cwd/version into events that were collected before
    # the first line with those fields appeared
    for ev in events:
        if ev["cwd"] is None:
            ev["cwd"] = cwd
        if ev["cc_version"] is None:
            ev["cc_version"] = cc_version
        if ev["git_branch"] is None:
            ev["git_branch"] = git_branch

    ts_values = [e["ts"] for e in events if e.get("ts")]
    ts_start = min(ts_values) if ts_values else None
    ts_end = max(ts_values) if ts_values else None
    n_tk = sum(1 for e in events if e.get("is_tk"))
    n_tk_invocations = sum(
        e.get("tk_invocation_count", 1)
        for e in events if e.get("is_tk")
    )

    # Aggregate cost from per-turn records
    cost = _aggregate_cost(_turn_costs)

    session_summary = {
        "session_id": session_id_from_file or session_id,
        "session_file": rel_path,
        "agent_type": agent_type,
        "is_subagent": is_subagent,
        "parent_session_id": parent_session_id,
        "group_id": group_id,
        "cwd": cwd,
        "git_branch": git_branch,
        "cc_version": cc_version,
        "ts_start": ts_start,
        "ts_end": ts_end,
        "n_events": len(events),
        "n_tk_events": n_tk,
        "n_tk_invocations": n_tk_invocations,
        "cost": cost,
        "events": events,
    }
    return session_summary


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def load_sessions(from_dt: datetime | None = None, to_dt: datetime | None = None) -> list[dict]:
    """
    Return all parsed sessions (list of session dicts with events embedded).
    Sessions are included if any event falls within [from_dt, to_dt].
    """
    projects_dir = _projects_dir()
    if not projects_dir.exists():
        return []

    all_jsonl = sorted(projects_dir.glob("**/*.jsonl"))
    sessions = []
    for path in all_jsonl:
        session = _parse_session(path, projects_dir)
        if session is None:
            continue
        # Filter events by date range
        if from_dt or to_dt:
            session["events"] = [
                e for e in session["events"]
                if _in_range(e.get("ts"), from_dt, to_dt)
            ]
            if not session["events"]:
                continue
            # Recompute summary fields after filtering
            ts_values = [e["ts"] for e in session["events"] if e.get("ts")]
            session["ts_start"] = min(ts_values) if ts_values else None
            session["ts_end"] = max(ts_values) if ts_values else None
            session["n_events"] = len(session["events"])
            session["n_tk_events"] = sum(1 for e in session["events"] if e.get("is_tk"))
            session["n_tk_invocations"] = sum(
                e.get("tk_invocation_count", 1)
                for e in session["events"] if e.get("is_tk")
            )
        sessions.append(session)
    return sessions


def load_events(from_dt: datetime | None = None, to_dt: datetime | None = None) -> list[dict]:
    """Return a flat list of all events across all sessions."""
    events = []
    for session in load_sessions(from_dt, to_dt):
        events.extend(session["events"])
    return events


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def _top_commands(events: list[dict], n: int = 10) -> list[tuple[str, int]]:
    from collections import Counter
    counts: Counter = Counter()
    for e in events:
        if e.get("is_tk"):
            for invocation in (e.get("tk_invocations") or [e]):
                key = invocation.get("tk_command") or "(empty)"
                sub = invocation.get("tk_sub")
                if sub:
                    key = f"{key} {sub}"
                counts[key] += 1
    return counts.most_common(n)


def main() -> None:
    parser = argparse.ArgumentParser(description="Ingest Claude Code transcripts")
    parser.add_argument("--from", dest="from_dt", metavar="ISO", help="Start date/datetime (inclusive)")
    parser.add_argument("--to", dest="to_dt", metavar="ISO", help="End date/datetime (inclusive)")
    args = parser.parse_args()

    from_dt = _parse_cli_dt(args.from_dt) if args.from_dt else None
    to_dt = _parse_cli_dt(args.to_dt, end_of_day=True) if args.to_dt else None

    sessions = load_sessions(from_dt, to_dt)
    all_events = [e for s in sessions for e in s["events"]]
    tk_events = [e for e in all_events if e.get("is_tk")]

    ts_all = [e["ts"] for e in all_events if e.get("ts")]
    date_range = f"{min(ts_all)[:10]} → {max(ts_all)[:10]}" if ts_all else "n/a"

    print(f"Sessions:    {len(sessions)}")
    print(f"Events:      {len(all_events)}")
    print(f"tk events:   {len(tk_events)}")
    print(f"Date range:  {date_range}")
    print()
    print("Top tk commands:")
    for cmd, count in _top_commands(all_events, 15):
        print(f"  {count:4d}  {cmd}")


if __name__ == "__main__":
    main()
