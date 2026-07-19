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
from datetime import datetime, timezone
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

    def test_operand_overlap_uses_secondary_tk_invocations(self) -> None:
        main = {
            "session_id": "m1", "is_subagent": False, "group_id": "m1",
            "cost": _cost(),
            "events": [{
                "tk_operand_values": ["MainOnly"],
                "tk_invocations": [
                    {"tk_operand_values": ["MainOnly"]},
                    {"tk_operand_values": ["Shared"]},
                ],
            }],
        }
        sub = {
            "session_id": "s1", "is_subagent": True, "group_id": "m1",
            "cost": _cost(),
            "events": [{"tk_operand_values": ["Shared"]}],
        }

        group = join._build_groups([main, sub])[0]

        self.assertEqual(group["parent_sub_operand_overlap"], 1)
        self.assertEqual(group["parent_sub_operand_examples"], ["Shared"])


class ByVersionProjection(unittest.TestCase):
    def test_run_join_tracks_event_and_invocation_coverage_separately(self) -> None:
        session = {
            "session_id": "s1", "session_file": "fake.jsonl",
            "is_subagent": False, "group_id": "s1", "parent_session_id": None,
            "cwd": "/repo", "ts_start": "2026-07-10T10:00:00Z",
            "ts_end": "2026-07-10T10:00:00Z", "events": [{
                "tool": "Bash", "is_tk": True, "tk_command": "def",
                "tk_sub": None, "ts": "2026-07-10T10:00:00Z", "cwd": "/repo",
                "tk_invocations": [
                    {"tk_command": "def", "tk_sub": None},
                    {"tk_command": "refs", "tk_sub": None},
                ],
            }],
            "cost": _cost(0.0),
        }
        matches = [
            {"rawChars": None, "shownChars": 10, "tkVersion": "0.10.0"},
            {"rawChars": 100, "shownChars": 20, "tkVersion": "0.10.0"},
        ]
        with patch("join.load_sessions", return_value=[session]), \
             patch("join.load_own_log", return_value=[]), \
             patch("join.load_interventions", return_value=[]), \
             patch("join._find_match", side_effect=matches):
            model = join.run_join()

        stats = model["match_stats"]
        self.assertEqual(stats["total_tk_events"], 1)
        self.assertEqual(stats["total_tk_invocations"], 2)
        self.assertEqual(stats["own_log_matched_events"], 1)
        self.assertEqual(stats["raw_baseline_events"], 0)
        self.assertEqual(stats["own_log_matched_invocations"], 2)
        self.assertEqual(stats["raw_baseline_invocations"], 1)
        self.assertEqual(len(model["sessions"][0]["events"]), 1)
        self.assertEqual(len(model["sessions"][0]["events"][0]["tk_invocations"]), 2)

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

    def test_filter_removes_cross_version_interventions_and_recomputes_adoption(self) -> None:
        def iso(epoch: float) -> str:
            return datetime.fromtimestamp(epoch, tz=timezone.utc).isoformat()

        model = {
            "by_version": [
                {"version": "0.9.0", "n": 1},
                {"version": "0.10.0", "n": 1},
            ],
            "sessions": [{
                "session_id": "s1",
                "group_id": "s1",
                "tk_version": "0.9.0",
                "events": [
                    {
                        "tool": "Bash", "is_tk": True, "tk_command": "def",
                        "tk_version": "0.9.0", "ts": iso(1001),
                        "outcome": "UNKNOWN", "own_log_matched": True,
                        "raw_chars": None,
                    },
                    {
                        "tool": "Bash", "is_tk": True, "tk_command": "def",
                        "tk_version": "0.10.0", "ts": iso(2001),
                        "outcome": "UNKNOWN", "own_log_matched": True,
                        "raw_chars": None,
                    },
                    {
                        "tool": "Read", "is_tk": False, "ts": iso(1002),
                        "tool_input_summary": "/repo/Old.cs",
                        "read_offset": 1, "read_limit": 100,
                    },
                    {
                        "tool": "Read", "is_tk": False, "ts": iso(2002),
                        "tool_input_summary": "/repo/New.cs",
                        "read_offset": 1, "read_limit": 100,
                    },
                    {
                        "tool": "Read", "is_tk": False,
                        "tool_input_summary": "/repo/UnknownVersion.cs",
                    },
                ],
            }],
            "groups": [],
            "interventions": [
                {
                    "ts_epoch": 1000.0, "decision": "cap-read", "session_id": "s1",
                    "cwd": "/repo", "command": "/repo/Old.cs", "tk_version": "0.9.0",
                    "adopted": True, "nudge_subtype": "cap_read",
                },
                {
                    "ts_epoch": 2000.0, "decision": "cap-read", "session_id": "s1",
                    "cwd": "/repo", "command": "/repo/New.cs", "tk_version": "0.10.0",
                    "adopted": False, "nudge_subtype": "cap_read",
                },
            ],
            "outcome_counts": {}, "aggregates": {},
            "match_stats": {"total_tk_events": 2},
            "generated_for_range": {},
            "adoption": {"by_subtype": {"cap_read": {"n_interventions": 2}}},
        }

        inferred_versions = {
            iso(1002): "0.9.0",
            iso(2002): "0.10.0",
        }
        with patch("join._infer_version", side_effect=lambda ts: inferred_versions.get(ts)):
            filtered = join.apply_version_filter(model, "0.10.0")

        self.assertEqual(filtered["sessions"][0]["tk_version"], "0.10.0")
        self.assertEqual(filtered["sessions"][0]["ts_start"], iso(2001))
        self.assertEqual(filtered["sessions"][0]["ts_end"], iso(2002))
        self.assertEqual(filtered["generated_for_range"]["actual_from"], iso(2001))
        self.assertEqual(filtered["generated_for_range"]["actual_to"], iso(2002))
        self.assertEqual(
            [event["ts"] for event in filtered["sessions"][0]["events"]],
            [iso(2001), iso(2002)],
        )
        self.assertEqual(len(filtered["interventions"]), 1)
        self.assertEqual(filtered["interventions"][0]["tk_version"], "0.10.0")
        self.assertTrue(filtered["interventions"][0]["adopted"])
        self.assertEqual(
            filtered["adoption"]["by_subtype"]["cap_read"]["n_interventions"], 1
        )
        self.assertEqual(filtered["match_stats"]["own_log_matched_events"], 1)
        self.assertEqual(filtered["match_stats"]["raw_baseline_events"], 0)

    def test_filter_keeps_latest_native_activity_without_latest_tk_event(self) -> None:
        def iso(epoch: float) -> str:
            return datetime.fromtimestamp(epoch, tz=timezone.utc).isoformat()

        native_ts = iso(2002)
        model = {
            "by_version": [
                {"version": "0.9.0", "n": 1},
                {"version": "0.10.0", "n": 1},
            ],
            "sessions": [{
                "session_id": "s1",
                "group_id": "s1",
                "tk_version": "0.9.0",
                "events": [
                    {
                        "tool": "Bash", "is_tk": True, "tk_command": "def",
                        "tk_version": "0.9.0", "ts": iso(1001),
                        "outcome": "UNKNOWN",
                    },
                    {
                        "tool": "Read", "is_tk": False, "ts": native_ts,
                        "tool_input_summary": "/repo/New.cs",
                        "read_offset": 1, "read_limit": 100,
                    },
                ],
            }],
            "groups": [],
            "interventions": [{
                "ts_epoch": 2000.0, "decision": "cap-read", "session_id": "s1",
                "cwd": "/repo", "command": "/repo/New.cs", "tk_version": "0.10.0",
            }],
            "outcome_counts": {}, "aggregates": {},
            "match_stats": {"total_tk_events": 1},
            "generated_for_range": {},
        }

        with patch(
            "join._infer_version",
            side_effect=lambda ts: "0.10.0" if ts == native_ts else None,
        ):
            filtered = join.apply_version_filter(model, "0.10.0")

        self.assertEqual(len(filtered["sessions"]), 1)
        self.assertEqual(filtered["sessions"][0]["events"][0]["tool"], "Read")
        self.assertEqual(len(filtered["interventions"]), 1)
        self.assertTrue(filtered["interventions"][0]["adopted"])

    def test_all_returns_original_model(self) -> None:
        model = {"by_version": [], "sessions": []}
        self.assertIs(join.apply_version_filter(model, "all"), model)


if __name__ == "__main__":
    unittest.main()
