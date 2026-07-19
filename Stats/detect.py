"""
P2a — detect.py

Pure annotation functions that enrich a joined model dict in place.
Operates per session on the ts-ordered event list.

Importable API
--------------
  annotate(model) -> model   # mutates and returns the same dict
"""

from __future__ import annotations

import re as _re
import shlex

from config import (
    ADOPTION_EDIT_WRITE_COMMANDS,
    ADOPTION_LOOKAHEAD,
    ADOPTION_NAV_COMMANDS,
    ADOPTION_SYMBOL_COMMANDS,
    EDIT_RESULT_USED_TOOLS,
    NATIVE_GREP_RETRY_LOOKBACK,
    STEALTH_READ_EXTENSIONS,
    _FALLBACK_TOOLS,
    _RESULT_USED_TOOLS,
    _SEARCH_NAV_COMMANDS,
)
from ingest import _parse_ts  # type: ignore[import]


# ---------------------------------------------------------------------------
# Search/nav tk commands eligible for fallback detection
# ---------------------------------------------------------------------------
# Constants are imported from config.py (single source of truth).


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

# Grep-like binaries that count as a native grep when invoked via Bash
_GREP_CMDS = frozenset({"grep", "rg", "egrep", "fgrep"})

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

# A sed line-range arg: "p" (whole file), "Np" (single line), or "N,Mp" (range).
_SED_RANGE_PATTERN = _re.compile(r"^(\d+(,\d+)?)?p$")


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
# S4 — post-hook adoption stats
# ---------------------------------------------------------------------------
# Measures whether the agent obeys tk-route.sh hook interventions ("cap-read",
# "nudge", and "nudge-edit-write" — additionalContext-only hints it complies
# with voluntarily). "route-tk"/"route-rtk" are auto-applied rewrites, not
# voluntary adoption, and are left unannotated here (report.py tallies them
# separately).

# Stealth-read nudge binaries (mirrors CS_STEALTH_READ_CMD_RE in tk-route.sh).
_NUDGE_STEALTH_CMDS = frozenset({"cat", "sed", "head", "tail"})


def _classify_nudge_subtype(command: str) -> str:
    """Classify a 'nudge' intervention by its logged command's first token.

    A "nudge" decision only ever comes from one of two hook triggers: a
    symbol-grep (grep/egrep/fgrep/rg) or a .cs stealth read (cat/sed/head/
    tail) — see hooks/tk-route.sh. "ambiguous" is a defensive fallback that
    should not occur for a real nudge line.
    """
    try:
        parts = shlex.split(command)
    except ValueError:
        parts = command.split()
    first = parts[0] if parts else ""
    if first in _GREP_CMDS:
        return "symbol_grep"
    if first in _NUDGE_STEALTH_CMDS:
        return "stealth_read"
    return "ambiguous"


def _extract_stealth_read_file(command: str) -> str:
    """Extract the target .cs path from a stealth-read nudge command
    (cat/sed/head/tail ... file.cs), mirroring the extraction in
    _detect_stealth_read. Returns "" if no plausible .cs path token is found.
    """
    try:
        parts = shlex.split(command)
    except ValueError:
        parts = command.split()
    for tok in parts[1:]:
        if _is_garbage_path(tok):
            continue
        if tok.endswith(".cs"):
            return tok
    return ""


def _adoption_signal_commands(subtype: str) -> frozenset[str]:
    """tk commands that count as adoption for a given nudge/cap-read subtype."""
    if subtype == "symbol_grep":
        return ADOPTION_SYMBOL_COMMANDS
    if subtype == "ambiguous":
        return ADOPTION_NAV_COMMANDS | ADOPTION_SYMBOL_COMMANDS
    return ADOPTION_NAV_COMMANDS  # cap_read, stealth_read


