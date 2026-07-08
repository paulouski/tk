"""
P3 — test_aggregates.py

Projection-key cardinality tests for Stats/join.py:
  - _build_groups links main + subagent sessions by group_id; sums
    main_usd + subs_usd == total_usd; surfaces n_subs and main_session_id.
  - main-only group yields delegation_delta_usd = None (no subs branch).
  - run_join + apply_version_filter agrees that 2 events with the same
    tk_version project to a single by_version row (cardinality test for the
    version projection key).

All tests use synthetic session dicts; no DB or jsonl reads.
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parent.parent))

import join  # noqa: E402


def _cost(usd: float = 1.0) -> dict:
    return {"model": "", "usd": usd, "tokens": {}, "usd_by_type": {}}


class BuildGroups(unittest.TestCase):
    def test_two_subs_one_main(self) -> None:
        main = {
            "session_id": "m1", "is_subagent": False, "group_id": "m1",
            "parent_session_id": None, "cost": _cost(1.0),
        }
        sub1 = {
            "session_id": "s1", "is_subagent": True, "group_id": "m1",
            "parent_session_id": "m1", "cost": _cost(1.0),
        }
        sub2 = {
            "session_id": "s2", "is_subagent": True, "group_id": "m1",
            "parent_session_id": "m1", "cost": _cost(1.0),
        }
        groups = join._build_groups([main, sub1, sub2])
        self.assertEqual(len(groups), 1)
        g = groups[0]
        self.assertEqual(g["n_subs"], 2)
        self.assertEqual(g["main_session_id"], "m1")
        self.assertAlmostEqual(g["main_usd"] + g["subs_usd"], g["total_usd"], places=6)
        self.assertEqual(g["main_usd"], 1.0)
        self.assertEqual(g["subs_usd"], 2.0)
        self.assertEqual(g["total_usd"], 3.0)

    def test_opencode_group_linking(self) -> None:
        # opencode-style: main has group_id==session_id, sub has group_id==main.id
        main = {
            "session_id": "abc", "is_subagent": False, "group_id": "abc",
            "parent_session_id": None, "cost": _cost(0.0),
        }
        sub = {
            "session_id": "xyz", "is_subagent": True, "group_id": "abc",
            "parent_session_id": "abc", "cost": _cost(0.0),
        }
        groups = join._build_groups([main, sub])
        self.assertEqual(len(groups), 1)
        g = groups[0]
        self.assertEqual(g["n_subs"], 1)
        self.assertEqual(g["main_session_id"], "abc")
        self.assertEqual(g["group_id"], "abc")

    def test_no_subs_no_delta(self) -> None:
        main = {
            "session_id": "m1", "is_subagent": False, "group_id": "m1",
            "parent_session_id": None, "cost": _cost(1.0),
        }
        groups = join._build_groups([main])
        self.assertEqual(len(groups), 1)
        self.assertEqual(groups[0]["n_subs"], 0)
        self.assertIsNone(groups[0]["delegation_delta_usd"])
        self.assertIsNone(groups[0]["inline_estimate_usd"])


class ByVersionProjection(unittest.TestCase):
    def test_two_events_same_version(self) -> None:
        # One session, 2 tk events, both with tk_version="0.8.0".
        # After apply_version_filter("0.8.0") the projection must yield 1
        # by_version row with n==2.
        session = {
            "session_id": "s1",
            "session_file": "fake.jsonl",
            "is_subagent": False,
            "group_id": "s1",
            "parent_session_id": None,
            "cwd": "/test",
            "git_branch": None,
            "cc_version": None,
            "ts_start": "2025-01-01T00:00:00Z",
            "ts_end":   "2025-01-01T00:01:00Z",
            "cost": {"model": "", "usd": 0.0, "tokens": {}, "usd_by_type": {}},
            "events": [
                {
                    "tool": "Bash", "is_tk": True, "tk_command": "def",
                    "ts": "2025-01-01T00:00:00Z",
                    "tk_version": "0.8.0", "tool_use_id": "u1",
                },
                {
                    "tool": "Bash", "is_tk": True, "tk_command": "def",
                    "ts": "2025-01-01T00:01:00Z",
                    "tk_version": "0.8.0", "tool_use_id": "u2",
                },
            ],
        }
        with patch("join.load_sessions", return_value=[session]), \
             patch("join.load_own_log", return_value=[]), \
             patch("join._infer_version", return_value="0.8.0"):
            model = join.run_join()
        # run_join does not populate by_version; seed it with our version so
        # apply_version_filter accepts the target. apply_version_filter then
        # recomputes by_version from the (filtered) tk events.
        model.setdefault("by_version", []).append({
            "version": "0.8.0", "n": 0, "WIN": 0, "NEUTRAL": 0,
            "NET_NEGATIVE": 0, "UNKNOWN": 0, "saved_chars": 0,
        })
        filtered = join.apply_version_filter(model, "0.8.0")
        self.assertEqual(len(filtered["by_version"]), 1)
        self.assertEqual(filtered["by_version"][0]["version"], "0.8.0")
        self.assertEqual(filtered["by_version"][0]["n"], 2)

    def test_latest_resolves_to_highest_version(self) -> None:
        # Regression: stats.sh passes target="latest" by default; the filter
        # must resolve it to the highest non-unknown version before applying.
        # Pre-fix, "latest" was treated as a literal unknown version and raised.
        model = {
            "by_version": [
                {"version": "unknown", "n": 5},
                {"version": "0.7.0", "n": 3},
                {"version": "0.8.0", "n": 2},
                {"version": "0.6.0", "n": 4},
            ],
            "sessions": [], "groups": [], "outcome_counts": {},
            "aggregates": {}, "match_stats": {"total_tk_events": 0},
            "generated_for_range": {},
        }
        filtered = join.apply_version_filter(model, "latest")
        self.assertEqual(filtered["version_filter"], "0.8.0")

    def test_unknown_version_raises(self) -> None:
        model = {
            "by_version": [{"version": "0.8.0", "n": 2}],
            "sessions": [], "groups": [], "outcome_counts": {},
            "aggregates": {}, "match_stats": {"total_tk_events": 0},
            "generated_for_range": {},
        }
        with self.assertRaises(ValueError):
            join.apply_version_filter(model, "0.0.0")


if __name__ == "__main__":
    unittest.main()
