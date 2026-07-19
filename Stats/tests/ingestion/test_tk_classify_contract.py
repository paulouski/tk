"""
P3 — test_classify_contract.py

Mirror contract tests for Stats/ingest.py:
  - _strip_detail_flags + _classify_tk classify golden commands exactly as
    Analytics.cs Classify does. Drives from a JSON cases file so the contract
    is reviewable in one place.
  - _is_symbol_grep (Stats/detect.py) returns expected booleans for a few
    bash command strings covering the F1 fix path.
"""

from __future__ import annotations

import json
import shlex
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from tkstats.detect import events as detect  # noqa: E402
from tkstats.ingestion import common  # noqa: E402


_CASES_PATH = Path(__file__).parent / "classify_cases.json"


def _split(cmd: str) -> list[str]:
    try:
        return shlex.split(cmd)
    except ValueError:
        return cmd.split()


def _classify(cmd: str) -> dict:
    toks = _split(cmd)
    tk_args = toks[1:]  # drop the "tk" prefix
    stripped, detail = common._strip_detail_flags(tk_args)
    command, sub, flags, operands, operand_values = common._classify_tk(stripped)
    return {
        "command": command,
        "sub": sub,
        "flags": flags,
        "operands": operands,
        "operand_values": operand_values,
        "detail": detail,
    }


class ClassifyContract(unittest.TestCase):
    def test_every_case_matches(self) -> None:
        cases = json.loads(_CASES_PATH.read_text())
        self.assertGreaterEqual(
            len(cases), 15,
            f"classify_cases.json has {len(cases)} cases, spec requires >=15",
        )
        for case in cases:
            with self.subTest(cmd=case["cmd"]):
                actual = _classify(case["cmd"])
                expected = case["expect"]
                self.assertEqual(actual, expected, f"cmd={case['cmd']!r}")


class SymbolGrep(unittest.TestCase):
    def _ev(self, cmd: str) -> dict:
        return {"tool": "Bash", "tool_input_summary": cmd, "is_tk": False}

    def test_alternation_pascal(self) -> None:
        self.assertTrue(detect._is_symbol_grep(
            self._ev('grep -rn "AvailableBalance|PendingHolds" Wallet.cs')))

    def test_alternation_decl_in_pattern(self) -> None:
        # "class X" substring anywhere in cmd matches via the DECL backstop.
        self.assertTrue(detect._is_symbol_grep(
            self._ev('grep -n "RunAsync|class PayrollRunOrchestrator" x.cs')))

    def test_lowercase_pattern_negative(self) -> None:
        self.assertFalse(detect._is_symbol_grep(
            self._ev('grep -i "error" log.txt')))

    def test_all_caps_pattern_negative(self) -> None:
        # "TODO" is SCREAMING_SNAKE, not PascalCase — must not flag.
        self.assertFalse(detect._is_symbol_grep(
            self._ev('grep TODO src')))


if __name__ == "__main__":
    unittest.main()
