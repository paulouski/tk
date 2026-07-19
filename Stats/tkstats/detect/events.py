"""
Event-level detectors.

Pure annotation functions that enrich a session's ts-ordered event list in
place: empty-result, fallback/result-used, retry, escalation, symbol-grep,
reread, native-grep-retry, and stealth-read detection.
"""

from __future__ import annotations

import re as _re
import shlex

from tkstats.config import (
    EDIT_RESULT_USED_TOOLS,
    NATIVE_GREP_RETRY_LOOKBACK,
    STEALTH_READ_EXTENSIONS,
    _FALLBACK_TOOLS,
    _RESULT_USED_TOOLS,
    _SEARCH_NAV_COMMANDS,
)

# Grep-like binaries that count as a native grep when invoked via Bash.
# Shared with tkstats.detect.adoption (nudge-subtype classification).
_GREP_CMDS = frozenset({"grep", "rg", "egrep", "fgrep"})


# ---------------------------------------------------------------------------
# empty_result heuristic
# ---------------------------------------------------------------------------

def _looks_empty(ev: dict) -> bool:
    """Best-effort check whether a tk event produced an empty/not-found result.

    Explicit signal (preferred): result_count from own-log.
    Heuristics (fallback, any → True):
    - Non-zero exit code (command failed).
    - shown_chars == 0 (nothing displayed).
    - empty_marker == True (ingest detected empty/not-found text in output).
    """
    result_count = ev.get("result_count")
    if result_count is not None:
        return result_count == 0

    exit_code = ev.get("exit")
    if exit_code is not None and exit_code != 0:
        return True

    shown = ev.get("shown_chars")
    if shown == 0:
        return True

    if ev.get("empty_marker"):
        return True

    return False


# ---------------------------------------------------------------------------
# Fallback detection
# ---------------------------------------------------------------------------

def _detect_fallback(events: list[dict]) -> None:
    """Annotate tk search/nav events with fallback and result_used signals.

    fallback (negative): tk search/nav followed within 3 events by Grep/Glob.
    result_used (positive): tk search/nav followed within 3 events by an exact
        native read or code-snippet lookup.
    result_used_edit (positive, C3): tk search/nav followed within 3 events
        by Edit/MultiEdit/Write. Distinct from result_used (which is Read-only)
        so the read vs edit success signal stays separable.
    """
    n = len(events)
    for i, ev in enumerate(events):
        if not ev.get("is_tk"):
            continue
        ev["fallback"] = False
        ev["fallback_same_target"] = False
        ev["fallback_after_empty"] = False
        ev["result_used"] = False
        ev["result_used_same_target"] = False
        ev["result_used_edit"] = False
        ev["result_used_edit_same_target"] = False

        if ev.get("tk_command") not in _SEARCH_NAV_COMMANDS:
            continue

        operand_vals = ev.get("tk_operand_values") or []

        # Look at next 3 events in the session
        for j in range(i + 1, min(i + 4, n)):
            following = events[j]
            if following.get("is_tk"):
                continue  # only interested in native tool calls
            tool = following.get("tool")

            if tool in _FALLBACK_TOOLS and not ev["fallback"]:
                ev["fallback"] = True
                if ev.get("empty_result"):
                    ev["fallback_after_empty"] = True
                if operand_vals:
                    summary = following.get("tool_input_summary", "")
                    if summary and any(op in summary for op in operand_vals):
                        ev["fallback_same_target"] = True

            if _is_result_used(following) and not ev["result_used"]:
                ev["result_used"] = True
                if operand_vals:
                    summary = following.get("tool_input_summary", "")
                    if summary and any(op in summary for op in operand_vals):
                        ev["result_used_same_target"] = True

            if tool in EDIT_RESULT_USED_TOOLS and not ev["result_used_edit"]:
                ev["result_used_edit"] = True
                if operand_vals:
                    summary = following.get("tool_input_summary", "")
                    if summary and any(op in summary for op in operand_vals):
                        ev["result_used_edit_same_target"] = True


def _is_result_used(event: dict) -> bool:
    if event.get("tool") in _RESULT_USED_TOOLS or event.get("stealth_read"):
        return True
    return any(
        str(tool).endswith("get_code_snippet")
        for tool in event.get("nested_tools") or []
    )


# ---------------------------------------------------------------------------
# Retry detection
# ---------------------------------------------------------------------------

