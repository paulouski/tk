"""
Codex CLI transcript adapter.

Ingest Codex rollout JSONL files from ``$CODEX_HOME/sessions`` and emit the
same normalized session/event shape as tkstats.ingestion.claude.

Modern Codex tool calls are often wrapped in one ``exec`` custom tool whose
JavaScript input orchestrates several nested tools (see codex_js.py for the
literal-command reconstruction). One outer call remains one normalized event
so its single result is not double-counted. A ``wait`` tool call polls a
background-running cell by ``cell_id``; its outputs are joined back onto the
originating event that reported "Script running with cell ID X".

CLI
---
  python3 Stats/ingest_codex.py [--from ISO] [--to ISO]
"""

from __future__ import annotations

import argparse
import json
import os
import re
from pathlib import Path

from tkstats.ingestion.codex_js import _extract_exec_commands
from tkstats.ingestion.common import _compute_empty_marker, _extract_tk_info
from tkstats.pricing import cost_for_usage
from tkstats.timeparse import _in_range, _parse_cli_dt


_NESTED_TOOL_RE = re.compile(r"\btools\.([A-Za-z_][A-Za-z0-9_]*)\s*\(")
_RUNNING_CELL_RE = re.compile(r"Script running with cell ID ([^\s]+)")


def _sessions_dir() -> Path:
    codex_home = os.environ.get("CODEX_HOME")
    return Path(codex_home) / "sessions" if codex_home else Path.home() / ".codex" / "sessions"


def _read_rows(path: Path) -> list[dict]:
    rows: list[dict] = []
    try:
        with path.open(encoding="utf-8", errors="replace") as f:
            for line in f:
                try:
                    value = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if isinstance(value, dict):
                    rows.append(value)
    except OSError:
        return []
    return rows


def _output_text(payload: dict | None) -> str:
    if not payload:
        return ""
    output = payload.get("output")
    if isinstance(output, str):
        return output
    if isinstance(output, list):
        parts: list[str] = []
        for block in output:
            if isinstance(block, dict) and isinstance(block.get("text"), str):
                parts.append(block["text"])
            elif isinstance(block, str):
                parts.append(block)
        return "".join(parts)
    return ""


def _parse_arguments(value: object) -> dict:
    if isinstance(value, dict):
        return value
    if not isinstance(value, str):
        return {}
    try:
        parsed = json.loads(value)
    except json.JSONDecodeError:
        return {}
    return parsed if isinstance(parsed, dict) else {}


def _agent_metadata(meta: dict) -> tuple[str, bool, str | None]:
    source = meta.get("source")
    if isinstance(source, dict) and isinstance(source.get("subagent"), dict):
        subagent = source["subagent"]
        spawn = subagent.get("thread_spawn")
        parent = spawn.get("parent_thread_id") if isinstance(spawn, dict) else None
        return "codex:subagent", True, parent if isinstance(parent, str) else None
    if meta.get("originator") == "Claude Code":
        return "codex:companion", True, None
    return "codex", False, None


def _build_cost(rows: list[dict]) -> dict:
    usage: dict = {}
    model = ""
    for row in rows:
        payload = row.get("payload") or {}
        if row.get("type") == "turn_context" and payload.get("model"):
            model = str(payload["model"])
        if row.get("type") == "event_msg" and payload.get("type") == "token_count":
            info = payload.get("info") or {}
            total = info.get("total_token_usage")
            if isinstance(total, dict):
                usage = total
    input_total = int(usage.get("input_tokens") or 0)
    cache_read = int(usage.get("cached_input_tokens") or 0)
    input_uncached = max(0, input_total - cache_read)
    output = int(usage.get("output_tokens") or 0)
    pricing_usage = {
        "input_tokens": input_uncached,
        "output_tokens": output,
        "cache_read_input_tokens": cache_read,
        "cache_creation_input_tokens": 0,
    }
    return {
        "model": model,
        "usd": round(cost_for_usage(pricing_usage, model), 6) if model else 0.0,
        "tokens": {
            "input": input_uncached,
            "output": output,
            "cache_read": cache_read,
            "cache_write_5m": 0,
            "cache_write_1h": 0,
        },
        "usd_by_type": {},
    }


