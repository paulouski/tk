"""
tkstats/config.py

Single source of truth for tunable thresholds and classifier constants
used across the Stats pipeline. Centralizing these here makes it easy to
adjust detection sensitivity without grepping the whole tree.

Importable API
--------------
  MATCH_WINDOW                     tkstats/matching/ownlog.py
  _SEARCH_NAV_COMMANDS             tkstats/detect/events.py  (B1 / fallback)
  _FALLBACK_TOOLS                  tkstats/detect/events.py  (B1 / fallback)
  _RESULT_USED_TOOLS               tkstats/detect/events.py  (B1 / result-used)
  EDIT_RESULT_USED_TOOLS           tkstats/detect/events.py  (C3 / result-used-edit)
  NATIVE_GREP_RETRY_LOOKBACK       tkstats/detect/events.py  (B2 / native grep retry)
  STEALTH_READ_EXTENSIONS          tkstats/detect/events.py  (B3 / stealth reads)
  FOLLOW_UP_WINDOW                 tkstats/bashanalytics/classify.py  (reserved)
  LSP_NAV_COMMANDS                 tkstats/bashanalytics/classify.py  (reserved)
  CHARS_BUCKETS                    tkstats/bashanalytics/readcost.py
  LINE_BUCKETS                     tkstats/bashanalytics/readcost.py
  ADOPTION_LOOKAHEAD               tkstats/detect/adoption.py  (S4 / hook adoption)
  ADOPTION_NAV_COMMANDS            tkstats/detect/adoption.py  (S4 / hook adoption)
  ADOPTION_SYMBOL_COMMANDS         tkstats/detect/adoption.py  (S4 / hook adoption)
  ADOPTION_EDIT_WRITE_COMMANDS     tkstats/detect/adoption.py  (S4 / hook adoption)
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


# ---------------------------------------------------------------------------
# Post-hook adoption stats (detect.py S4)
# ---------------------------------------------------------------------------

# How many following events (per session, forward-scanned) to check for an
# adoption signal after a cap-read/nudge intervention.
ADOPTION_LOOKAHEAD = 5

# tk commands that count as adoption of a cap-read or stealth-read nudge
# (structure-aware nav after a raw/stealth file read was capped or flagged).
ADOPTION_NAV_COMMANDS = frozenset({"view", "def", "refs", "callers", "impl"})

# tk commands that count as adoption of a symbol-grep nudge.
ADOPTION_SYMBOL_COMMANDS = frozenset({"def", "refs", "callers", "sym"})

# tk commands that count as adoption of an edit-write nudge (large native Edit ->
# `tk write` on the SAME file — see hooks/tk-route.sh EDIT_WRITE_NUDGE_CHARS).
ADOPTION_EDIT_WRITE_COMMANDS = frozenset({"write"})
