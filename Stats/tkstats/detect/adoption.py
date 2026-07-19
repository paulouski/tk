"""
S4 — post-hook adoption stats.

Measures whether the agent obeys tk-route.sh hook interventions ("cap-read",
"nudge", and "nudge-edit-write" — additionalContext-only hints it complies
with voluntarily). "route-tk"/"route-rtk" are auto-applied rewrites, not
voluntary adoption, and are left unannotated here (the report tallies them
separately).
"""

from __future__ import annotations

import shlex

from tkstats.config import (
    ADOPTION_EDIT_WRITE_COMMANDS,
    ADOPTION_LOOKAHEAD,
    ADOPTION_NAV_COMMANDS,
    ADOPTION_SYMBOL_COMMANDS,
)
from tkstats.detect.events import _GREP_CMDS, _event_tk_invocations, _is_garbage_path
from tkstats.timeparse import _parse_ts

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
