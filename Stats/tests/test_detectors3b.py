"""
P3 — test_detectors3b.py

Wave 3b detector tests: B2 native-grep retry, B3 stealth read, C3
result_used_edit, B5 parent-sub operand overlap. Synthetic events only;
no DB or jsonl reads.
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

import detect  # noqa: E402
import join  # noqa: E402


def _bash_ev(cmd: str, *, is_tk: bool = False, shown: int = 0) -> dict:
    return {
        "tool": "Bash",
        "is_tk": is_tk,
        "tool_input_summary": cmd,
        "shown_chars": shown,
    }


def _tk_ev(cmd: str, operands: list[str], tool: str = "Read") -> dict:
    return {
        "tool": tool,
        "is_tk": True,
        "tk_command": cmd,
        "tk_operand_values": operands,
        "tool_input_summary": f"{tool} foo",
        "shown_chars": 100,
    }


class NativeGrepRetry(unittest.TestCase):
    def test_same_pattern_within_window_marks_retry(self) -> None:
        events = [
            _bash_ev("grep -rn TODO src/"),
            _bash_ev("grep -rn TODO src/"),
        ]
        detect._detect_native_grep_retry(events)
        self.assertFalse(events[0]["native_grep_retry"])
        self.assertTrue(events[1]["native_grep_retry"])
        self.assertEqual(events[1]["native_grep_retry_pattern"], "TODO")

    def test_different_pattern_does_not_mark(self) -> None:
        events = [
            _bash_ev("grep -rn TODO src/"),
            _bash_ev("grep -rn FIXME src/"),
        ]
        detect._detect_native_grep_retry(events)
        self.assertFalse(events[0]["native_grep_retry"])
        self.assertFalse(events[1]["native_grep_retry"])

    def test_tk_grep_is_skipped(self) -> None:
        # Grep via the tk tool (is_tk=True) must not participate.
        events = [
            {"tool": "Bash", "is_tk": True, "tool_input_summary": "tk grep TODO"},
            {"tool": "Bash", "is_tk": True, "tool_input_summary": "tk grep TODO"},
        ]
        detect._detect_native_grep_retry(events)
        self.assertFalse(events[0]["native_grep_retry"])
        self.assertFalse(events[1]["native_grep_retry"])

    def test_empty_pattern_skipped(self) -> None:
        events = [
            _bash_ev("grep -r --include=*"),
        ]
        detect._detect_native_grep_retry(events)
        self.assertFalse(events[0]["native_grep_retry"])
        self.assertEqual(events[0]["native_grep_retry_pattern"], "")


class StealthRead(unittest.TestCase):
    def test_cat_tracked_ext(self) -> None:
        ev = _bash_ev("cat src/Foo.cs", shown=5000)
        detect._detect_stealth_read([ev])
        self.assertTrue(ev["stealth_read"])
        self.assertEqual(ev["stealth_read_file"], "src/Foo.cs")
        self.assertEqual(ev["stealth_read_chars"], 5000)

    def test_sed_with_range_and_tracked_ext(self) -> None:
        ev = _bash_ev("sed -n 70,170p src/Bar.cs", shown=3000)
        detect._detect_stealth_read([ev])
        self.assertTrue(ev["stealth_read"])
        self.assertEqual(ev["stealth_read_file"], "src/Bar.cs")

    def test_grep_is_not_stealth_read(self) -> None:
        ev = _bash_ev("grep -rn x .", shown=100)
        detect._detect_stealth_read([ev])
        self.assertFalse(ev["stealth_read"])

    def test_unext_path_not_marked(self) -> None:
        ev = _bash_ev("cat /etc/passwd")
        detect._detect_stealth_read([ev])
        self.assertFalse(ev["stealth_read"])

    def test_glob_path_skipped(self) -> None:
        ev = _bash_ev("cat *.cs")
        detect._detect_stealth_read([ev])
        self.assertFalse(ev["stealth_read"])

    def test_tk_cat_skipped(self) -> None:
        ev = _bash_ev("cat src/Foo.cs", is_tk=True)
        detect._detect_stealth_read([ev])
        self.assertFalse(ev["stealth_read"])


class ReadRepeatKinds(unittest.TestCase):
    @staticmethod
    def _read(path: str, offset=None, limit=None, shown: int = 100) -> dict:
        return {
            "tool": "Read",
            "is_tk": False,
            "tool_input_summary": path,
            "read_offset": offset,
            "read_limit": limit,
            "shown_chars": shown,
        }

    def test_exact_slice_different_slice_and_whole_file_are_distinct(self) -> None:
        events = [
            self._read("Foo.cs", offset=1, limit=100),
            self._read("Foo.cs", offset=101, limit=100),
            self._read("Foo.cs", offset=1, limit=100),
            self._read("Bar.cs"),
            self._read("Bar.cs"),
        ]

        detect._detect_reread(events)

        self.assertEqual(events[1]["read_repeat_kind"], "different_slice")
        self.assertFalse(events[1]["reread"])
        self.assertEqual(events[2]["read_repeat_kind"], "exact_slice_repeat")
        self.assertTrue(events[2]["reread"])
        self.assertEqual(events[4]["read_repeat_kind"], "whole_file_repeat")
        self.assertTrue(events[4]["reread"])

    def test_rollup_keeps_repeat_kind_counts(self) -> None:
        events = [
            self._read("Foo.cs", offset=1, limit=100),
            self._read("Foo.cs", offset=101, limit=100, shown=200),
            self._read("Foo.cs", offset=101, limit=100, shown=300),
        ]
        detect._detect_reread(events)
        session = {"events": events}

        detect._rollup_session(session)

        self.assertEqual(session["n_reread"], 1)
        self.assertEqual(session["read_repeat_counts"]["different_slice"], 1)
        self.assertEqual(session["read_repeat_counts"]["exact_slice_repeat"], 1)
        self.assertEqual(session["read_repeat_chars"]["different_slice"], 200)

class ResultUsedEdit(unittest.TestCase):
    def test_codex_exact_bash_read_marks_result_used(self) -> None:
        events = [
            _tk_ev("def", ["Foo"]),
            _bash_ev("sed -n 1,20p src/Foo.cs"),
        ]

        detect._detect_stealth_read(events)
        detect._detect_fallback(events)

        self.assertTrue(events[0]["result_used"])
        self.assertTrue(events[0]["result_used_same_target"])

    def test_codex_code_snippet_marks_result_used(self) -> None:
        events = [
            _tk_ev("def", ["Foo"]),
            {
                "tool": "Exec",
                "is_tk": False,
                "tool_input_summary": "mcp__codebase_memory_mcp__get_code_snippet",
                "nested_tools": ["mcp__codebase_memory_mcp__get_code_snippet"],
            },
        ]

        detect._detect_fallback(events)

        self.assertTrue(events[0]["result_used"])

    def test_edit_in_next_three_marks_result_used_edit(self) -> None:
        events = [
            _tk_ev("def", ["Foo"]),
            {"tool": "Edit", "is_tk": False,
             "tool_input_summary": "src/Foo.cs old=10 new=20"},
        ]
        detect._detect_fallback(events)
        self.assertTrue(events[0]["result_used_edit"])
        self.assertTrue(events[0]["result_used_edit_same_target"])
        # Existing Read-based result_used fields must remain False.
        self.assertFalse(events[0]["result_used"])
        self.assertFalse(events[0]["result_used_same_target"])

    def test_no_edit_within_window(self) -> None:
        events = [
            _tk_ev("def", ["Foo"]),
            {"tool": "Bash", "is_tk": False, "tool_input_summary": "ls"},
        ]
        detect._detect_fallback(events)
        self.assertFalse(events[0]["result_used_edit"])
        self.assertFalse(events[0]["result_used_edit_same_target"])

    def test_edit_far_away_not_counted(self) -> None:
        events = [
            _tk_ev("def", ["Foo"]),
            {"tool": "Bash", "is_tk": False, "tool_input_summary": "ls"},
            {"tool": "Bash", "is_tk": False, "tool_input_summary": "ls"},
            {"tool": "Bash", "is_tk": False, "tool_input_summary": "ls"},
            {"tool": "Edit", "is_tk": False,
             "tool_input_summary": "src/Foo.cs old=10 new=20"},
        ]
        detect._detect_fallback(events)
        self.assertFalse(events[0]["result_used_edit"])


class OperandOverlap(unittest.TestCase):
    def _sm(self, sid: str, *, sub: bool, parent: str | None,
            ops: list[str], group: str | None = None) -> dict:
        return {
            "session_id": sid,
            "is_subagent": sub,
            "group_id": group if group is not None else ("g1" if not sub else "g1"),
            "parent_session_id": parent,
            "cost": {"model": "", "usd": 0.0, "tokens": {}, "usd_by_type": {}},
            "events": [
                {"tool": "Bash", "is_tk": True, "tk_command": "def",
                 "tk_operand_values": ops, "tool_input_summary": "tk def x"},
            ],
        }

    def test_overlap_counts_common_operands(self) -> None:
        main = self._sm("m1", sub=False, parent=None, ops=["Foo", "Bar", "Baz"])
        sub = self._sm("s1", sub=True, parent="m1", ops=["Foo", "Bar", "Qux"])
        groups = join._build_groups([main, sub])
        self.assertEqual(len(groups), 1)
        g = groups[0]
        self.assertEqual(g["parent_sub_operand_overlap"], 2)
        self.assertIn("Foo", g["parent_sub_operand_examples"])
        self.assertIn("Bar", g["parent_sub_operand_examples"])

    def test_no_subs_no_overlap(self) -> None:
        main = self._sm("m1", sub=False, parent=None, ops=["Foo"])
        groups = join._build_groups([main])
        self.assertEqual(groups[0]["parent_sub_operand_overlap"], 0)
        self.assertEqual(groups[0]["parent_sub_operand_examples"], [])

    def test_disjoint_operands_zero_overlap(self) -> None:
        main = self._sm("m1", sub=False, parent=None, ops=["Foo"])
        sub = self._sm("s1", sub=True, parent="m1", ops=["Bar"])
        groups = join._build_groups([main, sub])
        self.assertEqual(groups[0]["parent_sub_operand_overlap"], 0)


if __name__ == "__main__":
    unittest.main()
