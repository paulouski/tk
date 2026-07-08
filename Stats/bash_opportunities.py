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
    python3 Stats/bash_opportunities.py --version 0.6.0 --event-level
    python3 Stats/bash_opportunities.py --version 0.6.0 --event-level --compound
    python3 Stats/bash_opportunities.py --version 0.6.0 --main-only --json
    python3 Stats/bash_opportunities.py --json
"""

from __future__ import annotations

import argparse
import json
import re
import shlex
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Iterable

from ingest import _projects_dir  # type: ignore[import]


DEFAULT_MODEL = Path("Stats/result/model.json")

AUTO_TK = "auto_tk"
MANUAL_TK = "manual_tk"
NUDGE_TK = "nudge_tk"
NEW_TK = "new_tk"
NATIVE_READ = "native_read"
KEEP_NATIVE = "keep_native"
COMPOUND = "compound"
UNKNOWN = "unknown"

LEVEL_SEGMENT = "segment"
LEVEL_EVENT = "event"
SCOPE_ALL = "all"
SCOPE_MAIN = "main"
SCOPE_SUBAGENTS = "subagents"

EVENT_CATEGORY_PRIORITY = (
    NUDGE_TK,
    AUTO_TK,
    MANUAL_TK,
    NEW_TK,
    NATIVE_READ,
    UNKNOWN,
    KEEP_NATIVE,
)

ROUTABLE_CATEGORIES = {AUTO_TK, MANUAL_TK, NUDGE_TK, NEW_TK}
PIPE_PATTERN_COMMANDS = {"head", "tail", "grep", "rg", "sort", "wc"}
GREP_COMMANDS = {"grep", "rg", "egrep", "fgrep"}

GREP_SYMBOL_SHAPED = "symbol_shaped"
GREP_FREE_TEXT = "free_text"
GREP_SCOPE_MAIN = "main"
GREP_SCOPE_SUBAGENTS = "subagents"
GREP_SCOPE_OVERALL = "overall"

EDIT_TOOL_NAMES = {"Edit", "MultiEdit", "Write"}
READ_TOOL_NAMES = {"Read"}


# ---------------------------------------------------------------------------
# Parsing / ingest: model.json loading and raw-transcript access
# ---------------------------------------------------------------------------

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


def _iter_transcript_tool_uses(path: Path, tool_names: set[str]) -> Iterable[dict]:
    """Yield raw tool_use blocks for the given tool names from one .jsonl transcript file."""
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for raw_line in f:
                raw_line = raw_line.strip()
                if not raw_line:
                    continue
                try:
                    line = json.loads(raw_line)
                except json.JSONDecodeError:
                    continue
                msg = line.get("message")
                content = msg.get("content") if isinstance(msg, dict) else None
                if not isinstance(content, list):
                    continue
                for block in content:
                    if isinstance(block, dict) and block.get("type") == "tool_use" and block.get("name") in tool_names:
                        yield block
    except OSError:
        return


def _tool_result_text(content) -> str:
    """Flatten a tool_result content field (string, or list of content blocks) to text."""
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts = []
        for item in content:
            if isinstance(item, dict) and item.get("type") == "text":
                parts.append(item.get("text", "") or "")
        return "".join(parts)
    return ""


def _iter_transcript_tool_use_results(path: Path, tool_names: set[str]) -> Iterable[dict]:
    """Yield {tool_input, result_chars, num_lines, total_lines, is_error} for each tool_use
    of the given names paired with its tool_result from one .jsonl transcript file.

    num_lines/total_lines come from the sidecar `toolUseResult.file` block Claude Code
    attaches to Read results (cheap: already parsed, no extra `wc -l`); None if absent.
    """
    pending: dict[str, dict] = {}
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            for raw_line in f:
                raw_line = raw_line.strip()
                if not raw_line:
                    continue
                try:
                    line = json.loads(raw_line)
                except json.JSONDecodeError:
                    continue
                msg = line.get("message")
                content = msg.get("content") if isinstance(msg, dict) else None
                if not isinstance(content, list):
                    continue
                for block in content:
                    if not isinstance(block, dict):
                        continue
                    if block.get("type") == "tool_use" and block.get("name") in tool_names:
                        pending[block.get("id")] = block.get("input") or {}
                    elif block.get("type") == "tool_result":
                        tool_use_id = block.get("tool_use_id")
                        if tool_use_id not in pending:
                            continue
                        tool_input = pending.pop(tool_use_id)
                        result_text = _tool_result_text(block.get("content"))
                        num_lines = None
                        total_lines = None
                        tur = line.get("toolUseResult")
                        if isinstance(tur, dict):
                            file_info = tur.get("file")
                            if isinstance(file_info, dict):
                                num_lines = file_info.get("numLines")
                                total_lines = file_info.get("totalLines")
                        yield {
                            "tool_input": tool_input,
                            "result_chars": len(result_text),
                            "num_lines": num_lines,
                            "total_lines": total_lines,
                            "is_error": bool(block.get("is_error")),
                        }
    except OSError:
        return


# ---------------------------------------------------------------------------
# Segment splitting: break one shell command string into pipeline/chain parts
# ---------------------------------------------------------------------------

def _split_on_shell_operators(command: str, split_pipes: bool = True) -> list[str]:
    parts = []
    buf = []
    quote = ""
    escaped = False
    i = 0
    while i < len(command):
        ch = command[i]
        nxt = command[i + 1] if i + 1 < len(command) else ""

        if escaped:
            buf.append(ch)
            escaped = False
            i += 1
            continue

        if ch == "\\" and quote != "'":
            buf.append(ch)
            escaped = True
            i += 1
            continue

        if quote:
            buf.append(ch)
            if ch == quote:
                quote = ""
            i += 1
            continue

        if ch in {"'", '"'}:
            quote = ch
            buf.append(ch)
            i += 1
            continue

        if ch == "&" and nxt == "&":
            parts.append("".join(buf))
            buf = []
            i += 2
            continue

        if ch == "|" and nxt == "|":
            if split_pipes:
                parts.append("".join(buf))
                buf = []
            else:
                buf.append(ch)
                buf.append(nxt)
            i += 2
            continue

        if ch == ";" or (split_pipes and ch == "|"):
            parts.append("".join(buf))
            buf = []
            i += 1
            continue

        buf.append(ch)
        i += 1

    parts.append("".join(buf))
    return parts


def _split_segments(command: str) -> list[list[str]]:
    parts = _split_on_shell_operators(command)
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


def _shell_control_marks(command: str) -> set[str]:
    marks = set()
    quote = ""
    escaped = False
    i = 0
    while i < len(command):
        ch = command[i]
        nxt = command[i + 1] if i + 1 < len(command) else ""

        if ch == "\n":
            marks.add("multi_line_script")

        if escaped:
            escaped = False
            i += 1
            continue

        if ch == "\\" and quote != "'":
            escaped = True
            i += 1
            continue

        if quote:
            if quote != "'" and ch == "$" and nxt == "(":
                marks.add("command_substitution")
                i += 2
                continue
            if quote != "'" and ch == "`":
                marks.add("command_substitution")
            if ch == quote:
                quote = ""
            i += 1
            continue

        if ch in {"'", '"'}:
            quote = ch
            i += 1
            continue

        if ch == "$" and nxt == "(":
            marks.add("command_substitution")
            i += 2
            continue
        if ch == "`":
            marks.add("command_substitution")
        elif ch == "&" and nxt == "&":
            marks.add("chain_and")
            i += 2
            continue
        elif ch == "|" and nxt == "|":
            marks.add("chain_or")
            i += 2
            continue
        elif ch == "|":
            marks.add("pipeline")
        elif ch == ";":
            marks.add("chain_semicolon")
        i += 1
    return marks


def _has_shell_control(command: str) -> bool:
    return bool(_shell_control_marks(command))


def _split_pipeline_parts(command: str) -> list[str]:
    parts = []
    buf = []
    quote = ""
    escaped = False
    i = 0
    while i < len(command):
        ch = command[i]
        nxt = command[i + 1] if i + 1 < len(command) else ""

        if escaped:
            buf.append(ch)
            escaped = False
            i += 1
            continue

        if ch == "\\" and quote != "'":
            buf.append(ch)
            escaped = True
            i += 1
            continue

        if quote:
            buf.append(ch)
            if ch == quote:
                quote = ""
            i += 1
            continue

        if ch in {"'", '"'}:
            quote = ch
            buf.append(ch)
            i += 1
            continue

        if ch == "|" and nxt == "|":
            buf.append(ch)
            buf.append(nxt)
            i += 2
            continue

        if ch == "|":
            parts.append("".join(buf))
            buf = []
            i += 1
            continue

        buf.append(ch)
        i += 1

    parts.append("".join(buf))
    return parts


def _is_compound_event(command: str, segments: list[list[str]], shell_control: bool) -> bool:
    return shell_control and (len(segments) > 1 or "\n" in command)


def _strip_prefix(tokens: list[str]) -> list[str]:
    """Drop simple env assignments, command/time/nohup wrappers, and cd segments."""
    out = tokens[:]
    while out and re.match(r"^[A-Za-z_][A-Za-z0-9_]*=", out[0]):
        out.pop(0)
    while out and out[0] in {"command", "time", "nohup"}:
        out.pop(0)
    return out


def _subcommand(tokens: list[str], options_with_value: set[str] | None = None) -> str:
    options_with_value = options_with_value or set()
    skip_next = False
    for token in tokens[1:]:
        if skip_next:
            skip_next = False
            continue
        if token in options_with_value:
            skip_next = True
            continue
        if token == "--":
            continue
        if token.startswith("-"):
            continue
        return token
    return ""


def _norm_command(tokens: list[str]) -> str:
    tokens = _strip_prefix(tokens)
    if not tokens:
        return "(empty)"
    cmd = tokens[0]
    if cmd == "git" and len(tokens) > 1:
        sub = _subcommand(tokens, {"-C", "-c", "--git-dir", "--work-tree", "--namespace"})
        return f"{cmd} {sub}".strip()
    if cmd == "dotnet" and len(tokens) > 1:
        sub = _subcommand(tokens)
        return f"{cmd} {sub}".strip()
    if cmd in {
        "grep", "rg", "find", "ls", "cat", "sed", "head", "tail", "wc",
        "ps", "kill", "rm", "mkdir", "du", "sort", "awk", "xargs", "cut",
        "echo", "cd", "for", "do", "done",
    }:
        return cmd
    return cmd


# ---------------------------------------------------------------------------
# Classification: map a command/segment to a tk-routing category
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


# ---------------------------------------------------------------------------
# Aggregation: fold classified events/segments into report structures
# ---------------------------------------------------------------------------

@dataclass
class BucketStats:
    """Segment/event/char counters for one (class, scope) bucket, deduped by event id."""

    n: int = 0
    event_n: int = 0
    shown_chars: int = 0
    _event_seen: set[int] = field(default_factory=set)

    def add(self, event_id: int, shown: int) -> None:
        self.n += 1
        if event_id not in self._event_seen:
            self._event_seen.add(event_id)
            self.event_n += 1
            self.shown_chars += shown

    def to_dict(self) -> dict:
        return {"n": self.n, "event_n": self.event_n, "shown_chars": self.shown_chars}


def build_report(
    model: dict,
    version: str,
    include_tk: bool,
    examples_per_bucket: int,
    level: str,
    scope: str,
) -> dict:
    event_totals = Counter()
    segment_totals = Counter()
    by_norm: dict[str, Counter] = defaultdict(Counter)
    command_chars = Counter()
    examples: dict[str, list[dict]] = defaultdict(list)
    event_shown_chars = Counter()
    segment_shown_chars = Counter()
    sessions_seen: set[str] = set()
    subagent_events = 0
    main_events = 0
    segments_seen = 0
    total_shown_chars = 0
    compound_event_id = 0
    compound_events = 0
    compound_segments = 0
    compound_shown_chars = 0
    compound_multiline_events = 0
    first_counts = Counter()
    first_categories: dict[str, Counter] = defaultdict(Counter)
    first_event_seen: dict[str, set[int]] = defaultdict(set)
    first_event_chars = Counter()
    anywhere_counts = Counter()
    anywhere_categories: dict[str, Counter] = defaultdict(Counter)
    anywhere_event_seen: dict[str, set[int]] = defaultdict(set)
    anywhere_event_chars = Counter()
    routable_counts = Counter()
    routable_categories: dict[str, Counter] = defaultdict(Counter)
    routable_event_seen: dict[str, set[int]] = defaultdict(set)
    routable_event_chars = Counter()
    category_counts = Counter()
    category_event_seen: dict[str, set[int]] = defaultdict(set)
    category_event_chars = Counter()
    pattern_counts = Counter()
    pattern_event_seen: dict[str, set[int]] = defaultdict(set)
    pattern_event_chars = Counter()
    suffix_counts = Counter()
    suffix_event_seen: dict[str, set[int]] = defaultdict(set)
    suffix_event_chars = Counter()
    grep_stats: dict[str, dict[str, BucketStats]] = {
        GREP_SYMBOL_SHAPED: defaultdict(BucketStats),
        GREP_FREE_TEXT: defaultdict(BucketStats),
    }
    grep_examples: dict[str, list[dict]] = defaultdict(list)
    bash_event_id = 0

    for session, ev in _iter_bash_events(model, version, include_tk, scope):
        bash_event_id += 1
        sessions_seen.add(session.get("session_id", ""))
        command = ev.get("tool_input_summary", "")
        if not command:
            continue

        segments = _split_segments(command)
        shell_control = _has_shell_control(command)
        if not segments:
            continue
        is_compound = _is_compound_event(command, segments, shell_control)

        if ev.get("is_subagent"):
            subagent_events += 1
        else:
            main_events += 1

        shown = ev.get("shown_chars") or 0
        total_shown_chars += shown

        event_category, event_norm, event_reason = _classify_event(
            command,
            segments,
            shell_control,
            bool(ev.get("symbol_grep")),
        )
        event_totals[event_category] += 1
        event_shown_chars[event_category] += shown
        if level == LEVEL_EVENT:
            _add_command(by_norm, command_chars, event_norm, event_category, shown)
            if len(examples[event_category]) < examples_per_bucket:
                examples[event_category].append(_example(session, ev, event_reason))

        if is_compound:
            segment_totals[COMPOUND] += 1
            segment_shown_chars[COMPOUND] += shown
            norm = "compound"
            if level == LEVEL_SEGMENT:
                _add_command(by_norm, command_chars, norm, COMPOUND, shown)
            if level == LEVEL_SEGMENT and len(examples[COMPOUND]) < examples_per_bucket:
                examples[COMPOUND].append(_example(session, ev, "split or keep native"))

            compound_event_id += 1
            compound_events += 1
            compound_segments += len(segments)
            compound_shown_chars += shown
            if "\n" in command:
                compound_multiline_events += 1

            first_category, first_norm, _ = _classify(command, segments[0], bool(ev.get("symbol_grep")))
            _add_compound_row(
                first_counts,
                first_categories,
                first_event_seen,
                first_event_chars,
                first_norm,
                first_category,
                compound_event_id,
                shown,
            )

            for pattern in _compound_patterns(command):
                _add_compound_row(
                    pattern_counts,
                    None,
                    pattern_event_seen,
                    pattern_event_chars,
                    pattern,
                    "",
                    compound_event_id,
                    shown,
                )

            for suffix in _pipeline_suffixes(command):
                _add_compound_row(
                    suffix_counts,
                    None,
                    suffix_event_seen,
                    suffix_event_chars,
                    suffix,
                    "",
                    compound_event_id,
                    shown,
                )

        for tokens in segments:
            segments_seen += 1
            category, norm, reason = _classify(command, tokens, bool(ev.get("symbol_grep")))
            segment_totals[category] += 1
            segment_shown_chars[category] += shown

            seg_cmd = _strip_prefix(tokens)
            if seg_cmd and seg_cmd[0] in GREP_COMMANDS:
                pattern = _grep_pattern(seg_cmd)
                grep_class = _classify_grep_pattern(pattern)
                grep_scope = GREP_SCOPE_SUBAGENTS if ev.get("is_subagent") else GREP_SCOPE_MAIN
                grep_stats[grep_class][grep_scope].add(bash_event_id, shown)
                grep_stats[grep_class][GREP_SCOPE_OVERALL].add(bash_event_id, shown)
                if len(grep_examples[grep_class]) < examples_per_bucket:
                    grep_examples[grep_class].append(
                        _grep_example(session, ev, pattern, grep_class)
                    )

            if is_compound:
                _add_compound_row(
                    anywhere_counts,
                    anywhere_categories,
                    anywhere_event_seen,
                    anywhere_event_chars,
                    norm,
                    category,
                    compound_event_id,
                    shown,
                )
                if category in ROUTABLE_CATEGORIES:
                    _add_compound_row(
                        routable_counts,
                        routable_categories,
                        routable_event_seen,
                        routable_event_chars,
                        norm,
                        category,
                        compound_event_id,
                        shown,
                    )
                    category_counts[category] += 1
                    if compound_event_id not in category_event_seen[category]:
                        category_event_seen[category].add(compound_event_id)
                        category_event_chars[category] += shown
            if level == LEVEL_SEGMENT:
                _add_command(by_norm, command_chars, norm, category, shown)
            if level == LEVEL_SEGMENT and len(examples[category]) < examples_per_bucket:
                examples[category].append(_example(session, ev, reason))

    active_totals = event_totals if level == LEVEL_EVENT else segment_totals
    active_shown_chars = event_shown_chars if level == LEVEL_EVENT else segment_shown_chars
    shown_chars_basis = (
        "event shown_chars counted once per Bash event"
        if level == LEVEL_EVENT
        else "segment shown_chars; compound commands can count one event in multiple segment categories"
    )

    return {
        "level": level,
        "scope": scope,
        "version": version,
        "sessions": len(sessions_seen),
        "bash_events": main_events + subagent_events,
        "bash_segments": segments_seen,
        "main_bash_events": main_events,
        "subagent_bash_events": subagent_events,
        "event_totals": dict(event_totals),
        "segment_totals": dict(segment_totals),
        "totals": dict(active_totals),
        "total_shown_chars": total_shown_chars,
        "event_shown_chars": dict(event_shown_chars),
        "segment_shown_chars": dict(segment_shown_chars),
        "shown_chars": dict(active_shown_chars),
        "shown_chars_basis": shown_chars_basis,
        "top_commands": _top_commands(by_norm, command_chars),
        "top_commands_by_count": _top_commands(by_norm, command_chars),
        "top_commands_by_shown_chars": _top_commands_by_shown_chars(by_norm, command_chars),
        "compound": {
            "events": compound_events,
            "segments": compound_segments,
            "shown_chars": compound_shown_chars,
            "multi_line_events": compound_multiline_events,
            "top_first_commands": _compound_rows(
                first_counts,
                first_event_seen,
                first_event_chars,
                first_categories,
            ),
            "top_anywhere_commands": _compound_rows(
                anywhere_counts,
                anywhere_event_seen,
                anywhere_event_chars,
                anywhere_categories,
            ),
            "top_routable_commands": _compound_rows(
                routable_counts,
                routable_event_seen,
                routable_event_chars,
                routable_categories,
            ),
            "top_routable_categories": _compound_rows(
                category_counts,
                category_event_seen,
                category_event_chars,
                name="category",
            ),
            "top_patterns": _compound_rows(
                pattern_counts,
                pattern_event_seen,
                pattern_event_chars,
                name="pattern",
            ),
            "top_pipeline_suffixes": _compound_rows(
                suffix_counts,
                suffix_event_seen,
                suffix_event_chars,
            ),
        },
        "grep_classes": {
            klass: {
                GREP_SCOPE_MAIN: grep_stats[klass][GREP_SCOPE_MAIN].to_dict(),
                GREP_SCOPE_SUBAGENTS: grep_stats[klass][GREP_SCOPE_SUBAGENTS].to_dict(),
                GREP_SCOPE_OVERALL: grep_stats[klass][GREP_SCOPE_OVERALL].to_dict(),
                "examples": grep_examples[klass],
            }
            for klass in (GREP_SYMBOL_SHAPED, GREP_FREE_TEXT)
        },
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


def _grep_example(session: dict, ev: dict, pattern: str | None, grep_class: str) -> dict:
    return {
        "agent": ev.get("agent_type") or session.get("agent_type"),
        "subagent": bool(ev.get("is_subagent")),
        "cwd": ev.get("cwd") or session.get("cwd"),
        "cmd": ev.get("tool_input_summary", ""),
        "pattern": pattern or "",
        "class": grep_class,
    }


def _top_commands(by_norm: dict[str, Counter], command_chars: Counter) -> list[dict]:
    rows = []
    for norm, counts in by_norm.items():
        total = sum(counts.values())
        category, n = counts.most_common(1)[0]
        rows.append({
            "command": norm,
            "n": total,
            "shown_chars": command_chars[norm],
            "top_category": category,
            "top_category_n": n,
            "categories": dict(counts),
        })
    rows.sort(key=lambda r: r["n"], reverse=True)
    return rows


def _top_commands_by_shown_chars(by_norm: dict[str, Counter], command_chars: Counter) -> list[dict]:
    rows = _top_commands(by_norm, command_chars)
    rows.sort(key=lambda r: r["shown_chars"], reverse=True)
    return rows


# ---------------------------------------------------------------------------
# Aggregation: edit-cost report (Edit/MultiEdit/Write old_string/new_string sizes)
# ---------------------------------------------------------------------------

@dataclass
class EditOp:
    tool: str
    file_path: str
    old_chars: int
    new_chars: int


def _edit_ops_from_tool_use(block: dict) -> tuple[list[EditOp], int]:
    """Return (edit_ops, write_chars) for one Edit/MultiEdit/Write tool_use block."""
    name = block.get("name")
    tool_input = block.get("input") or {}
    file_path = tool_input.get("file_path", "") or ""

    if name == "Edit":
        old = tool_input.get("old_string", "") or ""
        new = tool_input.get("new_string", "") or ""
        return [EditOp("Edit", file_path, len(old), len(new))], 0

    if name == "MultiEdit":
        ops = []
        for edit in tool_input.get("edits") or []:
            if not isinstance(edit, dict):
                continue
            old = edit.get("old_string", "") or ""
            new = edit.get("new_string", "") or ""
            ops.append(EditOp("MultiEdit", file_path, len(old), len(new)))
        return ops, 0

    if name == "Write":
        content = tool_input.get("content", "") or ""
        return [], len(content)

    return [], 0


@dataclass
class EditCostBucket:
    edit_events: int = 0
    multiedit_events: int = 0
    multiedit_ops: int = 0
    write_events: int = 0
    old_chars: int = 0
    new_chars: int = 0
    write_chars: int = 0

    def add(self, other: "EditCostBucket") -> None:
        self.edit_events += other.edit_events
        self.multiedit_events += other.multiedit_events
        self.multiedit_ops += other.multiedit_ops
        self.write_events += other.write_events
        self.old_chars += other.old_chars
        self.new_chars += other.new_chars
        self.write_chars += other.write_chars

    def to_dict(self) -> dict:
        return {
            "edit_events": self.edit_events,
            "multiedit_events": self.multiedit_events,
            "multiedit_ops": self.multiedit_ops,
            "write_events": self.write_events,
            "old_chars": self.old_chars,
            "new_chars": self.new_chars,
            "write_chars": self.write_chars,
        }


def build_edit_cost_report(model: dict, version: str, scope: str) -> dict:
    """Sum Edit/MultiEdit/Write old_string/new_string/content chars from raw transcripts,
    split main vs subagent, for sessions matching `version` and `scope`."""
    projects_dir = _projects_dir()
    main_bucket = EditCostBucket()
    sub_bucket = EditCostBucket()
    top_edits: list[dict] = []
    sessions_with_edits = 0

    for session in model.get("sessions", []):
        if session.get("tk_version", "unknown") != version:
            continue
        if not _event_in_scope(session, scope):
            continue
        session_file = session.get("session_file")
        if not session_file:
            continue

        is_subagent = bool(session.get("is_subagent"))
        bucket = sub_bucket if is_subagent else main_bucket
        path = projects_dir / session_file
        found_any = False

        for block in _iter_transcript_tool_uses(path, EDIT_TOOL_NAMES):
            found_any = True
            name = block.get("name")
            ops, write_chars = _edit_ops_from_tool_use(block)

            if name == "Edit":
                bucket.edit_events += 1
            elif name == "MultiEdit":
                bucket.multiedit_events += 1
                bucket.multiedit_ops += len(ops)
            elif name == "Write":
                bucket.write_events += 1
                bucket.write_chars += write_chars

            for op in ops:
                bucket.old_chars += op.old_chars
                bucket.new_chars += op.new_chars
                top_edits.append({
                    "tool": op.tool,
                    "file": op.file_path,
                    "old_chars": op.old_chars,
                    "new_chars": op.new_chars,
                    "subagent": is_subagent,
                })

        if found_any:
            sessions_with_edits += 1

    top_edits.sort(key=lambda e: e["old_chars"] + e["new_chars"], reverse=True)

    total_bucket = EditCostBucket()
    total_bucket.add(main_bucket)
    total_bucket.add(sub_bucket)

    return {
        "version": version,
        "scope": scope,
        "sessions_with_edits": sessions_with_edits,
        "main": main_bucket.to_dict(),
        "subagents": sub_bucket.to_dict(),
        "total": total_bucket.to_dict(),
        "old_string_total_chars": total_bucket.old_chars,
        "top_edits": top_edits[:5],
    }


# ---------------------------------------------------------------------------
# Aggregation: read-cost report (Read tool_use vs tool_result sizes, offset/limit use)
# ---------------------------------------------------------------------------

CHARS_BUCKETS = ("<5k", "5-15k", "15-40k", ">40k")
LINE_BUCKETS = ("<=400", "401-500", "501-600", "601-700", "701-800", "801-1500", ">1500")


def _chars_bucket(chars: int) -> str:
    if chars < 5000:
        return "<5k"
    if chars < 15000:
        return "5-15k"
    if chars < 40000:
        return "15-40k"
    return ">40k"


def _lines_bucket(lines: int) -> str:
    if lines <= 400:
        return "<=400"
    if lines <= 500:
        return "401-500"
    if lines <= 600:
        return "501-600"
    if lines <= 700:
        return "601-700"
    if lines <= 800:
        return "701-800"
    if lines <= 1500:
        return "801-1500"
    return ">1500"


@dataclass
class ReadCostBucket:
    read_events: int = 0
    uncapped_events: int = 0
    total_chars: int = 0
    uncapped_chars: int = 0
    repeat_pairs: int = 0  # distinct (session, file) pairs read 2+ times
    repeat_excess_events: int = 0  # reads beyond the first, for those pairs
    uncapped_chars_buckets: Counter = field(default_factory=Counter)
    uncapped_line_buckets: Counter = field(default_factory=Counter)

    def add(self, other: "ReadCostBucket") -> None:
        self.read_events += other.read_events
        self.uncapped_events += other.uncapped_events
        self.total_chars += other.total_chars
        self.uncapped_chars += other.uncapped_chars
        self.repeat_pairs += other.repeat_pairs
        self.repeat_excess_events += other.repeat_excess_events
        self.uncapped_chars_buckets.update(other.uncapped_chars_buckets)
        self.uncapped_line_buckets.update(other.uncapped_line_buckets)

    def to_dict(self) -> dict:
        share = self.uncapped_events / self.read_events if self.read_events else 0.0
        return {
            "read_events": self.read_events,
            "uncapped_events": self.uncapped_events,
            "uncapped_share": share,
            "total_chars": self.total_chars,
            "uncapped_chars": self.uncapped_chars,
            "repeat_pairs": self.repeat_pairs,
            "repeat_excess_events": self.repeat_excess_events,
            "uncapped_chars_buckets": {k: self.uncapped_chars_buckets.get(k, 0) for k in CHARS_BUCKETS},
            "uncapped_line_buckets": {k: self.uncapped_line_buckets.get(k, 0) for k in LINE_BUCKETS},
        }


def build_read_cost_report(model: dict, version: str, scope: str) -> dict:
    """Sum Read tool_use/tool_result sizes from raw transcripts, split main vs subagent,
    for sessions matching `version` and `scope`. Tracks offset/limit use (uncapped =
    neither given), the char/line size of uncapped reads, and same-session repeat reads
    of the same file_path (2+ times)."""
    projects_dir = _projects_dir()
    main_bucket = ReadCostBucket()
    sub_bucket = ReadCostBucket()
    top_reads: list[dict] = []
    repeat_offenders: list[dict] = []
    sessions_with_reads = 0

    for session in model.get("sessions", []):
        if session.get("tk_version", "unknown") != version:
            continue
        if not _event_in_scope(session, scope):
            continue
        session_file = session.get("session_file")
        if not session_file:
            continue

        is_subagent = bool(session.get("is_subagent"))
        bucket = sub_bucket if is_subagent else main_bucket
        path = projects_dir / session_file
        found_any = False
        file_counts: Counter[str] = Counter()

        for rec in _iter_transcript_tool_use_results(path, READ_TOOL_NAMES):
            if rec["is_error"]:
                continue
            found_any = True
            tool_input = rec["tool_input"]
            file_path = tool_input.get("file_path", "") or ""
            offset = tool_input.get("offset")
            limit = tool_input.get("limit")
            uncapped = offset is None and limit is None
            chars = rec["result_chars"]
            lines = rec["total_lines"] if rec["total_lines"] is not None else rec["num_lines"]

            bucket.read_events += 1
            bucket.total_chars += chars
            if uncapped:
                bucket.uncapped_events += 1
                bucket.uncapped_chars += chars
                bucket.uncapped_chars_buckets[_chars_bucket(chars)] += 1
                if lines is not None:
                    bucket.uncapped_line_buckets[_lines_bucket(lines)] += 1

            top_reads.append({
                "file": file_path,
                "chars": chars,
                "lines": lines,
                "uncapped": uncapped,
                "subagent": is_subagent,
            })
            if file_path:
                file_counts[file_path] += 1

        if found_any:
            sessions_with_reads += 1

        for file_path, count in file_counts.items():
            if count < 2:
                continue
            bucket.repeat_pairs += 1
            bucket.repeat_excess_events += count - 1
            repeat_offenders.append({
                "file": file_path,
                "count": count,
                "session_file": session_file,
                "subagent": is_subagent,
            })

    top_reads.sort(key=lambda r: r["chars"], reverse=True)
    repeat_offenders.sort(key=lambda r: r["count"], reverse=True)

    total_bucket = ReadCostBucket()
    total_bucket.add(main_bucket)
    total_bucket.add(sub_bucket)

    return {
        "version": version,
        "scope": scope,
        "sessions_with_reads": sessions_with_reads,
        "main": main_bucket.to_dict(),
        "subagents": sub_bucket.to_dict(),
        "total": total_bucket.to_dict(),
        "top_reads": top_reads[:10],
        "repeat_offenders": repeat_offenders[:5],
    }


# ---------------------------------------------------------------------------
# Reporting: text output
# ---------------------------------------------------------------------------

def print_text(report: dict, top: int, show_compound: bool) -> None:
    totals = Counter(report["totals"])
    replaceable = totals[AUTO_TK] + totals[MANUAL_TK] + totals[NUDGE_TK]
    bash_events = report["bash_events"]

    print(
        f"version={report['version']} sessions={report['sessions']} "
        f"level={report['level']} scope={report['scope']} "
        f"bash_events={bash_events} bash_segments={report['bash_segments']}"
    )
    print(f"main={report['main_bash_events']} subagents={report['subagent_bash_events']}")
    print(f"shown_chars={report['total_shown_chars']} basis={report['shown_chars_basis']}")
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
        print(f"  {row['n']:>4} {row['shown_chars']:>9} chars {row['command']:<18} {cats}")

    if show_compound:
        compound = report["compound"]
        print()
        print(
            "Compound events "
            f"events={compound['events']} segments={compound['segments']} "
            f"shown_chars={compound['shown_chars']} multi_line={compound['multi_line_events']}"
        )
        _print_compound_rows("First commands", compound["top_first_commands"], top, "command")
        _print_compound_rows("Commands anywhere", compound["top_anywhere_commands"], top, "command")
        _print_compound_rows("Routable commands anywhere", compound["top_routable_commands"], top, "command")
        _print_compound_rows("Routable categories", compound["top_routable_categories"], top, "category")
        _print_compound_rows("Shell patterns/suffixes", compound["top_patterns"], top, "pattern")
        _print_compound_rows("Pipeline suffix commands", compound["top_pipeline_suffixes"], top, "command")

    print()
    print("Grep pattern classes (grep/rg/egrep/fgrep segments, standalone + inside compounds):")
    for klass in (GREP_SYMBOL_SHAPED, GREP_FREE_TEXT):
        g = report["grep_classes"][klass]
        o = g[GREP_SCOPE_OVERALL]
        m = g[GREP_SCOPE_MAIN]
        s = g[GREP_SCOPE_SUBAGENTS]
        print(
            f"  {klass:<14} n={o['n']:>4} events={o['event_n']:>4} shown_chars={o['shown_chars']:>9} "
            f"(main n={m['n']} events={m['event_n']} | subagents n={s['n']} events={s['event_n']})"
        )
        for item in g["examples"]:
            where = "sub" if item["subagent"] else "main"
            print(f"    - {where} {item['agent']}: pattern={item['pattern']!r} cmd={item['cmd']}")

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


def _print_compound_rows(title: str, rows: list[dict], top: int, key: str) -> None:
    if not rows:
        return
    print()
    print(f"{title}:")
    for row in rows[:top]:
        cats = ""
        if "categories" in row:
            cats = " " + " ".join(f"{k}={v}" for k, v in sorted(row["categories"].items()))
        print(
            f"  {row['n']:>4} events={row['event_n']:>4} "
            f"{row['event_shown_chars']:>9} chars {row[key]:<18}{cats}"
        )


def print_edit_cost_text(report: dict) -> None:
    print(
        f"version={report['version']} scope={report['scope']} "
        f"sessions_with_edits={report['sessions_with_edits']}"
    )
    print()
    for name in ("main", "subagents", "total"):
        b = report[name]
        print(
            f"  {name:<10} edit={b['edit_events']:>5} "
            f"multiedit={b['multiedit_events']:>4}(ops={b['multiedit_ops']:>5}) "
            f"write={b['write_events']:>5} "
            f"old_chars={b['old_chars']:>9} new_chars={b['new_chars']:>9} "
            f"write_chars={b['write_chars']:>9}"
        )
    print()
    print(
        f"old_string_total_chars={report['old_string_total_chars']} "
        "(upper bound of output-token savings if line-anchored patching replaced old_string echoing)"
    )
    print()
    print("Top 5 largest single edits:")
    for e in report["top_edits"]:
        where = "sub" if e["subagent"] else "main"
        print(
            f"  {where} {e['tool']:<9} old={e['old_chars']:>7} new={e['new_chars']:>7} {e['file']}"
        )


def print_read_cost_text(report: dict) -> None:
    print(
        f"version={report['version']} scope={report['scope']} "
        f"sessions_with_reads={report['sessions_with_reads']}"
    )
    print()
    for name in ("main", "subagents", "total"):
        b = report[name]
        print(
            f"  {name:<10} reads={b['read_events']:>5} "
            f"uncapped={b['uncapped_events']:>5}({b['uncapped_share']:.0%}) "
            f"total_chars={b['total_chars']:>10} uncapped_chars={b['uncapped_chars']:>10} "
            f"repeat_pairs={b['repeat_pairs']:>4} repeat_excess={b['repeat_excess_events']:>5}"
        )
        chars_line = " ".join(f"{k}={b['uncapped_chars_buckets'][k]}" for k in CHARS_BUCKETS)
        print(f"             uncapped chars dist: {chars_line}")
        lines_line = " ".join(f"{k}={b['uncapped_line_buckets'][k]}" for k in LINE_BUCKETS)
        print(f"             uncapped lines dist: {lines_line}")
    print()
    print("Top 10 largest single reads:")
    for r in report["top_reads"]:
        where = "sub" if r["subagent"] else "main"
        cap = "uncapped" if r["uncapped"] else "capped"
        lines = r["lines"] if r["lines"] is not None else "?"
        print(f"  {where} {cap:<8} chars={r['chars']:>8} lines={lines!s:>6} {r['file']}")
    print()
    print("Top 5 repeat-read offenders (same file read 2+ times in one session):")
    for r in report["repeat_offenders"]:
        where = "sub" if r["subagent"] else "main"
        print(f"  {where} count={r['count']:>3} {r['file']} ({r['session_file']})")


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------

def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model", default=str(DEFAULT_MODEL), help="Path to model.json")
    parser.add_argument("--version", default="latest", help="tk version to inspect, or 'latest'")
    parser.add_argument("--top", type=int, default=20, help="Top command shapes to print")
    parser.add_argument("--examples", type=int, default=3, help="Examples per category")
    parser.add_argument("--include-tk", action="store_true", help="Include Bash events that already invoke tk")
    parser.add_argument("--event-level", action="store_true", help="Classify each Bash event once")
    scope_group = parser.add_mutually_exclusive_group()
    scope_group.add_argument("--main-only", action="store_true", help="Only include main-session Bash events")
    scope_group.add_argument("--subagents-only", action="store_true", help="Only include subagent Bash events")
    parser.add_argument("--compound", action="store_true", help="Print compound Bash breakdown in text mode")
    parser.add_argument(
        "--edit-cost", action="store_true",
        help="Report Edit/MultiEdit/Write old_string/new_string/content char costs instead of the Bash report",
    )
    parser.add_argument(
        "--read-cost", action="store_true",
        help="Report Read tool_use/tool_result sizes and offset/limit use instead of the Bash report",
    )
    parser.add_argument("--json", action="store_true", help="Print JSON instead of text")
    args = parser.parse_args()

    model_path = Path(args.model)
    model = json.loads(model_path.read_text())
    version = _latest_version(model) if args.version == "latest" else args.version
    scope = SCOPE_MAIN if args.main_only else SCOPE_SUBAGENTS if args.subagents_only else SCOPE_ALL

    try:
        if args.edit_cost:
            edit_report = build_edit_cost_report(model, version, scope)
            if args.json:
                print(json.dumps(edit_report, indent=2))
            else:
                print_edit_cost_text(edit_report)
            return 0

        if args.read_cost:
            read_report = build_read_cost_report(model, version, scope)
            if args.json:
                print(json.dumps(read_report, indent=2))
            else:
                print_read_cost_text(read_report)
            return 0

        level = LEVEL_EVENT if args.event_level else LEVEL_SEGMENT
        report = build_report(model, version, args.include_tk, args.examples, level, scope)
        if args.json:
            print(json.dumps(report, indent=2))
        else:
            print_text(report, args.top, args.compound)
    except BrokenPipeError:
        return 0
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
