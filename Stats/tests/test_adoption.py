"""
P3 — test_adoption.py

S4 post-hook adoption stats: does the agent obey tk-route.sh hook
interventions ("cap-read"/"nudge")? Synthetic sessions + a tiny hook-log
fixture only; no real transcripts or hook log reads.

Interventions live in a flat, model-level list (model["interventions"]),
grouped by session_id inside detect._detect_adoption itself — NOT attached
per session dict, because a resumed Claude Code session can span multiple
transcript files (one session dict per file) that all share the same
session_id.

Cardinality (RECURRENCE-GUARD): TWO interventions in the SAME session
(annotated independently); TWO DIFFERENT sessions sharing a decision
(forward-scan must not leak across the session_id boundary); and a session
split across TWO file-dicts sharing one session_id (must not double-count
or miss an adoption signal that lands in the other fragment).
"""

from __future__ import annotations

import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parent.parent))

import detect  # noqa: E402
import ingest_hooklog  # noqa: E402
import report  # noqa: E402


def _iso(epoch: float) -> str:
    return datetime.fromtimestamp(epoch, tz=timezone.utc).isoformat()


def _iv(decision: str, command: str, ts_epoch: float, session_id: str = "s1") -> dict:
    return {
        "ts_epoch": ts_epoch,
        "decision": decision,
        "session_id": session_id,
        "cwd": "/repo",
        "command": command,
    }


def _tk_nav_ev(cmd: str, ts_epoch: float) -> dict:
    return {
        "tool": "Bash", "is_tk": True, "tk_command": cmd,
        "ts": _iso(ts_epoch), "tool_input_summary": f"tk {cmd}",
    }


def _bash_ev(cmd: str, ts_epoch: float) -> dict:
    return {"tool": "Bash", "is_tk": False, "ts": _iso(ts_epoch), "tool_input_summary": cmd}


def _read_ev(path: str, ts_epoch: float, offset=None, limit=None) -> dict:
    return {
        "tool": "Read", "is_tk": False, "ts": _iso(ts_epoch),
        "tool_input_summary": path, "read_offset": offset, "read_limit": limit,
    }


def _tk_write_ev(file_path: str, ts_epoch: float) -> dict:
    return {
        "tool": "Bash", "is_tk": True, "tk_command": "write",
        "tk_operand_values": [file_path, "10-20"],
        "ts": _iso(ts_epoch), "tool_input_summary": f"tk write {file_path} 10-20",
    }


def _edit_ev(file_path: str, ts_epoch: float) -> dict:
    return {"tool": "Edit", "is_tk": False, "ts": _iso(ts_epoch), "tool_input_summary": file_path}


def _model(sessions: list[dict], interventions: list[dict]) -> dict:
    return {"sessions": sessions, "interventions": interventions}


def _session(session_id: str, events: list[dict]) -> dict:
    return {"session_id": session_id, "events": events}


class StealthReadNudge(unittest.TestCase):
    def test_nudge_view_within_window_adopted(self) -> None:
        iv = _iv("nudge", "cat Foo/Bar.cs", 1000.0)
        model = _model(
            [_session("s1", [_bash_ev("ls", 1001.0), _tk_nav_ev("view", 1002.0)])],
            [iv],
        )
        detect._detect_adoption(model)
        self.assertEqual(iv["nudge_subtype"], "stealth_read")
        self.assertTrue(iv["adopted"])

    def test_nudge_unrelated_events_not_adopted(self) -> None:
        iv = _iv("nudge", "cat Foo/Bar.cs", 1000.0)
        model = _model(
            [_session("s1", [_bash_ev("ls", 1001.0), _bash_ev("git status", 1002.0)])],
            [iv],
        )
        detect._detect_adoption(model)
        self.assertFalse(iv["adopted"])

    def test_intervention_near_session_end_no_crash(self) -> None:
        # Only 1 event follows — well under ADOPTION_LOOKAHEAD (5).
        iv = _iv("nudge", "cat Foo/Bar.cs", 1000.0)
        model = _model([_session("s1", [_bash_ev("ls", 1001.0)])], [iv])
        detect._detect_adoption(model)  # must not raise
        self.assertFalse(iv["adopted"])

    def test_intervention_with_no_matching_session_no_crash(self) -> None:
        # session_id has no session dict at all (e.g. hook fired outside any
        # ingested transcript window).
        iv = _iv("nudge", "cat Foo/Bar.cs", 1000.0, session_id="missing")
        model = _model([_session("s1", [_tk_nav_ev("view", 1001.0)])], [iv])
        detect._detect_adoption(model)  # must not raise
        self.assertFalse(iv["adopted"])


