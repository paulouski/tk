"""
Shared constants and tiny helpers used by all other bash_opp_* modules.

Pure data + the two pure version helpers (no I/O, no side effects).
"""

from __future__ import annotations

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
# Version helpers (used by reports and by the CLI for "latest" resolution)
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


__all__ = [
    "AUTO_TK",
    "MANUAL_TK",
    "NUDGE_TK",
    "NEW_TK",
    "NATIVE_READ",
    "KEEP_NATIVE",
    "COMPOUND",
    "UNKNOWN",
    "LEVEL_SEGMENT",
    "LEVEL_EVENT",
    "SCOPE_ALL",
    "SCOPE_MAIN",
    "SCOPE_SUBAGENTS",
    "EVENT_CATEGORY_PRIORITY",
    "ROUTABLE_CATEGORIES",
    "PIPE_PATTERN_COMMANDS",
    "GREP_COMMANDS",
    "GREP_SYMBOL_SHAPED",
    "GREP_FREE_TEXT",
    "GREP_SCOPE_MAIN",
    "GREP_SCOPE_SUBAGENTS",
    "GREP_SCOPE_OVERALL",
    "EDIT_TOOL_NAMES",
    "READ_TOOL_NAMES",
    "_version_key",
    "_latest_version",
]
