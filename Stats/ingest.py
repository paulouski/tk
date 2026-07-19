"""
P0 — ingest.py

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
import glob
import hashlib
import json
import os
import shlex
import sys
from datetime import datetime, timezone
from pathlib import Path
from typing import Optional

sys.path.insert(0, str(Path(__file__).parent))
from pricing import cost_for_usage, cost_tokens_by_type  # type: ignore[import]
from bash_opp_shell import (  # type: ignore[import]
    _split_on_shell_operators,
    _strip_heredoc_bodies,
)


# ---------------------------------------------------------------------------
# Path helpers
# ---------------------------------------------------------------------------

def _projects_dir() -> Path:
    return Path.home() / ".claude" / "projects"


# ---------------------------------------------------------------------------
# Timestamp parsing
# ---------------------------------------------------------------------------

def _parse_ts(ts_str: str | None) -> datetime | None:
    """Parse an ISO-8601 timestamp to a UTC-aware datetime.

    Handles:
    - Trailing 'Z' (replace with +00:00)
    - Fractional seconds with more than 6 digits (.NET emits 7, Python fromisoformat
      only accepts up to 6) — truncated to 6.
    """
    if not ts_str:
        return None
    s = ts_str.strip()
    if s.endswith("Z"):
        s = s[:-1] + "+00:00"
    # Truncate fractional seconds to at most 6 digits (Python fromisoformat limit).
    # Match the decimal point and digits before the timezone part.
    import re as _re
    s = _re.sub(r"(\.\d{6})\d+", r"\1", s)
    try:
        dt = datetime.fromisoformat(s)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except ValueError:
        return None


def _parse_cli_dt(s: str, end_of_day: bool = False) -> datetime:
    """Parse a CLI --from/--to value (date or datetime).

    When end_of_day=True and a bare date (no time) is given, the returned
    datetime is 23:59:59 UTC on that date so the entire day is included.
    """
    bare_date_fmt = "%Y-%m-%d"
    for fmt in ("%Y-%m-%dT%H:%M:%S", "%Y-%m-%dT%H:%M", bare_date_fmt):
        try:
            dt = datetime.strptime(s, fmt)
            if end_of_day and fmt == bare_date_fmt:
                dt = dt.replace(hour=23, minute=59, second=59)
            return dt.replace(tzinfo=timezone.utc)
        except ValueError:
            continue
    raise ValueError(f"Cannot parse datetime: {s!r}")


# ---------------------------------------------------------------------------
# tk command parsing — mirrors Analytics.cs Classify exactly
# ---------------------------------------------------------------------------

def _strip_detail_flags(args: list[str]) -> tuple[list[str], str]:
    """
    Strip leading --raw / --more tokens from args, mirroring CliOptionsParser.Parse.
    Returns (remaining_args, detail_level) where detail_level is one of
    "raw", "more", or "default".
    Only strips --raw and --more, repeatedly, from the start; stops at the
    first token that is neither.
    """
    detail = "default"
    i = 0
    while i < len(args):
        if args[i] == "--raw":
            detail = "raw"
            i += 1
        elif args[i] == "--more":
            if detail != "raw":
                detail = "more"
            i += 1
        else:
            break
    return args[i:], detail


def _classify_tk(args: list[str]) -> tuple[str, str | None, list[str], int, list[str]]:
    """
    Given the token list after 'tk', return (command, sub, flags, operands, operand_values).
    Mirrors Common/Analytics.cs Classify.
    """
    if not args:
        return ("", None, [], 0, [])

    command = args[0]
    # F7: tk --version / -v / --help / -h / -V take no args, collapse to "meta".
    if command in ("--version", "-v", "--help", "-h", "-V"):
        return ("meta", None, [], 0, [])

    wants_sub = command in ("git", "dotnet")

    sub = None
    flags: list[str] = []
    operand_values: list[str] = []

    for arg in args[1:]:
        if arg.startswith("-"):
            flags.append(arg)
        elif wants_sub and sub is None:
            sub = arg
        else:
            operand_values.append(arg)

    return (command, sub, flags, len(operand_values), operand_values)


def _is_tk_segment(tokens: list[str]) -> bool:
    """Return True if this shell segment's first real token is 'tk'."""
    return bool(tokens) and tokens[0] == "tk"


def _split_command(cmd: str) -> list[list[str]]:
    """
    Split a shell command string on control operators and newlines, remove
    redirections, then tokenize each segment. Returns a list of token-lists.
    """
    segments: list[list[str]] = []
    for part in _split_on_shell_operators(_strip_heredoc_bodies(cmd)):
        part = _strip_shell_redirections(part).strip()
        if not part:
            continue
        try:
            tokens = shlex.split(part)
        except ValueError:
            tokens = part.split()
        if tokens:
            segments.append(tokens)
    return segments


