"""
Inspect native Bash calls in Stats/result/model.json and estimate where tk could
have been used instead.

The report is intentionally separate from Stats/report.py: it answers a
different question. report.py measures tk results; this script looks at non-tk
Bash commands in sessions for a selected tk version and groups likely
replacement opportunities.

Usage:
    python3 Stats/bash_opportunities.py
    python3 Stats/bash_opportunities.py --version 0.6.0 --top 30
    python3 Stats/bash_opportunities.py --json
"""

from __future__ import annotations

import argparse
import json
import re
import shlex
from collections import Counter, defaultdict
from pathlib import Path
from typing import Iterable


DEFAULT_MODEL = Path("Stats/result/model.json")

AUTO_TK = "auto_tk"
MANUAL_TK = "manual_tk"
NUDGE_TK = "nudge_tk"
NEW_TK = "new_tk"
NATIVE_READ = "native_read"
KEEP_NATIVE = "keep_native"
COMPOUND = "compound"
UNKNOWN = "unknown"


def _version_key(version: str) -> tuple[int, ...]:
    if version == "unknown":
        return (-1,)
    try:
        return tuple(int(part) for part in version.split("."))
    except ValueError:
        return (-1,)


def _latest_version(model: dict) -> str:
    versions = [
        row.get("version", "unknown")
        for row in model.get("by_version", [])
        if row.get("version") and row.get("version") != "unknown"
    ]
    return max(versions, key=_version_key) if versions else "unknown"


def _split_segments(command: str) -> list[list[str]]:
    parts = re.split(r"\s*(?:&&|\|\||;|\|)\s*", command)
    segments: list[list[str]] = []
    for part in parts:
        part = part.strip()
        if not part:
            continue
        try:
            tokens = shlex.split(part)
        except ValueError:
            tokens = part.split()
        if tokens:
            segments.append(tokens)
    return segments


def _has_shell_control(command: str) -> bool:
    return bool(re.search(r"&&|\|\||;|\||`|\$\(|\n", command))


def _strip_prefix(tokens: list[str]) -> list[str]:
    """Drop simple env assignments, command/time/nohup wrappers, and cd segments."""
    out = tokens[:]
    while out and re.match(r"^[A-Za-z_][A-Za-z0-9_]*=", out[0]):
        out.pop(0)
    while out and out[0] in {"command", "time", "nohup"}:
        out.pop(0)
    return out


def _norm_command(tokens: list[str]) -> str:
    tokens = _strip_prefix(tokens)
    if not tokens:
        return "(empty)"
    cmd = tokens[0]
    if cmd in {"git", "dotnet"} and len(tokens) > 1:
        sub = next((t for t in tokens[1:] if not t.startswith("-")), "")
        return f"{cmd} {sub}".strip()
    if cmd in {
        "grep", "rg", "find", "ls", "cat", "sed", "head", "tail", "wc",
        "ps", "kill", "rm", "mkdir", "du", "sort", "awk", "xargs", "cut",
        "echo", "cd", "for", "do", "done",
    }:
        return cmd
    return cmd


def _looks_symbol_grep(command: str, tokens: list[str], symbol_grep: bool) -> bool:
    if symbol_grep:
        return True
    if not tokens or tokens[0] not in {"grep", "rg", "egrep", "fgrep"}:
        return False
    return bool(
        re.search(r"\b(class|record|interface|enum|struct)\s+[A-Z]", command)
        or (len(tokens) > 1 and re.match(r"^[A-Z][A-Za-z0-9]+$", tokens[1] or ""))
    )


def _classify(command: str, tokens: list[str], symbol_grep: bool) -> tuple[str, str, str]:
    tokens = _strip_prefix(tokens)
    if not tokens:
        return UNKNOWN, "(empty)", "empty command"

    cmd = tokens[0]
    norm = _norm_command(tokens)

    if cmd == "tk":
        return KEEP_NATIVE, norm, "already tk"

    if cmd == "dotnet" and len(tokens) > 1:
        sub = next((t for t in tokens[1:] if not t.startswith("-")), "")
        if sub in {"build", "test", "restore"}:
            return AUTO_TK, norm, f"use tk dotnet {sub}"
        return KEEP_NATIVE, norm, "dotnet subcommand has no tk filter"

    if cmd == "git" and len(tokens) > 1:
        sub = next((t for t in tokens[1:] if not t.startswith("-")), "")
        if sub in {"status", "log"}:
            return AUTO_TK, norm, f"use tk git {sub}"
        if sub in {"diff", "show"}:
            return MANUAL_TK, norm, f"tk git {sub} exists, but full patch is often needed"
        if sub in {"branch", "stash"}:
            return MANUAL_TK, norm, "tk has compact git fallback, but verify command shape"
        return KEEP_NATIVE, norm, "git command should stay explicit"

    if cmd in {"grep", "rg", "egrep", "fgrep"}:
        if _looks_symbol_grep(command, tokens, symbol_grep):
            return NUDGE_TK, norm, "symbol lookup should use tk def/refs/callers"
        return MANUAL_TK, norm, "tk grep can summarize broad searches"

    if cmd == "find":
        return AUTO_TK, norm, "use tk find"

    if cmd == "ls":
        return AUTO_TK, norm, "use tk ls"

    if cmd in {"cat", "sed", "head", "tail", "wc"}:
        return NATIVE_READ, norm, "file reads are better handled by Read/sed as needed, not tk"

    if cmd == "du":
        return NEW_TK, norm, "possible new tk size/top-dirs command"

    if cmd in {
        "cd", "echo", "sort", "awk", "xargs", "cut", "for", "do", "done",
        "ps", "kill", "rm", "mkdir", "touch", "cp", "mv",
    }:
        return KEEP_NATIVE, norm, "shell/system operation"

    return UNKNOWN, norm, "no tk mapping"