def _is_adoption_signal(ev: dict, subtype: str, target_file: str) -> bool:
    """True if ev is an adoption signal for the given intervention subtype."""
    invocations = _event_tk_invocations(ev)
    if subtype == "edit_write":
        # Unlike the other subtypes, adoption requires `tk write` on the SAME
        # file the nudge fired for — any `tk write` would over-count, since the
        # nudge is per-file and the agent may be mid-edit on unrelated files.
        if not target_file:
            return False
        for invocation in invocations:
            if invocation.get("tk_command") not in ADOPTION_EDIT_WRITE_COMMANDS:
                continue
            operands = invocation.get("tk_operand_values") or []
            if operands and operands[0] == target_file:
                return True
        return False
    if any(
        invocation.get("tk_command") in _adoption_signal_commands(subtype)
        for invocation in invocations
    ):
        return True
    # A sliced Read (non-empty offset/limit) of the same file that was
    # capped/stealth-read also counts — not applicable to symbol-grep nudges
    # (there is no "target file", just a symbol pattern).
    if subtype != "symbol_grep" and target_file and ev.get("tool") == "Read":
        path = ev.get("tool_input_summary") or ""
        if path == target_file and (ev.get("read_offset") or ev.get("read_limit")):
            return True
    return False


def _event_tk_invocations(ev: dict) -> list[dict]:
    """Return structured tk segments, falling back to the legacy event shape."""
    invocations = ev.get("tk_invocations")
    if isinstance(invocations, list) and invocations:
        return [item for item in invocations if isinstance(item, dict)]
    return [ev] if ev.get("is_tk") else []


def _event_epoch(ev: dict) -> float | None:
    """Convert an event's ISO ts to epoch seconds, comparable to the hook
    log's ts_epoch (unix seconds)."""
    dt = _parse_ts(ev.get("ts"))
    return dt.timestamp() if dt is not None else None


def _annotate_intervention(iv: dict, events: list[dict], event_epochs: list[float | None]) -> None:
    """Set adopted (bool|None) and nudge_subtype (str|None) on one intervention.

    events/event_epochs must be that intervention's session_id's own events,
    ts-ordered (see _detect_adoption for how these are assembled).
    """
    iv["adopted"] = None
    iv["nudge_subtype"] = None
    decision = iv.get("decision")
    if decision not in ("cap-read", "nudge", "nudge-edit-write"):
        return

    command = iv.get("command") or ""
    if decision == "cap-read":
        subtype = "cap_read"
        target_file = command
    elif decision == "nudge-edit-write":
        # tk-route.sh logs the edited file path (not a shell command) as the
        # command column for this decision — see _log_decision call site.
        subtype = "edit_write"
        target_file = command
    else:
        subtype = _classify_nudge_subtype(command)
        target_file = _extract_stealth_read_file(command) if subtype != "symbol_grep" else ""
    iv["nudge_subtype"] = subtype
    iv["adopted"] = False

    iv_ts = iv.get("ts_epoch")
    if iv_ts is None:
        return

    start = None
    for i, ep in enumerate(event_epochs):
        if ep is not None and ep > iv_ts:
            start = i
            break
    if start is None:
        return

    for j in range(start, min(start + ADOPTION_LOOKAHEAD, len(events))):
        if _is_adoption_signal(events[j], subtype, target_file):
            iv["adopted"] = True
            break


def _detect_adoption(model: dict) -> None:
    """Annotate every hook-log intervention in model["interventions"] with
    adopted (bool|None) and nudge_subtype (str|None). Mutates in place.

    Interventions live in a single flat, model-level list (not attached per
    session dict): a resumed Claude Code session can span multiple transcript
    files that all share the same session_id (one session dict per file), so
    grouping happens here, by session_id, against that session_id's events
    MERGED across every file that shares it and re-sorted by ts — otherwise
    an intervention logged in one file-fragment could never see an adoption
    signal that lands in a later fragment, and (were interventions instead
    attached per file) the same intervention would get scored once per
    fragment. "route-tk"/"route-rtk" are auto-applied rewrites, not voluntary
    adoption: adopted/nudge_subtype are left as None (see
    _annotate_intervention).
    """
    interventions = model.get("interventions") or []
    if not interventions:
        return

    events_by_sid: dict[str, list[dict]] = {}
    for session in model.get("sessions", []):
        sid = session.get("session_id", "")
        events_by_sid.setdefault(sid, []).extend(session.get("events", []))

    interventions_by_sid: dict[str, list[dict]] = {}
    for iv in interventions:
        interventions_by_sid.setdefault(iv.get("session_id", ""), []).append(iv)

    for sid, ivs in interventions_by_sid.items():
        events = events_by_sid.get(sid, [])
        events = sorted(events, key=lambda ev: _event_epoch(ev) or 0.0)
        event_epochs = [_event_epoch(ev) for ev in events]
        for iv in ivs:
            _annotate_intervention(iv, events, event_epochs)


