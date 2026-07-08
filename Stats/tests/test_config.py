"""
P3 — test_config.py

Sanity checks that config.py exposes each extracted constant with the
expected literal value. Catches any accidental rename/typo during the
threshold-extraction pass.
"""

from __future__ import annotations

import sys
import unittest
from datetime import timedelta
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))

import config  # noqa: E402


class ConfigValues(unittest.TestCase):
    def test_match_window(self) -> None:
        self.assertEqual(config.MATCH_WINDOW, timedelta(seconds=30))

    def test_search_nav_commands(self) -> None:
        self.assertEqual(
            config._SEARCH_NAV_COMMANDS,
            frozenset({
                "grep", "focus", "refs", "def", "callers",
                "view", "files", "tree", "find", "ls",
            }),
        )

    def test_fallback_tools(self) -> None:
        self.assertEqual(config._FALLBACK_TOOLS, frozenset({"Grep", "Glob"}))

    def test_result_used_tools(self) -> None:
        self.assertEqual(config._RESULT_USED_TOOLS, frozenset({"Read"}))

    def test_edit_result_used_tools(self) -> None:
        self.assertEqual(
            config.EDIT_RESULT_USED_TOOLS,
            frozenset({"Edit", "MultiEdit", "Write"}),
        )

    def test_native_grep_retry_lookback(self) -> None:
        self.assertEqual(config.NATIVE_GREP_RETRY_LOOKBACK, 10)

    def test_stealth_read_extensions(self) -> None:
        self.assertEqual(
            config.STEALTH_READ_EXTENSIONS,
            frozenset({
                ".cs", ".py", ".ts", ".tsx", ".js", ".json",
                ".md", ".yaml", ".yml", ".sh",
            }),
        )

    def test_chars_buckets(self) -> None:
        self.assertEqual(
            config.CHARS_BUCKETS,
            ("<5k", "5-15k", "15-40k", ">40k"),
        )

    def test_line_buckets(self) -> None:
        self.assertEqual(
            config.LINE_BUCKETS,
            ("<=400", "401-500", "501-600", "601-700", "701-800", "801-1500", ">1500"),
        )

    def test_follow_up_window(self) -> None:
        self.assertEqual(config.FOLLOW_UP_WINDOW, 5)


if __name__ == "__main__":
    unittest.main()