def _iter_bash_events(model: dict, version: str, include_tk: bool) -> Iterable[tuple[dict, dict]]:
    for session in model.get("sessions", []):
        if session.get("tk_version", "unknown") != version:
            continue
        for ev in session.get("events", []):
            if ev.get("tool") != "Bash":
                continue
            if ev.get("is_tk") and not include_tk:
                continue
            yield session, ev


def build_report(model: dict, version: str, include_tk: bool, examples_per_bucket: int) -> dict:
    totals = Counter()
    by_norm: dict[str, Counter] = defaultdict(Counter)
    examples: dict[str, list[dict]] = defaultdict(list)
    shown_chars = Counter()
    sessions_seen: set[str] = set()
    subagent_events = 0
    main_events = 0
    segments_seen = 0

    for session, ev in _iter_bash_events(model, version, include_tk):
        sessions_seen.add(session.get("session_id", ""))
        command = ev.get("tool_input_summary", "")
        if not command:
            continue

        segments = _split_segments(command)
        shell_control = _has_shell_control(command)
        if not segments:
            continue

        if ev.get("is_subagent"):
            subagent_events += 1
        else:
            main_events += 1

        if shell_control and len(segments) > 1:
            totals[COMPOUND] += 1
            shown_chars[COMPOUND] += ev.get("shown_chars") or 0
            norm = "compound"
            by_norm[norm][COMPOUND] += 1
            if len(examples[COMPOUND]) < examples_per_bucket:
                examples[COMPOUND].append(_example(session, ev, "split or keep native"))

        for tokens in segments:
            segments_seen += 1
            category, norm, reason = _classify(command, tokens, bool(ev.get("symbol_grep")))
            totals[category] += 1
            by_norm[norm][category] += 1
            shown_chars[category] += ev.get("shown_chars") or 0
            if len(examples[category]) < examples_per_bucket:
                examples[category].append(_example(session, ev, reason))

    return {
        "version": version,
        "sessions": len(sessions_seen),
        "bash_events": main_events + subagent_events,
        "bash_segments": segments_seen,
        "main_bash_events": main_events,
        "subagent_bash_events": subagent_events,
        "totals": dict(totals),
        "shown_chars": dict(shown_chars),
        "top_commands": _top_commands(by_norm),
        "examples": dict(examples),
    }


def _example(session: dict, ev: dict, reason: str) -> dict:
    return {
        "agent": ev.get("agent_type") or session.get("agent_type"),
        "subagent": bool(ev.get("is_subagent")),
        "cwd": ev.get("cwd") or session.get("cwd"),
        "cmd": ev.get("tool_input_summary", ""),
        "reason": reason,
    }


def _top_commands(by_norm: dict[str, Counter]) -> list[dict]:
    rows = []
    for norm, counts in by_norm.items():
        total = sum(counts.values())
        category, n = counts.most_common(1)[0]
        rows.append({
            "command": norm,
            "n": total,
            "top_category": category,
            "top_category_n": n,
            "categories": dict(counts),
        })
    rows.sort(key=lambda r: r["n"], reverse=True)
    return rows


def print_text(report: dict, top: int) -> None:
    totals = Counter(report["totals"])
    replaceable = totals[AUTO_TK] + totals[MANUAL_TK] + totals[NUDGE_TK]
    bash_events = report["bash_events"]

    print(
        f"version={report['version']} sessions={report['sessions']} "
        f"bash_events={bash_events} bash_segments={report['bash_segments']}"
    )
    print(f"main={report['main_bash_events']} subagents={report['subagent_bash_events']}")
    print(
        "opportunities "
        f"replaceable={replaceable} auto={totals[AUTO_TK]} manual={totals[MANUAL_TK]} "
        f"nudge={totals[NUDGE_TK]} new_tool={totals[NEW_TK]} native_read={totals[NATIVE_READ]} "
        f"compound={totals[COMPOUND]} keep={totals[KEEP_NATIVE]} unknown={totals[UNKNOWN]}"
    )
    print()
    print("Top Bash command shapes:")
    for row in report["top_commands"][:top]:
        cats = " ".join(f"{k}={v}" for k, v in sorted(row["categories"].items()))
        print(f"  {row['n']:>4} {row['command']:<18} {cats}")

    print()
    print("Examples:")
    for category in (AUTO_TK, MANUAL_TK, NUDGE_TK, NEW_TK, NATIVE_READ, COMPOUND, UNKNOWN):
        items = report["examples"].get(category, [])
        if not items:
            continue
        print(f"  {category}:")
        for item in items:
            where = "sub" if item["subagent"] else "main"
            print(f"    - {where} {item['agent']}: {item['cmd']}")
            print(f"      {item['reason']}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default=str(DEFAULT_MODEL), help="Path to model.json")
    parser.add_argument("--version", default="latest", help="tk version to inspect, or 'latest'")
    parser.add_argument("--top", type=int, default=20, help="Top command shapes to print")
    parser.add_argument("--examples", type=int, default=3, help="Examples per category")
    parser.add_argument("--include-tk", action="store_true", help="Include Bash events that already invoke tk")
    parser.add_argument("--json", action="store_true", help="Print JSON instead of text")
    args = parser.parse_args()

    model_path = Path(args.model)
    model = json.loads(model_path.read_text())
    version = _latest_version(model) if args.version == "latest" else args.version
    report = build_report(model, version, args.include_tk, args.examples)

    try:
        if args.json:
            print(json.dumps(report, indent=2))
        else:
            print_text(report, args.top)
    except BrokenPipeError:
        return 0
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
