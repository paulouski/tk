"""
Per-session rollup: aggregates the flags set by tkstats.detect.events'
detectors into session-level summary counts.
"""

from __future__ import annotations


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
