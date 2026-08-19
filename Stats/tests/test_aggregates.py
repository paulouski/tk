"""
test_aggregates.py

fallback_n aggregation must count fallback_same_target (genuinely the same
symbol/target followed up on with a native tool), not the looser fallback
flag (any native tool call within 3 events, coincidental or not).
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

from tkstats.aggregates import aggregate_events  # noqa: E402
from tkstats.detect.events import _detect_fallback  # noqa: E402


def _session(events: list[dict], agent_type: str = "claude", session_id: str = "s1") -> dict:
    return {"session_id": session_id, "agent_type": agent_type, "events": events}


class AggregateEventsFallback(unittest.TestCase):
    def test_unrelated_native_followup_not_counted(self) -> None:
        # tk refs followed by a Grep for entirely different identifiers:
        # fallback=True (coincidence) but fallback_same_target=False.
        events = [{
            "is_tk": True, "tk_command": "refs", "tk_sub": None,
            "outcome": "NEUTRAL", "fallback": True, "fallback_same_target": False,
        }]
        result = aggregate_events([_session(events)])

        cs = next(c for c in result["by_command"] if c["command"] == "refs")
        self.assertEqual(cs["fallback_n"], 0)

        at = next(a for a in result["by_agent_type"] if a["agent_type"] == "claude")
        self.assertEqual(at["fallback_n"], 0)

    def test_real_case_refs_then_unrelated_grep_not_counted(self) -> None:
        # Reproduces the ses_feb630597ffeXkf84zNJ0iC6yj (opencode) case from
        # the 2026-08-19 audit: tk refs CaseValuesPipelineContext succeeded,
        # then a Grep for two entirely different identifiers followed within
        # 3 events. Detected as fallback (coincidence) but NOT
        # fallback_same_target, so it must no longer count in fallback_n.
        events = [
            {
                "is_tk": True, "tk_command": "refs", "tk_sub": None,
                "outcome": "NEUTRAL",
                "tk_operand_values": ["CaseValuesPipelineContext"],
            },
            {
                "is_tk": False, "tool": "Grep",
                "tool_input_summary": "SumTransfersByCustomerAsync|SumPaidOwnAccountsByCustomersAsync",
            },
        ]
        _detect_fallback(events)
        self.assertTrue(events[0]["fallback"])
        self.assertFalse(events[0]["fallback_same_target"])

        result = aggregate_events([_session(events, agent_type="opencode")])
        cs = next(c for c in result["by_command"] if c["command"] == "refs")
        self.assertEqual(cs["fallback_n"], 0)

    def test_same_target_followup_counted(self) -> None:
        events = [{
            "is_tk": True, "tk_command": "def", "tk_sub": None,
            "outcome": "NEUTRAL", "fallback": True, "fallback_same_target": True,
        }]
        result = aggregate_events([_session(events)])

        cs = next(c for c in result["by_command"] if c["command"] == "def")
        self.assertEqual(cs["fallback_n"], 1)

        at = next(a for a in result["by_agent_type"] if a["agent_type"] == "claude")
        self.assertEqual(at["fallback_n"], 1)


if __name__ == "__main__":
    unittest.main()
