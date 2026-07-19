"""
Ingest Codex rollout JSONL files from ``$CODEX_HOME/sessions`` and emit the
same normalized session/event shape as ingest.py.

Modern Codex tool calls are often wrapped in one ``exec`` custom tool whose
JavaScript input orchestrates several nested tools.  One outer call remains
one normalized event so its single result is not double-counted.  Literal
``tools.exec_command({cmd: ...})`` commands are joined into that event's Bash
summary for the existing compound-command analysis.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from ingest import (  # type: ignore[import]
    _compute_empty_marker,
    _extract_tk_info,
    _in_range,
    _parse_cli_dt,
)
from pricing import cost_for_usage  # type: ignore[import]


_NESTED_TOOL_RE = re.compile(r"\btools\.([A-Za-z_][A-Za-z0-9_]*)\s*\(")
_EXEC_COMMAND_RE = re.compile(r"\btools\.exec_command\s*\(")
_CMD_PROPERTY_RE = re.compile(r"(?:^|[,\s{])(?:cmd|\"cmd\"|'cmd')\s*:\s*")
_CMD_SHORTHAND_RE = re.compile(r"(?:^|[,\s{])cmd\s*(?=[,}])")
_IDENTIFIER_RE = re.compile(r"[A-Za-z_$][A-Za-z0-9_$]*")
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


def _balanced_slice(text: str, start: int, opening: str, closing: str) -> str | None:
    if start >= len(text) or text[start] != opening:
        return None
    depth = 0
    quote: str | None = None
    escaped = False
    i = start
    while i < len(text):
        char = text[i]
        if quote is not None:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == quote:
                quote = None
            i += 1
            continue
        if char in ('"', "'", "`"):
            quote = char
        elif char == opening:
            depth += 1
        elif char == closing:
            depth -= 1
            if depth == 0:
                return text[start:i + 1]
        i += 1
    return None


def _decode_js_string(literal: str) -> str | None:
    if len(literal) < 2 or literal[0] not in ('"', "'", "`"):
        return None
    quote = literal[0]
    if literal[-1] != quote:
        return None
    body = literal[1:-1]
    if quote == "`" and re.search(r"(?<!\\)\$\{", body):
        return None

    escapes = {
        "n": "\n", "r": "\r", "t": "\t", "b": "\b", "f": "\f",
        "v": "\v", "0": "\0", "\\": "\\", "'": "'", '"': '"',
        "`": "`", "/": "/",
    }
    result: list[str] = []
    i = 0
    while i < len(body):
        char = body[i]
        if char != "\\":
            result.append(char)
            i += 1
            continue
        i += 1
        if i >= len(body):
            return None
        escaped = body[i]
        if escaped in escapes:
            result.append(escapes[escaped])
            i += 1
            continue
        if escaped == "x" and i + 2 < len(body):
            try:
                result.append(chr(int(body[i + 1:i + 3], 16)))
            except ValueError:
                return None
            i += 3
            continue
        if escaped == "u" and i + 4 < len(body):
            try:
                result.append(chr(int(body[i + 1:i + 5], 16)))
            except ValueError:
                return None
            i += 5
            continue
        if escaped in ("\n", "\r"):
            if escaped == "\r" and i + 1 < len(body) and body[i + 1] == "\n":
                i += 1
            i += 1
            continue
        # JavaScript treats unknown escapes such as \q as q.
        result.append(escaped)
        i += 1
    return "".join(result)


def _raw_string_literal_at(text: str, start: int) -> tuple[str | None, int]:
    while start < len(text) and text[start].isspace():
        start += 1
    if start >= len(text) or text[start] not in ('"', "'", "`"):
        return None, start
    quote = text[start]
    escaped = False
    i = start + 1
    while i < len(text):
        char = text[i]
        if escaped:
            escaped = False
        elif char == "\\":
            escaped = True
        elif char == quote:
            return text[start:i + 1], i + 1
        i += 1
    return None, start


def _string_literal_at(text: str, start: int) -> tuple[str | None, int]:
    literal, end = _raw_string_literal_at(text, start)
    return (_decode_js_string(literal) if literal is not None else None), end


def _decode_template_literal(
    literal: str,
    source: str,
    bindings: dict[str, str] | None = None,
) -> str | None:
    if not literal.startswith("`") or not literal.endswith("`"):
        return None
    values = bindings or {}
    missing = False

    def replace(match: re.Match[str]) -> str:
        nonlocal missing
        identifier = match.group(1)
        value = values.get(identifier)
        if value is None:
            value = _resolve_identifier_string(source, identifier)
        if value is None:
            missing = True
            return match.group(0)
        return value.replace("\\", "\\\\").replace("`", "\\`")

    rendered = re.sub(
        r"(?<!\\)\$\{\s*([A-Za-z_$][A-Za-z0-9_$]*)\s*\}",
        replace,
        literal[1:-1],
    )
    if missing or re.search(r"(?<!\\)\$\{", rendered):
        return None
    return _decode_js_string(f"`{rendered}`")


def _resolve_identifier_string(source: str, identifier: str) -> str | None:
    pattern = re.compile(rf"\b(?:const|let|var)\s+{re.escape(identifier)}\s*=\s*")
    matches = list(pattern.finditer(source))
    if not matches:
        return None
    literal, _ = _raw_string_literal_at(source, matches[-1].end())
    if literal is None:
        return None
    return _decode_js_string(literal) or _decode_template_literal(literal, source)


def _parse_string_array(array: str) -> tuple[list[str], int]:
    values: list[str] = []
    unparsed = 0
    i = 1
    while i < len(array) - 1:
        while i < len(array) - 1 and (array[i].isspace() or array[i] == ","):
            i += 1
        if i >= len(array) - 1:
            break
        value, end = _string_literal_at(array, i)
        if value is None:
            unparsed += 1
            while i < len(array) - 1 and array[i] != ",":
                i += 1
        else:
            values.append(value)
            i = end
    return values, unparsed


def _resolve_identifier_array(source: str, identifier: str) -> tuple[list[str], int]:
    pattern = re.compile(rf"\b(?:const|let|var)\s+{re.escape(identifier)}\s*=\s*")
    matches = list(pattern.finditer(source))
    if not matches:
        return [], 1
    start = matches[-1].end()
    while start < len(source) and source[start].isspace():
        start += 1
    array = _balanced_slice(source, start, "[", "]")
    if array is None:
        return [], 1

    return _parse_string_array(array)


def _cmd_literal(obj: str) -> str | None:
    match = _CMD_PROPERTY_RE.search(obj)
    if not match:
        return None
    literal, _ = _raw_string_literal_at(obj, match.end())
    return literal


def _cmd_identifier(obj: str) -> str | None:
    match = _CMD_PROPERTY_RE.search(obj)
    if not match:
        return "cmd" if _CMD_SHORTHAND_RE.search(obj) else None
    _, end = _raw_string_literal_at(obj, match.end())
    identifier = _IDENTIFIER_RE.match(obj, end)
    return identifier.group(0) if identifier else None


def _loop_values(source: str) -> tuple[str, list[str], int] | None:
    matches = list(re.finditer(
        r"\bfor\s*\(\s*const\s+([A-Za-z_$][A-Za-z0-9_$]*)\s+of\s+",
        source,
    ))
    if not matches:
        return None
    match = matches[-1]
    start = match.end()
    while start < len(source) and source[start].isspace():
        start += 1
    if start < len(source) and source[start] == "[":
        array = _balanced_slice(source, start, "[", "]")
        if array is None:
            return match.group(1), [], 1
        values, unparsed = _parse_string_array(array)
        return match.group(1), values, unparsed
    identifier = _IDENTIFIER_RE.match(source, start)
    if not identifier:
        return match.group(1), [], 1
    values, unparsed = _resolve_identifier_array(source, identifier.group(0))
    return match.group(1), values, unparsed


def _extract_cmd_from_object(source: str, obj: str) -> str | None:
    match = _CMD_PROPERTY_RE.search(obj)
    if not match:
        if _CMD_SHORTHAND_RE.search(obj):
            return _resolve_identifier_string(source, "cmd")
        return None
    literal, end = _raw_string_literal_at(obj, match.end())
    value = _decode_js_string(literal) if literal is not None else None
    if value is None and literal is not None:
        value = _decode_template_literal(literal, source)
    if value is not None:
        return value
    identifier = _IDENTIFIER_RE.match(obj, end)
    if identifier:
        return _resolve_identifier_string(source, identifier.group(0))
    return None


def _extract_exec_commands(source: str) -> tuple[list[str], int]:
    commands: list[str] = []
    unparsed = 0
    for match in _EXEC_COMMAND_RE.finditer(source):
        i = match.end()
        while i < len(source) and source[i].isspace():
            i += 1
        obj = _balanced_slice(source, i, "{", "}")
        if obj is None:
            unparsed += 1
            continue
        source_before = source[:match.start()]
        command = _extract_cmd_from_object(source_before + obj, obj)
        if command is None:
            loop = _loop_values(source_before)
            if loop:
                loop_variable, values, loop_unparsed = loop
                identifier = _cmd_identifier(obj)
                literal = _cmd_literal(obj)
                if identifier == loop_variable:
                    commands.extend(values)
                    unparsed += loop_unparsed
                    continue
                if literal is not None:
                    rendered = [
                        _decode_template_literal(
                            literal,
                            source_before,
                            {loop_variable: value},
                        )
                        for value in values
                    ]
                    if rendered and all(value is not None for value in rendered):
                        commands.extend(value for value in rendered if value is not None)
                        unparsed += loop_unparsed
                        continue
            map_match = re.search(
                r"\b([A-Za-z_$][A-Za-z0-9_$]*)\.map\(\s*"
                r"([A-Za-z_$][A-Za-z0-9_$]*)\s*=>\s*$",
                source_before,
            )
            if map_match and (
                _CMD_SHORTHAND_RE.search(obj)
                or re.search(
                    rf"(?:^|[,\s{{])cmd\s*:\s*{re.escape(map_match.group(2))}\s*(?=[,}}])",
                    obj,
                )
            ):
                values, array_unparsed = _resolve_identifier_array(
                    source_before, map_match.group(1)
                )
                commands.extend(values)
                unparsed += array_unparsed
                continue
            unparsed += 1
        else:
            commands.append(command)
    return commands, unparsed


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