class SymbolGrepNudge(unittest.TestCase):
    def test_def_within_window_adopted(self) -> None:
        iv = _iv("nudge", "grep -rn 'class FooService' src/", 1000.0)
        model = _model([_session("s1", [_tk_nav_ev("def", 1001.0)])], [iv])
        detect._detect_adoption(model)
        self.assertEqual(iv["nudge_subtype"], "symbol_grep")
        self.assertTrue(iv["adopted"])

    def test_unrelated_grep_retry_not_adopted(self) -> None:
        iv = _iv("nudge", "grep -rn 'class FooService' src/", 1000.0)
        model = _model([_session("s1", [_bash_ev("grep -rn FooService src/", 1001.0)])], [iv])
        detect._detect_adoption(model)
        self.assertFalse(iv["adopted"])


class CapRead(unittest.TestCase):
    def test_sliced_read_same_file_adopted(self) -> None:
        iv = _iv("cap-read", "/repo/Foo.cs", 1000.0)
        model = _model([_session("s1", [_read_ev("/repo/Foo.cs", 1001.0, offset=1, limit=200)])], [iv])
        detect._detect_adoption(model)
        self.assertEqual(iv["nudge_subtype"], "cap_read")
        self.assertTrue(iv["adopted"])

    def test_whole_file_reread_not_adopted(self) -> None:
        # Re-reading with no offset/limit is not an adoption signal.
        iv = _iv("cap-read", "/repo/Foo.cs", 1000.0)
        model = _model([_session("s1", [_read_ev("/repo/Foo.cs", 1001.0)])], [iv])
        detect._detect_adoption(model)
        self.assertFalse(iv["adopted"])

    def test_tk_view_adopts_regardless_of_target_file(self) -> None:
        iv = _iv("cap-read", "/repo/Foo.cs", 1000.0)
        model = _model([_session("s1", [_tk_nav_ev("callers", 1001.0)])], [iv])
        detect._detect_adoption(model)
        self.assertTrue(iv["adopted"])


class EditWriteNudge(unittest.TestCase):
    def test_tk_write_same_file_adopted(self) -> None:
        iv = _iv("nudge-edit-write", "/repo/Foo.cs", 1000.0)
        model = _model([_session("s1", [_tk_write_ev("/repo/Foo.cs", 1001.0)])], [iv])
        detect._detect_adoption(model)
        self.assertEqual(iv["nudge_subtype"], "edit_write")
        self.assertTrue(iv["adopted"])

    def test_secondary_tk_invocation_can_adopt_without_synthetic_event(self) -> None:
        iv = _iv("nudge-edit-write", "/repo/Foo.cs", 1000.0)
        event = _tk_nav_ev("diag", 1001.0)
        event["tk_invocations"] = [
            {"tk_command": "diag", "tk_operand_values": ["/repo"]},
            {"tk_command": "write", "tk_operand_values": ["/repo/Foo.cs", "10-20"]},
        ]
        model = _model([_session("s1", [event])], [iv])

        detect._detect_adoption(model)

        self.assertTrue(iv["adopted"])

    def test_tk_write_different_file_not_adopted(self) -> None:
        iv = _iv("nudge-edit-write", "/repo/Foo.cs", 1000.0)
        model = _model([_session("s1", [_tk_write_ev("/repo/Bar.cs", 1001.0)])], [iv])
        detect._detect_adoption(model)
        self.assertFalse(iv["adopted"])

    def test_another_native_edit_not_adopted(self) -> None:
        iv = _iv("nudge-edit-write", "/repo/Foo.cs", 1000.0)
        model = _model([_session("s1", [_edit_ev("/repo/Foo.cs", 1001.0)])], [iv])
        detect._detect_adoption(model)
        self.assertFalse(iv["adopted"])

    def test_two_interventions_same_session_independent(self) -> None:
        # Cardinality (RECURRENCE-GUARD): two edit_write interventions in the
        # SAME session, on different files, must be scored independently.
        iv1 = _iv("nudge-edit-write", "/repo/A.cs", 1000.0)
        iv2 = _iv("nudge-edit-write", "/repo/B.cs", 2000.0)
        model = _model(
            [_session("s1", [
                _tk_write_ev("/repo/A.cs", 1001.0),   # adopts iv1 only
                _edit_ev("/repo/B.cs", 2001.0),         # NOT an adoption signal for iv2
            ])],
            [iv1, iv2],
        )
        detect._detect_adoption(model)
        self.assertTrue(iv1["adopted"])
        self.assertFalse(iv2["adopted"])

    def test_forward_scan_does_not_leak_across_sessions(self) -> None:
        # Cardinality (RECURRENCE-GUARD): TWO DIFFERENT sessions sharing the
        # same target file — forward-scan must not leak across the
        # session_id boundary.
        iv_a = _iv("nudge-edit-write", "/repo/Foo.cs", 1000.0, session_id="sa")
        iv_b = _iv("nudge-edit-write", "/repo/Foo.cs", 1000.0, session_id="sb")
        model = _model(
            [
                _session("sa", [_tk_write_ev("/repo/Foo.cs", 1001.0)]),
                _session("sb", [_edit_ev("/repo/Foo.cs", 1001.0)]),
            ],
            [iv_a, iv_b],
        )
        detect._detect_adoption(model)
        self.assertTrue(iv_a["adopted"])
        self.assertFalse(iv_b["adopted"])


