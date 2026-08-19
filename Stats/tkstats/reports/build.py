"""
Compact report.json construction from an annotated model.
"""

from __future__ import annotations

from tkstats.aggregates import (
    aggregate_by_version,
    aggregate_cost_summary,
    aggregate_events,
    aggregate_retry_total,
)
from tkstats.detect.adoption import _summarize_adoption


def build_report(model: dict) -> dict:
    """Build the compact report.json dict from the annotated model."""
    rng = model["generated_for_range"]
    ms = model["match_stats"]
    oc = model["outcome_counts"]
    agg = model["aggregates"]
    sessions = model["sessions"]

    events_agg = aggregate_events(sessions)
    by_command = events_agg["by_command"]
    by_agent_type = events_agg["by_agent_type"]
    symbol_grep_block = events_agg["symbol_grep"]
    native_grep_retry_block = events_agg["native_grep_retry"]
    reread_block = events_agg["reread"]
    tk_invocations_block = events_agg["tk_invocations"]
    sg_total = symbol_grep_block["total"]
    ngr_total = native_grep_retry_block["total"]
    reread_total = reread_block["total"]

    # -- findings ------------------------------------------------------------
    findings: list[dict] = []

    # Top NET_NEGATIVE commands (only for search/nav eligible rows)
    neg_cmds = sorted(
        [(c["command"], c["net_negative"]) for c in by_command if c.get("net_negative", 0) > 0],
        key=lambda x: x[1], reverse=True,
    )
    for cmd, n in neg_cmds[:2]:
        examples = _find_examples(sessions, "net_negative", cmd, 3)
        findings.append({
            "type": "net_negative",
            "command": cmd,
            "n": n,
            "detail": f"{cmd} produced more output than raw in {n} events",
            "examples": examples,
        })

    # Top fallback-heavy commands
    fb_cmds = sorted(
        [(c["command"], c["fallback_n"]) for c in by_command if c.get("fallback_n", 0) > 0],
        key=lambda x: x[1], reverse=True,
    )
    for cmd, n in fb_cmds[:2]:
        examples = _find_examples(sessions, "fallback", cmd, 3)
        findings.append({
            "type": "fallback_heavy",
            "command": cmd,
            "n": n,
            "detail": f"{cmd} followed by native fallback in {n} events",
            "examples": examples,
        })

    # Top empty-heavy commands
    em_cmds = sorted(
        [(c["command"], c["empty_n"]) for c in by_command if c.get("empty_n", 0) > 0],
        key=lambda x: x[1], reverse=True,
    )
    if em_cmds:
        cmd, n = em_cmds[0]
        examples = _find_examples(sessions, "empty", cmd, 3)
        findings.append({
            "type": "empty_heavy",
            "command": cmd,
            "n": n,
            "detail": f"{cmd} returned empty in {n} events",
            "examples": examples,
        })

    findings = findings[:5]

    net_saved = agg["win"]["saved_chars"] - agg["net_negative"]["extra_chars"]
    retry_total = aggregate_retry_total(sessions)
    cost_summary = aggregate_cost_summary(model)
    by_version = aggregate_by_version(sessions)
    adoption_block = _summarize_adoption(model)

    return {
        "range": {
            "from": rng.get("from"),
            "to": rng.get("to"),
            "actual_from": rng.get("actual_from"),
            "actual_to": rng.get("actual_to"),
        },
        "totals": {
            "sessions": len(sessions),
            "tk_events": ms["total_tk_events"],
            "matched": ms["matched"],
            "match_rate": ms["match_rate"],
            "own_log_matched_events": ms.get("own_log_matched_events", ms["matched"]),
            "own_log_event_match_rate": ms.get("own_log_event_match_rate", ms["match_rate"]),
            "raw_baseline_events": ms.get("raw_baseline_events", ms["matched"]),
            "raw_baseline_event_rate": ms.get("raw_baseline_event_rate", ms["match_rate"]),
            "tk_invocations": ms.get("total_tk_invocations", ms["total_tk_events"]),
            "own_log_matched_invocations": ms.get("own_log_matched_invocations", ms["matched"]),
            "raw_baseline_invocations": ms.get("raw_baseline_invocations", ms["matched"]),
            "retry_total": retry_total,
            "symbol_grep_total": sg_total,
            "native_grep_retry_total": ngr_total,
            "reread_total": reread_total,
        },
        "outcomes": oc,
        "savings": {
            "win_saved_chars": agg["win"]["saved_chars"],
            "net_negative_extra_chars": agg["net_negative"]["extra_chars"],
            "net_chars": net_saved,
        },
        "symbol_grep": symbol_grep_block,
        "native_grep_retry": native_grep_retry_block,
        "reread": reread_block,
        "cost": cost_summary,
        "by_command": by_command,
        "by_agent_type": by_agent_type,
        "by_version": by_version,
        "tk_invocations": tk_invocations_block,
        "adoption": adoption_block,
        "findings": findings,
    }


def _find_examples(sessions: list[dict], finding_type: str, command: str, limit: int) -> list[dict]:
    """Find example events for a finding."""
    examples: list[dict] = []
    for session in sessions:
        for ev in session.get("events", []):
            if not ev.get("is_tk"):
                continue
            cmd = ev.get("tk_command") or "(empty)"
            sub = ev.get("tk_sub")
            if sub:
                cmd = f"{cmd} {sub}"
            if cmd != command:
                continue

            match = False
            if finding_type == "net_negative" and ev.get("outcome") == "NET_NEGATIVE":
                match = True
            elif finding_type == "fallback" and ev.get("fallback_same_target"):
                match = True
            elif finding_type == "empty" and ev.get("empty_result"):
                match = True

            if match:
                examples.append({
                    "session_id": session.get("session_id", ""),
                    "ts": ev.get("ts", ""),
                    "operands": ev.get("tk_operand_values", []),
                })
                if len(examples) >= limit:
                    return examples
    return examples
