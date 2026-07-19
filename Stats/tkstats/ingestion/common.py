"""
Shared cross-source ingestion helpers.

Every source adapter (tkstats.ingestion.claude, .codex, .opencode) emits the
same normalized session/event shape and needs the same tk-invocation parser,
content flattening, empty-result heuristic, and per-turn cost aggregation.
This module holds that shared logic so no adapter reaches into another
adapter's internals.
"""

from __future__ import annotations

import os
import shlex

from tkstats.pricing import cost_tokens_by_type
from tkstats.shellsplit import _split_on_shell_operators, _strip_heredoc_bodies


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
