"""
Classification: map each Bash segment / event to a tk-routing category
(auto_tk, manual_tk, nudge_tk, new_tk, native_read, keep_native, compound,
unknown), detect grep/rg pattern shape, walk joined model sessions.

Pure logic: no I/O, no printing.
"""

from __future__ import annotations

import re
import shlex
from collections import Counter
from typing import Iterable

from bash_opp_common import (
    AUTO_TK,
    COMPOUND,
    EVENT_CATEGORY_PRIORITY,
    GREP_COMMANDS,
    GREP_FREE_TEXT,
    GREP_SYMBOL_SHAPED,
    KEEP_NATIVE,
    MANUAL_TK,
    NATIVE_READ,
    NEW_TK,
    NUDGE_TK,
    PIPE_PATTERN_COMMANDS,
    SCOPE_MAIN,
    SCOPE_SUBAGENTS,
    UNKNOWN,
)
from bash_opp_shell import (
    _is_compound_event,
    _norm_command,
    _shell_control_marks,
    _split_on_shell_operators,
    _split_pipeline_parts,
    _strip_heredoc_bodies,
    _strip_prefix,
    _subcommand,
)


# ---------------------------------------------------------------------------
# Classification primitives: per-segment command → category
# ---------------------------------------------------------------------------

def _looks_symbol_grep(command: str, tokens: list[str], symbol_grep: bool) -> bool:
    if symbol_grep:
        return True
    if not tokens or tokens[0] not in GREP_COMMANDS:
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
        sub = _subcommand(tokens)
        if sub in {"build", "test", "restore"}:
            return AUTO_TK, norm, f"use tk dotnet {sub}"
        return KEEP_NATIVE, norm, "dotnet subcommand has no tk filter"

    if cmd == "git" and len(tokens) > 1:
        sub = _subcommand(tokens, {"-C", "-c", "--git-dir", "--work-tree", "--namespace"})
        if sub in {"status", "log"}:
            return AUTO_TK, norm, f"use tk git {sub}"
        if sub in {"diff", "show"}:
            return MANUAL_TK, norm, f"tk git {sub} exists, but full patch is often needed"
        if sub in {"branch", "stash"}:
            return MANUAL_TK, norm, "tk has compact git fallback, but verify command shape"
        return KEEP_NATIVE, norm, "git command should stay explicit"

    if cmd in GREP_COMMANDS:
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


# Options that consume the following token as a value (not the search pattern).
_GREP_VALUE_OPTS = {
    "-f", "--file", "-m", "--max-count", "-A", "-B", "-C", "--context",
    "--include", "--exclude", "--exclude-dir", "-g", "--glob",
    "-t", "--type", "--type-not", "--context-separator",
}

_GREP_SYMBOL_KEYWORD_RE = re.compile(r"\b(class|interface|record|struct|enum)\s+[A-Za-z_]\w*")
_GREP_METHOD_CALL_RE = re.compile(r"\b[A-Z][A-Za-z0-9_]*\\?\(")
_GREP_PASCAL_RE = re.compile(r"[A-Z][A-Za-z0-9]*")


def _grep_pattern(tokens: list[str]) -> str | None:
    """Extract the search-pattern argument from a grep/rg-shaped token list."""
    i = 1
    positional: list[str] = []
    while i < len(tokens):
        token = tokens[i]
        if token == "--":
            positional.extend(tokens[i + 1:])
            break
        if token in {"-e", "--regexp"}:
            if i + 1 < len(tokens):
                return tokens[i + 1]
            i += 1
            continue
        if token in _GREP_VALUE_OPTS:
            i += 2
            continue
        if len(token) > 1 and token.startswith("-"):
            i += 1
            continue
        positional.append(token)
        i += 1
    return positional[0] if positional else None


def _classify_grep_pattern(pattern: str | None) -> str:
    """Classify a grep/rg pattern as symbol_shaped (targets a C# symbol) or free_text."""
    if not pattern:
        return GREP_FREE_TEXT
    p = pattern.strip().strip("\"'")
    if not p:
        return GREP_FREE_TEXT

    if _GREP_SYMBOL_KEYWORD_RE.search(p):
        return GREP_SYMBOL_SHAPED

    if _GREP_METHOD_CALL_RE.search(p):
        return GREP_SYMBOL_SHAPED

    core = p
    if core.startswith(r"\b"):
        core = core[2:]
    if core.endswith(r"\b"):
        core = core[:-2]
    core = core.strip()
    if core.startswith("(") and core.endswith(")"):
        core = core[1:-1]
    parts = core.split("|") if "|" in core else [core]
    if parts and all(_GREP_PASCAL_RE.fullmatch(part) for part in parts):
        return GREP_SYMBOL_SHAPED

    return GREP_FREE_TEXT


