"""
Pure aggregation computations over an annotated model, used by
tkstats.reports.build to assemble report.json.
"""

from __future__ import annotations

from collections import Counter, defaultdict

from tkstats.config import _SEARCH_NAV_COMMANDS
from tkstats.detect.events import _event_tk_invocations
from tkstats.pricing import get_unknown_models


def aggregate_events(sessions: list[dict]) -> dict:
    """
    Single pass over every session/event computing the per-command,
    per-agent-type, symbol_grep, native_grep_retry, reread, and tk-invocation
    aggregates report.json needs.
    """
    cmd_stats: dict[str, dict] = defaultdict(lambda: {
        "n": 0, "own_log_matched": 0, "raw_baseline": 0,
        "win": 0, "neutral": 0, "net_negative": 0,
        "empty_n": 0, "fallback_n": 0, "result_used_n": 0, "retry_n": 0,
        "sum_shown": 0, "sum_raw": 0, "n_shown": 0, "n_raw": 0,
        "symbol_grep_n": 0,
    })
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

    top_sg_sessions = sorted(sg_by_session.items(), key=lambda x: x[1], reverse=True)[:5]
    top_sg_cmds = sorted(sg_by_cmd.items(), key=lambda x: x[1], reverse=True)[:5]
    symbol_grep_block = {
        "total": sg_total,
        "top_sessions": [{"session_id": sid, "n": n} for sid, n in top_sg_sessions],
        "top_commands": [{"label": label, "n": n} for label, n in top_sg_cmds],
    }

    top_ngr_sessions = sorted(ngr_by_session.items(), key=lambda x: x[1], reverse=True)[:5]
    native_grep_retry_block = {
        "total": ngr_total,
        "top_sessions": [{"session_id": sid, "n": n} for sid, n in top_ngr_sessions],
    }

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

    return {
        "by_command": by_command,
        "by_agent_type": by_agent_type,
        "symbol_grep": symbol_grep_block,
        "native_grep_retry": native_grep_retry_block,
        "reread": reread_block,
        "tk_invocations": {
            "total": sum(tk_invocation_counts.values()),
            "by_command": [
                {"command": command, "n": n}
                for command, n in tk_invocation_counts.most_common()
            ],
        },
    }


def aggregate_retry_total(sessions: list[dict]) -> int:
    return sum(
        ev.get("retry", False)
        for session in sessions
        for ev in session.get("events", [])
        if ev.get("is_tk")
    )


def aggregate_cost_summary(model: dict) -> dict:
    sessions = model["sessions"]
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

    return {
        "total_usd": round(total_usd, 6),
        "main_usd": round(main_usd, 6),
        "sub_usd": round(sub_usd, 6),
        "top_groups": top_groups,
        "unknown_models": get_unknown_models(),
    }


def aggregate_by_version(sessions: list[dict]) -> list[dict]:
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
    return by_version
