"""
Self-contained JavaScript-literal reconstruction engine.

Modern Codex tool calls are often wrapped in one ``exec`` custom tool whose
JavaScript input orchestrates several nested tools. This module recovers the
literal shell commands passed to ``tools.exec_command({cmd: ...})`` calls —
including string/template literals, identifiers resolved back to their
declaration, and array/loop-driven command lists — without a real JS parser.
Zero dependency on the rest of the ingestion package: pure text parsing.
"""

from __future__ import annotations

import re


_EXEC_COMMAND_RE = re.compile(r"\btools\.exec_command\s*\(")
_CMD_PROPERTY_RE = re.compile(r"(?:^|[,\s{])(?:cmd|\"cmd\"|'cmd')\s*:\s*")
_CMD_SHORTHAND_RE = re.compile(r"(?:^|[,\s{])cmd\s*(?=[,}])")
_IDENTIFIER_RE = re.compile(r"[A-Za-z_$][A-Za-z0-9_$]*")


def _balanced_slice(text: str, start: int, opening: str, closing: str) -> str | None:
    if start >= len(text) or text[start] != opening:
        return None
    depth = 0
    quote: str | None = None
    escaped = False
    i = start
    while i < len(text):
        char = text[i]
        if quote is not None:
            if escaped:
                escaped = False
            elif char == "\\":
                escaped = True
            elif char == quote:
                quote = None
            i += 1
            continue
        if char in ('"', "'", "`"):
            quote = char
        elif char == opening:
            depth += 1
        elif char == closing:
            depth -= 1
            if depth == 0:
                return text[start:i + 1]
        i += 1
    return None


def _decode_js_string(literal: str) -> str | None:
    if len(literal) < 2 or literal[0] not in ('"', "'", "`"):
        return None
    quote = literal[0]
    if literal[-1] != quote:
        return None
    body = literal[1:-1]
    if quote == "`" and re.search(r"(?<!\\)\$\{", body):
        return None

    escapes = {
        "n": "\n", "r": "\r", "t": "\t", "b": "\b", "f": "\f",
        "v": "\v", "0": "\0", "\\": "\\", "'": "'", '"': '"',
        "`": "`", "/": "/",
    }
    result: list[str] = []
    i = 0
    while i < len(body):
        char = body[i]
        if char != "\\":
            result.append(char)
            i += 1
            continue
        i += 1
        if i >= len(body):
            return None
        escaped = body[i]
        if escaped in escapes:
            result.append(escapes[escaped])
            i += 1
            continue
        if escaped == "x" and i + 2 < len(body):
            try:
                result.append(chr(int(body[i + 1:i + 3], 16)))
            except ValueError:
                return None
            i += 3
            continue
        if escaped == "u" and i + 4 < len(body):
            try:
                result.append(chr(int(body[i + 1:i + 5], 16)))
            except ValueError:
                return None
            i += 5
            continue
        if escaped in ("\n", "\r"):
            if escaped == "\r" and i + 1 < len(body) and body[i + 1] == "\n":
                i += 1
            i += 1
            continue
        # JavaScript treats unknown escapes such as \q as q.
        result.append(escaped)
        i += 1
    return "".join(result)


def _raw_string_literal_at(text: str, start: int) -> tuple[str | None, int]:
    while start < len(text) and text[start].isspace():
        start += 1
    if start >= len(text) or text[start] not in ('"', "'", "`"):
        return None, start
    quote = text[start]
    escaped = False
    i = start + 1
    while i < len(text):
        char = text[i]
        if escaped:
            escaped = False
        elif char == "\\":
            escaped = True
        elif char == quote:
            return text[start:i + 1], i + 1
        i += 1
    return None, start


def _string_literal_at(text: str, start: int) -> tuple[str | None, int]:
    literal, end = _raw_string_literal_at(text, start)
    return (_decode_js_string(literal) if literal is not None else None), end


def _decode_template_literal(
    literal: str,
    source: str,
    bindings: dict[str, str] | None = None,
) -> str | None:
    if not literal.startswith("`") or not literal.endswith("`"):
        return None
    values = bindings or {}
    missing = False

    def replace(match: re.Match[str]) -> str:
        nonlocal missing
        identifier = match.group(1)
        value = values.get(identifier)
        if value is None:
            value = _resolve_identifier_string(source, identifier)
        if value is None:
            missing = True
            return match.group(0)
        return value.replace("\\", "\\\\").replace("`", "\\`")

    rendered = re.sub(
        r"(?<!\\)\$\{\s*([A-Za-z_$][A-Za-z0-9_$]*)\s*\}",
        replace,
        literal[1:-1],
    )
    if missing or re.search(r"(?<!\\)\$\{", rendered):
        return None
    return _decode_js_string(f"`{rendered}`")


def _resolve_identifier_string(source: str, identifier: str) -> str | None:
    pattern = re.compile(rf"\b(?:const|let|var)\s+{re.escape(identifier)}\s*=\s*")
    matches = list(pattern.finditer(source))
    if not matches:
        return None
    literal, _ = _raw_string_literal_at(source, matches[-1].end())
    if literal is None:
        return None
    return _decode_js_string(literal) or _decode_template_literal(literal, source)


