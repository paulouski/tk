"""
Detector pipeline: event-level detectors (events.py) + hook-adoption
annotation (adoption.py) + per-session rollups (rollup.py).

Importable API
--------------
  annotate(model) -> model   # mutates and returns the same dict
"""

from __future__ import annotations

from tkstats.detect.adoption import _detect_adoption
from tkstats.detect.events import (
    _detect_empty,
    _detect_escalated,
    _detect_fallback,
    _detect_native_grep_retry,
    _detect_reread,
    _detect_retry,
    _detect_stealth_read,
    _detect_symbol_grep,
)
from tkstats.detect.rollup import _rollup_session


def annotate(model: dict) -> dict:
    """Run all detectors on every session in the model, add rollups. Mutates in place."""
    _detect_adoption(model)  # model-level: groups interventions by session_id itself
    for session in model.get("sessions", []):
        events = session.get("events", [])
        _detect_stealth_read(events)
        _detect_fallback(events)
        _detect_retry(events)
        _detect_escalated(events)
        _detect_empty(events)
        _detect_symbol_grep(events)
        _detect_reread(events)
        _detect_native_grep_retry(events)
        _rollup_session(session)
    return model
