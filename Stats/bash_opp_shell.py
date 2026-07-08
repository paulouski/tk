"""
Shell command parsing: split a raw Bash command string into segments, detect
shell control structures, and normalise a token list to a canonical "command shape".
"""

from __future__ import annotations

import re
import shlex


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


__all__ = [
    "_split_on_shell_operators",
    "_split_segments",
    "_shell_control_marks",
    "_has_shell_control",
    "_split_pipeline_parts",
    "_is_compound_event",
    "_strip_prefix",
    "_subcommand",
    "_norm_command",
]