# ---------------------------------------------------------------------------
# Event walk: iterate joined-model sessions and classify each Bash event
# ---------------------------------------------------------------------------

def _event_in_scope(ev: dict, scope: str) -> bool:
    if scope == SCOPE_MAIN:
        return not ev.get("is_subagent")
    if scope == SCOPE_SUBAGENTS:
        return bool(ev.get("is_subagent"))
    return True


def _iter_bash_events(
    model: dict,
    version: str,
    include_tk: bool,
    scope: str,
) -> Iterable[tuple[dict, dict]]:
    for session in model.get("sessions", []):
        if session.get("tk_version", "unknown") != version:
            continue
        for ev in session.get("events", []):
            if ev.get("tool") != "Bash":
                continue
            if ev.get("is_tk") and not include_tk:
                continue
            if not _event_in_scope(ev, scope):
                continue
            yield session, ev


def _classify_event(
    command: str,
    segments: list[list[str]],
    shell_control: bool,
    symbol_grep: bool,
) -> tuple[str, str, str]:
    if _is_compound_event(command, segments, shell_control):
        return COMPOUND, "compound", "split or keep native"

    candidates = [
        _classify(command, tokens, symbol_grep)
        for tokens in segments
    ]
    if not candidates:
        return UNKNOWN, "(empty)", "empty command"

    by_category = {category: (category, norm, reason) for category, norm, reason in candidates}
    for category in EVENT_CATEGORY_PRIORITY:
        if category in by_category:
            return by_category[category]
    return candidates[0]


def _add_command(
    by_norm: dict[str, Counter],
    command_chars: Counter,
    norm: str,
    category: str,
    shown: int,
) -> None:
    by_norm[norm][category] += 1
    command_chars[norm] += shown


def _add_compound_row(
    counts: Counter,
    categories: dict[str, Counter] | None,
    event_seen: dict[str, set[int]],
    event_chars: Counter,
    key: str,
    category: str,
    event_id: int,
    shown: int,
) -> None:
    counts[key] += 1
    if categories is not None:
        categories[key][category] += 1
    if event_id not in event_seen[key]:
        event_seen[key].add(event_id)
        event_chars[key] += shown


def _compound_rows(
    counts: Counter,
    event_seen: dict[str, set[int]],
    event_chars: Counter,
    categories: dict[str, Counter] | None = None,
    name: str = "command",
) -> list[dict]:
    rows = []
    for key, n in counts.items():
        row = {
            name: key,
            "n": n,
            "event_n": len(event_seen[key]),
            "event_shown_chars": event_chars[key],
        }
        if categories is not None:
            cats = categories[key]
            category, category_n = cats.most_common(1)[0]
            row.update({
                "top_category": category,
                "top_category_n": category_n,
                "categories": dict(cats),
            })
        rows.append(row)
    rows.sort(key=lambda r: (r["n"], r["event_shown_chars"]), reverse=True)
    return rows


def _pipeline_suffixes(command: str) -> list[str]:
    command = _strip_heredoc_bodies(command)
    parts = _split_pipeline_parts(command)
    suffixes = []
    for part in parts[1:]:
        part = _split_on_shell_operators(part, split_pipes=False)[0]
        part = part.strip()
        if not part:
            continue
        try:
            tokens = shlex.split(part)
        except ValueError:
            tokens = part.split()
        if tokens:
            norm = _norm_command(tokens)
            if norm in PIPE_PATTERN_COMMANDS:
                suffixes.append(norm)
    return suffixes


def _compound_patterns(command: str) -> list[str]:
    patterns = []
    marks = _shell_control_marks(command)
    suffixes = _pipeline_suffixes(command)
    if "pipeline" in marks:
        patterns.append("pipeline")
    for suffix in suffixes:
        if suffix in PIPE_PATTERN_COMMANDS:
            patterns.append(f"pipe_to_{suffix}")
    if "chain_and" in marks:
        patterns.append("chain_and")
    if "chain_or" in marks:
        patterns.append("chain_or")
    if "chain_semicolon" in marks:
        patterns.append("chain_semicolon")
    if "multi_line_script" in marks:
        patterns.append("multi_line_script")
    if "command_substitution" in marks:
        patterns.append("command_substitution")
    return patterns


__all__ = [
    "_looks_symbol_grep",
    "_classify",
    "_grep_pattern",
    "_classify_grep_pattern",
    "_event_in_scope",
    "_iter_bash_events",
    "_classify_event",
    "_add_command",
    "_add_compound_row",
    "_compound_rows",
    "_pipeline_suffixes",
    "_compound_patterns",
]