def _summarize_adoption(model: dict) -> dict:
    """Aggregate already-annotated hook interventions without session duplication."""
    stats: dict[str, dict[str, int]] = {}
    auto_routed_n = 0
    for iv in model.get("interventions") or []:
        decision = iv.get("decision")
        if decision in ("route-tk", "route-rtk"):
            auto_routed_n += 1
            continue
        if decision not in ("cap-read", "nudge", "nudge-edit-write"):
            continue
        subtype = iv.get("nudge_subtype") or decision
        row = stats.setdefault(subtype, {"n_interventions": 0, "n_adopted": 0})
        row["n_interventions"] += 1
        if iv.get("adopted"):
            row["n_adopted"] += 1
    return {
        "by_subtype": {
            subtype: {
                **row,
                "rate": round(row["n_adopted"] / row["n_interventions"], 4)
                if row["n_interventions"] else 0.0,
            }
            for subtype, row in sorted(stats.items())
        },
        "auto_routed": {"n": auto_routed_n},
    }


# ---------------------------------------------------------------------------
# Session rollups
# ---------------------------------------------------------------------------

def _rollup_session(session: dict) -> None:
    """Add per-session summary counts."""
    events = session.get("events", [])
    n_fallback = 0
    n_result_used = 0
    n_result_used_edit = 0
    n_retry = 0
    n_escalated = 0
    n_empty = 0
    n_symbol_grep = 0
    n_reread = 0
    reread_chars_total = 0
    read_repeat_counts = {
        "exact_slice_repeat": 0,
        "whole_file_repeat": 0,
        "different_slice": 0,
    }
    read_repeat_chars = {key: 0 for key in read_repeat_counts}
    n_native_grep_retry = 0
    n_stealth_read = 0
    stealth_read_chars_total = 0
    outcomes: dict[str, int] = {"WIN": 0, "NEUTRAL": 0, "NET_NEGATIVE": 0, "UNKNOWN": 0}

    for ev in events:
        if ev.get("symbol_grep"):
            n_symbol_grep += 1
        if ev.get("reread"):
            n_reread += 1
            reread_chars_total += ev.get("reread_chars") or 0
        repeat_kind = ev.get("read_repeat_kind")
        if repeat_kind in read_repeat_counts:
            read_repeat_counts[repeat_kind] += 1
            read_repeat_chars[repeat_kind] += ev.get("shown_chars") or 0
        if ev.get("native_grep_retry"):
            n_native_grep_retry += 1
        if ev.get("stealth_read"):
            n_stealth_read += 1
            stealth_read_chars_total += ev.get("stealth_read_chars") or 0
        if not ev.get("is_tk"):
            continue
        if ev.get("fallback"):
            n_fallback += 1
        if ev.get("result_used"):
            n_result_used += 1
        if ev.get("result_used_edit"):
            n_result_used_edit += 1
        if ev.get("retry"):
            n_retry += 1
        if ev.get("escalated"):
            n_escalated += 1
        if ev.get("empty_result"):
            n_empty += 1
        oc = ev.get("outcome", "UNKNOWN")
        outcomes[oc] = outcomes.get(oc, 0) + 1

    session["n_fallback"] = n_fallback
    session["n_result_used"] = n_result_used
    session["n_result_used_edit"] = n_result_used_edit
    session["n_retry"] = n_retry
    session["n_escalated"] = n_escalated
    session["n_empty"] = n_empty
    session["n_symbol_grep"] = n_symbol_grep
    session["n_reread"] = n_reread
    session["reread_chars_total"] = reread_chars_total
    session["read_repeat_counts"] = read_repeat_counts
    session["read_repeat_chars"] = read_repeat_chars
    session["n_native_grep_retry"] = n_native_grep_retry
    session["n_stealth_read"] = n_stealth_read
    session["stealth_read_chars_total"] = stealth_read_chars_total
    session["outcomes"] = outcomes


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def annotate(model: dict) -> dict:
    """Run all detectors on every session in the model, add rollups. Mutates in place."""
    _detect_adoption(model)  # model-level: groups interventions by session_id itself
    for session in model.get("sessions", []):
        events = session.get("events", [])
        _detect_stealth_read(events)
        _detect_fallback(events)
        _detect_retry(events)
        _detect_escalated(events)
        _detect_empty(events)
        _detect_symbol_grep(events)
        _detect_reread(events)
        _detect_native_grep_retry(events)
        _rollup_session(session)
    return model
