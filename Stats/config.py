"""
Stats/config.py

Single source of truth for tunable thresholds and classifier constants
used across the Stats pipeline. Centralizing these here makes it easy to
adjust detection sensitivity without grepping the whole tree.

Importable API
--------------
  MATCH_WINDOW                     join.py
  _SEARCH_NAV_COMMANDS             detect.py  (B1 / fallback)
  _FALLBACK_TOOLS                  detect.py  (B1 / fallback)
  _RESULT_USED_TOOLS               detect.py  (B1 / result-used)
  EDIT_RESULT_USED_TOOLS           detect.py  (C3 / result-used-edit)
  NATIVE_GREP_RETRY_LOOKBACK       detect.py  (B2 / native grep retry)
  STEALTH_READ_EXTENSIONS          detect.py  (B3 / stealth reads)
  FOLLOW_UP_WINDOW                 bash_opp_classify.py  (reserved)
  LSP_NAV_COMMANDS                 bash_opp_classify.py  (reserved)
  CHARS_BUCKETS                    bash_opp_reports.py   (read-cost)
  LINE_BUCKETS                     bash_opp_reports.py   (read-cost)
"""

from __future__ import annotations

from datetime import timedelta


# ---------------------------------------------------------------------------
# Match window for joining tk events with own-log entries (join.py)
# ---------------------------------------------------------------------------

MATCH_WINDOW = timedelta(seconds=30)


# ---------------------------------------------------------------------------
# Search/nav tk commands eligible for fallback detection (detect.py)
# ---------------------------------------------------------------------------

_SEARCH_NAV_COMMANDS = frozenset({
    "grep", "focus", "refs", "def", "callers",
    "view", "files", "tree", "find", "ls",
})

# Native tool names that count as fallback (negative signal: re-searched manually)
_FALLBACK_TOOLS = frozenset({"Grep", "Glob"})

# Native tool name that counts as result-used (positive signal: tk led to file)
_RESULT_USED_TOOLS = frozenset({"Read"})

# Edit-family tool names that count as result-used-edit
# (positive signal: tk led to a code change, not just a read).
EDIT_RESULT_USED_TOOLS = frozenset({"Edit", "MultiEdit", "Write"})


# ---------------------------------------------------------------------------
# Native-grep retry detection (detect.py B2)
# ---------------------------------------------------------------------------

# How many prior non-tk grep events to look back when checking for retry.
NATIVE_GREP_RETRY_LOOKBACK = 10


# ---------------------------------------------------------------------------
# Stealth-read detection (detect.py B3)
# ---------------------------------------------------------------------------

# File extensions that count as a "tracked source file" — when a non-tk Bash
# command reads a file matching one of these extensions, it is a stealth read.
STEALTH_READ_EXTENSIONS = frozenset({
    ".cs", ".py", ".ts", ".tsx", ".js", ".json", ".md", ".yaml", ".yml", ".sh",
})


# ---------------------------------------------------------------------------
# Bash opportunity classification thresholds (bash_opp_classify.py)
# ---------------------------------------------------------------------------

# Events within this many follow-ups of a tk event count as a follow-up.
FOLLOW_UP_WINDOW = 5

# LSP-style navigation commands (reserved — see classify).
LSP_NAV_COMMANDS: frozenset[str] = frozenset()


# ---------------------------------------------------------------------------
# Read-cost report bucket labels (bash_opp_reports.py)
# ---------------------------------------------------------------------------

CHARS_BUCKETS = ("<5k", "5-15k", "15-40k", ">40k")
LINE_BUCKETS = ("<=400", "401-500", "501-600", "601-700", "701-800", "801-1500", ">1500")