def _strip_shell_redirections(segment: str) -> str:
    """Remove unquoted shell redirects while preserving quoted query operands."""
    result: list[str] = []
    quote = ""
    escaped = False
    i = 0
    while i < len(segment):
        ch = segment[i]
        if escaped:
            result.append(ch)
            escaped = False
            i += 1
            continue
        if ch == "\\" and quote != "'":
            result.append(ch)
            escaped = True
            i += 1
            continue
        if quote:
            result.append(ch)
            if ch == quote:
                quote = ""
            i += 1
            continue
        if ch in {"'", '"'}:
            quote = ch
            result.append(ch)
            i += 1
            continue
        if ch not in {"<", ">"}:
            result.append(ch)
            i += 1
            continue

        # Numeric text directly adjacent to the operator is a file descriptor.
        token_start = len(result)
        while token_start and not result[token_start - 1].isspace():
            token_start -= 1
        if token_start < len(result) and "".join(result[token_start:]).isdigit():
            del result[token_start:]
        if ch == ">" and i > 0 and segment[i - 1] == "&" and result:
            backslashes = 0
            source_index = i - 2
            while source_index >= 0 and segment[source_index] == "\\":
                backslashes += 1
                source_index -= 1
            if backslashes % 2 == 0 and result[-1] == "&":
                result.pop()

        if segment.startswith("<<<", i):
            i += 3
        elif i + 1 < len(segment) and segment[i + 1] in {ch, "&", ">"}:
            i += 2
        else:
            i += 1
        while i < len(segment) and segment[i].isspace():
            i += 1

        # Consume the redirect target as one shell word, including quoted paths.
        target_quote = ""
        target_escaped = False
        while i < len(segment):
            target = segment[i]
            if target_escaped:
                target_escaped = False
                i += 1
                continue
            if target == "\\" and target_quote != "'":
                target_escaped = True
                i += 1
                continue
            if target_quote:
                if target == target_quote:
                    target_quote = ""
                i += 1
                continue
            if target in {"'", '"'}:
                target_quote = target
                i += 1
                continue
            if target.isspace():
                break
            i += 1
        result.append(" ")
    return "".join(result)


def _resolve_effective_cwd(segments: list[list[str]], tk_idx: int, session_cwd: str | None) -> str | None:
    """
    Scan segments before tk_idx for a 'cd <path>' segment.
    Returns the effective cwd: last cd path resolved against session_cwd,
    or session_cwd if no suitable cd is found.
    Skip: cd with no arg, cd -, cd $VAR, paths containing $ or ~.
    """
    base = session_cwd or ""
    result = base
    for i in range(tk_idx):
        seg = segments[i]
        if not seg or seg[0] != "cd":
            continue
        if len(seg) < 2:
            continue
        path = seg[1]
        # Skip risky cases
        if path in ("-",) or "$" in path or "~" in path:
            continue
        if os.path.isabs(path):
            result = path
        else:
            current = result or base
            if current:
                result = os.path.normpath(os.path.join(current, path))
            else:
                result = path
    return result or None


def _extract_tk_invocations(cmd: str, session_cwd: str | None = None) -> list[dict]:
    """Return every standalone tk segment in a Bash tool invocation."""
    segments = _split_command(cmd)
    invocations: list[dict] = []
    for idx, tokens in enumerate(segments):
        if not _is_tk_segment(tokens):
            continue
        tk_args = tokens[1:]
        stripped_args, tk_detail = _strip_detail_flags(tk_args)
        command, sub, flags, operands, operand_values = _classify_tk(stripped_args)
        effective_cwd = _resolve_effective_cwd(segments, idx, session_cwd)
        invocations.append({
            "segment_index": idx,
            "tk_command": command,
            "tk_sub": sub,
            "tk_flags": flags,
            "tk_operands": operands,
            "tk_operand_values": operand_values,
            "tk_detail": tk_detail,
            "effective_cwd": effective_cwd,
        })
    return invocations


def _extract_tk_info(cmd: str, session_cwd: str | None = None) -> dict | None:
    """Return compatibility fields for the first tk segment plus all segments."""
    invocations = _extract_tk_invocations(cmd, session_cwd)
    if not invocations:
        return None
    return {
        "is_tk": True,
        **invocations[0],
        "tk_invocations": invocations,
        "tk_invocation_count": len(invocations),
    }


# ---------------------------------------------------------------------------
# Content flattening
# ---------------------------------------------------------------------------

def _flatten_content(content) -> str:
    """Flatten tool_result content (str or list of {type,text} blocks) to str."""
    if content is None:
        return ""
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts = []
        for block in content:
            if isinstance(block, dict):
                parts.append(block.get("text", ""))
            elif isinstance(block, str):
                parts.append(block)
        return "".join(parts)
    return str(content)


