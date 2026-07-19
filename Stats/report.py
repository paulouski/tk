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
from detect import (  # type: ignore[import]
    annotate,
    _event_tk_invocations,
    _SEARCH_NAV_COMMANDS,
    _summarize_adoption,
)
from pricing import get_unknown_models  # type: ignore[import]


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
        "n": 0, "own_log_matched": 0, "raw_baseline": 0,
        "win": 0, "neutral": 0, "net_negative": 0,
        "empty_n": 0, "fallback_n": 0, "result_used_n": 0, "retry_n": 0,
        "sum_shown": 0, "sum_raw": 0, "n_shown": 0, "n_raw": 0,
        "symbol_grep_n": 0,
    })
    # -- by_agent_type -------------------------------------------------------
    agent_stats: dict[str, dict] = defaultdict(lambda: {
        "n_sessions": 0, "n_tk": 0, "fallback_n": 0, "result_used_n": 0, "empty_n": 0,
    })
    seen_sessions: dict[str, set] = defaultdict(set)

    # symbol_grep tracking: per session and per native-tool "command" label
    sg_by_session: dict[str, int] = defaultdict(int)   # session_id -> count
    sg_by_cmd: dict[str, int] = defaultdict(int)        # tool+pattern key -> count
    sg_total = 0

    # native_grep_retry / reread tracking: per session, from rollup fields
    ngr_by_session: dict[str, int] = defaultdict(int)
    ngr_total = 0
    reread_by_session: dict[str, int] = defaultdict(int)
    reread_chars_by_session: dict[str, int] = defaultdict(int)
    reread_total = 0
    reread_chars_total = 0
    read_repeat_counts: Counter = Counter()
    read_repeat_chars: Counter = Counter()
    tk_invocation_counts: Counter = Counter()

    for session in sessions:
        at = session.get("agent_type", "unknown")
        sid = session.get("session_id", "")
        if sid not in seen_sessions[at]:
            seen_sessions[at].add(sid)
            agent_stats[at]["n_sessions"] += 1

        # native_grep_retry / reread: session-level rollup fields set by
        # detect._rollup_session (n_native_grep_retry, n_reread, reread_chars_total).
        ngr = session.get("n_native_grep_retry", 0) or 0
        ngr_total += ngr
        if ngr:
            ngr_by_session[sid] += ngr

        rr = session.get("n_reread", 0) or 0
        rr_chars = session.get("reread_chars_total", 0) or 0
        reread_total += rr
        reread_chars_total += rr_chars
        if rr:
            reread_by_session[sid] += rr
            reread_chars_by_session[sid] += rr_chars
        read_repeat_counts.update(session.get("read_repeat_counts") or {})
        read_repeat_chars.update(session.get("read_repeat_chars") or {})

        for ev in session.get("events", []):
            # symbol_grep is on ALL events (tk and native)
            if ev.get("symbol_grep"):
                sg_total += 1
                sg_by_session[sid] += 1
                # Label by tool + first token of summary (the grep binary or pattern)
                summary = ev.get("tool_input_summary", "")
                tool = ev.get("tool", "")
                first = summary.split()[0] if summary.split() else ""
                label = f"{tool}:{first}" if first else tool
                sg_by_cmd[label] += 1

            if not ev.get("is_tk"):
                continue

            for invocation in _event_tk_invocations(ev):
                invocation_cmd = invocation.get("tk_command") or "(empty)"
                invocation_sub = invocation.get("tk_sub")
                if invocation_sub:
                    invocation_cmd = f"{invocation_cmd} {invocation_sub}"
                tk_invocation_counts[invocation_cmd] += 1

            cmd = ev.get("tk_command") or "(empty)"
            sub = ev.get("tk_sub")
            if sub:
                cmd = f"{cmd} {sub}"

            cs = cmd_stats[cmd]
            cs["n"] += 1

            outcome = ev.get("outcome", "UNKNOWN")
            if outcome == "WIN":
                cs["win"] += 1
            elif outcome == "NEUTRAL":
                cs["neutral"] += 1
            elif outcome == "NET_NEGATIVE":
                cs["net_negative"] += 1

            if ev.get("own_log_matched", ev.get("raw_chars") is not None):
                cs["own_log_matched"] += 1
            if ev.get("raw_chars") is not None:
                cs["raw_baseline"] += 1

            if ev.get("empty_result"):
                cs["empty_n"] += 1
            if ev.get("fallback"):
                cs["fallback_n"] += 1
            if ev.get("result_used"):
                cs["result_used_n"] += 1
            if ev.get("retry"):
                cs["retry_n"] += 1

            shown = ev.get("shown_chars_ownlog")
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
        # Determine the base command (strip sub-command for eligibility check)
        base_cmd = cmd.split()[0] if cmd else cmd
        is_search_nav = base_cmd in _SEARCH_NAV_COMMANDS
        row: dict = {
            "command": cmd,
            "n": cs["n"],
            "retry_n": cs["retry_n"],
            "avg_shown_chars": round(cs["sum_shown"] / cs["n_shown"]) if cs["n_shown"] else None,
            "avg_raw_chars": round(cs["sum_raw"] / cs["n_raw"]) if cs["n_raw"] else None,
        }
        if is_search_nav:
            row["matched"] = cs["own_log_matched"]
            row["own_log_matched"] = cs["own_log_matched"]
            row["raw_baseline"] = cs["raw_baseline"]
            row["win"] = cs["win"]
            row["neutral"] = cs["neutral"]
            row["net_negative"] = cs["net_negative"]
            row["empty_n"] = cs["empty_n"]
            row["fallback_n"] = cs["fallback_n"]
            row["result_used_n"] = cs["result_used_n"]
        else:
            row["applicable"] = False
        by_command.append(row)
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

    # -- retry total ----------------------------------------------------------
    retry_total = sum(
        ev.get("retry", False)
        for session in sessions
        for ev in session.get("events", [])
        if ev.get("is_tk")
    )

    # -- symbol_grep summary --------------------------------------------------
    top_sg_sessions = sorted(sg_by_session.items(), key=lambda x: x[1], reverse=True)[:5]
    top_sg_cmds = sorted(sg_by_cmd.items(), key=lambda x: x[1], reverse=True)[:5]
    symbol_grep_block = {
        "total": sg_total,
        "top_sessions": [{"session_id": sid, "n": n} for sid, n in top_sg_sessions],
        "top_commands": [{"label": label, "n": n} for label, n in top_sg_cmds],
    }

    # -- native_grep_retry summary --------------------------------------------
    top_ngr_sessions = sorted(ngr_by_session.items(), key=lambda x: x[1], reverse=True)[:5]
    native_grep_retry_block = {
        "total": ngr_total,
        "top_sessions": [{"session_id": sid, "n": n} for sid, n in top_ngr_sessions],
    }

    # -- reread summary --------------------------------------------------------
    top_reread_sessions = sorted(reread_by_session.items(), key=lambda x: x[1], reverse=True)[:5]
    top_reread_chars_sessions = sorted(
        reread_chars_by_session.items(), key=lambda x: x[1], reverse=True
    )[:5]
    reread_block = {
        "total": reread_total,
        "chars_total": reread_chars_total,
        "by_kind": {
            kind: {
                "n": read_repeat_counts.get(kind, 0),
                "chars": read_repeat_chars.get(kind, 0),
            }
            for kind in ("exact_slice_repeat", "whole_file_repeat", "different_slice")
        },
        "same_path_total": sum(read_repeat_counts.values()),
        "top_sessions": [{"session_id": sid, "n": n} for sid, n in top_reread_sessions],
        "top_sessions_by_chars": [
            {"session_id": sid, "chars": c} for sid, c in top_reread_chars_sessions
        ],
    }

    # -- cost summary --------------------------------------------------------
    total_usd = sum((s.get("cost") or {}).get("usd", 0.0) for s in sessions)
    main_usd  = sum(
        (s.get("cost") or {}).get("usd", 0.0) for s in sessions if not s.get("is_subagent")
    )
    sub_usd   = sum(
        (s.get("cost") or {}).get("usd", 0.0) for s in sessions if s.get("is_subagent")
    )

    groups = model.get("groups") or []
    top_groups = [
        {
            "group_id": g["group_id"],
            "main_session_id": g["main_session_id"],
            "n_subs": g["n_subs"],
            "main_usd": g["main_usd"],
            "subs_usd": g["subs_usd"],
            "total_usd": g["total_usd"],
            "delegation_delta_usd": g["delegation_delta_usd"],
            "inline_estimate_usd": g["inline_estimate_usd"],
            "delegation_delta_lower_usd": g["delegation_delta_lower_usd"],
            "inline_estimate_lower_usd": g["inline_estimate_lower_usd"],
            "has_unknown_model": g.get("has_unknown_model", False),
        }
        for g in groups[:5]
    ]

    cost_summary = {
        "total_usd": round(total_usd, 6),
        "main_usd": round(main_usd, 6),
        "sub_usd": round(sub_usd, 6),
        "top_groups": top_groups,
        "unknown_models": get_unknown_models(),
    }

    # -- by_version ----------------------------------------------------------
    ver_stats: dict[str, dict] = defaultdict(lambda: {
        "n": 0,
        "WIN": 0, "NEUTRAL": 0, "NET_NEGATIVE": 0, "UNKNOWN": 0,
        "saved_chars": 0,
    })
    for session in sessions:
        for ev in session.get("events", []):
            if not ev.get("is_tk"):
                continue
            ver = ev.get("tk_version") or "unknown"
            vs = ver_stats[ver]
            vs["n"] += 1
            outcome = ev.get("outcome", "UNKNOWN")
            vs[outcome] = vs.get(outcome, 0) + 1
            if outcome == "WIN":
                raw = ev.get("raw_chars") or 0
                shown = ev.get("shown_chars_ownlog") or 0
                vs["saved_chars"] += raw - shown

    def _ver_sort_key(ver: str) -> tuple:
        if ver == "unknown":
            return (1, [])
        parts = []
        for p in ver.split("."):
            try:
                parts.append(int(p))
            except ValueError:
                parts.append(p)
        return (0, parts)

    by_version = [
        {"version": ver, **vs}
        for ver, vs in sorted(ver_stats.items(), key=lambda kv: _ver_sort_key(kv[0]), reverse=True)
    ]
    # Fix reversed sort: unknown last, versions descending
    by_version.sort(key=lambda x: _ver_sort_key(x["version"]))
    by_version.reverse()

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
        "tk_invocations": {
            "total": sum(tk_invocation_counts.values()),
            "by_command": [
                {"command": command, "n": n}
                for command, n in tk_invocation_counts.most_common()
            ],
        },
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
    print(
        f"tk events:     {t['tk_events']}  "
        f"(own-log {t['own_log_matched_events']}, {t['own_log_event_match_rate']*100:.1f}%; "
        f"raw baseline {t['raw_baseline_events']}, {t['raw_baseline_event_rate']*100:.1f}%)"
    )
    print(f"tk invocations:{t['tk_invocations']:>7}")
    print(f"Outcomes:      WIN={oc['WIN']}  NEU={oc.get('NEUTRAL', 0)}  NET_NEG={oc['NET_NEGATIVE']}  UNK={oc['UNKNOWN']}")
    print(f"Savings:       saved={sv['win_saved_chars']:,}  extra={sv['net_negative_extra_chars']:,}  net={sv['net_chars']:,}")
    print()

    print("Top primary event commands:")
    for c in report["by_command"][:5]:
        parts = [f"n={c['n']}"]
        if c.get("win"):
            parts.append(f"win={c['win']}")
        if c.get("net_negative"):
            parts.append(f"neg={c['net_negative']}")
        if c.get("fallback_n"):
            parts.append(f"fb={c['fallback_n']}")
        if c.get("result_used_n"):
            parts.append(f"ru={c['result_used_n']}")
        if c.get("empty_n"):
            parts.append(f"empty={c['empty_n']}")
        if c.get("retry_n"):
            parts.append(f"retry={c['retry_n']}")
        if not c.get("applicable", True):
            parts.append("applicable=false")
        print(f"  {c['command']:20s} {', '.join(parts)}")
    print()

    print("Top tk invocations:")
    for item in (report.get("tk_invocations") or {}).get("by_command", [])[:5]:
        print(f"  {item['command']:20s} n={item['n']}")
    print()

    sg = report.get("symbol_grep") or {}
    if sg.get("total"):
        print(f"Symbol greps:  {sg['total']} total (nav gap baseline)")
        for item in (sg.get("top_sessions") or [])[:3]:
            print(f"  session {item['session_id'][:8]}  n={item['n']}")
    print()

    ngr = report.get("native_grep_retry") or {}
    if ngr.get("total"):
        print(f"Grep retries:  {ngr['total']} total (native grep repeated within lookback)")
        for item in (ngr.get("top_sessions") or [])[:3]:
            print(f"  session {item['session_id'][:8]}  n={item['n']}")
    print()

    rr = report.get("reread") or {}
    if rr.get("total"):
        kinds = rr.get("by_kind") or {}
        print(
            f"Exact rereads: {rr['total']} total, {rr['chars_total']:,} chars "
            f"(different slices {kinds.get('different_slice', {}).get('n', 0)})"
        )
        for item in (rr.get("top_sessions") or [])[:3]:
            print(f"  session {item['session_id'][:8]}  n={item['n']}")
    print()

    if report["findings"]:
        print("Findings:")
        for f in report["findings"]:
            print(f"  [{f['type']}] {f['detail']}")

    bv = report.get("by_version") or []
    if bv:
        print()
        print("By tk version:")
        for v in bv:
            ver = v["version"]
            saved = v.get("saved_chars", 0)
            print(
                f"  {ver:10s}  n={v['n']:4d}  WIN={v.get('WIN',0):3d}  "
                f"NEU={v.get('NEUTRAL',0):3d}  NEG={v.get('NET_NEGATIVE',0):3d}  "
                f"UNK={v.get('UNKNOWN',0):3d}  saved={saved:,}"
            )

    cs = report.get("cost") or {}
    if cs:
        print()
        print("Cost (USD):")
        print(f"  Total:  ${cs['total_usd']:.4f}")
        print(f"  Main:   ${cs['main_usd']:.4f}")
        print(f"  Subs:   ${cs['sub_usd']:.4f}")
        top = cs.get("top_groups") or []
        if top:
            print()
            print("  Top groups by cost:")
            for g in top:
                delta = g.get("delegation_delta_usd")
                delta_lower = g.get("delegation_delta_lower_usd")
                inline = g.get("inline_estimate_usd")
                gid_short = (g["group_id"] or "")[:8]
                n_subs = g["n_subs"]
                delta_str = ""
                if delta_lower is not None and delta is not None:
                    lo_sign = "+" if delta_lower >= 0 else ""
                    hi_sign = "+" if delta >= 0 else ""
                    delta_str = f"  delta=[{lo_sign}{delta_lower:.4f} .. {hi_sign}{delta:.4f}] (lower..upper)"
                elif delta is not None:
                    sign = "+" if delta >= 0 else ""
                    delta_str = f"  delta={sign}{delta:.4f} ({'saved' if delta >= 0 else 'overhead'}, upper-bound est)"
                if delta is None and g.get("has_unknown_model"):
                    delta_str = "  delta=n/a (unknown model — cost understated)"
                inline_str = f"  inline≈${inline:.4f}" if inline is not None else ""
                print(
                    f"    {gid_short}  subs={n_subs}"
                    f"  main=${g['main_usd']:.4f}"
                    f"  subs=${g['subs_usd']:.4f}"
                    f"  total=${g['total_usd']:.4f}"
                    f"{inline_str}{delta_str}"
                )
        unknown = cs.get("unknown_models") or []
        if unknown:
            print()
            print(f"  WARNING: unknown models (cost not calculated): {', '.join(unknown)}")

    ad = report.get("adoption") or {}
    by_sub = ad.get("by_subtype") or {}
    auto_routed = ad.get("auto_routed") or {}
    if by_sub or auto_routed.get("n"):
        print()
        print("Hook adoption:")
        for subtype, st in by_sub.items():
            print(
                f"  {subtype:16s} n={st['n_interventions']:4d}  "
                f"adopted={st['n_adopted']:4d}  rate={st['rate']*100:.0f}%"
            )
        if auto_routed.get("n"):
            print(f"  {'auto_routed':16s} n={auto_routed['n']:4d}  (route-tk/route-rtk, no adoption rate)")