def _detect_retry(events: list[dict]) -> None:
    """Annotate tk events where a recent prior tk event had same command + overlapping operands."""
    # Collect tk events in order with their indices
    tk_events: list[tuple[int, dict]] = []
    for i, ev in enumerate(events):
        if ev.get("is_tk"):
            tk_events.append((i, ev))

    for pos, (idx, ev) in enumerate(tk_events):
        ev["retry"] = False
        cmd = ev.get("tk_command")
        operands = ev.get("tk_operand_values") or []
        if not cmd or not operands:
            continue

        op_set = set(operands)
        # Look back at prior 3 tk events
        start = max(0, pos - 3)
        for prev_pos in range(start, pos):
            _, prev_ev = tk_events[prev_pos]
            if prev_ev.get("tk_command") != cmd:
                continue
            prev_ops = prev_ev.get("tk_operand_values") or []
            if prev_ops and op_set & set(prev_ops):
                ev["retry"] = True
                break


# ---------------------------------------------------------------------------
# Escalation detection
# ---------------------------------------------------------------------------

def _detect_escalated(events: list[dict]) -> None:
    """Annotate tk events where a prior same-command event had detail=default and this has more/raw."""
    # Track the most recent detail level per command within the session
    seen_default: dict[str, bool] = {}  # command -> has a prior default-detail event

    for ev in events:
        if not ev.get("is_tk"):
            continue
        ev["escalated"] = False
        cmd = ev.get("tk_command")
        if not cmd:
            continue

        detail = ev.get("tk_detail") or ev.get("detail")
        if detail in ("more", "raw") and seen_default.get(cmd, False):
            ev["escalated"] = True

        if detail == "default":
            seen_default[cmd] = True


# ---------------------------------------------------------------------------
# Empty result annotation
# ---------------------------------------------------------------------------

def _detect_empty(events: list[dict]) -> None:
    """Annotate tk events with empty_result."""
    for ev in events:
        if not ev.get("is_tk"):
            continue
        ev["empty_result"] = _looks_empty(ev)


# ---------------------------------------------------------------------------
# Symbol-grep detection
# ---------------------------------------------------------------------------

# Declaration keyword followed by a capital letter (mirrors tk-route.sh nudge)
_DECL_PATTERN = _re.compile(
    r'\b(class|record|interface|enum|struct)\s+[A-Z]'
)

# Bare PascalCase identifier: starts with uppercase, all alnum, ≥2 chars
_PASCAL_PATTERN = _re.compile(r'^[A-Z][A-Za-z0-9]+$')


def _is_symbol_grep(ev: dict) -> bool:
    """Return True if this event is a native grep that looks like a symbol lookup."""
    tool = ev.get("tool", "")
    summary = ev.get("tool_input_summary", "")

    if tool == "Grep":
        # Native Grep tool — check the pattern portion (everything before any space+path)
        # tool_input_summary for Grep = "{pattern} {path}".strip()
        # Extract the pattern: everything up to the first space that looks like a path
        # Simpler: just check the whole summary for symbol patterns
        pattern_part = summary.split(" ")[0] if summary else ""
        return bool(
            _DECL_PATTERN.search(summary)
            or _PASCAL_PATTERN.match(pattern_part)
        )

    if tool == "Bash":
        # Must not be a tk command
        if ev.get("is_tk"):
            return False
        # command must start with a grep binary
        cmd = summary  # tool_input_summary for Bash is the command string (truncated)
        try:
            parts = shlex.split(cmd)
        except ValueError:
            parts = cmd.split()
        if not parts:
            return False
        first_token = parts[0]
        if first_token not in _GREP_CMDS:
            return False
        # F1 backstop: "class X" / "interface X" / etc. anywhere in the command.
        if _DECL_PATTERN.search(cmd):
            return True
        # Pattern = first non-flag token (shlex has already stripped quotes).
        pattern_tok = ""
        for tok in parts[1:]:
            if not tok.startswith("-"):
                pattern_tok = tok
                break
        if not pattern_tok:
            return False
        # PascalCase (one of the OR-alternatives has at least one lowercase
        # letter — SCREAMING_SNAKE like "TODO" must not flag).
        for alt in pattern_tok.split("|"):
            alt = alt.strip()
            if alt and _PASCAL_PATTERN.match(alt) and any(c.islower() for c in alt):
                return True
        return False

    return False


def _detect_symbol_grep(events: list[dict]) -> None:
    """Annotate events with symbol_grep=True when they are native symbol-lookup greps."""
    for ev in events:
        ev["symbol_grep"] = _is_symbol_grep(ev)


# ---------------------------------------------------------------------------
# Re-read detection
# ---------------------------------------------------------------------------

def _detect_reread(events: list[dict]) -> None:
    """Classify repeated Read paths without treating new slices as wasted reads."""
    seen: dict[str, set[tuple[object, object]]] = {}
    for ev in events:
        ev["reread"] = False
        ev["read_path_seen"] = False
        ev["read_repeat_kind"] = None
        ev["reread_chars"] = None
        if ev.get("tool") != "Read":
            continue
        path = ev.get("tool_input_summary") or ""
        if not path:
            continue
        read_range = (ev.get("read_offset"), ev.get("read_limit"))
        prior_ranges = seen.setdefault(path, set())
        if prior_ranges:
            ev["read_path_seen"] = True
        if read_range in prior_ranges:
            kind = "whole_file_repeat" if read_range == (None, None) else "exact_slice_repeat"
            ev["reread"] = True
            ev["read_repeat_kind"] = kind
            ev["reread_chars"] = ev.get("shown_chars")
        elif prior_ranges:
            ev["read_repeat_kind"] = "different_slice"
        prior_ranges.add(read_range)