class AutoRoutedDecisions(unittest.TestCase):
    def test_route_tk_and_route_rtk_left_unannotated(self) -> None:
        iv_tk = {"ts_epoch": 1000.0, "decision": "route-tk", "session_id": "s1", "cwd": "/repo", "command": "tk dotnet build"}
        iv_rtk = {"ts_epoch": 1000.0, "decision": "route-rtk", "session_id": "s1", "cwd": "/repo", "command": "rtk read x"}
        model = _model([_session("s1", [])], [iv_tk, iv_rtk])
        detect._detect_adoption(model)
        self.assertIsNone(iv_tk["adopted"])
        self.assertIsNone(iv_tk["nudge_subtype"])
        self.assertIsNone(iv_rtk["adopted"])
        self.assertIsNone(iv_rtk["nudge_subtype"])


class AdoptionCardinality(unittest.TestCase):
    """Cardinality>1 on the join key (session_id): two interventions in the
    SAME session must be scored independently."""

    def test_two_interventions_same_session_independent(self) -> None:
        iv1 = _iv("cap-read", "/repo/A.cs", 1000.0)
        iv2 = _iv("cap-read", "/repo/B.cs", 2000.0)
        model = _model(
            [_session("s1", [
                _tk_nav_ev("view", 1001.0),          # adopts iv1 only
                _bash_ev("ls", 1500.0),
                _bash_ev("cat /repo/B.cs", 2001.0),   # NOT a nav/sliced-read signal for iv2
            ])],
            [iv1, iv2],
        )
        detect._detect_adoption(model)
        self.assertTrue(iv1["adopted"])
        self.assertFalse(iv2["adopted"])


class CrossSessionIsolation(unittest.TestCase):
    """TWO DIFFERENT sessions sharing the same decision/command shape:
    forward-scan must not leak across the session_id boundary."""

    def test_forward_scan_does_not_leak_across_sessions(self) -> None:
        iv_a = _iv("nudge", "cat Foo/Bar.cs", 1000.0, session_id="sa")
        iv_b = _iv("nudge", "cat Foo/Bar.cs", 1000.0, session_id="sb")
        model = _model(
            [
                _session("sa", [_tk_nav_ev("view", 1001.0)]),
                _session("sb", [_bash_ev("ls", 1001.0)]),
            ],
            [iv_a, iv_b],
        )
        detect._detect_adoption(model)
        self.assertTrue(iv_a["adopted"])
        self.assertFalse(iv_b["adopted"])


