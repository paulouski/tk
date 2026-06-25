"""
P2b — report.py

Top-level build CLI.  Pipeline: ingest → join → detect → write model.json + report.json.

Usage:
    python3 Stats/report.py [--from ISO] [--to ISO]

Outputs:
    Stats/model.json   — full annotated model
    Stats/report.json  — compact digest (no per-event lists)
"""

from __future__ import annotations

import argparse
import json
import sys
from collections import Counter, defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from ingest import _parse_cli_dt  # type: ignore[import]
from join import run_join  # type: ignore[import]
from detect import annotate  # type: ignore[import]


# ---------------------------------------------------------------------------
# Compact report builder
# ---------------------------------------------------------------------------

def _build_report(model: dict) -> dict:
    """Build the compact report.json dict from the annotated model."""
    rng = model["generated_for_range"]
    ms = model["match_stats"]
    oc = model["outcome_counts"]
    agg = model["aggregates"]
    sessions = model["sessions"]

    # -- by_command ----------------------------------------------------------
    cmd_stats: dict[str, dict] = defaultdict(lambda: {
        "n": 0, "matched": 0, "win": 0, "net_negative": 0,
        "empty_n": 0, "fallback_n": 0, "result_used_n": 0, "retry_n": 0,
        "sum_shown": 0, "sum_raw": 0, "n_shown": 0, "n_raw": 0,
    })
    # -- by_agent_type -------------------------------------------------------
    agent_stats: dict[str, dict] = defaultdict(lambda: {
        "n_sessions": 0, "n_tk": 0, "fallback_n": 0, "result_used_n": 0, "empty_n": 0,
    })
    seen_sessions: dict[str, set] = defaultdict(set)

    for session in sessions:
        at = session.get("agent_type", "unknown")
        sid = session.get("session_id", "")
        if sid not in seen_sessions[at]:
            seen_sessions[at].add(sid)
            agent_stats[at]["n_sessions"] += 1

        for ev in session.get("events", []):
            if not ev.get("is_tk"):
                continue

            cmd = ev.get("tk_command") or "(empty)"
            sub = ev.get("tk_sub")
            if sub:
                cmd = f"{cmd} {sub}"

            cs = cmd_stats[cmd]
            cs["n"] += 1

            outcome = ev.get("outcome", "UNKNOWN")
            if outcome == "WIN":
                cs["win"] += 1
                cs["matched"] += 1
            elif outcome == "NET_NEGATIVE":
                cs["net_negative"] += 1
                cs["matched"] += 1
            # UNKNOWN with raw_chars present is still matched
            elif ev.get("raw_chars") is not None:
                cs["matched"] += 1

            if ev.get("empty_result"):
                cs["empty_n"] += 1
            if ev.get("fallback"):
                cs["fallback_n"] += 1
            if ev.get("result_used"):
                cs["result_used_n"] += 1
            if ev.get("retry"):
                cs["retry_n"] += 1

            shown = ev.get("shown_chars")
            if shown is not None:
                cs["sum_shown"] += shown
                cs["n_shown"] += 1
            raw = ev.get("raw_chars")
            if raw is not None:
                cs["sum_raw"] += raw
                cs["n_raw"] += 1

            # agent-type tallies
            agent_stats[at]["n_tk"] += 1
            if ev.get("fallback"):
                agent_stats[at]["fallback_n"] += 1
            if ev.get("result_used"):
                agent_stats[at]["result_used_n"] += 1
            if ev.get("empty_result"):
                agent_stats[at]["empty_n"] += 1

    by_command = []
    for cmd, cs in cmd_stats.items():
        by_command.append({
            "command": cmd,
            "n": cs["n"],
            "matched": cs["matched"],
            "win": cs["win"],
            "net_negative": cs["net_negative"],
            "empty_n": cs["empty_n"],
            "fallback_n": cs["fallback_n"],
            "result_used_n": cs["result_used_n"],
            "retry_n": cs["retry_n"],
            "avg_shown_chars": round(cs["sum_shown"] / cs["n_shown"]) if cs["n_shown"] else None,
            "avg_raw_chars": round(cs["sum_raw"] / cs["n_raw"]) if cs["n_raw"] else None,
        })
    by_command.sort(key=lambda x: x["n"], reverse=True)

    by_agent_type = []
    for at, ast in agent_stats.items():
        by_agent_type.append({
            "agent_type": at,
            **ast,
        })
    by_agent_type.sort(key=lambda x: x["n_sessions"], reverse=True)

    # -- findings ------------------------------------------------------------
    findings: list[dict] = []

    # Top NET_NEGATIVE commands
    neg_cmds = sorted(
        [(c["command"], c["net_negative"]) for c in by_command if c["net_negative"] > 0],
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
        [(c["command"], c["fallback_n"]) for c in by_command if c["fallback_n"] > 0],
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
        [(c["command"], c["empty_n"]) for c in by_command if c["empty_n"] > 0],
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
        },
        "outcomes": oc,
        "savings": {
            "win_saved_chars": agg["win"]["saved_chars"],
            "net_negative_extra_chars": agg["net_negative"]["extra_chars"],
            "net_chars": net_saved,
        },
        "by_command": by_command,
        "by_agent_type": by_agent_type,
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
            elif finding_type == "fallback" and ev.get("fallback"):
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