# ---------------------------------------------------------------------------
# Native-grep retry detection (B2)
# ---------------------------------------------------------------------------

def _extract_grep_pattern(parts: list[str]) -> str:
    """First non-flag token in shlex-split grep args, with quotes stripped."""
    for tok in parts[1:]:
        if tok.startswith("-"):
            continue
        return tok.strip("'\"")
    return ""


def _detect_native_grep_retry(events: list[dict]) -> None:
    """Annotate non-tk Bash grep events as a retry if the same pattern
    appeared in a prior non-tk grep event within NATIVE_GREP_RETRY_LOOKBACK.

    Tracks seen patterns in a list (not a set) so iteration order is
    preserved for debugging. Empty patterns are skipped.
    """
    seen_patterns: list[str] = []
    for ev in events:
        ev["native_grep_retry"] = False
        ev["native_grep_retry_pattern"] = ""
        if ev.get("tool") != "Bash":
            continue
        if ev.get("is_tk"):
            continue
        cmd = ev.get("tool_input_summary") or ""
        try:
            parts = shlex.split(cmd)
        except ValueError:
            parts = cmd.split()
        if not parts or parts[0] not in _GREP_CMDS:
            continue
        pattern = _extract_grep_pattern(parts)
        if not pattern:
            continue
        ev["native_grep_retry_pattern"] = pattern
        window = seen_patterns[-NATIVE_GREP_RETRY_LOOKBACK:]
        if pattern in window:
            ev["native_grep_retry"] = True
        seen_patterns.append(pattern)


# ---------------------------------------------------------------------------
# Stealth-read detection (B3)
# ---------------------------------------------------------------------------

# A sed line-range arg: "p" (whole file), "Np" (single line), or "N,Mp" (range).
_SED_RANGE_PATTERN = _re.compile(r"^(\d+(,\d+)?)?p$")


def _is_garbage_path(tok: str) -> bool:
    """Skip flag-like or glob/var path tokens defensively."""
    if tok.startswith("-"):
        return True
    if "*" in tok or "$" in tok:
        return True
    return False


def _detect_stealth_read(events: list[dict]) -> None:
    """Annotate non-tk Bash events that read a tracked source file via
    cat/head/tail (any tracked extension) or sed with a line-range.

    For marked events: stealth_read=True, stealth_read_file=path,
    stealth_read_chars=shown_chars (or 0).
    """
    for ev in events:
        ev["stealth_read"] = False
        ev["stealth_read_file"] = ""
        ev["stealth_read_chars"] = 0
        if ev.get("tool") != "Bash":
            continue
        if ev.get("is_tk"):
            continue
        cmd = ev.get("tool_input_summary") or ""
        try:
            parts = shlex.split(cmd)
        except ValueError:
            parts = cmd.split()
        if not parts:
            continue
        first = parts[0]
        file_path = ""

        if first in {"cat", "head", "tail"}:
            for tok in parts[1:]:
                if tok.startswith("-"):
                    continue
                if any(tok.endswith(ext) for ext in STEALTH_READ_EXTENSIONS):
                    if not _is_garbage_path(tok):
                        file_path = tok
                        break

        elif first == "sed":
            has_range = any(_SED_RANGE_PATTERN.match(tok) for tok in parts[1:])
            if has_range:
                for tok in parts[1:]:
                    if tok.startswith("-"):
                        continue
                    if _SED_RANGE_PATTERN.match(tok):
                        continue
                    if any(tok.endswith(ext) for ext in STEALTH_READ_EXTENSIONS):
                        if not _is_garbage_path(tok):
                            file_path = tok
                            break

        if file_path:
            ev["stealth_read"] = True
            ev["stealth_read_file"] = file_path
            ev["stealth_read_chars"] = ev.get("shown_chars") or 0


# ---------------------------------------------------------------------------
# Structured tk-invocation accessor (general-purpose, used by aggregates.py
# and tkstats.detect.adoption)
# ---------------------------------------------------------------------------

def _event_tk_invocations(ev: dict) -> list[dict]:
    """Return structured tk segments, falling back to the legacy event shape."""
    invocations = ev.get("tk_invocations")
    if isinstance(invocations, list) and invocations:
        return [item for item in invocations if isinstance(item, dict)]
    return [ev] if ev.get("is_tk") else []