# ---------------------------------------------------------------------------
# CLI entry point
# ---------------------------------------------------------------------------

def main() -> None:
    parser = argparse.ArgumentParser(description="Build annotated model + compact report")
    parser.add_argument("--from", dest="from_dt", metavar="ISO")
    parser.add_argument("--to", dest="to_dt", metavar="ISO")
    parser.add_argument(
        "--source", dest="source",
        choices=("claude", "opencode", "codex", "both", "all"),
                        default="claude",
        help="Data source: claude (default), opencode, codex, both, or all",
    )
    args = parser.parse_args()

    from_dt = _parse_cli_dt(args.from_dt) if args.from_dt else None
    to_dt = _parse_cli_dt(args.to_dt, end_of_day=True) if args.to_dt else None

    # Pipeline: join -> annotate -> report
    model = run_join(from_dt, to_dt, source=args.source)
    annotate(model)

    # Write model.json
    result_dir = Path(__file__).parent / "result"
    result_dir.mkdir(exist_ok=True)
    model_path = result_dir / "model.json"
    with open(model_path, "w", encoding="utf-8") as f:
        json.dump(model, f, indent=2)

    # Build and write report.json
    report = _build_report(model)

    # Expose by_version and adoption in model.json so the dashboard can render them
    model["by_version"] = report["by_version"]
    model["adoption"] = report["adoption"]

    # Re-write model.json with by_version included
    with open(model_path, "w", encoding="utf-8") as f:
        json.dump(model, f, indent=2)

    report_path = result_dir / "report.json"
    with open(report_path, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)

    _print_summary(report)
    print()
    print(f"model.json:  {model_path}  ({model_path.stat().st_size:,} bytes)")
    print(f"report.json: {report_path}  ({report_path.stat().st_size:,} bytes)")


if __name__ == "__main__":
    main()