# ---------------------------------------------------------------------------
# Human summary
# ---------------------------------------------------------------------------

def _print_summary(report: dict) -> None:
    """Print a concise human-readable summary."""
    t = report["totals"]
    oc = report["outcomes"]
    sv = report["savings"]
    rng = report["range"]

    af = rng.get("actual_from") or "?"
    at = rng.get("actual_to") or "?"
    print(f"Range:         {af[:10]} -> {at[:10]}")
    print(f"Sessions:      {t['sessions']}")
    print(f"tk events:     {t['tk_events']}  (matched {t['matched']}, rate {t['match_rate']*100:.1f}%)")
    print(f"Outcomes:      WIN={oc['WIN']}  NET_NEG={oc['NET_NEGATIVE']}  UNK={oc['UNKNOWN']}")
    print(f"Savings:       saved={sv['win_saved_chars']:,}  extra={sv['net_negative_extra_chars']:,}  net={sv['net_chars']:,}")
    print()

    print("Top commands:")
    for c in report["by_command"][:5]:
        parts = [f"n={c['n']}"]
        if c["win"]:
            parts.append(f"win={c['win']}")
        if c["net_negative"]:
            parts.append(f"neg={c['net_negative']}")
        if c["fallback_n"]:
            parts.append(f"fb={c['fallback_n']}")
        if c.get("result_used_n"):
            parts.append(f"ru={c['result_used_n']}")
        if c["empty_n"]:
            parts.append(f"empty={c['empty_n']}")
        if c["retry_n"]:
            parts.append(f"retry={c['retry_n']}")
        print(f"  {c['command']:20s} {', '.join(parts)}")
    print()

    if report["findings"]:
        print("Findings:")
        for f in report["findings"]:
            print(f"  [{f['type']}] {f['detail']}")


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main() -> None:
    parser = argparse.ArgumentParser(description="Build annotated model + compact report")
    parser.add_argument("--from", dest="from_dt", metavar="ISO")
    parser.add_argument("--to", dest="to_dt", metavar="ISO")
    args = parser.parse_args()

    from_dt = _parse_cli_dt(args.from_dt) if args.from_dt else None
    to_dt = _parse_cli_dt(args.to_dt, end_of_day=True) if args.to_dt else None

    # Pipeline: join -> annotate -> report
    model = run_join(from_dt, to_dt)
    annotate(model)

    # Write model.json
    result_dir = Path(__file__).parent / "result"
    result_dir.mkdir(exist_ok=True)
    model_path = result_dir / "model.json"
    with open(model_path, "w", encoding="utf-8") as f:
        json.dump(model, f, indent=2)

    # Build and write report.json
    report = _build_report(model)
    report_path = result_dir / "report.json"
    with open(report_path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)

    _print_summary(report)
    print()
    print(f"model.json:  {model_path}  ({model_path.stat().st_size:,} bytes)")
    print(f"report.json: {report_path}  ({report_path.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