class ResumedSessionFileFragmentation(unittest.TestCase):
    """A resumed Claude Code session can span multiple transcript files that
    all share the same session_id (one session dict per file). Interventions
    must not be double-counted, and an adoption signal landing in a LATER
    file-fragment than the intervention's own fragment must still count."""

    def test_signal_in_later_file_fragment_still_adopts(self) -> None:
        iv = _iv("nudge", "cat Foo/Bar.cs", 1000.0, session_id="s1")
        model = _model(
            [
                _session("s1", [_bash_ev("ls", 1001.0)]),           # fragment 1: no signal
                _session("s1", [_tk_nav_ev("view", 1002.0)]),       # fragment 2: adopts
            ],
            [iv],
        )
        detect._detect_adoption(model)
        self.assertTrue(iv["adopted"])

    def test_intervention_not_double_counted_across_fragments(self) -> None:
        iv = _iv("cap-read", "/repo/Foo.cs", 1000.0, session_id="s1")
        model = _model(
            [
                _session("s1", [_bash_ev("ls", 1001.0)]),
                _session("s1", [_bash_ev("git status", 1002.0)]),
            ],
            [iv],
        )
        detect._detect_adoption(model)  # must not raise, must not touch iv twice inconsistently
        self.assertFalse(iv["adopted"])
        rpt_model = {
            "generated_for_range": {},
            "match_stats": {"total_tk_events": 0, "matched": 0, "unmatched": 0, "match_rate": 0.0},
            "outcome_counts": {"WIN": 0, "NEUTRAL": 0, "NET_NEGATIVE": 0, "UNKNOWN": 0},
            "aggregates": {
                "win": {"raw_chars": 0, "shown_chars": 0, "saved_chars": 0},
                "net_negative": {"raw_chars": 0, "shown_chars": 0, "extra_chars": 0},
            },
            "sessions": model["sessions"],
            "groups": [],
            "interventions": model["interventions"],
        }
        rpt = report._build_report(rpt_model)
        # One intervention -> exactly one n_interventions, not one per fragment.
        self.assertEqual(rpt["adoption"]["by_subtype"]["cap_read"]["n_interventions"], 1)


class RouteTkAutoRouted(unittest.TestCase):
    def test_route_tk_in_auto_routed_not_in_adoption_rate(self) -> None:
        interventions = [
            {"decision": "route-tk", "adopted": None, "nudge_subtype": None},
            {"decision": "route-rtk", "adopted": None, "nudge_subtype": None},
            {"decision": "nudge", "adopted": True, "nudge_subtype": "stealth_read"},
        ]
        model = {
            "generated_for_range": {},
            "match_stats": {"total_tk_events": 0, "matched": 0, "unmatched": 0, "match_rate": 0.0},
            "outcome_counts": {"WIN": 0, "NEUTRAL": 0, "NET_NEGATIVE": 0, "UNKNOWN": 0},
            "aggregates": {
                "win": {"raw_chars": 0, "shown_chars": 0, "saved_chars": 0},
                "net_negative": {"raw_chars": 0, "shown_chars": 0, "extra_chars": 0},
            },
            "sessions": [{"session_id": "s1", "agent_type": "main", "cost": {}, "events": []}],
            "groups": [],
            "interventions": interventions,
        }
        rpt = report._build_report(model)
        adoption = rpt["adoption"]
        self.assertEqual(adoption["auto_routed"]["n"], 2)
        self.assertNotIn("route-tk", adoption["by_subtype"])
        self.assertNotIn("route-rtk", adoption["by_subtype"])
        self.assertIn("stealth_read", adoption["by_subtype"])
        self.assertEqual(adoption["by_subtype"]["stealth_read"]["n_interventions"], 1)
        self.assertEqual(adoption["by_subtype"]["stealth_read"]["n_adopted"], 1)
        self.assertEqual(adoption["by_subtype"]["stealth_read"]["rate"], 1.0)


class HookLogIngest(unittest.TestCase):
    def test_malformed_lines_ignored(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            log_path = Path(tmp) / "tk-hook-20260709.log"
            log_path.write_text(
                "1700000000\tnudge\ts1\t/repo\tcat Foo.cs\n"
                "not-a-valid-line\n"
                "1700000001\tcap-read\ts1\t/repo\n"  # missing the command column
                "1700000002\troute-tk\ts1\t/repo\tdotnet build\n"
            )
            with patch.object(ingest_hooklog, "_log_dir", return_value=Path(tmp)):
                records = ingest_hooklog.load_interventions()
        self.assertEqual(len(records), 2)
        self.assertEqual(records[0]["decision"], "nudge")
        self.assertEqual(records[1]["decision"], "route-tk")


if __name__ == "__main__":
    unittest.main()