# ---------------------------------------------------------------------------
# Tool input summary
# ---------------------------------------------------------------------------

def _tool_input_summary(tool_name: str, tool_input: dict) -> str:
    """Build a short summary of tool input for fallback detection.

    Bash = command string; Grep = pattern+path; Read = file_path;
    Glob = pattern; else empty.  Truncated to 300 chars.
    """
    s = ""
    if tool_name == "Bash":
        s = tool_input.get("command", "")
    elif tool_name == "Grep":
        pat = tool_input.get("pattern", "")
        path = tool_input.get("path", "")
        s = f"{pat} {path}".strip()
    elif tool_name == "Read":
        s = tool_input.get("file_path", "")
    elif tool_name == "Glob":
        s = tool_input.get("pattern", "")
    elif tool_name in ("Edit", "MultiEdit"):
        fp = tool_input.get("file_path", "")
        old = tool_input.get("old_string", "") or ""
        new = tool_input.get("new_string", "") or ""
        s = f"{fp} old={len(old)} new={len(new)}"
    elif tool_name == "Write":
        fp = tool_input.get("file_path", "")
        content = tool_input.get("content", "") or ""
        s = f"{fp} content={len(content)}"
    elif tool_name in ("Agent", "Task"):
        s = tool_input.get("description", "") or ""
    elif tool_name == "Skill":
        s = tool_input.get("name", "") or ""
    elif tool_name == "AskUserQuestion":
        qs = tool_input.get("questions") or []
        if isinstance(qs, list) and qs:
            s = qs[0].get("question", "") or ""
    elif tool_name == "ToolSearch":
        s = tool_input.get("query", "") or ""
    elif tool_name == "SendMessage":
        s = tool_input.get("content", "") or tool_input.get("text", "") or ""
    return s[:300]


# ---------------------------------------------------------------------------
# empty_marker — detect empty/not-found from tk output text
# ---------------------------------------------------------------------------

def _compute_empty_marker(text: str, tk_command: str | None) -> bool:
    """Return True if the tk output text indicates an empty/not-found result.

    Heuristics:
    - focus: text contains "m=0 f=0"
    - refs/def/callers: text contains "not found" (case-insensitive)
    - any command: stripped text is empty
    - any command: text contains "No matches" or "no results" (case-insensitive)
    """
    if not text or not text.strip():
        return True
    lower = text.lower()
    if tk_command == "focus" and "m=0 f=0" in text:
        return True
    if tk_command in ("refs", "def", "callers") and "not found" in lower:
        return True
    if "no matches" in lower or "no results" in lower:
        return True
    return False


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
# Cost aggregation
# ---------------------------------------------------------------------------

def _aggregate_cost(turn_costs: list[dict]) -> dict:
    """
    Aggregate per-turn cost records into a session-level cost dict.
    Dominant model = model of the turn with the most total tokens.
    """
    total_usd = 0.0
    tok_input = tok_output = tok_cr = tok_cw5m = tok_cw1h = 0
    usd_input = usd_output = usd_cr = usd_cw = 0.0

    dominant_model = ""
    dominant_tok = -1

    for t in turn_costs:
        total_usd += t["usd"]
        tok_input  += t["input"]
        tok_output += t["output"]
        tok_cr     += t["cache_read"]
        tok_cw5m   += t["cw5m"]
        tok_cw1h   += t["cw1h"]
        m = t["model"]
        usd_input  += cost_tokens_by_type(t["input"],      "input",         m)
        usd_output += cost_tokens_by_type(t["output"],     "output",        m)
        usd_cr     += cost_tokens_by_type(t["cache_read"], "cache_read",    m)
        usd_cw     += cost_tokens_by_type(t["cw5m"],       "cache_write_5m", m)
        usd_cw     += cost_tokens_by_type(t["cw1h"],       "cache_write_1h", m)
        if t["tok_total"] > dominant_tok:
            dominant_tok = t["tok_total"]
            dominant_model = m

    return {
        "model": dominant_model,
        "usd": round(total_usd, 6),
        "tokens": {
            "input":        tok_input,
            "output":       tok_output,
            "cache_read":   tok_cr,
            "cache_write_5m": tok_cw5m,
            "cache_write_1h": tok_cw1h,
        },
        "usd_by_type": {
            "input":      round(usd_input,  6),
            "output":     round(usd_output, 6),
            "cache_read": round(usd_cr,     6),
            "cache_write": round(usd_cw,   6),
        },
    }


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

def _in_range(ts_str: str | None, from_dt: datetime | None, to_dt: datetime | None) -> bool:
    if from_dt is None and to_dt is None:
        return True
    dt = _parse_ts(ts_str)
    if dt is None:
        return True  # include events without timestamps
    if from_dt and dt < from_dt:
        return False
    if to_dt and dt > to_dt:
        return False
    return True


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
