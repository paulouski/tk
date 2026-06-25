"""
P2a — detect.py

Pure annotation functions that enrich a joined model dict in place.
Operates per session on the ts-ordered event list.

Importable API
--------------
  annotate(model) -> model   # mutates and returns the same dict
"""

from __future__ import annotations


# ---------------------------------------------------------------------------
# Search/nav tk commands eligible for fallback detection
# ---------------------------------------------------------------------------

_SEARCH_NAV_COMMANDS = frozenset({
    "grep", "focus", "refs", "def", "callers",
    "view", "files", "tree", "find", "ls",
})

# Native tool names that count as fallback (negative signal: re-searched manually)
_FALLBACK_TOOLS = frozenset({"Grep", "Glob"})

# Native tool name that counts as result-used (positive signal: tk led to file)
_RESULT_USED_TOOLS = frozenset({"Read"})


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
    result_used (positive): tk search/nav followed within 3 events by Read.
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

            if tool in _RESULT_USED_TOOLS and not ev["result_used"]:
                ev["result_used"] = True
                if operand_vals:
                    summary = following.get("tool_input_summary", "")
                    if summary and any(op in summary for op in operand_vals):
                        ev["result_used_same_target"] = True


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
# Session rollups
# ---------------------------------------------------------------------------

def _rollup_session(session: dict) -> None:
    """Add per-session summary counts."""
    events = session.get("events", [])
    n_fallback = 0
    n_result_used = 0
    n_retry = 0
    n_escalated = 0
    n_empty = 0
    outcomes: dict[str, int] = {"WIN": 0, "NEUTRAL": 0, "NET_NEGATIVE": 0, "UNKNOWN": 0}

    for ev in events:
        if not ev.get("is_tk"):
            continue
        if ev.get("fallback"):
            n_fallback += 1
        if ev.get("result_used"):
            n_result_used += 1
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
    session["n_retry"] = n_retry
    session["n_escalated"] = n_escalated
    session["n_empty"] = n_empty
    session["outcomes"] = outcomes


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def annotate(model: dict) -> dict:
    """Run all detectors on every session in the model, add rollups. Mutates in place."""
    for session in model.get("sessions", []):
        events = session.get("events", [])
        _detect_fallback(events)
        _detect_retry(events)
        _detect_escalated(events)
        _detect_empty(events)
        _rollup_session(session)
    return model
