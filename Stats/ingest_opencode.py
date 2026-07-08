"""
P0-opencode — ingest_opencode.py

Walk the opencode SQLite DB at ~/.local/share/opencode/opencode.db (override
via OPENCODE_DB env var), parse completed tool parts, detect tk invocations,
and emit the same normalized event/session shape that ingest.py produces.
This lets join.py / report.py / detect.py / stats.sh consume opencode sessions
unchanged.

Importable API
--------------
  load_sessions(from_dt=None, to_dt=None) -> list[dict]   # session summaries
  load_events(from_dt=None, to_dt=None)   -> list[dict]   # all events

CLI
---
  python3 Stats/ingest_opencode.py [--from ISO] [--to ISO]
"""

from __future__ import annotations

import argparse
import json
import os
import sqlite3
import sys
from datetime import datetime, timezone
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from ingest import (  # type: ignore[import]
    _aggregate_cost,
    _compute_empty_marker,
    _extract_tk_info,
    _in_range,
    _parse_cli_dt,
    _tool_input_summary,
)
from pricing import cost_for_usage  # type: ignore[import]


# ---------------------------------------------------------------------------
# Path helpers
# ---------------------------------------------------------------------------

def _db_path() -> Path:
    override = os.environ.get("OPENCODE_DB")
    if override:
        return Path(override)
    return Path.home() / ".local" / "share" / "opencode" / "opencode.db"


# ---------------------------------------------------------------------------
# Timestamp conversion
# ---------------------------------------------------------------------------

def _ms_to_iso(ms: int | None) -> str | None:
    """Convert opencode ms-epoch int to ISO-8601 UTC string."""
    if ms is None:
        return None
    try:
        return datetime.fromtimestamp(ms / 1000, tz=timezone.utc).isoformat()
    except (TypeError, ValueError, OSError):
        return None


# ---------------------------------------------------------------------------
# Tool-name normalization
# ---------------------------------------------------------------------------

# opencode uses lowercase tool names; detect.py expects Claude-cased names.
_TOOL_NAME_MAP: dict[str, str] = {
    "bash": "Bash",
    "grep": "Grep",
    "glob": "Glob",
    "read": "Read",
    "edit": "Edit",
    "write": "Write",
    "task": "Task",
}


def _map_tool_name(raw: str) -> str:
    return _TOOL_NAME_MAP.get(raw, raw)


# opencode uses camelCase keys; _tool_input_summary expects Claude snake_case.
# Remap only the keys it reads.
_INPUT_KEY_REMAP: dict[str, str] = {
    "filePath": "file_path",
    "oldString": "old_string",
    "newString": "new_string",
}


def _remap_input_keys(inp: dict) -> dict:
    out: dict = {}
    for k, v in (inp or {}).items():
        out[_INPUT_KEY_REMAP.get(k, k)] = v
    return out


# ---------------------------------------------------------------------------
# DB connection
# ---------------------------------------------------------------------------

def _connect(db_path: Path) -> sqlite3.Connection:
    return sqlite3.connect(f"file:{db_path}?mode=ro", uri=True)


# ---------------------------------------------------------------------------
# Per-session cost aggregation
# ---------------------------------------------------------------------------

def _build_session_cost(
    conn: sqlite3.Connection,
    session_id: str,
) -> dict:
    """Aggregate cost for a single session by walking its assistant messages."""
    turn_costs: list[dict] = []
    cur = conn.execute(
        "SELECT data FROM message WHERE session_id=? "
        "ORDER BY time_created ASC",
        (session_id,),
    )
    for (data_text,) in cur:
        try:
            t = json.loads(data_text)
        except json.JSONDecodeError:
            continue
        if t.get("role") != "assistant":
            continue
        tok = t.get("tokens") or {}
        cache = tok.get("cache") or {}
        cw_5m = cache.get("write", 0) or 0
        cw_1h = 0
        input_tokens = tok.get("input", 0) or 0
        output_tokens = tok.get("output", 0) or 0
        cache_read = cache.get("read", 0) or 0
        usage = {
            "input_tokens": input_tokens,
            "output_tokens": output_tokens,
            "cache_read_input_tokens": cache_read,
            "cache_creation_input_tokens": cw_5m,
        }
        model = t.get("modelID", "") or ""
        usd = cost_for_usage(usage, model)
        tok_total = input_tokens + output_tokens + cache_read + cw_5m + cw_1h
        turn_costs.append({
            "model": model,
            "usd": usd,
            "tok_total": tok_total,
            "input": input_tokens,
            "output": output_tokens,
            "cache_read": cache_read,
            "cw5m": cw_5m,
            "cw1h": cw_1h,
        })
    return _aggregate_cost(turn_costs)


# ---------------------------------------------------------------------------
# Session parsing
# ---------------------------------------------------------------------------

