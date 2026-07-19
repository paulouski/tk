"""
P3 — test_build.py

build_report (tkstats.reports.build) surfaces two loss-detector rollups that
previously only lived per-session in model.json:
  - native_grep_retry: sum of session["n_native_grep_retry"] across sessions,
    plus top_sessions by count.
  - reread: sum of session["n_reread"] / session["reread_chars_total"] across
    sessions, plus top_sessions by count and by chars.

These fields are set by detect._rollup_session (see tkstats/detect.py), so
the tests build synthetic session dicts with the rollup fields already
populated rather than replaying _detect_reread / _detect_native_grep_retry
on events.

Cardinality (RECURRENCE-GUARD): each test uses >=2 sessions that each
contribute a nonzero count, so a wrong sum (e.g. only counting the last
session) or a wrong key axis (e.g. summing across all sessions into one
"top_sessions" row) is caught.
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from tkstats.reports.build import build_report  # noqa: E402


def _session(session_id: str, **rollup_fields) -> dict:
    s = {"session_id": session_id, "agent_type": "claude", "events": []}
    s.update(rollup_fields)
    return s


def _rpt_model(sessions: list[dict]) -> dict:
    return {
        "generated_for_range": {},
        "match_stats": {"total_tk_events": 0, "matched": 0, "unmatched": 0, "match_rate": 0.0},
        "outcome_counts": {"WIN": 0, "NEUTRAL": 0, "NET_NEGATIVE": 0, "UNKNOWN": 0},
        "aggregates": {
            "win": {"raw_chars": 0, "shown_chars": 0, "saved_chars": 0},
            "net_negative": {"raw_chars": 0, "shown_chars": 0, "extra_chars": 0},
        },
        "sessions": sessions,
        "groups": [],
        "interventions": [],
    }


class NativeGrepRetryAggregate(unittest.TestCase):
    def test_two_sessions_sum_and_top_sessions(self) -> None:
        sessions = [
            _session("s1", n_native_grep_retry=3),
            _session("s2", n_native_grep_retry=5),
            _session("s3", n_native_grep_retry=0),
        ]
        rpt = build_report(_rpt_model(sessions))
        ngr = rpt["native_grep_retry"]
        self.assertEqual(ngr["total"], 8)
        self.assertEqual(rpt["totals"]["native_grep_retry_total"], 8)
        top = {row["session_id"]: row["n"] for row in ngr["top_sessions"]}
        self.assertEqual(top, {"s1": 3, "s2": 5})


class RereadAggregate(unittest.TestCase):
    def test_two_sessions_sum_and_top_sessions(self) -> None:
        sessions = [
            _session(
                "s1", n_reread=2, reread_chars_total=400,
                read_repeat_counts={"exact_slice_repeat": 2, "different_slice": 3},
                read_repeat_chars={"exact_slice_repeat": 400, "different_slice": 600},
            ),
            _session(
                "s2", n_reread=1, reread_chars_total=900,
                read_repeat_counts={"whole_file_repeat": 1, "different_slice": 2},
                read_repeat_chars={"whole_file_repeat": 900, "different_slice": 500},
            ),
            _session("s3", n_reread=0, reread_chars_total=0),
        ]
        rpt = build_report(_rpt_model(sessions))
        rr = rpt["reread"]
        self.assertEqual(rr["total"], 3)
        self.assertEqual(rr["chars_total"], 1300)
        self.assertEqual(rpt["totals"]["reread_total"], 3)
        self.assertEqual(rr["by_kind"]["exact_slice_repeat"]["n"], 2)
        self.assertEqual(rr["by_kind"]["whole_file_repeat"]["n"], 1)
        self.assertEqual(rr["by_kind"]["different_slice"]["n"], 5)
        self.assertEqual(rr["same_path_total"], 8)

        top_n = {row["session_id"]: row["n"] for row in rr["top_sessions"]}
        self.assertEqual(top_n, {"s1": 2, "s2": 1})

        # by-chars ranking is independent of by-n ranking: s2 has fewer
        # rereads but more chars, so it must rank first here.
        top_chars = [row["session_id"] for row in rr["top_sessions_by_chars"]]
        self.assertEqual(top_chars[0], "s2")


class CoverageLabels(unittest.TestCase):
    def test_own_log_and_raw_baseline_are_separate(self) -> None:
        model = _rpt_model([])
        model["match_stats"] = {
            "total_tk_events": 3,
            "matched": 2,
            "unmatched": 1,
            "match_rate": 2 / 3,
            "own_log_matched_events": 2,
            "own_log_event_match_rate": 2 / 3,
            "raw_baseline_events": 1,
            "raw_baseline_event_rate": 1 / 3,
            "total_tk_invocations": 4,
            "own_log_matched_invocations": 3,
            "raw_baseline_invocations": 1,
        }

        totals = build_report(model)["totals"]

        self.assertEqual(totals["own_log_matched_events"], 2)
        self.assertEqual(totals["raw_baseline_events"], 1)
        self.assertEqual(totals["tk_invocations"], 4)

    def test_structured_invocations_are_counted_without_duplicating_events(self) -> None:
        model = _rpt_model([{
            "session_id": "s1",
            "agent_type": "main",
            "events": [{
                "tool": "Bash", "is_tk": True, "tk_command": "def",
                "outcome": "UNKNOWN",
                "tk_invocations": [
                    {"tk_command": "def", "tk_sub": None},
                    {"tk_command": "refs", "tk_sub": None},
                ],
            }],
        }])
        model["match_stats"]["total_tk_events"] = 1
        model["match_stats"]["total_tk_invocations"] = 2

        result = build_report(model)

        self.assertEqual(result["totals"]["tk_events"], 1)
        self.assertEqual(result["tk_invocations"]["total"], 2)
        self.assertEqual(
            {row["command"]: row["n"] for row in result["tk_invocations"]["by_command"]},
            {"def": 1, "refs": 1},
        )


if __name__ == "__main__":
    unittest.main()
