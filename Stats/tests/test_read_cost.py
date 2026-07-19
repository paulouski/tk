from __future__ import annotations

import json
import sys
import tempfile
import unittest
from datetime import datetime, timezone
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parent.parent))

import bash_opp_reports  # noqa: E402


def _iso(epoch: float) -> str:
    return datetime.fromtimestamp(epoch, tz=timezone.utc).isoformat()


class ReadCostReport(unittest.TestCase):
    def test_cap_errors_and_repeat_kinds_are_reported_separately(self) -> None:
        reads = [
            ("u1", 1001, {"file_path": "Foo.cs"}, "capped output", False),
            ("u2", 1002, {"file_path": "Foo.cs", "offset": 101, "limit": 100}, "slice", False),
            ("u3", 1003, {"file_path": "Foo.cs", "offset": 101, "limit": 100}, "same slice", False),
            ("u4", 1004, {"file_path": "Bar.cs"}, "whole", False),
            ("u5", 1005, {"file_path": "Bar.cs"}, "read error", True),
        ]

        with tempfile.TemporaryDirectory() as tmp:
            projects_dir = Path(tmp)
            transcript = projects_dir / "session.jsonl"
            lines = []
            for tool_use_id, epoch, tool_input, result, is_error in reads:
                lines.append({
                    "timestamp": _iso(epoch),
                    "message": {
                        "role": "assistant",
                        "content": [{
                            "type": "tool_use", "id": tool_use_id,
                            "name": "Read", "input": tool_input,
                        }],
                    },
                })
                result_line = {
                    "timestamp": _iso(epoch + 0.1),
                    "message": {
                        "role": "user",
                        "content": [{
                            "type": "tool_result", "tool_use_id": tool_use_id,
                            "content": result, "is_error": is_error,
                        }],
                    },
                }
                if tool_use_id == "u1":
                    result_line["toolUseResult"] = {
                        "file": {"numLines": 48, "totalLines": 337}
                    }
                lines.append(result_line)
            transcript.write_text("\n".join(json.dumps(line) for line in lines))
            model = {
                "sessions": [{
                    "session_id": "s1", "session_file": "session.jsonl",
                    "tk_version": "0.10.0", "is_subagent": False,
                }],
                "interventions": [{
                    "ts_epoch": 1000.5, "decision": "cap-read",
                    "session_id": "s1", "command": "Foo.cs",
                }],
            }

            with patch.object(bash_opp_reports, "_projects_dir", return_value=projects_dir):
                report = bash_opp_reports.build_read_cost_report(model, "0.10.0", "all")

        total = report["total"]
        self.assertEqual(total["read_events"], 5)
        self.assertEqual(total["successful_events"], 4)
        self.assertEqual(total["error_events"], 1)
        self.assertEqual(total["requested_uncapped_events"], 3)
        self.assertEqual(total["known_auto_capped_events"], 1)
        self.assertEqual(total["known_auto_caps_with_returned_lines"], 1)
        self.assertEqual(total["known_auto_cap_returned_lines_total"], 48)
        self.assertEqual(total["known_auto_cap_returned_line_buckets"]["<=400"], 1)
        self.assertEqual(total["no_known_cap_events"], 2)
        self.assertEqual(total["explicit_slice_events"], 2)
        self.assertEqual(total["different_slice_events"], 1)
        self.assertEqual(total["exact_slice_repeat_events"], 1)
        self.assertEqual(total["whole_file_repeat_events"], 1)
        self.assertTrue(any(row["is_error"] for row in report["top_reads"]))
        capped = next(row for row in report["top_reads"] if row["known_auto_cap"])
        self.assertEqual(capped["returned_lines"], 48)
        self.assertEqual(capped["total_lines"], 337)
        self.assertTrue(report["cap_correlation"]["effective_limit_recorded"])
        self.assertFalse(report["cap_correlation"]["configured_limit_recorded"])


if __name__ == "__main__":
    unittest.main()