def _parse_session(
    conn: sqlite3.Connection,
    sess_row: sqlite3.Row,
) -> dict | None:
    """
    Parse one opencode session row into the normalized session dict.
    """
    session_id: str = sess_row["id"]
    parent_id: str | None = sess_row["parent_id"]
    is_subagent = bool(parent_id)
    agent_type: str = sess_row["agent"] or ("main" if not is_subagent else "unknown")
    directory: str | None = sess_row["directory"]
    cc_version: str | None = sess_row["version"]
    # opencode stores model as JSON like {"id":"glm-5.2","providerID":"opencode-go"}
    raw_model = sess_row["model"]
    if raw_model:
        try:
            model_obj = json.loads(raw_model)
            if not isinstance(model_obj, dict):
                model_obj = {}
        except json.JSONDecodeError:
            model_obj = {}
    else:
        model_obj = {}

    group_id = parent_id if is_subagent else session_id
    parent_session_id: str | None = parent_id if is_subagent else None

    # Pull all tool parts for this session in chronological order
    cur = conn.execute(
        "SELECT id, time_created, data FROM part "
        "WHERE session_id=? AND json_extract(data,'$.type')='tool' "
        "ORDER BY time_created ASC",
        (session_id,),
    )
    events: list[dict] = []
    for part_id, part_time_ms, part_data_text in cur:
        try:
            pdata = json.loads(part_data_text)
        except json.JSONDecodeError:
            continue
        state = pdata.get("state") or {}
        status = state.get("status")
        # Skip genuinely in-flight parts (no result yet); only "completed"
        # and "error" parts are done. Edit/Write/Read never populate
        # state.metadata.output, only state.output — metadata.output is
        # bash-specific, so try it first (preserves existing bash behavior)
        # and fall back to the top-level field.
        if status not in ("completed", "error"):
            continue
        output_text = (state.get("metadata") or {}).get("output")
        if output_text is None:
            output_text = state.get("output")
        raw_tool = pdata.get("tool") or ""
        mapped_tool = _map_tool_name(raw_tool)
        call_id = pdata.get("callID") or part_id
        inp_raw = state.get("input") or {}
        # Remap camelCase keys to the names _tool_input_summary expects.
        inp_for_summary = _remap_input_keys(inp_raw)

        time_obj = pdata.get("time") or {}
        start_ms = time_obj.get("start") or part_time_ms
        ts = _ms_to_iso(start_ms)

        event: dict = {
            "session_id": session_id,
            "session_file": f"opencode/{session_id}",
            "agent_type": agent_type,
            "is_subagent": is_subagent,
            "cwd": directory,
            "git_branch": None,
            "cc_version": cc_version,
            "ts": ts,
            "tool": mapped_tool,
            "tool_use_id": call_id,
            "is_tk": False,
            "tool_input_summary": _tool_input_summary(mapped_tool, inp_for_summary),
            "shown_chars": len(output_text) if output_text is not None else None,
            "result_is_error": (status == "error"),
        }
        if mapped_tool == "Bash":
            cmd = inp_raw.get("command", "") or ""
            tk_info = _extract_tk_info(cmd, session_cwd=directory)
            if tk_info:
                event.update(tk_info)
                event["empty_marker"] = _compute_empty_marker(
                    output_text, event.get("tk_command"))
        events.append(event)

    if not events:
        return None

    # Sort by ts asc (parts already came in order, but be defensive)
    events.sort(key=lambda e: e.get("ts") or "")

    ts_values = [e["ts"] for e in events if e.get("ts")]
    ts_start = min(ts_values) if ts_values else None
    ts_end = max(ts_values) if ts_values else None
    n_tk = sum(1 for e in events if e.get("is_tk"))

    cost = _build_session_cost(conn, session_id)

    return {
        "session_id": session_id,
        "session_file": f"opencode/{session_id}",
        "agent_type": agent_type,
        "is_subagent": is_subagent,
        "parent_session_id": parent_session_id,
        "group_id": group_id,
        "cwd": directory,
        "git_branch": None,
        "cc_version": cc_version,
        "ts_start": ts_start,
        "ts_end": ts_end,
        "n_events": len(events),
        "n_tk_events": n_tk,
        "cost": cost,
        "events": events,
    }


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def load_sessions(
    from_dt: datetime | None = None,
    to_dt: datetime | None = None,
) -> list[dict]:
    """
    Return all parsed opencode sessions (list of session dicts with events).
    Sessions are included if any event falls within [from_dt, to_dt].
    """
    db_path = _db_path()
    if not db_path.exists():
        return []
    conn = _connect(db_path)
    try:
        conn.row_factory = sqlite3.Row
        cur = conn.execute(
            "SELECT id, parent_id, agent, model, version, directory "
            "FROM session ORDER BY time_created ASC"
        )
        sessions: list[dict] = []
        for sess_row in cur:
            session = _parse_session(conn, sess_row)
            if session is None:
                continue
            if from_dt or to_dt:
                session["events"] = [
                    e for e in session["events"]
                    if _in_range(e.get("ts"), from_dt, to_dt)
                ]
                if not session["events"]:
                    continue
                ts_values = [e["ts"] for e in session["events"] if e.get("ts")]
                session["ts_start"] = min(ts_values) if ts_values else None
                session["ts_end"] = max(ts_values) if ts_values else None
                session["n_events"] = len(session["events"])
                session["n_tk_events"] = sum(1 for e in session["events"] if e.get("is_tk"))
            sessions.append(session)
        return sessions
    finally:
        conn.close()


def load_events(
    from_dt: datetime | None = None,
    to_dt: datetime | None = None,
) -> list[dict]:
    """Return a flat list of all events across all opencode sessions."""
    events: list[dict] = []
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
            key = e.get("tk_command") or "(empty)"
            sub = e.get("tk_sub")
            if sub:
                key = f"{key} {sub}"
            counts[key] += 1
    return counts.most_common(n)


def main() -> None:
    parser = argparse.ArgumentParser(description="Ingest opencode sessions")
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