def _parse_string_array(array: str) -> tuple[list[str], int]:
    values: list[str] = []
    unparsed = 0
    i = 1
    while i < len(array) - 1:
        while i < len(array) - 1 and (array[i].isspace() or array[i] == ","):
            i += 1
        if i >= len(array) - 1:
            break
        value, end = _string_literal_at(array, i)
        if value is None:
            unparsed += 1
            while i < len(array) - 1 and array[i] != ",":
                i += 1
        else:
            values.append(value)
            i = end
    return values, unparsed


def _resolve_identifier_array(source: str, identifier: str) -> tuple[list[str], int]:
    pattern = re.compile(rf"\b(?:const|let|var)\s+{re.escape(identifier)}\s*=\s*")
    matches = list(pattern.finditer(source))
    if not matches:
        return [], 1
    start = matches[-1].end()
    while start < len(source) and source[start].isspace():
        start += 1
    array = _balanced_slice(source, start, "[", "]")
    if array is None:
        return [], 1

    return _parse_string_array(array)


def _cmd_literal(obj: str) -> str | None:
    match = _CMD_PROPERTY_RE.search(obj)
    if not match:
        return None
    literal, _ = _raw_string_literal_at(obj, match.end())
    return literal


def _cmd_identifier(obj: str) -> str | None:
    match = _CMD_PROPERTY_RE.search(obj)
    if not match:
        return "cmd" if _CMD_SHORTHAND_RE.search(obj) else None
    _, end = _raw_string_literal_at(obj, match.end())
    identifier = _IDENTIFIER_RE.match(obj, end)
    return identifier.group(0) if identifier else None


def _loop_values(source: str) -> tuple[str, list[str], int] | None:
    matches = list(re.finditer(
        r"\bfor\s*\(\s*const\s+([A-Za-z_$][A-Za-z0-9_$]*)\s+of\s+",
        source,
    ))
    if not matches:
        return None
    match = matches[-1]
    start = match.end()
    while start < len(source) and source[start].isspace():
        start += 1
    if start < len(source) and source[start] == "[":
        array = _balanced_slice(source, start, "[", "]")
        if array is None:
            return match.group(1), [], 1
        values, unparsed = _parse_string_array(array)
        return match.group(1), values, unparsed
    identifier = _IDENTIFIER_RE.match(source, start)
    if not identifier:
        return match.group(1), [], 1
    values, unparsed = _resolve_identifier_array(source, identifier.group(0))
    return match.group(1), values, unparsed


def _extract_cmd_from_object(source: str, obj: str) -> str | None:
    match = _CMD_PROPERTY_RE.search(obj)
    if not match:
        if _CMD_SHORTHAND_RE.search(obj):
            return _resolve_identifier_string(source, "cmd")
        return None
    literal, end = _raw_string_literal_at(obj, match.end())
    value = _decode_js_string(literal) if literal is not None else None
    if value is None and literal is not None:
        value = _decode_template_literal(literal, source)
    if value is not None:
        return value
    identifier = _IDENTIFIER_RE.match(obj, end)
    if identifier:
        return _resolve_identifier_string(source, identifier.group(0))
    return None


def _extract_exec_commands(source: str) -> tuple[list[str], int]:
    commands: list[str] = []
    unparsed = 0
    for match in _EXEC_COMMAND_RE.finditer(source):
        i = match.end()
        while i < len(source) and source[i].isspace():
            i += 1
        obj = _balanced_slice(source, i, "{", "}")
        if obj is None:
            unparsed += 1
            continue
        source_before = source[:match.start()]
        command = _extract_cmd_from_object(source_before + obj, obj)
        if command is None:
            loop = _loop_values(source_before)
            if loop:
                loop_variable, values, loop_unparsed = loop
                identifier = _cmd_identifier(obj)
                literal = _cmd_literal(obj)
                if identifier == loop_variable:
                    commands.extend(values)
                    unparsed += loop_unparsed
                    continue
                if literal is not None:
                    rendered = [
                        _decode_template_literal(
                            literal,
                            source_before,
                            {loop_variable: value},
                        )
                        for value in values
                    ]
                    if rendered and all(value is not None for value in rendered):
                        commands.extend(value for value in rendered if value is not None)
                        unparsed += loop_unparsed
                        continue
            map_match = re.search(
                r"\b([A-Za-z_$][A-Za-z0-9_$]*)\.map\(\s*"
                r"([A-Za-z_$][A-Za-z0-9_$]*)\s*=>\s*$",
                source_before,
            )
            if map_match and (
                _CMD_SHORTHAND_RE.search(obj)
                or re.search(
                    rf"(?:^|[,\s{{])cmd\s*:\s*{re.escape(map_match.group(2))}\s*(?=[,}}])",
                    obj,
                )
            ):
                values, array_unparsed = _resolve_identifier_array(
                    source_before, map_match.group(1)
                )
                commands.extend(values)
                unparsed += array_unparsed
                continue
            unparsed += 1
        else:
            commands.append(command)
    return commands, unparsed