def _parse_session(path: Path) -> dict | None:
    rows = _read_rows(path)
    meta = next(
        (row.get("payload") or {} for row in rows if row.get("type") == "session_meta"),
        None,
    )
    if not isinstance(meta, dict):
        return None

    session_id = str(meta.get("id") or meta.get("session_id") or path.stem)
    cwd = meta.get("cwd") if isinstance(meta.get("cwd"), str) else None
    agent_type, is_subagent, parent_session_id = _agent_metadata(meta)
    group_id = parent_session_id or session_id

    outputs: dict[str, dict] = {}
    for row in rows:
        payload = row.get("payload") or {}
        if payload.get("type") in ("custom_tool_call_output", "function_call_output"):
            call_id = payload.get("call_id")
            if isinstance(call_id, str):
                outputs[call_id] = payload

    continuations: dict[str, list[str]] = {}
    for row in rows:
        payload = row.get("payload") or {}
        if payload.get("type") not in ("custom_tool_call", "function_call"):
            continue
        if payload.get("name") != "wait":
            continue
        cell_id = _parse_arguments(payload.get("arguments")).get("cell_id")
        output = _output_text(outputs.get(str(payload.get("call_id") or "")))
        if cell_id is not None and output:
            continuations.setdefault(str(cell_id), []).append(output)

    events: list[dict] = []
    for row in rows:
        if row.get("type") != "response_item":
            continue
        payload = row.get("payload") or {}
        if payload.get("type") not in ("custom_tool_call", "function_call"):
            continue
        name = str(payload.get("name") or "unknown")
        if name == "wait":
            continue
        call_id = str(payload.get("call_id") or payload.get("id") or "")
        output_payload = outputs.get(call_id)
        output_text = _output_text(output_payload)
        running = _RUNNING_CELL_RE.search(output_text)
        if running and running.group(1) in continuations:
            output_text = "".join(continuations[running.group(1)])

        tool = name
        summary = name
        nested_tools: list[str] = []
        unparsed_exec_commands = 0
        if name == "exec":
            source = payload.get("input") if isinstance(payload.get("input"), str) else ""
            nested_tools = _NESTED_TOOL_RE.findall(source)
            commands, unparsed_exec_commands = _extract_exec_commands(source)
            if commands:
                tool = "Bash"
                summary = "\n".join(commands)
            else:
                summary = " ".join(nested_tools) if nested_tools else "exec"
                tool = "Exec"
        elif name == "exec_command":
            args = _parse_arguments(payload.get("arguments"))
            command = args.get("cmd") or args.get("command")
            if isinstance(command, str) and command:
                tool = "Bash"
                summary = command

        event: dict = {
            "session_id": session_id,
            "session_file": str(path),
            "source": "codex",
            "agent_type": agent_type,
            "is_subagent": is_subagent,
            "cwd": cwd,
            "git_branch": (meta.get("git") or {}).get("branch")
            if isinstance(meta.get("git"), dict) else None,
            "cc_version": meta.get("cli_version"),
            "codex_version": meta.get("cli_version"),
            "ts": row.get("timestamp"),
            "tool": tool,
            "tool_use_id": call_id,
            "is_tk": False,
            "tool_input_summary": summary,
            "shown_chars": len(output_text),
            "result_is_error": payload.get("status") == "failed",
        }
        if unparsed_exec_commands:
            event["unparsed_exec_commands"] = unparsed_exec_commands
        if nested_tools:
            event["nested_tools"] = nested_tools
        if tool == "Bash":
            tk_info = _extract_tk_info(summary, session_cwd=cwd)
            if tk_info:
                event.update(tk_info)
                event["empty_marker"] = _compute_empty_marker(
                    output_text, event.get("tk_command")
                )
        events.append(event)

    if not events:
        return None
    events.sort(key=lambda event: event.get("ts") or "")
    timestamps = [event["ts"] for event in events if event.get("ts")]
    n_tk = sum(1 for event in events if event.get("is_tk"))
    return {
        "session_id": session_id,
        "session_file": str(path),
        "source": "codex",
        "agent_type": agent_type,
        "is_subagent": is_subagent,
        "parent_session_id": parent_session_id,
        "group_id": group_id,
        "cwd": cwd,
        "git_branch": (meta.get("git") or {}).get("branch")
        if isinstance(meta.get("git"), dict) else None,
        "cc_version": meta.get("cli_version"),
        "codex_version": meta.get("cli_version"),
        "ts_start": min(timestamps) if timestamps else None,
        "ts_end": max(timestamps) if timestamps else None,
        "n_events": len(events),
        "n_tk_events": n_tk,
        "n_tk_invocations": sum(
            event.get("tk_invocation_count", 1)
            for event in events if event.get("is_tk")
        ),
        "cost": _build_cost(rows),
        "events": events,
    }


def load_sessions(
    from_dt=None,
    to_dt=None,
    sessions_dir: Path | None = None,
) -> list[dict]:
    root = sessions_dir or _sessions_dir()
    if not root.exists():
        return []
    sessions: list[dict] = []
    for path in sorted(root.rglob("rollout-*.jsonl")):
        session = _parse_session(path)
        if session is None:
            continue
        if from_dt or to_dt:
            session["events"] = [
                event for event in session["events"]
                if _in_range(event.get("ts"), from_dt, to_dt)
            ]
            if not session["events"]:
                continue
            timestamps = [event["ts"] for event in session["events"] if event.get("ts")]
            session["ts_start"] = min(timestamps) if timestamps else None
            session["ts_end"] = max(timestamps) if timestamps else None
            session["n_events"] = len(session["events"])
            session["n_tk_events"] = sum(
                1 for event in session["events"] if event.get("is_tk")
            )
            session["n_tk_invocations"] = sum(
                event.get("tk_invocation_count", 1)
                for event in session["events"] if event.get("is_tk")
            )
        sessions.append(session)
    return sessions


def load_events(from_dt=None, to_dt=None) -> list[dict]:
    return [
        event
        for session in load_sessions(from_dt, to_dt)
        for event in session["events"]
    ]


def main() -> None:
    parser = argparse.ArgumentParser(description="Ingest Codex rollout sessions")
    parser.add_argument("--from", dest="from_dt", metavar="ISO")
    parser.add_argument("--to", dest="to_dt", metavar="ISO")
    args = parser.parse_args()
    from_dt = _parse_cli_dt(args.from_dt) if args.from_dt else None
    to_dt = _parse_cli_dt(args.to_dt, end_of_day=True) if args.to_dt else None
    sessions = load_sessions(from_dt, to_dt)
    events = [event for session in sessions for event in session["events"]]
    print(f"Sessions:    {len(sessions)}")
    print(f"Events:      {len(events)}")
    print(f"Bash events: {sum(1 for event in events if event.get('tool') == 'Bash')}")
    print(f"tk events:   {sum(1 for event in events if event.get('is_tk'))}")
    print(f"Unparsed:    {sum(event.get('unparsed_exec_commands', 0) for event in events)}")


if __name__ == "__main__":
    main()
